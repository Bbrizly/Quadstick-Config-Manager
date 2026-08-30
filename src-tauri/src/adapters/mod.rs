//! The outside world.
//!
//! Everything here implements a port from `qcm-core` against a real operating
//! system. The rule the split exists for: a path, a drive letter or a mount
//! point may appear inside an adapter and nowhere above one.

pub mod device_picker;
pub mod library;
pub mod picker;
pub mod settings;
pub mod storage;
