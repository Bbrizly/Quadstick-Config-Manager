//! The live input port: reading the stick while somebody tunes it.
//!
//! The QuadStick is already a gamepad on the USB cable, sending its position
//! several hundred times a second because that is its job. Nothing on this side
//! of the port asks it for anything, turns its console on, or writes to it. The
//! adapter opens the same report stream a game reads and looks at it.
//!
//! Two traits rather than one. Enumeration answers "is there anything to read",
//! and a session is one open stream. Splitting them is what lets the manager
//! back off, restart and report a device it can see but cannot open, without
//! the port owning a thread or a timer.
//!
//! Blocking, not `async`, for the reason the storage port already records: an
//! async port would pull a runtime into a crate whose whole claim is that it has
//! no OS dependency. The adapter runs the pump on a worker thread.

use crate::error::DeviceError;
use std::fmt;

/// Opaque handle to one readable interface. The device path stays inside the
/// adapter, the same way a mount point does.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct LiveDeviceId(u64);

impl LiveDeviceId {
    #[must_use]
    pub const fn from_raw(raw: u64) -> Self {
        Self(raw)
    }

    #[must_use]
    pub const fn raw(self) -> u64 {
        self.0
    }
}

impl fmt::Display for LiveDeviceId {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "live-{}", self.0)
    }
}

/// Whether a QuadStick that is plugged in can be read at all.
///
/// Emulation mode 3, Xbox 360 native, publishes interface class 0xFF. That is
/// XInput and not HID, so no HID reader can open it however it is enumerated.
/// The app has to be able to say that out loud: a person who has that mode set
/// is otherwise looking at a live view that never moves and no reason why.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum CandidateKind {
    /// A HID interface whose descriptor says it is the stick.
    Readable,
    /// A QuadStick is here, in a mode nothing can read.
    XInputOnly,
}

/// One QuadStick interface the adapter found.
///
/// `product` is what the device announces itself as. It is display text and
/// never a device path, so a live status line pasted into a bug report cannot
/// spell out where the user's devices are.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LiveCandidate {
    pub id: LiveDeviceId,
    pub kind: CandidateKind,
    pub product: String,
}

impl LiveCandidate {
    #[must_use]
    pub const fn is_readable(&self) -> bool {
        matches!(self.kind, CandidateKind::Readable)
    }
}

/// One report, already turned into axes and buttons by the adapter.
///
/// Reading by descriptor usage rather than by byte offset is the adapter's job,
/// because the report shape is a different one in every emulation mode and the
/// descriptor the device publishes is the only thing that knows which.
///
/// Which button is a hard sip is a question nothing here can answer. That
/// mapping lives in the profile the device has loaded, and the report only
/// carries the button that came out the other end.
#[derive(Debug, Clone, PartialEq)]
pub struct Reading {
    /// Left to right, -1 to 1.
    pub x: f64,
    /// Up to down, -1 to 1. Down is positive, which is how the device sends it
    /// and how the screen draws it.
    pub y: f64,
    /// Which buttons are down, numbered from 1 the way the device's own report
    /// numbers them.
    pub buttons: Vec<u16>,
}

/// One open stream.
///
/// Dropping the session closes the device. There is no `close`, so a reader that
/// forgets to call it cannot exist.
pub trait LiveInputSession {
    /// What the device announces itself as.
    fn product(&self) -> &str;

    /// The next report.
    ///
    /// `Ok(None)` means nothing arrived inside the adapter's own wait, which is
    /// ordinary: the device sends when something moves, so a second of silence
    /// from a stick at rest is normal and is not staleness on its own.
    ///
    /// An `Err` ends the stream. The manager drops the session and starts
    /// looking again, so an adapter reports the one thing the user can act on
    /// rather than an errno: `DeviceError::NotFound` covers both a stick that
    /// was unplugged and one the operating system will not hand over. That
    /// flattening is deliberate and matches the shipped reader, where a
    /// permission the OS did not grant and a device pulled mid-read mean the
    /// same thing to the person tuning: no live reading, and every setting on
    /// the page still works.
    fn read(&mut self) -> Result<Option<Reading>, DeviceError>;
}

/// Where live input comes from.
pub trait LiveInputPort {
    type Session: LiveInputSession;

    /// Every QuadStick interface currently enumerable, readable or not.
    ///
    /// A QuadStick in most modes puts three HID interfaces behind one USB
    /// identity, because a profile can drive a gamepad, a mouse and a keyboard
    /// at once. They enumerate in no promised order, and the mouse reports X and
    /// Y as well, so an adapter that returned the first interface at a matching
    /// identity would some of the time hand back the pointer's motion as the
    /// stick. Only interfaces whose descriptor says they are the stick may be
    /// returned as [`CandidateKind::Readable`].
    fn candidates(&self) -> Result<Vec<LiveCandidate>, DeviceError>;

    /// Open one candidate for reading.
    fn open(&self, device: LiveDeviceId) -> Result<Self::Session, DeviceError>;
}

impl<P: LiveInputPort + ?Sized> LiveInputPort for &P {
    type Session = P::Session;

    fn candidates(&self) -> Result<Vec<LiveCandidate>, DeviceError> {
        (**self).candidates()
    }

    fn open(&self, device: LiveDeviceId) -> Result<Self::Session, DeviceError> {
        (**self).open(device)
    }
}
