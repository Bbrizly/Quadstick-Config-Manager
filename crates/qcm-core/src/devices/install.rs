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

    #[must_use]
    pub fn bytes(&self) -> &[u8] {
        &self.bytes
    }

    #[must_use]
    pub const fn confirmation(&self) -> Option<&ConfirmationRequirement> {
        self.confirmation.as_ref()
    }

    #[must_use]
    pub const fn needs_confirmation(&self) -> bool {
        self.confirmation.is_some()
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InstallReceipt {
    pub operation: OperationId,
    pub device: StorageDeviceId,
    pub target: SafeDeviceFileName,
    pub bytes: usize,
    pub backup: Option<BackupLocationDisplay>,
    pub confirmed_on_device: bool,
    pub stages: Vec<StorageStage>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InstallFailure {
    pub operation: OperationId,
    pub error: QcmError,
    pub stage: StorageStage,
    pub target: TargetState,
    pub backup: Option<BackupLocationDisplay>,
    pub stages: Vec<StorageStage>,
}

struct Run {
    operation: OperationId,
    stages: Vec<StorageStage>,
    backup: Option<BackupLocationDisplay>,
}

impl Run {
    fn done(&mut self, stage: StorageStage, progress: &mut impl FnMut(StorageStage)) {
        self.stages.push(stage);
        progress(stage);
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

        let kind = if file.document.is_default_config() {
            Some(ConfirmationKind::OverwriteDefaultCsv)
        } else if file.document.is_device_preferences() {
            Some(ConfirmationKind::OverwriteDevicePreferences)
        } else {
            None
        };

        let handle = self.resolve_device(device)?;
        let target = SafeDeviceFileName::new(declared)
            .map_err(|reason| StorageError::NameRejected { reason })?;

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

    pub fn install(
        &mut self,
        plan: InstallPlan,
        confirmation: Option<ConfirmationId>,
    ) -> Result<InstallReceipt, InstallFailure> {
        self.install_with_progress(plan, confirmation, |_| {})
    }

    /// Run a plan and report only stages that have actually completed.
    ///
    /// The callback is observational. It cannot cancel or change the transaction,
    /// and it is never called for a stage that failed. That keeps progress UI
    /// subordinate to the receipt/failure rather than turning UI state into a
    /// second transaction model.
    pub fn install_with_progress(
        &mut self,
        plan: InstallPlan,
        confirmation: Option<ConfirmationId>,
        progress: impl FnMut(StorageStage),
    ) -> Result<InstallReceipt, InstallFailure> {
        self.install_with_cancel_and_progress(plan, confirmation, &NeverCancels, progress)
    }

    pub fn install_with_cancel(
        &mut self,
        plan: InstallPlan,
        confirmation: Option<ConfirmationId>,
        cancel: &impl CancelSignal,
    ) -> Result<InstallReceipt, InstallFailure> {
        self.install_with_cancel_and_progress(plan, confirmation, cancel, |_| {})
    }

    fn install_with_cancel_and_progress(
        &mut self,
        plan: InstallPlan,
        confirmation: Option<ConfirmationId>,
        cancel: &impl CancelSignal,
        mut progress: impl FnMut(StorageStage),
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
        run.done(StorageStage::Revalidate, &mut progress);

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
        run.done(StorageStage::ReadFile, &mut progress);

        if let Some(old) = &existing {
            match self.backups.store(&name, old) {
                Ok(receipt) => run.backup = Some(receipt.location),
                Err(error) => {
                    return Err(run.fail(StorageStage::Backup, TargetState::Unchanged, error));
                }
            }
            run.done(StorageStage::Backup, &mut progress);
        }

        if cancel.cancelled() {
            return Err(run.fail(
                StorageStage::TempCreate,
                TargetState::Unchanged,
                QcmError::Cancelled,
            ));
        }
        // Last cancellation point. Everything past here is the unsafe swap
        // region and must run forward or restore; progress can observe it but
        // never interrupt it.
        let staged = self
            .storage
            .stage_write(handle.device, handle.generation, &plan.target, &plan.bytes)
            .map_err(|error| {
                let stage = stage_of(&error).unwrap_or(StorageStage::TempWrite);
                run.fail(stage, TargetState::Unchanged, error)
            })?;
        run.done(StorageStage::TempWrite, &mut progress);

        if let Err(error) = self.storage.verify_staged(&staged, &plan.bytes) {
            self.discard(staged);
            return Err(run.fail(StorageStage::TempReadBack, TargetState::Unchanged, error));
        }
        run.done(StorageStage::TempReadBack, &mut progress);

        match self.storage.commit_staged(staged) {
            Ok(()) => {
                run.done(StorageStage::ReplaceAfterDisplace, &mut progress);
                Ok(self.finish(run, handle, plan, &name))
            }
            Err(failure) => Err(self.recover(
                run,
                handle,
                &name,
                existing.as_deref(),
                failure,
                &mut progress,
            )),
        }
    }

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

    fn recover(
        &mut self,
        mut run: Run,
        handle: DeviceHandle,
        name: &DeviceFileName,
        existing: Option<&[u8]>,
        failure: CommitFailure,
        progress: &mut impl FnMut(StorageStage),
    ) -> InstallFailure {
        let CommitFailure { error, staged } = failure;
        if let Some(staged) = staged {
            self.discard(staged);
        }
        let reported = error.target_state().unwrap_or(TargetState::Uncertain);
        if reported != TargetState::Missing {
            let stage = stage_of(&error).unwrap_or(StorageStage::ReplaceAfterDisplace);
            return run.fail(stage, reported, error);
        }

        let Some(old) = existing else {
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
                run.done(StorageStage::RestoreReplace, progress);
                run.fail(
                    StorageStage::ReplaceAfterDisplace,
                    TargetState::Restored,
                    error,
                )
            }
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

fn os_detail(error: &StorageError) -> crate::error::OsDetail {
    crate::error::OsDetail::new(format!("{error:?}"))
}
