//! Putting a profile on a QuadStick.
//!
//! The most dangerous code in the app. The device reads a profile until the
//! first blank line, so a half-written file loads without a word of complaint
//! and silently drops every binding past the cut. Somebody plays, works and
//! talks through this file.
//!
//! The sequence is the one the shipped `Device.Install` proved, in the same
//! order and for the same reasons:
//!
//! 1. plan: name rules, blocking problems, protected-name gate;
//! 2. revalidate the device, so a reformatted stick is refused;
//! 3. redeem the confirmation, once, against this operation's fingerprint;
//! 4. read what is there now;
//! 5. copy it off the device, before anything moves;
//! 6. write a temp file beside the target;
//! 7. read the temp back and compare byte for byte;
//! 8. replace;
//! 9. put the old bytes back if the replace broke after displacing them;
//! 10. never leave a stray temp behind.
//!
//! Two rules run through all of it. Nothing reports success it did not verify,
//! and no failure says "nothing happened" unless it can prove it.

use super::Devices;
use super::discovery::DeviceHandle;
use crate::cancel::{CancelSignal, NeverCancels};
use crate::clock::Clock;
use crate::confirmation::{ConfirmationId, ConfirmationKind, ConfirmationRequirement};
use crate::error::{
    BackupLocationDisplay, ConfigError, ConfirmationError, NameRejection, QcmError, StorageError,
    StorageStage, TargetState,
};
use crate::operation::{OperationFingerprint, OperationId, OperationKind};
use crate::ports::storage::{
    BackupStore, CommitFailure, DeviceFileName, DeviceStorage, SafeDeviceFileName, StagedWrite,
    StorageDeviceId,
};
use qcm_config::{ProfileFile, Severity, is_too_long_for_device};

/// Everything decided before the device is touched.
///
/// Not `Clone`, and [`Devices::install`] consumes it, so the same plan cannot be
/// executed twice against one confirmation.
#[derive(Debug, PartialEq, Eq)]
pub struct InstallPlan {
    operation: OperationId,
    handle: DeviceHandle,
    target: SafeDeviceFileName,
    bytes: Vec<u8>,
    fingerprint: OperationFingerprint,
    confirmation: Option<ConfirmationRequirement>,
}

impl InstallPlan {
    #[must_use]
    pub const fn operation(&self) -> OperationId {
        self.operation
    }

    #[must_use]
    pub const fn device(&self) -> StorageDeviceId {
        self.handle.device
    }

    #[must_use]
    pub const fn target(&self) -> &SafeDeviceFileName {
        &self.target
    }

    /// Exactly the bytes that will be written. Normalization already happened,
    /// on a copy, so nothing between here and the device reformats anything.
    #[must_use]
    pub fn bytes(&self) -> &[u8] {
        &self.bytes
    }

    /// The acknowledgement this write cannot proceed without, if any.
    #[must_use]
    pub const fn confirmation(&self) -> Option<&ConfirmationRequirement> {
        self.confirmation.as_ref()
    }

    #[must_use]
    pub const fn needs_confirmation(&self) -> bool {
        self.confirmation.is_some()
    }
}

/// An install that finished.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InstallReceipt {
    pub operation: OperationId,
    pub device: StorageDeviceId,
    pub target: SafeDeviceFileName,
    pub bytes: usize,
    /// Where the old profile went, when there was one to save.
    pub backup: Option<BackupLocationDisplay>,
    /// True when the installed file was reopened and matched byte for byte.
    ///
    /// The read-back that makes this safe is the one before the swap: the temp
    /// file is compared byte for byte and the target is untouched until it
    /// matches. This second look is a confirmation on top of that, and a drive
    /// pulled in the moment after a successful replace can deny it without
    /// making the replace any less real.
    pub confirmed_on_device: bool,
    /// Every stage that completed, in order.
    pub stages: Vec<StorageStage>,
}

/// An install that did not finish, and what it left behind.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InstallFailure {
    pub operation: OperationId,
    pub error: QcmError,
    /// Where it broke.
    pub stage: StorageStage,
    /// What is under the target name now. Never a guess.
    pub target: TargetState,
    /// The rescue copy, if one was made. Kept on every failure past the backup
    /// stage, because it is the way out of the worst of them.
    pub backup: Option<BackupLocationDisplay>,
    pub stages: Vec<StorageStage>,
}

