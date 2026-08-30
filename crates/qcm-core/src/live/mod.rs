//! Live input: one stream, owned away from the window.
//!
//! The shipped reader put the search, the retry timers and the callback into the
//! same object as the device handle, so its lifetime was the window's. This
//! splits them. [`LiveInputManager`] owns the stream and the state machine and
//! is pumped by whoever has a thread to spare; [`LiveStream`] is the only thing
//! the window touches. Closing the window drops the stream, not the reader, and
//! the reader can be stopped and started again without the window knowing.
//!
//! Nothing here fails. Every way live input can go wrong, from a permission the
//! operating system did not grant to a stick unplugged mid-read, means the same
//! thing to the person tuning: no live reading. It is never an error the app
//! stops for, because every setting on the page still works without it. So
//! [`LiveInputManager::poll`] returns nothing and folds each fault into a state
//! the window can render.

pub mod stream;

use crate::clock::{Clock, Moment};
use crate::error::{DeviceError, QcmError};
use crate::ports::live_input::{CandidateKind, LiveDeviceId, LiveInputPort, LiveInputSession};
use std::sync::Arc;
use std::time::Duration;

pub use stream::{JITTER, LiveSnapshot, LiveStatus, LiveStream, LiveStreamStats, Motion};

/// How long a stream may say nothing before the window stops claiming to know
/// where the stick is.
///
/// Longer than the shipped reader's one second wait on purpose. The device sends
/// when something moves, so a second of silence from a stick at rest is ordinary
/// and must not blank the page.
pub const DEFAULT_STALE_AFTER: Duration = Duration::from_secs(2);

/// How long to wait before looking again when nothing was found. Ported from the
/// shipped reader's 1500 ms rescan.
pub const DEFAULT_RESCAN_AFTER: Duration = Duration::from_millis(1500);

/// How long to wait after a stream ended, which is usually somebody unplugging.
/// Ported from the shipped reader's 1000 ms. Without it the retry is a spin on a
/// device that will not open.
pub const DEFAULT_RECONNECT_AFTER: Duration = Duration::from_millis(1000);

/// How long to wait after the port itself failed. Ported from the shipped
/// reader's 2000 ms.
pub const DEFAULT_ERROR_AFTER: Duration = Duration::from_millis(2000);

/// The four timers, in one value so a test can shrink them all.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LiveInputSettings {
    pub stale_after: Duration,
    pub rescan_after: Duration,
    pub reconnect_after: Duration,
    pub error_after: Duration,
}

impl Default for LiveInputSettings {
    fn default() -> Self {
        Self {
            stale_after: DEFAULT_STALE_AFTER,
            rescan_after: DEFAULT_RESCAN_AFTER,
            reconnect_after: DEFAULT_RECONNECT_AFTER,
            error_after: DEFAULT_ERROR_AFTER,
        }
    }
}

/// Reading the stick, separately from anything that draws it.
pub struct LiveInputManager<P: LiveInputPort, C: Clock> {
    port: P,
    clock: C,
    settings: LiveInputSettings,
    stream: Arc<LiveStream>,
    session: Option<P::Session>,
    /// Cached at open, so publishing a report at a few hundred hertz does not
    /// allocate the device name again every time.
    product: Option<Arc<str>>,
    running: bool,
    seq: u64,
    /// When the next attempt is allowed. `None` means now.
    wait_until: Option<Moment>,
    /// The last report actually published, for the jitter filter. Cleared
    /// whenever the state stops being `Reading`, so the first report after a
    /// reconnect always draws.
    last_motion: Option<Motion>,
    /// When the last report arrived, for the stale window.
    last_report_at: Moment,
    /// What the window was last told. Publishing only on change is what keeps a
    /// device that is simply not plugged in from filling the slot forever.
    published: Option<LiveStatus>,
}

impl<P: LiveInputPort, C: Clock> LiveInputManager<P, C> {
    #[must_use]
    pub fn new(port: P, clock: C) -> Self {
        Self::with_settings(port, clock, LiveInputSettings::default())
    }

    #[must_use]
    pub fn with_settings(port: P, clock: C, settings: LiveInputSettings) -> Self {
        let at = clock.now();
        Self {
            port,
            clock,
            settings,
            stream: Arc::new(LiveStream::new()),
            session: None,
            product: None,
            running: false,
            seq: 0,
            wait_until: None,
            last_motion: None,
            last_report_at: at,
            published: None,
        }
    }

    /// The read side. Hand this to the window; it is the only part of live input
    /// the window may hold, and dropping it does not stop the reader.
    #[must_use]
    pub fn stream(&self) -> Arc<LiveStream> {
        Arc::clone(&self.stream)
    }

    #[must_use]
    pub const fn is_running(&self) -> bool {
        self.running
    }

    /// What the window was last told, without draining the stream.
    #[must_use]
    pub fn status(&self) -> LiveStatus {
        self.published.clone().unwrap_or(LiveStatus::Stopped)
    }

    /// Begin looking. Idempotent: starting an already running reader does not
    /// close the stream it has open.
    pub fn start(&mut self) {
        if self.running {
            return;
        }
        self.running = true;
        self.wait_until = None;
        self.publish(LiveStatus::Searching);
    }

