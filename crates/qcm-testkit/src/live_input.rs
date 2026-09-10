//! A QuadStick that reports whatever a test tells it to.
//!
//! The moments that matter in live input cannot be staged on real hardware:
//! nobody can unplug a stick at exactly the microsecond a button is held, or
//! hold a device in Xbox 360 native mode and a readable mode at once. Each of
//! those is one line here.
//!
//! Cheap to clone, because the manager takes the port by value and the test has
//! to keep hold of the controls. Every clone shares one state.

use qcm_core::error::DeviceError;
use qcm_core::ports::live_input::{
    CandidateKind, LiveCandidate, LiveDeviceId, LiveInputPort, LiveInputSession, Reading,
};
use std::collections::VecDeque;
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};

/// The id the fake hands out for its one readable interface.
pub const FAKE_DEVICE: LiveDeviceId = LiveDeviceId::from_raw(1);

/// What the fake will say when asked to enumerate.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Plugged {
    Nothing,
    /// A stick whose descriptor says it is the stick.
    Readable,
    /// Emulation mode 3: present, and nothing can open it.
    XInputOnly,
}

#[derive(Debug)]
struct State {
    plugged: Plugged,
    product: String,
    candidates_error: Option<DeviceError>,
    open_error: Option<DeviceError>,
    queued: VecDeque<Reading>,
    /// Set once the queue is meant to end in a disconnect rather than silence.
    unplug_when_drained: Option<DeviceError>,
    enumerations: usize,
    open_attempts: usize,
    opens: usize,
    reads: usize,
}

impl Default for State {
    fn default() -> Self {
        Self {
            plugged: Plugged::Nothing,
            product: "QuadStick".to_owned(),
            candidates_error: None,
            open_error: None,
            queued: VecDeque::new(),
            unplug_when_drained: None,
            enumerations: 0,
            open_attempts: 0,
            opens: 0,
            reads: 0,
        }
    }
}

/// A live input port under a test's control.
#[derive(Debug, Clone, Default)]
pub struct FakeLiveInput {
    shared: Arc<Mutex<State>>,
}

impl FakeLiveInput {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// A readable QuadStick announcing itself under `product`.
    pub fn plug_in(&self, product: &str) {
        let mut state = self.lock();
        state.plugged = Plugged::Readable;
        state.product = product.to_owned();
        state.unplug_when_drained = None;
        state.open_error = None;
    }

    /// A QuadStick in Xbox 360 native mode: present, and no HID reader can open
    /// it, because that mode publishes interface class 0xFF.
    pub fn plug_in_xinput_only(&self) {
        let mut state = self.lock();
        state.plugged = Plugged::XInputOnly;
    }

    /// Nothing plugged in at all.
    pub fn nothing_plugged_in(&self) {
        let mut state = self.lock();
        state.plugged = Plugged::Nothing;
    }

    /// Queue one report for the open session to hand back.
    pub fn report(&self, x: f64, y: f64, buttons: impl IntoIterator<Item = u16>) {
        let mut state = self.lock();
        state.queued.push_back(Reading {
            x,
            y,
            buttons: buttons.into_iter().collect(),
        });
    }

    /// The stream ends once everything queued has been read.
    ///
    /// Queued first, disconnect after, so a test can prove that reports already
    /// handed over do not survive the disconnect that follows them.
    pub fn unplug(&self) {
        let mut state = self.lock();
        state.plugged = Plugged::Nothing;
        state.unplug_when_drained = Some(DeviceError::NotFound);
    }

    /// Enumeration itself fails.
    pub fn fail_to_enumerate(&self, error: DeviceError) {
        self.lock().candidates_error = Some(error);
    }

    /// The device is there and will not open.
    pub fn fail_to_open(&self, error: DeviceError) {
        let mut state = self.lock();
        state.plugged = Plugged::Readable;
        state.open_error = Some(error);
    }

    /// How many sessions have been opened. A reader that reopens the device on
    /// every poll would still pass a state test and fail this one.
    #[must_use]
    pub fn opens(&self) -> usize {
        self.lock().opens
    }

    /// How many times enumeration was asked for, successful or not. The measure
    /// of whether a reader is backing off or spinning.
    #[must_use]
    pub fn enumerations(&self) -> usize {
        self.lock().enumerations
    }

    /// How many times a device was asked to open, including the times it
    /// refused. Counting only successes would hide a reader hammering a device
    /// that will never open.
    #[must_use]
    pub fn open_attempts(&self) -> usize {
        self.lock().open_attempts
    }

    /// How many reads the sessions have served.
    #[must_use]
    pub fn reads(&self) -> usize {
        self.lock().reads
    }

    /// How many reports are still waiting to be read.
    #[must_use]
    pub fn queued(&self) -> usize {
        self.lock().queued.len()
    }

    fn lock(&self) -> MutexGuard<'_, State> {
        self.shared.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

impl LiveInputPort for FakeLiveInput {
    type Session = FakeSession;

    fn candidates(&self) -> Result<Vec<LiveCandidate>, DeviceError> {
        let mut state = self.lock();
        state.enumerations += 1;
        if let Some(error) = state.candidates_error.clone() {
            return Err(error);
        }
        Ok(match state.plugged {
            Plugged::Nothing => Vec::new(),
            Plugged::Readable => vec![LiveCandidate {
                id: FAKE_DEVICE,
                kind: CandidateKind::Readable,
                product: state.product.clone(),
            }],
            Plugged::XInputOnly => vec![LiveCandidate {
                id: FAKE_DEVICE,
                kind: CandidateKind::XInputOnly,
                product: state.product.clone(),
            }],
        })
    }

    fn open(&self, device: LiveDeviceId) -> Result<Self::Session, DeviceError> {
        let mut state = self.lock();
        state.open_attempts += 1;
        if let Some(error) = state.open_error.clone() {
            return Err(error);
        }
        if device != FAKE_DEVICE || state.plugged != Plugged::Readable {
            return Err(DeviceError::NotFound);
        }
        state.opens += 1;
        let product = state.product.clone();
        drop(state);
        Ok(FakeSession {
            shared: Arc::clone(&self.shared),
            product,
        })
    }
}

/// One open stream on the fake.
#[derive(Debug)]
pub struct FakeSession {
    shared: Arc<Mutex<State>>,
    product: String,
}

impl LiveInputSession for FakeSession {
    fn product(&self) -> &str {
        &self.product
    }

    fn read(&mut self) -> Result<Option<Reading>, DeviceError> {
        let mut state = self.shared.lock().unwrap_or_else(PoisonError::into_inner);
        state.reads += 1;
        if let Some(reading) = state.queued.pop_front() {
            return Ok(Some(reading));
        }
        match state.unplug_when_drained.clone() {
            Some(error) => Err(error),
            // Nothing queued and still plugged in: the device sends when
            // something moves, so silence is the ordinary answer.
            None => Ok(None),
        }
    }
}