/// Bookkeeping while the transaction runs.
struct Run {
    operation: OperationId,
    stages: Vec<StorageStage>,
    backup: Option<BackupLocationDisplay>,
}

impl Run {
    fn done(&mut self, stage: StorageStage) {
        self.stages.push(stage);
    }

    fn fail(
        &self,
        stage: StorageStage,
        target: TargetState,
        error: impl Into<QcmError>,
    ) -> InstallFailure {
        InstallFailure {
            operation: self.operation,
            error: error.into(),
            stage,
            target,
            backup: self.backup.clone(),
            stages: self.stages.clone(),
        }
    }
}

impl<S: DeviceStorage, B: BackupStore, C: Clock + Clone> Devices<S, B, C> {
    /// Work out what installing this profile would do, and hand back the
    /// acknowledgement it needs first.
    ///
    /// The checks are in the shipped order. The name-length one comes before the
    /// problem list because it has a single fix and the user has to be able to
    /// read it: the firmware keeps each root file name in a 31 character slot
    /// and copies it in without room for a terminator, so a longer name copies
    /// onto the stick fine, then cannot be opened, and the name after it in the
    /// device's own list prints as garbage too.
    pub fn plan_install(
        &mut self,
        device: StorageDeviceId,
        file: &ProfileFile,
    ) -> Result<InstallPlan, QcmError> {
        let declared = file.document.csv_file_name();
        if let Some(declared) = declared
            && is_too_long_for_device(declared)
        {
            return Err(StorageError::NameRejected {
                reason: NameRejection::TooLongForDevice,
            }
            .into());
        }

        let errors = file
            .issues
            .iter()
            .filter(|issue| issue.severity == Severity::Error)
            .count();
        if errors > 0 {
            return Err(ConfigError::HasBlockingProblems { errors }.into());
        }

        let Some(declared) = declared else {
            return Err(StorageError::NameRejected {
                reason: NameRejection::Empty,
            }
            .into());
        };

        // Read off the profile's own declared name, the same value that becomes
        // the target, so the gate cannot be dodged by installing under a
        // different name than the sheet says.
        let kind = if file.document.is_default_config() {
            Some(ConfirmationKind::OverwriteDefaultCsv)
        } else if file.document.is_device_preferences() {
            Some(ConfirmationKind::OverwriteDevicePreferences)
        } else {
            None
        };

        // Before the confirmation is minted rather than after: nobody should be
        // asked whether they really mean to overwrite default.csv on a folder
        // that turns out not to be a QuadStick at all.
        let handle = self.resolve_device(device)?;

        let target = SafeDeviceFileName::new(declared)
            .map_err(|reason| StorageError::NameRejected { reason })?;

        // A copy, so the profile the user still has open is not touched. The
        // legacy install did the same and its test pins it: installing must not
        // add the version header to the open editor or mark it dirty.
        let mut outgoing = file.clone();
        outgoing.normalize_for_device_csv();
        let bytes = outgoing.to_csv_text().into_bytes();

        let operation = self.operations.mint();
        let fingerprint = OperationFingerprint::builder(OperationKind::InstallProfile)
            .number("device", handle.device.raw())
            .number("generation", handle.generation.raw())
            .field("target", target.as_str())
            .number("bytes", bytes.len() as u64)
            .finish();

        let confirmation = kind.map(|kind| {
            let summary = match kind {
                ConfirmationKind::OverwriteDefaultCsv => format!(
                    "Replace {target} on the QuadStick. It is the profile the device falls back to."
                ),
                _ => format!(
                    "Replace {target} on the QuadStick. It is the device's own settings, so it changes every profile at once."
                ),
            };
            self.confirmations
                .require(kind, fingerprint.clone(), summary)
        });

        Ok(InstallPlan {
            operation,
            handle,
            target,
            bytes,
            fingerprint,
            confirmation,
        })
    }