    /// Stop reading and close whatever is open.
    ///
    /// The published state clears the visualizer. A stop that left the last
    /// report on screen would show a button held down by a reader that is no
    /// longer running, and nothing would ever come along to release it.
    pub fn stop(&mut self) {
        let idle = !self.running && matches!(self.published, None | Some(LiveStatus::Stopped));
        self.session = None;
        self.product = None;
        self.last_motion = None;
        self.wait_until = None;
        self.running = false;
        if !idle {
            self.publish(LiveStatus::Stopped);
        }
    }

    /// How long the worker should sleep before calling [`Self::poll`] again, or
    /// `None` when there is nothing to wait for.
    ///
    /// Without this a worker looping on `poll` would spin a core through every
    /// backoff, which is the state a machine with no QuadStick plugged in sits
    /// in for as long as the app is open. Nothing here sleeps: the core has no
    /// thread, so the answer is a number and the worker does the waiting.
    #[must_use]
    pub fn next_attempt_in(&self) -> Option<Duration> {
        let until = self.wait_until?;
        let now = self.clock.now();
        let remaining = until.since_start().saturating_sub(now.since_start());
        (!remaining.is_zero()).then_some(remaining)
    }

    /// Do one round of work: find a device, or read one report from the one
    /// already open. Called in a loop by a worker thread; it never blocks longer
    /// than the port's own read does.
    pub fn poll(&mut self) {
        if !self.running {
            return;
        }
        if self.session.is_some() {
            self.pump();
        } else {
            self.search();
        }
    }

    fn search(&mut self) {
        let now = self.clock.now();
        if self.wait_until.is_some_and(|until| now < until) {
            return;
        }

        let candidates = match self.port.candidates() {
            Ok(candidates) => candidates,
            Err(error) => return self.give_up_for_now(error, self.settings.error_after),
        };

        let readable = candidates.iter().find(|candidate| candidate.is_readable());
        let Some(candidate) = readable else {
            // Nothing to open. Saying which of the two reasons it is matters:
            // an Xbox 360 native QuadStick is plugged in and working, and the
            // person looking at a live view that never moves deserves better
            // than a page that says nothing is there.
            let xinput = candidates
                .iter()
                .any(|candidate| matches!(candidate.kind, CandidateKind::XInputOnly));
            self.publish_changed(if xinput {
                LiveStatus::XInputOnly
            } else {
                LiveStatus::Searching
            });
            self.wait_until = Some(now.plus(self.settings.rescan_after));
            return;
        };

        let id: LiveDeviceId = candidate.id;
        match self.port.open(id) {
            Ok(session) => {
                let product: Arc<str> = Arc::from(session.product());
                self.session = Some(session);
                self.product = Some(Arc::clone(&product));
                self.wait_until = None;
                self.last_motion = None;
                self.last_report_at = now;
                // Open, with nothing said yet. Inventing a centred reading here
                // would be claiming to know where the stick is.
                self.publish_changed(LiveStatus::Stale { product });
            }
            Err(error) => self.give_up_for_now(error, self.settings.error_after),
        }
    }

    fn pump(&mut self) {
        let now = self.clock.now();
        let (Some(session), Some(product)) = (self.session.as_mut(), self.product.as_ref()) else {
            return;
        };
        let product = Arc::clone(product);

        match session.read() {
            Ok(Some(reading)) => {
                let motion = Motion::new(reading.x, reading.y, reading.buttons);
                self.last_report_at = now;
                if self
                    .last_motion
                    .as_ref()
                    .is_some_and(|last| motion.is_same_as(last))
                {
                    return;
                }
                self.last_motion = Some(motion.clone());
                self.publish(LiveStatus::Reading { product, motion });
            }
            Ok(None) => {
                let quiet = now
                    .since_start()
                    .saturating_sub(self.last_report_at.since_start());
                if quiet >= self.settings.stale_after {
                    // The stream is open and saying nothing. Whatever was held
                    // when it went quiet is not held now, and the window must
                    // stop drawing it.
                    self.last_motion = None;
                    self.publish_changed(LiveStatus::Stale { product });
                }
            }
            // The stream ended, which is usually somebody unplugging. The
            // publish inside this call is the one that releases a held button,
            // so it is unconditional: no change test stands between a
            // disconnect and the window that is still drawing a pressed button.
            Err(error) => self.give_up_for_now(error, self.settings.reconnect_after),
        }
    }

    fn give_up_for_now(&mut self, error: DeviceError, wait: Duration) {
        let code = QcmError::Device(error).code();
        self.session = None;
        self.product = None;
        self.last_motion = None;
        self.wait_until = Some(self.clock.now().plus(wait));
        self.publish(LiveStatus::Unavailable { code });
    }

    /// Publish only if the window would see something different. Used for the
    /// states that repeat forever while nothing is plugged in.
    fn publish_changed(&mut self, status: LiveStatus) {
        if self.published.as_ref() == Some(&status) {
            return;
        }
        self.publish(status);
    }

    fn publish(&mut self, status: LiveStatus) {
        self.seq += 1;
        self.published = Some(status.clone());
        self.stream.publish(LiveSnapshot {
            seq: self.seq,
            at: self.clock.now(),
            status,
        });
    }
}
