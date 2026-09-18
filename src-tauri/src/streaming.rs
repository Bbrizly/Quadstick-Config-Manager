//! Scoped IPC streams.
//!
//! These are deliberately not Tauri global events. A caller owns a subscription
//! id and a typed `Channel`; disposing the id removes that one listener. Live
//! frames pass through qcm-core's capacity-one latest-wins slot before they ever
//! reach IPC, so a slow window cannot build an input-frame queue.

use crate::adapters::hid::HidLiveInput;
use qcm_core::clock::SystemClock;
use qcm_core::live::{LiveInputManager, LiveSnapshot, LiveStatus};
use serde::Serialize;
use std::collections::BTreeMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::thread;
use std::time::Duration;
use tauri::ipc::Channel;

const IDLE_POLL: Duration = Duration::from_millis(4);
const MAX_BACKOFF_SLICE: Duration = Duration::from_millis(50);

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct LiveMotionDto {
    pub x: f64,
    pub y: f64,
    pub buttons: Vec<u16>,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum LiveStatusDto {
    Stopped,
    Searching,
    XinputOnly,
    Stale {
        product: String,
    },
    Reading {
        product: String,
        motion: LiveMotionDto,
    },
    Unavailable {
        code: String,
    },
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct LiveSnapshotDto {
    pub seq: u64,
    pub at_millis: u64,
    pub status: LiveStatusDto,
}

impl From<&LiveSnapshot> for LiveSnapshotDto {
    fn from(snapshot: &LiveSnapshot) -> Self {
        let status = match &snapshot.status {
            LiveStatus::Stopped => LiveStatusDto::Stopped,
            LiveStatus::Searching => LiveStatusDto::Searching,
            LiveStatus::XInputOnly => LiveStatusDto::XinputOnly,
            LiveStatus::Stale { product } => LiveStatusDto::Stale {
                product: product.to_string(),
            },
            LiveStatus::Reading { product, motion } => LiveStatusDto::Reading {
                product: product.to_string(),
                motion: LiveMotionDto {
                    x: motion.x(),
                    y: motion.y(),
                    buttons: motion.buttons().to_vec(),
                },
            },
            LiveStatus::Unavailable { code } => LiveStatusDto::Unavailable {
                code: code.as_str().to_owned(),
            },
        };
        Self {
            seq: snapshot.seq,
            at_millis: u64::try_from(snapshot.at.since_start().as_millis()).unwrap_or(u64::MAX),
            status,
        }
    }
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SubscriptionDto {
    pub subscription_id: String,
}

#[derive(Default)]
struct LiveControl {
    listeners: BTreeMap<u64, Channel<LiveSnapshotDto>>,
    worker_running: bool,
}

/// Process-wide live-input owner. It starts the HID worker when the first window
/// subscribes and the worker exits after the last listener is removed.
pub struct LiveRuntime {
    control: Arc<Mutex<LiveControl>>,
    next_id: AtomicU64,
}

impl Default for LiveRuntime {
    fn default() -> Self {
        Self::new()
    }
}

impl LiveRuntime {
    #[must_use]
    pub fn new() -> Self {
        Self {
            control: Arc::new(Mutex::new(LiveControl::default())),
            next_id: AtomicU64::new(0),
        }
    }

    fn control(&self) -> MutexGuard<'_, LiveControl> {
        self.control.lock().unwrap_or_else(PoisonError::into_inner)
    }

    pub fn subscribe(&self, channel: Channel<LiveSnapshotDto>) -> SubscriptionDto {
        let id = self
            .next_id
            .fetch_add(1, Ordering::Relaxed)
            .saturating_add(1);
        let should_start = {
            let mut control = self.control();
            control.listeners.insert(id, channel);
            if control.worker_running {
                false
            } else {
                control.worker_running = true;
                true
            }
        };
        if should_start {
            spawn_live_worker(Arc::clone(&self.control));
        }
        SubscriptionDto {
            subscription_id: format!("live-{id}"),
        }
    }

    pub fn unsubscribe(&self, raw: &str) {
        if let Some(id) = parse_subscription(raw, "live-") {
            self.control().listeners.remove(&id);
        }
    }

    #[cfg(test)]
    #[must_use]
    pub fn listener_count(&self) -> usize {
        self.control().listeners.len()
    }
}

fn spawn_live_worker(control: Arc<Mutex<LiveControl>>) {
    thread::Builder::new()
        .name("qcm-live-input".to_owned())
        .spawn(move || {
            let mut manager = LiveInputManager::new(HidLiveInput::new(), SystemClock::new());
            let stream = manager.stream();
            manager.start();

            loop {
                if listener_snapshot(&control).is_empty() {
                    manager.stop();
                    let mut state = control.lock().unwrap_or_else(PoisonError::into_inner);
                    state.worker_running = false;
                    // A subscription may have arrived in the tiny window between
                    // the empty check and clearing the flag. Start its replacement
                    // worker here instead of making the caller race us.
                    if !state.listeners.is_empty() {
                        state.worker_running = true;
                        drop(state);
                        spawn_live_worker(Arc::clone(&control));
                    }
                    break;
                }

                manager.poll();
                if let Some(snapshot) = stream.take() {
                    send_live(&control, LiveSnapshotDto::from(&snapshot));
                }

                let pause = manager
                    .next_attempt_in()
                    .unwrap_or(IDLE_POLL)
                    .min(MAX_BACKOFF_SLICE);
                thread::sleep(pause);
            }
        })
        .expect("live input worker thread should start");
}

fn listener_snapshot(control: &Arc<Mutex<LiveControl>>) -> Vec<(u64, Channel<LiveSnapshotDto>)> {
    control
        .lock()
        .unwrap_or_else(PoisonError::into_inner)
        .listeners
        .iter()
        .map(|(&id, channel)| (id, channel.clone()))
        .collect()
}

fn send_live(control: &Arc<Mutex<LiveControl>>, snapshot: LiveSnapshotDto) {
    let failed: Vec<u64> = listener_snapshot(control)
        .into_iter()
        .filter_map(|(id, channel)| channel.send(snapshot.clone()).err().map(|_| id))
        .collect();
    if !failed.is_empty() {
        let mut state = control.lock().unwrap_or_else(PoisonError::into_inner);
        for id in failed {
            state.listeners.remove(&id);
        }
    }
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct DeviceInvalidationDto {
    pub revision: u64,
}

#[derive(Default)]
pub struct DeviceInvalidationHub {
    listeners: Mutex<BTreeMap<u64, Channel<DeviceInvalidationDto>>>,
    next_id: AtomicU64,
    revision: AtomicU64,
}

impl DeviceInvalidationHub {
    fn listeners(&self) -> MutexGuard<'_, BTreeMap<u64, Channel<DeviceInvalidationDto>>> {
        self.listeners
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
    }

    pub fn subscribe(&self, channel: Channel<DeviceInvalidationDto>) -> SubscriptionDto {
        let id = self
            .next_id
            .fetch_add(1, Ordering::Relaxed)
            .saturating_add(1);
        self.listeners().insert(id, channel);
        SubscriptionDto {
            subscription_id: format!("devices-{id}"),
        }
    }

    pub fn unsubscribe(&self, raw: &str) {
        if let Some(id) = parse_subscription(raw, "devices-") {
            self.listeners().remove(&id);
        }
    }

    /// Invalidate cached device/library views. Failed channels are listeners
    /// whose WebView went away without disposing; pruning them here prevents a
    /// dead window from living forever in native state.
    pub fn notify(&self) {
        let revision = self
            .revision
            .fetch_add(1, Ordering::Relaxed)
            .saturating_add(1);
        let event = DeviceInvalidationDto { revision };
        let listeners: Vec<(u64, Channel<DeviceInvalidationDto>)> = self
            .listeners()
            .iter()
            .map(|(&id, channel)| (id, channel.clone()))
            .collect();
        let failed: Vec<u64> = listeners
            .into_iter()
            .filter_map(|(id, channel)| channel.send(event.clone()).err().map(|_| id))
            .collect();
        if !failed.is_empty() {
            let mut listeners = self.listeners();
            for id in failed {
                listeners.remove(&id);
            }
        }
    }

    #[cfg(test)]
    #[must_use]
    pub fn listener_count(&self) -> usize {
        self.listeners().len()
    }
}

fn parse_subscription(raw: &str, prefix: &str) -> Option<u64> {
    raw.strip_prefix(prefix)?.parse().ok()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};

    #[test]
    fn invalidation_subscription_dispose_is_idempotent() {
        let hub = DeviceInvalidationHub::default();
        let messages = Arc::new(AtomicUsize::new(0));
        let seen = Arc::clone(&messages);
        let channel = Channel::new(move |_| {
            seen.fetch_add(1, Ordering::Relaxed);
            Ok(())
        });
        let subscription = hub.subscribe(channel);
        assert_eq!(hub.listener_count(), 1);
        hub.notify();
        assert_eq!(messages.load(Ordering::Relaxed), 1);
        hub.unsubscribe(&subscription.subscription_id);
        hub.unsubscribe(&subscription.subscription_id);
        assert_eq!(hub.listener_count(), 0);
        hub.notify();
        assert_eq!(messages.load(Ordering::Relaxed), 1);
    }

    #[test]
    fn live_subscription_double_dispose_leaves_no_listener() {
        let runtime = LiveRuntime::new();
        let channel = Channel::new(|_| Ok(()));
        let subscription = runtime.subscribe(channel);
        assert_eq!(runtime.listener_count(), 1);
        runtime.unsubscribe(&subscription.subscription_id);
        runtime.unsubscribe(&subscription.subscription_id);
        assert_eq!(runtime.listener_count(), 0);
    }

    #[test]
    fn invalid_subscription_ids_are_harmless() {
        assert_eq!(parse_subscription("live-7", "live-"), Some(7));
        assert_eq!(parse_subscription("devices-9", "devices-"), Some(9));
        assert_eq!(parse_subscription("/Users/private", "live-"), None);
    }
}