    /// Run a plan with no way to cancel it.
    pub fn install(
        &mut self,
        plan: InstallPlan,
        confirmation: Option<ConfirmationId>,
    ) -> Result<InstallReceipt, InstallFailure> {
        self.install_with_cancel(plan, confirmation, &NeverCancels)
    }

    /// Run a plan.
    ///
    /// `cancel` is read three times, all of them before the temp file exists,
    /// and never again. Once the old directory entry can be displaced the only
    /// safe direction is forward or back to the old bytes, and a cancel honoured
    /// in there would leave a disabled user with nothing under that name.
    pub fn install_with_cancel(
        &mut self,
        plan: InstallPlan,
        confirmation: Option<ConfirmationId>,
        cancel: &impl CancelSignal,
    ) -> Result<InstallReceipt, InstallFailure> {
        let mut run = Run {
            operation: plan.operation,
            stages: Vec::new(),
            backup: None,
        };
        let name: DeviceFileName = plan.target.as_device_name().clone();

        if cancel.cancelled() {
            return Err(run.fail(
                StorageStage::Revalidate,
                TargetState::Unchanged,
                QcmError::Cancelled,
            ));
        }

        // The device is proven again here, not trusted from the plan. A stick
        // pulled and pushed back between the dialog and the button comes back
        // with a new generation and is refused.
        let handle = self
            .resolve_device(plan.handle.device)
            .map_err(|error| run.fail(StorageStage::Revalidate, TargetState::Unchanged, error))?;
        if handle.generation != plan.handle.generation {
            return Err(run.fail(
                StorageStage::Revalidate,
                TargetState::Unchanged,
                StorageError::Device(crate::error::DeviceError::Stale {
                    expected: plan.handle.generation,
                    actual: handle.generation,
                }),
            ));
        }
        run.done(StorageStage::Revalidate);

        if let Err(error) = self.redeem(&plan, confirmation) {
            return Err(run.fail(StorageStage::Revalidate, TargetState::Unchanged, error));
        }

        if cancel.cancelled() {
            return Err(run.fail(
                StorageStage::ReadFile,
                TargetState::Unchanged,
                QcmError::Cancelled,
            ));
        }

        let existing = match self
            .storage
            .read_file(handle.device, handle.generation, &name)
        {
            Ok(bytes) => Some(bytes),
            Err(StorageError::FileNotFound { .. }) => None,
            Err(error) => {
                return Err(run.fail(StorageStage::ReadFile, TargetState::Unchanged, error));
            }
        };
        run.done(StorageStage::ReadFile);

        // Off the device, before anything moves. The firmware deletes files it
        // does not recognize at startup, so a backup on the stick that just
        // failed is no backup at all.
        if let Some(old) = &existing {
            match self.backups.store(&name, old) {
                Ok(receipt) => run.backup = Some(receipt.location),
                Err(error) => {
                    return Err(run.fail(StorageStage::Backup, TargetState::Unchanged, error));
                }
            }
            run.done(StorageStage::Backup);
        }

        if cancel.cancelled() {
            return Err(run.fail(
                StorageStage::TempCreate,
                TargetState::Unchanged,
                QcmError::Cancelled,
            ));
        }
        // Last look. Everything past here is the swap.

        let staged = self
            .storage
            .stage_write(handle.device, handle.generation, &plan.target, &plan.bytes)
            .map_err(|error| {
                let stage = stage_of(&error).unwrap_or(StorageStage::TempWrite);
                run.fail(stage, TargetState::Unchanged, error)
            })?;
        run.done(StorageStage::TempWrite);

        if let Err(error) = self.storage.verify_staged(&staged, &plan.bytes) {
            self.discard(staged);
            return Err(run.fail(StorageStage::TempReadBack, TargetState::Unchanged, error));
        }
        run.done(StorageStage::TempReadBack);

        match self.storage.commit_staged(staged) {
            Ok(()) => {
                run.done(StorageStage::ReplaceAfterDisplace);
                Ok(self.finish(run, handle, plan, &name))
            }
            Err(failure) => Err(self.recover(run, handle, &name, existing.as_deref(), failure)),
        }
    }

