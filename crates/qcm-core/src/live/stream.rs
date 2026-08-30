//! What the window is shown, and the one-slot buffer it is handed through.
//!
//! Live input is state, not an audit log. The reader runs on the device's clock
//! and the window paints on the screen's, so the two never agree for long, and a
//! queue between them is a queue that grows. This holds exactly one snapshot:
//! publishing over an undrained one replaces it, because the newer snapshot is
//! the truer answer to the only question the window asks, which is where the
//! stick is now.
//!
//! That choice is what makes the dangerous case safe rather than lucky. A
//! disconnect publishes a snapshot with no motion in it, and because the slot is
//! latest-wins it cannot be overtaken by a pressed frame that was queued behind
//! it. A stuck button on a sip-and-puff controller is somebody's input held down
//! with no way to let go.

use crate::clock::Moment;
use crate::error::ErrorCode;
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};

/// Where the stick is and what is held.
///
/// Only ever reachable through [`LiveStatus::Reading`]. That is the point: there
/// is no way to spell a held button in a state where nothing is being read.
#[derive(Debug, Clone, PartialEq)]
pub struct Motion {
    x: f64,
    y: f64,
    buttons: Vec<u16>,
}

impl Motion {
    /// Axes are clamped to the range the visualizer draws, and a non-finite axis
    /// reads as centred. A NaN would otherwise defeat the jitter filter, which
    /// compares against the last reading, and paint a stick that never settles.
    #[must_use]
    pub fn new(x: f64, y: f64, buttons: impl IntoIterator<Item = u16>) -> Self {
        let mut buttons: Vec<u16> = buttons.into_iter().collect();
        // Sorted and deduplicated so two readings of the same held set compare
        // equal whatever order the descriptor walked. The shipped reader
        // compared as a set too, but through a count-plus-difference test that
        // called {1,1} and {1,2} the same set. This is the stricter answer, and
        // strictness here can only mean one more frame drawn, never a wrong one.
        buttons.sort_unstable();
        buttons.dedup();
        Self {
            x: finite(x),
            y: finite(y),
            buttons,
        }
    }

    #[must_use]
    pub const fn x(&self) -> f64 {
        self.x
    }

    #[must_use]
    pub const fn y(&self) -> f64 {
        self.y
    }

    #[must_use]
    pub fn buttons(&self) -> &[u16] {
        &self.buttons
    }

    /// Whether redrawing for this would show the user anything.
    ///
    /// A stick at rest still jitters a count or two, and repainting on every one
    /// of those is a page that never stops moving. One percent of travel is
    /// under a pixel on the pad. Ported from the shipped reader, including that
    /// the comparison is against the last reading published rather than against
    /// an origin, so a slow drift does accumulate into a redraw.
    #[must_use]
    pub fn is_same_as(&self, other: &Self) -> bool {
        (self.x - other.x).abs() < JITTER
            && (self.y - other.y).abs() < JITTER
            && self.buttons == other.buttons
    }
}

/// How far an axis has to move before it is worth redrawing.
pub const JITTER: f64 = 0.01;

fn finite(value: f64) -> f64 {
    if value.is_finite() {
        value.clamp(-1.0, 1.0)
    } else {
        0.0
    }
}

