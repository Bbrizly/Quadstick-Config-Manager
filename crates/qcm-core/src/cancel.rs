//! Asking a long job to stop.
//!
//! A trait rather than a flag because of where it may not be read. The install
//! transaction has a window between the old directory entry going away and the
//! new one landing, and a cancel honoured inside it would leave a disabled user
//! with no profile under that name at all. So the signal is only ever consulted
//! at the points [`crate::devices`] names, and the swap window has none.

/// Something the user can ask to stop.
pub trait CancelSignal {
    fn cancelled(&self) -> bool;
}

/// The default. A job with no way to be cancelled runs to its own end.
#[derive(Debug, Clone, Copy, Default)]
pub struct NeverCancels;

impl CancelSignal for NeverCancels {
    fn cancelled(&self) -> bool {
        false
    }
}

impl<T: CancelSignal + ?Sized> CancelSignal for &T {
    fn cancelled(&self) -> bool {
        (**self).cancelled()
    }
}

#[cfg(test)]
mod tests {
    use super::{CancelSignal, NeverCancels};

    #[test]
    fn the_default_signal_never_asks_for_a_stop() {
        assert!(!NeverCancels.cancelled());
        // The reference impl is what a caller passing `&token` relies on.
        let by_reference: &dyn CancelSignal = &NeverCancels;
        assert!(!by_reference.cancelled());
    }
}