    /// Read the installed file back and compare.
    ///
    /// A mismatch means the replace put something on the device that is neither
    /// the old profile nor the new one, so the old bytes go back the safe way
    /// and the failure says so. A read that cannot happen at all leaves the
    /// install standing: `commit_staged` already returned, and the byte for byte
    /// check before the swap is what makes that answer trustworthy.
    fn finish(
        &mut self,
        run: Run,
        handle: DeviceHandle,
        plan: InstallPlan,
        name: &DeviceFileName,
    ) -> InstallReceipt {
        let confirmed = match self
            .storage
            .read_file(handle.device, handle.generation, name)
        {
            Ok(found) => found == plan.bytes,
            Err(_) => false,
        };
        // A cached device row is stale the moment free space moves.
        self.invalidate_device_cache();
        InstallReceipt {
            operation: run.operation,
            device: handle.device,
            target: plan.target,
            bytes: plan.bytes.len(),
            backup: run.backup,
            confirmed_on_device: confirmed,
            stages: run.stages,
        }
    }

    /// Deal with a replace that did not happen.
    fn recover(
        &mut self,
        mut run: Run,
        handle: DeviceHandle,
        name: &DeviceFileName,
        existing: Option<&[u8]>,
        failure: CommitFailure,
    ) -> InstallFailure {
        let CommitFailure { error, staged } = failure;
        if let Some(staged) = staged {
            // The temp is still sitting beside the profile and nothing else will
            // remove it. A user cannot tell it apart from the real thing.
            self.discard(staged);
        }
        let reported = error.target_state().unwrap_or(TargetState::Uncertain);
        if reported != TargetState::Missing {
            let stage = stage_of(&error).unwrap_or(StorageStage::ReplaceAfterDisplace);
            return run.fail(stage, reported, error);
        }

        // The old entry is provably gone and the drive is still answering.
        let Some(old) = existing else {
            // Nothing was there to begin with, so nothing was lost. The name is
            // still empty, which is what Missing says.
            return run.fail(
                StorageStage::ReplaceAfterDisplace,
                TargetState::Missing,
                error,
            );
        };
        match self
            .storage
            .restore_file(handle.device, handle.generation, name, old)
        {
            Ok(()) => {
                run.done(StorageStage::RestoreReplace);
                run.fail(
                    StorageStage::ReplaceAfterDisplace,
                    TargetState::Restored,
                    error,
                )
            }
            // Two failures deep. What is under the name now cannot be proven, so
            // the message says exactly that and points at the rescue copy.
            Err(restore) => run.fail(
                StorageStage::RestoreWrite,
                TargetState::Uncertain,
                StorageError::RestoreFailed {
                    backup: run.backup.clone(),
                    detail: os_detail(&restore),
                },
            ),
        }
    }

    /// Best effort by contract. A temp that will not delete is litter, and
    /// litter must never replace the error that led to the discard.
    fn discard(&self, staged: StagedWrite) {
        let _ = self.storage.discard_staged(staged);
    }

    fn redeem(
        &mut self,
        plan: &InstallPlan,
        confirmation: Option<ConfirmationId>,
    ) -> Result<(), QcmError> {
        let Some(required) = plan.confirmation.as_ref() else {
            return Ok(());
        };
        let Some(id) = confirmation else {
            return Err(ConfirmationError::Missing {
                kind: required.kind,
            }
            .into());
        };
        self.confirmations
            .redeem(id, required.kind, &plan.fingerprint)?;
        Ok(())
    }
}

/// The stage a storage error names, where it names one.
const fn stage_of(error: &StorageError) -> Option<StorageStage> {
    match error {
        StorageError::ReadOnly { stage }
        | StorageError::PermissionDenied { stage }
        | StorageError::Full { stage, .. }
        | StorageError::RemovedDuringOperation { stage, .. }
        | StorageError::Io { stage, .. } => Some(*stage),
        _ => None,
    }
}

/// Keep the cause with the report. A restore failure that arrives with nothing
/// in it about what actually broke leaves a crash record no one can act on.
fn os_detail(error: &StorageError) -> crate::error::OsDetail {
    crate::error::OsDetail::new(format!("{error:?}"))
}