/// What the app can currently say about live input.
///
/// Motion lives inside `Reading` and nowhere else, so every other state clears
/// the visualizer by construction rather than by remembering to.
#[derive(Debug, Clone, PartialEq)]
pub enum LiveStatus {
    /// Live input is switched off. Nothing is open and nothing is being looked
    /// for.
    Stopped,
    /// Looking. No QuadStick is plugged in, or none that can be read.
    Searching,
    /// A QuadStick is plugged in and cannot be read, because it is in Xbox 360
    /// native mode. That mode publishes interface class 0xFF, which is XInput
    /// and not HID at all. Nothing the app does can open it, so this is a state
    /// to explain rather than an error to retry.
    XInputOnly,
    /// The stream is open and nothing is arriving.
    ///
    /// Also where a stream starts: a device that has been found and opened but
    /// has said nothing yet is in exactly this condition, and inventing a
    /// centred reading for it would be claiming to know where the stick is.
    ///
    /// The name is shared rather than copied because it is the one field that
    /// would otherwise allocate on every report of a stream running at a few
    /// hundred hertz for hours.
    Stale {
        product: Arc<str>,
    },
    Reading {
        product: Arc<str>,
        motion: Motion,
    },
    /// Looking again after a failure. The code is carried for the log; the
    /// window shows the same "no live reading" it shows for every other reason,
    /// because every setting on the page still works either way.
    Unavailable {
        code: ErrorCode,
    },
}

impl LiveStatus {
    /// What is held right now, which is nothing unless a stream is delivering.
    #[must_use]
    pub fn motion(&self) -> Option<&Motion> {
        match self {
            Self::Reading { motion, .. } => Some(motion),
            _ => None,
        }
    }

    /// The device this is about, when there is one.
    #[must_use]
    pub fn product(&self) -> Option<&str> {
        match self {
            Self::Stale { product } | Self::Reading { product, .. } => Some(product),
            _ => None,
        }
    }

    /// Stable label for a log line or a test name. Not user text.
    #[must_use]
    pub const fn as_str(&self) -> &'static str {
        match self {
            Self::Stopped => "stopped",
            Self::Searching => "searching",
            Self::XInputOnly => "xinput_only",
            Self::Stale { .. } => "stale",
            Self::Reading { .. } => "reading",
            Self::Unavailable { .. } => "unavailable",
        }
    }
}

/// One published state of live input.
///
/// `seq` counts published snapshots, not reports read. A window that sees it
/// jump by more than one knows intermediate states were coalesced away, which is
/// allowed: the snapshot it holds is still current.
#[derive(Debug, Clone, PartialEq)]
pub struct LiveSnapshot {
    pub seq: u64,
    pub at: Moment,
    pub status: LiveStatus,
}

impl LiveSnapshot {
    #[must_use]
    pub fn motion(&self) -> Option<&Motion> {
        self.status.motion()
    }
}

/// How the slot has been used since it was made.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct LiveStreamStats {
    /// Snapshots handed in by the reader.
    pub published: u64,
    /// Snapshots taken out by the window.
    pub delivered: u64,
    /// Snapshots replaced before anyone took them. The measure of back pressure.
    pub coalesced: u64,
}

#[derive(Debug, Default)]
struct Slot {
    pending: Option<LiveSnapshot>,
    stats: LiveStreamStats,
}

/// The bounded hand-off between the reader and the window.
///
/// Capacity one, latest wins. Shared by reference, so the reader can hold it on
/// its worker thread while the window reads it on its own.
#[derive(Debug, Default)]
pub struct LiveStream {
    slot: Mutex<Slot>,
}

impl LiveStream {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Hand in the current state, replacing anything not yet taken.
    pub fn publish(&self, snapshot: LiveSnapshot) {
        let mut slot = self.lock();
        slot.stats.published += 1;
        if slot.pending.replace(snapshot).is_some() {
            slot.stats.coalesced += 1;
        }
    }

    /// Take the current state, leaving the slot empty.
    pub fn take(&self) -> Option<LiveSnapshot> {
        let mut slot = self.lock();
        let taken = slot.pending.take();
        if taken.is_some() {
            slot.stats.delivered += 1;
        }
        taken
    }

    /// Look without taking. For a window that repaints on its own timer rather
    /// than draining.
    #[must_use]
    pub fn peek(&self) -> Option<LiveSnapshot> {
        self.lock().pending.clone()
    }

    /// How many snapshots are waiting. Never more than one, which is the whole
    /// promise this type makes.
    #[must_use]
    pub fn depth(&self) -> usize {
        usize::from(self.lock().pending.is_some())
    }

