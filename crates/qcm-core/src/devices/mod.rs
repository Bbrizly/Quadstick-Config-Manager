//! Everything the app does to a mounted QuadStick.
//!
//! One service owns the storage port, the off-device backup area and the
//! confirmation ledger, because the three only make sense together: an install
//! that could reach the device without passing the ledger would be a way around
//! the `default.csv` gate, and a backup taken by a different object could be
//! taken after the file it is supposed to rescue has already gone.
//!
//! The modules split by job, not by type. [`discovery`] finds drives and hands
//! out opaque handles and [`install`] is the write transaction; the library
//! operations follow in TASK-025 as another `impl` block on [`Devices`].
//!
//! No path appears anywhere in here. The service names a device by an opaque id
//! and a file by a validated direct-child name, and the adapter behind the port
//! is the only thing that ever sees a mount point.

pub mod discovery;
pub mod install;

use crate::clock::Clock;
use crate::confirmation::ConfirmationLedger;
use crate::operation::OperationIds;
use crate::ports::storage::{BackupStore, DeviceStorage};
use discovery::Scan;
use std::time::Duration;

pub use discovery::{DEFAULT_SCAN_TTL, DeviceHandle, DeviceScan, DeviceSummary};
pub use install::{InstallFailure, InstallPlan, InstallReceipt};

/// The device side of the app.
#[derive(Debug)]
pub struct Devices<S: DeviceStorage, B: BackupStore, C: Clock + Clone> {
    storage: S,
    backups: B,
    clock: C,
    confirmations: ConfirmationLedger<C>,
    operations: OperationIds,
    scan: Option<Scan>,
    scan_ttl: Duration,
}

impl<S: DeviceStorage, B: BackupStore, C: Clock + Clone> Devices<S, B, C> {
    #[must_use]
    pub fn new(storage: S, backups: B, clock: C) -> Self {
        Self::with_scan_ttl(storage, backups, clock, DEFAULT_SCAN_TTL)
    }

    #[must_use]
    pub fn with_scan_ttl(storage: S, backups: B, clock: C, scan_ttl: Duration) -> Self {
        Self {
            confirmations: ConfirmationLedger::new(clock.clone()),
            storage,
            backups,
            clock,
            operations: OperationIds::new(),
            scan: None,
            scan_ttl,
        }
    }

    #[must_use]
    pub const fn storage(&self) -> &S {
        &self.storage
    }

    #[must_use]
    pub const fn backups(&self) -> &B {
        &self.backups
    }

    /// Confirmations that have been handed out and not yet spent. Exposed so a
    /// shutdown path can see whether a dialog is still open, not so a caller can
    /// reach past the gate: redeeming still needs the id and the fingerprint.
    #[must_use]
    pub fn outstanding_confirmations(&self) -> usize {
        self.confirmations.outstanding_count()
    }

    pub fn purge_expired_confirmations(&mut self) {
        self.confirmations.purge_expired();
    }
}
