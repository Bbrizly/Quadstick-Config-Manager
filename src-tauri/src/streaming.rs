//! Scoped IPC streams.
//!
//! These are deliberately not Tauri global events. A caller owns a subscription
//! id and a typed `Channel`; disposing the id removes that one listener. Live
//! frames pass through qcm-core's capacity-one latest-wins slot before they ever
//! reach IPC, so a slow window cannot build an input-frame queue.

use crate::adapters::hid::HidLiveInput;
use qcm_core::clock::SystemClock;
use qcm_core::live::{LiveInputManager, LiveSnapshot, LiveStatus};
use qcm_core::ports::live_input::LiveInputPort;
use serde::Serialize;
use std::collections::BTreeMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex, MutexGuard, PoisonError};
use std::thread;
use std::time::Duration;
use tauri::ipc::Channel;

/// The longest a worker without an open device sleeps in one go when the core
/// gives it no wait of its own. A game is usually running while this app sits
/// in the background, so the floor is about wakeups, not latency: a missing
/// stick is looked for again on the core's own 1.5 s rescan.
const IDLE_BACKOFF: Duration = Duration::from_millis(250);

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
    /// What the window was last told, so a window that subscribes to a running
    /// worker (a page opened, a window shown again) draws the current state at
    /// once instead of waiting for the stick to change.
    last: Option<LiveSnapshotDto>,
    /// Worker loop rounds, for the wakeup tests.
    #[cfg(test)]
    rounds: u64,
}

type Spawn = dyn Fn(Arc<LiveShared>) + Send + Sync;

struct LiveShared {
    control: Mutex<LiveControl>,
    /// Woken when the last listener leaves, so a worker sleeping through a
    /// backoff stops at once instead of at the end of it.
    wake: Condvar,
    spawn: Box<Spawn>,
}

impl LiveShared {
    fn control(&self) -> MutexGuard<'_, LiveControl> {
        self.control.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

/// Process-wide live-input owner. It starts the HID worker when the first window
/// subscribes and the worker exits after the last listener is removed. Nothing
/// enumerates HID while nobody is subscribed.
pub struct LiveRuntime {
    shared: Arc<LiveShared>,
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
        Self::with_spawn(|shared| spawn_live_worker(shared, HidLiveInput::new))
    }

    fn with_spawn(spawn: impl Fn(Arc<LiveShared>) + Send + Sync + 'static) -> Self {
        Self {
            shared: Arc::new(LiveShared {
                control: Mutex::new(LiveControl::default()),
                wake: Condvar::new(),
                spawn: Box::new(spawn),
            }),
            next_id: AtomicU64::new(0),
        }
    }

    pub fn subscribe(&self, channel: Channel<LiveSnapshotDto>) -> SubscriptionDto {
        let id = self
            .next_id
            .fetch_add(1, Ordering::Relaxed)
            .saturating_add(1);
        let (should_start, last) = {
            let mut control = self.shared.control();
            control.listeners.insert(id, channel.clone());
            let start = !control.worker_running;
            control.worker_running = true;
            (start, control.last.clone())
        };
        if should_start {
            (self.shared.spawn)(Arc::clone(&self.shared));
        } else if let Some(last) = last {
            let _ = channel.send(last);
        }
        SubscriptionDto {
            subscription_id: format!("live-{id}"),
        }
    }

    pub fn unsubscribe(&self, raw: &str) {
        if let Some(id) = parse_subscription(raw, "live-") {
            let mut control = self.shared.control();
            control.listeners.remove(&id);
            if control.listeners.is_empty() {
                self.shared.wake.notify_all();
            }
        }
    }

    #[cfg(test)]
    #[must_use]
    pub fn listener_count(&self) -> usize {
        self.shared.control().listeners.len()
    }
}