    #[must_use]
    pub fn stats(&self) -> LiveStreamStats {
        self.lock().stats
    }

    /// A poisoned lock here means a reader thread panicked mid-publish. The slot
    /// is one `Option`, so the worst state it can be left in is a snapshot that
    /// is either fully there or not, and refusing to show live input because of
    /// it would take the page down over the one thing on it that is optional.
    fn lock(&self) -> MutexGuard<'_, Slot> {
        self.slot.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

#[cfg(test)]
mod tests {
    use super::{LiveSnapshot, LiveStatus, LiveStream, Motion};
    use crate::clock::Moment;
    use std::sync::Arc;

    fn snapshot(seq: u64) -> LiveSnapshot {
        LiveSnapshot {
            seq,
            at: Moment::ZERO,
            status: LiveStatus::Reading {
                product: Arc::from("QuadStick"),
                motion: Motion::new(0.0, 0.0, [1u16]),
            },
        }
    }

    #[test]
    fn a_slow_consumer_never_makes_the_slot_grow() {
        let stream = LiveStream::new();
        for seq in 0..10_000 {
            stream.publish(snapshot(seq));
            assert!(stream.depth() <= 1, "the slot held more than one snapshot");
        }

        let stats = stream.stats();
        assert_eq!(stats.published, 10_000);
        assert_eq!(stats.coalesced, 9_999);
        assert_eq!(stats.delivered, 0);
    }

    #[test]
    fn the_newest_snapshot_is_the_one_that_survives() {
        let stream = LiveStream::new();
        stream.publish(snapshot(1));
        stream.publish(snapshot(2));
        stream.publish(snapshot(3));

        assert_eq!(stream.take().map(|s| s.seq), Some(3));
        assert_eq!(stream.take(), None);
    }

    #[test]
    fn every_published_snapshot_is_delivered_coalesced_or_pending() {
        let stream = LiveStream::new();
        for seq in 0..500 {
            stream.publish(snapshot(seq));
            if seq % 7 == 0 {
                stream.take();
            }
        }

        let stats = stream.stats();
        let pending = u64::try_from(stream.depth()).expect("depth is 0 or 1");
        assert_eq!(stats.published, stats.delivered + stats.coalesced + pending);
    }

    #[test]
    fn a_non_finite_axis_reads_as_centred() {
        let motion = Motion::new(f64::NAN, f64::INFINITY, []);
        assert_eq!(motion.x(), 0.0);
        assert_eq!(motion.y(), 0.0);
    }

    #[test]
    fn an_axis_past_full_travel_is_held_at_full_travel() {
        let motion = Motion::new(-4.0, 9.0, []);
        assert_eq!(motion.x(), -1.0);
        assert_eq!(motion.y(), 1.0);
    }

    #[test]
    fn buttons_come_back_sorted_and_without_repeats() {
        let motion = Motion::new(0.0, 0.0, [4u16, 1, 4]);
        assert_eq!(motion.buttons(), &[1, 4]);
    }

    #[test]
    fn a_jitter_of_less_than_one_percent_is_not_worth_redrawing() {
        let resting = Motion::new(0.0, 0.0, []);
        assert!(resting.is_same_as(&Motion::new(0.005, -0.005, [])));
        assert!(!resting.is_same_as(&Motion::new(0.02, 0.0, [])));
        assert!(!resting.is_same_as(&Motion::new(0.0, 0.0, [1u16])));
    }

    #[test]
    fn nothing_but_reading_can_carry_a_held_button() {
        for status in [
            LiveStatus::Stopped,
            LiveStatus::Searching,
            LiveStatus::XInputOnly,
            LiveStatus::Stale {
                product: Arc::from("QuadStick"),
            },
        ] {
            assert!(
                status.motion().is_none(),
                "{} carried motion",
                status.as_str()
            );
        }
    }
}
