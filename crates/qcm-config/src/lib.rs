#![forbid(unsafe_code)]
//! Lossless QuadStick profile parsing, validation, normalization, and editing.
//!
//! This crate is intentionally independent of Tauri, operating-system APIs,
//! networking, HID, and UI frameworks. During the rewrite it must earn byte
//! and semantic parity with the frozen C# `QuadStick.Format` oracle before any
//! legacy behavior is retired.

pub mod csv;
pub mod issue;
pub mod model;

/// Frozen legacy implementation this crate is required to match.
pub const LEGACY_BASE: &str = "f7783944387202bcafaeb7ff3f67789098fa6a4e";

/// Canonical migration schema emitted by the legacy oracle.
pub const PARITY_SCHEMA_VERSION: &str = "qcm-parity-1";

#[cfg(test)]
mod tests {
    use super::{LEGACY_BASE, PARITY_SCHEMA_VERSION};

    #[test]
    fn compatibility_identity_is_pinned() {
        assert_eq!(LEGACY_BASE.len(), 40);
        assert_eq!(PARITY_SCHEMA_VERSION, "qcm-parity-1");
    }
}
