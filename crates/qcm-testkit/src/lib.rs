#![forbid(unsafe_code)]
//! Deterministic fakes for the `qcm-core` ports.
//!
//! Device safety is the part of this app that can cost someone their working
//! controller, and it is the part that is hardest to reach on real hardware:
//! nobody can pull a USB stick out at exactly the wrong microsecond on demand.
//! These fakes make each of those moments a line in a test.

pub mod local;
pub mod storage;

pub use local::FakeProfileLibrary;
pub use storage::{FakeBackupStore, FakeQuadStick, Fault, TEMP_SUFFIX};
