#![forbid(unsafe_code)]
//! The QuadStick application core.
//!
//! Everything above the file format and below the window: what an operation is,
//! what a failure means, what the user has to agree to before a device is
//! written, and the ports the outside world is reached through.
//!
//! The boundary is the point. This crate has no Tauri, no OS crate, no network
//! and nothing that writes a file. The storage port is a trait, the adapter
//! that touches a real volume lives outside, and `qcm-testkit` supplies a fake
//! so every device-safety test runs without hardware.

pub mod clock;
pub mod confirmation;
pub mod error;
pub mod operation;
pub mod ports;

pub use clock::{Clock, ManualClock, Moment, SystemClock};
pub use confirmation::{
    ConfirmationId, ConfirmationKind, ConfirmationLedger, ConfirmationRequirement,
    DEFAULT_CONFIRMATION_TTL,
};
pub use error::{
    BackupLocationDisplay, ConfigError, ConfirmationError, DeviceError, ErrorCode, InternalError,
    NameRejection, OsDetail, ProfileError, QcmError, QcmErrorDto, RecoveryAction,
    RecoveryActionDto, StorageError, StorageStage, TargetState,
};
pub use operation::{
    FingerprintBuilder, Operation, OperationFingerprint, OperationId, OperationIds, OperationKind,
};
pub use ports::storage::{
    BackupReceipt, BackupStore, CommitFailure, DeviceDisplayName, DeviceFileEntry, DeviceFileName,
    DeviceFileRole, DeviceGeneration, DeviceListing, DeviceStorage, MARKER_FILE_NAME,
    PREFERENCES_FILE_NAME, SafeDeviceFileName, StagedWrite, StorageCapabilities, StorageDeviceId,
    StorageProbe,
};

/// Same promise `qcm-config` makes, one layer up.
pub const CORE_CRATE_POLICY: &str = "pure-rust-no-tauri-no-os-no-network-no-filesystem-write";