/// How long the worker may sleep after a poll, or `None` to poll again at once.
///
/// An open device's read already blocks for up to its own timeout, so sleeping
/// on top of it only adds latency. Without a device the core says when the
/// next look is due; it is never shorter than that, and a missing answer gets
/// the idle floor rather than a spin.
fn idle_pause(next_attempt: Option<Duration>, has_session: bool) -> Option<Duration> {
    match (next_attempt, has_session) {
        (Some(wait), _) => Some(wait),
        (None, true) => None,
        (None, false) => Some(IDLE_BACKOFF),
    }
}

fn spawn_live_worker<P, F>(shared: Arc<LiveShared>, make_port: F)
where
    P: LiveInputPort,
    F: FnOnce() -> P + Send + 'static,
{
    let fallback = Arc::clone(&shared);
    let started = thread::Builder::new()
        .name("qcm-live-input".to_owned())
        .spawn(move || run_live_worker(&shared, make_port()));
    // A thread the OS would not start means no live view, which the page
    // already copes with. Clear the flag so the next subscribe tries again.
    if started.is_err() {
        fallback.control().worker_running = false;
    }
}

fn run_live_worker<P: LiveInputPort>(shared: &Arc<LiveShared>, port: P) {
    let mut manager = LiveInputManager::new(port, SystemClock::new());
    let stream = manager.stream();
    manager.start();

    loop {
        {
            #[cfg_attr(not(test), allow(unused_mut))]
            let mut state = shared.control();
            #[cfg(test)]
            {
                state.rounds += 1;
            }
            if state.listeners.is_empty() {
                drop(state);
                // Close the device before saying the worker is gone, so a
                // replacement never finds the stick still held open by this one.
                manager.stop();
                let _ = stream.take();
                let mut state = shared.control();
                if state.listeners.is_empty() {
                    state.worker_running = false;
                    state.last = None;
                    break;
                }
                // A window subscribed while the device was closing: it saw a
                // running worker, so this one carries on for it.
                drop(state);
                manager.start();
                continue;
            }
        }

        manager.poll();
        if let Some(snapshot) = stream.take() {
            send_live(shared, LiveSnapshotDto::from(&snapshot));
        }

        if let Some(pause) = idle_pause(manager.next_attempt_in(), manager.has_session()) {
            let state = shared.control();
            // Checked under the same lock `unsubscribe` notifies under, so the
            // last listener leaving cannot slip in between and be slept through.
            if !state.listeners.is_empty() {
                drop(
                    shared
                        .wake
                        .wait_timeout(state, pause)
                        .unwrap_or_else(PoisonError::into_inner),
                );
            }
        }
    }
}

fn listener_snapshot(shared: &LiveShared) -> Vec<(u64, Channel<LiveSnapshotDto>)> {
    shared
        .control()
        .listeners
        .iter()
        .map(|(&id, channel)| (id, channel.clone()))
        .collect()
}

