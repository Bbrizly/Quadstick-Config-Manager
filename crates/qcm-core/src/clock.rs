//! A clock the tests can hold still.
//!
//! Confirmations expire, and an expiry test that sleeps is a flaky test. Every
//! deadline in the core is measured against this trait, never against
//! `Instant::now()` reached for at the point of use.

use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant};

/// Time since the clock started. Monotonic by construction: there is no way to
/// build one out of a wall-clock reading, so a user changing the system time
/// cannot expire or extend a confirmation.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct Moment(Duration);

impl Moment {
    pub const ZERO: Self = Self(Duration::ZERO);

    #[must_use]
    pub const fn from_start(elapsed: Duration) -> Self {
        Self(elapsed)
    }

    #[must_use]
    pub const fn since_start(self) -> Duration {
        self.0
    }

    /// Saturating so a deadline far in the future cannot wrap into the past.
    #[must_use]
    pub fn plus(self, span: Duration) -> Self {
        Self(self.0.saturating_add(span))
    }
}

pub trait Clock {
    fn now(&self) -> Moment;
}

/// The real clock. `Instant` is monotonic on every platform we ship.
#[derive(Debug)]
pub struct SystemClock {
    start: Instant,
}

impl SystemClock {
    #[must_use]
    pub fn new() -> Self {
        Self {
            start: Instant::now(),
        }
    }
}

impl Default for SystemClock {
    fn default() -> Self {
        Self::new()
    }
}

impl Clock for SystemClock {
    fn now(&self) -> Moment {
        Moment(self.start.elapsed())
    }
}

/// A clock that only moves when a test moves it.
#[derive(Debug, Default)]
pub struct ManualClock {
    nanos: AtomicU64,
}

impl ManualClock {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    pub fn advance(&self, span: Duration) {
        let nanos = u64::try_from(span.as_nanos()).unwrap_or(u64::MAX);
        self.nanos.fetch_add(nanos, Ordering::SeqCst);
    }
}

impl Clock for ManualClock {
    fn now(&self) -> Moment {
        Moment(Duration::from_nanos(self.nanos.load(Ordering::SeqCst)))
    }
}

impl<C: Clock + ?Sized> Clock for &C {
    fn now(&self) -> Moment {
        (**self).now()
    }
}

#[cfg(test)]
mod tests {
    use super::{Clock, ManualClock, Moment, SystemClock};
    use std::time::Duration;

    #[test]
    fn a_manual_clock_moves_only_when_told() {
        let clock = ManualClock::new();
        assert_eq!(clock.now(), Moment::ZERO);
        clock.advance(Duration::from_secs(30));
        assert_eq!(clock.now().since_start(), Duration::from_secs(30));
        assert_eq!(clock.now().since_start(), Duration::from_secs(30));
    }

    #[test]
    fn a_deadline_far_out_does_not_wrap_into_the_past() {
        let far = Moment::from_start(Duration::MAX).plus(Duration::from_secs(60));
        assert!(far > Moment::ZERO);
    }

    #[test]
    fn the_system_clock_never_goes_backwards() {
        let clock = SystemClock::new();
        let first = clock.now();
        let second = clock.now();
        assert!(second >= first);
    }
}