fn send_live(shared: &LiveShared, snapshot: LiveSnapshotDto) {
    let failed: Vec<u64> = listener_snapshot(shared)
        .into_iter()
        .filter_map(|(id, channel)| channel.send(snapshot.clone()).err().map(|_| id))
        .collect();
    let mut state = shared.control();
    state.last = Some(snapshot);
    for id in failed {
        state.listeners.remove(&id);
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
    use qcm_testkit::FakeLiveInput;
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

    fn fake_runtime(port: &FakeLiveInput) -> LiveRuntime {
        let port = port.clone();
        LiveRuntime::with_spawn(move |shared| {
            let port = port.clone();
            spawn_live_worker(shared, move || port);
        })
    }

    fn counting_channel() -> (Channel<LiveSnapshotDto>, Arc<AtomicUsize>) {
        let frames = Arc::new(AtomicUsize::new(0));
        let seen = Arc::clone(&frames);
        let channel = Channel::new(move |_| {
            seen.fetch_add(1, Ordering::Relaxed);
            Ok(())
        });
        (channel, frames)
    }

    fn worker_running(runtime: &LiveRuntime) -> bool {
        runtime.shared.control().worker_running
    }

    fn wait_for(mut done: impl FnMut() -> bool, within: Duration) -> bool {
        let deadline = std::time::Instant::now() + within;
        while std::time::Instant::now() < deadline {
            if done() {
                return true;
            }
            thread::sleep(Duration::from_millis(5));
        }
        done()
    }

    #[test]
    fn live_subscription_double_dispose_leaves_no_listener() {
        let port = FakeLiveInput::new();
        port.nothing_plugged_in();
        let runtime = fake_runtime(&port);
        let (channel, _) = counting_channel();
        let subscription = runtime.subscribe(channel);
        assert_eq!(runtime.listener_count(), 1);
        runtime.unsubscribe(&subscription.subscription_id);
        runtime.unsubscribe(&subscription.subscription_id);
        assert_eq!(runtime.listener_count(), 0);
    }

    #[test]
    fn an_open_device_is_not_slept_on_and_a_missing_one_backs_off() {
        assert_eq!(idle_pause(None, true), None);
        assert_eq!(idle_pause(None, false), Some(IDLE_BACKOFF));
        assert_eq!(
            idle_pause(Some(Duration::from_millis(1500)), false),
            Some(Duration::from_millis(1500))
        );
        assert!(IDLE_BACKOFF >= Duration::from_millis(250));
    }

    #[test]
    fn no_device_means_a_few_wakeups_not_a_poll_loop() {
        let port = FakeLiveInput::new();
        port.nothing_plugged_in();
        let runtime = fake_runtime(&port);
        let (channel, frames) = counting_channel();
        let subscription = runtime.subscribe(channel);
        thread::sleep(Duration::from_millis(600));
        let rounds = runtime.shared.control().rounds;
        // The old worker woke every 50 ms (12 rounds here) and at 4 ms between
        // polls. One look, then a sleep until the 1.5 s rescan.
        assert!(rounds <= 3, "worker woke {rounds} times in 600 ms");
        assert_eq!(port.enumerations(), 1);
        assert!(
            frames.load(Ordering::Relaxed) >= 1,
            "the first state was sent"
        );
        runtime.unsubscribe(&subscription.subscription_id);
    }

    #[test]
    fn the_last_unsubscribe_stops_the_worker_mid_backoff() {
        let port = FakeLiveInput::new();
        port.nothing_plugged_in();
        let runtime = fake_runtime(&port);
        let (channel, _) = counting_channel();
        let subscription = runtime.subscribe(channel);
        assert!(wait_for(
            || port.enumerations() == 1,
            Duration::from_secs(1)
        ));
        runtime.unsubscribe(&subscription.subscription_id);
        assert!(
            wait_for(|| !worker_running(&runtime), Duration::from_millis(200)),
            "the worker slept through its own stop"
        );
        thread::sleep(Duration::from_millis(1700));
        assert_eq!(
            port.enumerations(),
            1,
            "nothing rescans without a subscriber"
        );
    }

    #[test]
    fn a_second_window_gets_the_current_state_at_once() {
        let port = FakeLiveInput::new();
        port.nothing_plugged_in();
        let runtime = fake_runtime(&port);
        let (first, first_frames) = counting_channel();
        let one = runtime.subscribe(first);
        assert!(wait_for(
            || first_frames.load(Ordering::Relaxed) >= 1,
            Duration::from_secs(1)
        ));
        let (second, second_frames) = counting_channel();
        let two = runtime.subscribe(second);
        assert_eq!(second_frames.load(Ordering::Relaxed), 1);
        runtime.unsubscribe(&one.subscription_id);
        runtime.unsubscribe(&two.subscription_id);
    }

    #[test]
    fn invalid_subscription_ids_are_harmless() {
        assert_eq!(parse_subscription("live-7", "live-"), Some(7));
        assert_eq!(parse_subscription("devices-9", "devices-"), Some(9));
        assert_eq!(parse_subscription("/Users/private", "live-"), None);
    }
}
