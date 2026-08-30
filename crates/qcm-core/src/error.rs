//! Stable error families and the redacted shape a window is allowed to show.
//!
//! Two audiences, one type. [`QcmError`] keeps whatever a log or a crash record
//! needs, including operating-system text with the user's home directory in it.
//! [`QcmErrorDto`] is what reaches a window, and it is built only out of fields
//! that cannot hold a host path. That is the whole redaction rule: the
//! conversion never reads [`OsDetail`], and [`scrub`] is the net under it.

use crate::confirmation::ConfirmationKind;
use crate::operation::OperationId;
use crate::ports::storage::{DeviceFileName, DeviceGeneration};
use serde::{Deserialize, Serialize};
use std::fmt;

/// Stable public code. One code per recovery meaning, never one per OS errno:
/// the UI switches on these, so a new errno must not become a new UI branch.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ErrorCode {
    ConfigUnreadable,
    ConfigTooLarge,
    ConfigHasBlockingProblems,
    ProfileUnknownSession,
    ProfileRevisionConflict,
    ProfileNothingToUndo,
    ProfileOperationRejected,
    ProfileNeedsSaveTarget,
    ProfileSaveTargetOnDevice,
    DeviceNotFound,
    DeviceStale,
    DeviceBusy,
    DeviceNotQuadStick,
    DeviceRemovedDuringWrite,
    StoragePermissionDenied,
    StorageReadOnly,
    StorageFull,
    StorageBackupFailed,
    StorageVerifyFailed,
    StorageRestoreFailed,
    StorageSwapFailed,
    StorageNameRejected,
    StorageProtectedFile,
    StorageFileNotFound,
    StorageIo,
    ConfirmationRequired,
    ConfirmationUnknown,
    ConfirmationExpired,
    ConfirmationMismatch,
    ConfirmationAlreadyUsed,
    Cancelled,
    Internal,
}

impl ErrorCode {
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::ConfigUnreadable => "QCM_CONFIG_PARSE_UNREADABLE",
            Self::ConfigTooLarge => "QCM_CONFIG_PARSE_TOO_LARGE",
            Self::ConfigHasBlockingProblems => "QCM_CONFIG_VALIDATION_BLOCKING",
            Self::ProfileUnknownSession => "QCM_PROFILE_UNKNOWN_SESSION",
            Self::ProfileRevisionConflict => "QCM_PROFILE_REVISION_CONFLICT",
            Self::ProfileNothingToUndo => "QCM_PROFILE_NOTHING_TO_UNDO",
            Self::ProfileOperationRejected => "QCM_PROFILE_OPERATION_REJECTED",
            Self::ProfileNeedsSaveTarget => "QCM_PROFILE_NEEDS_SAVE_TARGET",
            Self::ProfileSaveTargetOnDevice => "QCM_PROFILE_SAVE_TARGET_ON_DEVICE",
            Self::DeviceNotFound => "QCM_DEVICE_NOT_FOUND",
            Self::DeviceStale => "QCM_DEVICE_STALE",
            Self::DeviceBusy => "QCM_DEVICE_BUSY",
            Self::DeviceNotQuadStick => "QCM_DEVICE_NOT_QUADSTICK",
            Self::DeviceRemovedDuringWrite => "QCM_DEVICE_REMOVED_DURING_WRITE",
            Self::StoragePermissionDenied => "QCM_STORAGE_PERMISSION_DENIED",
            Self::StorageReadOnly => "QCM_STORAGE_READ_ONLY",
            Self::StorageFull => "QCM_STORAGE_FULL",
            Self::StorageBackupFailed => "QCM_STORAGE_BACKUP_FAILED",
            Self::StorageVerifyFailed => "QCM_STORAGE_VERIFY_FAILED",
            Self::StorageRestoreFailed => "QCM_STORAGE_RESTORE_FAILED",
            Self::StorageSwapFailed => "QCM_STORAGE_SWAP_FAILED",
            Self::StorageNameRejected => "QCM_STORAGE_NAME_REJECTED",
            Self::StorageProtectedFile => "QCM_STORAGE_PROTECTED_FILE",
            Self::StorageFileNotFound => "QCM_STORAGE_FILE_NOT_FOUND",
            Self::StorageIo => "QCM_STORAGE_IO",
            Self::ConfirmationRequired => "QCM_CONFIRMATION_REQUIRED",
            Self::ConfirmationUnknown => "QCM_CONFIRMATION_UNKNOWN",
            Self::ConfirmationExpired => "QCM_CONFIRMATION_EXPIRED",
            Self::ConfirmationMismatch => "QCM_CONFIRMATION_MISMATCH",
            Self::ConfirmationAlreadyUsed => "QCM_CONFIRMATION_ALREADY_USED",
            Self::Cancelled => "QCM_CANCELLED",
            Self::Internal => "QCM_INTERNAL",
        }
    }
}

impl fmt::Display for ErrorCode {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.as_str())
    }
}

/// The one thing the user can do next. The UI turns this into a single button,
/// which is why there is no `Unknown`: an error with no way forward is a bug.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum RecoveryAction {
    Retry,
    ReconnectDevice,
    RefreshDevices,
    WaitForCurrentOperation,
    ChooseAnotherFile,
    ChooseAnotherName,
    ChooseSaveLocation,
    FreeSpaceOnDevice,
    MakeDeviceWritable,
    ConfirmAgain,
    ReopenProfile,
    FixProfileProblems,
    RestoreBackupByHand,
    ReportBug,
}

impl RecoveryAction {
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Retry => "retry",
            Self::ReconnectDevice => "reconnect_device",
            Self::RefreshDevices => "refresh_devices",
            Self::WaitForCurrentOperation => "wait_for_current_operation",
            Self::ChooseAnotherFile => "choose_another_file",
            Self::ChooseAnotherName => "choose_another_name",
            Self::ChooseSaveLocation => "choose_save_location",
            Self::FreeSpaceOnDevice => "free_space_on_device",
            Self::MakeDeviceWritable => "make_device_writable",
            Self::ConfirmAgain => "confirm_again",
            Self::ReopenProfile => "reopen_profile",
            Self::FixProfileProblems => "fix_profile_problems",
            Self::RestoreBackupByHand => "restore_backup_by_hand",
            Self::ReportBug => "report_bug",
        }
    }
}

/// What is on the device now. Every failure past the point of no return has to
/// answer this honestly: a "nothing happened" that is really "we cannot tell"
/// is the most dangerous sentence this app can print.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum TargetState {
    /// Provably untouched. Only for a failure before the swap began.
    Unchanged,
    /// The old file was displaced and nothing is in its place yet.
    Missing,
    /// The new bytes are committed.
    Replaced,
    /// The old bytes were put back and verified.
    Restored,
    /// We cannot prove which of the above is true. Say so.
    Uncertain,
}

impl TargetState {
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Unchanged => "unchanged",
            Self::Missing => "missing",
            Self::Replaced => "replaced",
            Self::Restored => "restored",
            Self::Uncertain => "uncertain",
        }
    }
}

/// Where a failure happened. The install transaction and its fault-injection
/// tests share this list, so a new stage cannot be untestable.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum StorageStage {
    Discover,
    /// Marker check: the root still has to prove it is a QuadStick.
    Revalidate,
    ListFiles,
    ReadFile,
    Backup,
    TempCreate,
    TempWrite,
    TempFlush,
    TempReadBack,
    /// The replace call, before the old directory entry is gone.
    ReplaceBeforeDisplace,
    /// The replace call, after the old entry is gone and before the new one is in.
    ReplaceAfterDisplace,
    RestoreWrite,
    RestoreReplace,
    Delete,
    Cleanup,
}

impl StorageStage {
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Discover => "discover",
            Self::Revalidate => "revalidate",
            Self::ListFiles => "list_files",
            Self::ReadFile => "read_file",
            Self::Backup => "backup",
            Self::TempCreate => "temp_create",
            Self::TempWrite => "temp_write",
            Self::TempFlush => "temp_flush",
            Self::TempReadBack => "temp_read_back",
            Self::ReplaceBeforeDisplace => "replace_before_displace",
            Self::ReplaceAfterDisplace => "replace_after_displace",
            Self::RestoreWrite => "restore_write",
            Self::RestoreReplace => "restore_replace",
            Self::Delete => "delete",
            Self::Cleanup => "cleanup",
        }
    }

    /// True while the target file cannot have been touched yet. A stage that
    /// answers false may never report [`TargetState::Unchanged`].
    #[must_use]
    pub const fn is_before_swap(self) -> bool {
        matches!(
            self,
            Self::Discover
                | Self::Revalidate
                | Self::ListFiles
                | Self::ReadFile
                | Self::Backup
                | Self::TempCreate
                | Self::TempWrite
                | Self::TempFlush
                | Self::TempReadBack
        )
    }
}

/// Raw operating-system failure text.
///
/// A trap, kept in one type so it is easy to see: OS messages routinely carry
/// the mount point and the user's home directory. This is for logs and crash
/// records. Nothing in the DTO conversion reads it.
#[derive(Clone, PartialEq, Eq, Hash)]
pub struct OsDetail(String);

impl OsDetail {
    pub fn new(detail: impl Into<String>) -> Self {
        Self(detail.into())
    }

    /// Only a diagnostics sink may call this, never the DTO conversion.
    #[must_use]
    pub fn raw(&self) -> &str {
        &self.0
    }
}

impl fmt::Debug for OsDetail {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "OsDetail({:?})", self.0)
    }
}

/// A backup location a user can actually find, built out of two plain names so
/// it can never spell out the home directory it sits in.
#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct BackupLocationDisplay(String);

impl BackupLocationDisplay {
    /// `folder` is the backup directory's own name, `file` the copy inside it.
    /// Separators are dropped rather than rejected: this is display text, and a
    /// broken label must never be the reason a rescue message fails to print.
    #[must_use]
    pub fn new(folder: &str, file: &str) -> Self {
        Self(format!(
            "{}/{}",
            plain_component(folder),
            plain_component(file)
        ))
    }

    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

fn plain_component(value: &str) -> String {
    value
        .rsplit(['/', '\\'])
        .find(|part| !part.is_empty())
        .unwrap_or("backup")
        .to_owned()
}

impl fmt::Display for BackupLocationDisplay {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

/// Why a name may not be written to a device.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum NameRejection {
    Empty,
    /// Contains a separator, a control character or something Windows refuses.
    NotAPlainName,
    NotCsv,
    /// Longer than the device's 31 character slot.
    TooLongForDevice,
    /// `NUL.csv` and friends resolve to a Windows device, not a file.
    ReservedOnWindows,
    /// A dot-leading name is an AppleDouble sidecar or a hidden file.
    HiddenName,
}

impl NameRejection {
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Empty => "empty",
            Self::NotAPlainName => "not_a_plain_name",
            Self::NotCsv => "not_csv",
            Self::TooLongForDevice => "too_long_for_device",
            Self::ReservedOnWindows => "reserved_on_windows",
            Self::HiddenName => "hidden_name",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ConfigError {
    Unreadable,
    TooLarge { limit_bytes: u64 },
    HasBlockingProblems { errors: usize },
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ProfileError {
    UnknownSession,
    RevisionConflict {
        expected: u64,
        actual: u64,
    },
    NothingToUndo,
    /// One operation in a batch did not apply, so none of the batch did. The
    /// position is carried because the window submitted the list and is the
    /// only thing that can say which control it came from.
    OperationRejected {
        index: usize,
        op: &'static str,
    },
    /// Save has nowhere to write yet. A new profile and a working copy read off
    /// a device both start here, which is why both go through Save As first.
    NeedsSaveTarget,
    /// The chosen place is on a mounted QuadStick. Save never writes there.
    SaveTargetOnDevice,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum DeviceError {
    /// The opaque ID no longer maps to anything mounted.
    NotFound,
    /// The mapping is stale: the mount was reused or the volume remounted.
    Stale {
        expected: DeviceGeneration,
        actual: DeviceGeneration,
    },
    /// The root stopped proving it is a QuadStick: `default.csv` is gone.
    NotQuadStick,
    Busy {
        operation: OperationId,
    },
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum StorageError {
    /// The device stopped being the device we planned against. Carried here so
    /// the storage port has one error type and a stale ID cannot be reported as
    /// a plain I/O failure.
    Device(DeviceError),
    ReadOnly {
        stage: StorageStage,
    },
    Full {
        stage: StorageStage,
        target: TargetState,
    },
    PermissionDenied {
        stage: StorageStage,
    },
    /// The volume went away mid-operation. `target` is what we can still prove.
    RemovedDuringOperation {
        stage: StorageStage,
        target: TargetState,
    },
    /// The temp file did not read back byte for byte. The target is untouched.
    VerifyFailed,
    BackupFailed {
        detail: OsDetail,
    },
    /// The swap failed and putting the old file back failed too.
    RestoreFailed {
        backup: Option<BackupLocationDisplay>,
        detail: OsDetail,
    },
    SwapFailed {
        target: TargetState,
        backup: Option<BackupLocationDisplay>,
        detail: OsDetail,
    },
    NameRejected {
        reason: NameRejection,
    },
    /// `default.csv` and `prefs.csv` are never deleted by the normal path.
    ProtectedFile {
        name: DeviceFileName,
    },
    FileNotFound {
        name: DeviceFileName,
    },
    Io {
        stage: StorageStage,
        target: TargetState,
        detail: OsDetail,
    },
}

impl StorageError {
    /// What is on the device now, for the failures that can say.
    #[must_use]
    pub const fn target_state(&self) -> Option<TargetState> {
        match self {
            Self::Full { target, .. }
            | Self::RemovedDuringOperation { target, .. }
            | Self::SwapFailed { target, .. }
            | Self::Io { target, .. } => Some(*target),
            Self::VerifyFailed | Self::BackupFailed { .. } => Some(TargetState::Unchanged),
            Self::RestoreFailed { .. } => Some(TargetState::Uncertain),
            // A stick can turn read-only halfway through, which is exactly how
            // a dying one behaves, so the answer comes from the stage rather
            // than from an assumption that it failed early.
            Self::ReadOnly { stage } | Self::PermissionDenied { stage } => {
                Some(if stage.is_before_swap() {
                    TargetState::Unchanged
                } else {
                    TargetState::Uncertain
                })
            }
            Self::Device(_)
            | Self::NameRejected { .. }
            | Self::ProtectedFile { .. }
            | Self::FileNotFound { .. } => None,
        }
    }

    #[must_use]
    pub fn backup(&self) -> Option<&BackupLocationDisplay> {
        match self {
            Self::RestoreFailed { backup, .. } | Self::SwapFailed { backup, .. } => backup.as_ref(),
            _ => None,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ConfirmationError {
    /// The operation needs an acknowledgement that was not supplied.
    Missing {
        kind: ConfirmationKind,
    },
    Unknown,
    Expired,
    /// The confirmation was issued for a different operation.
    Mismatch,
    AlreadyUsed,
}

/// A bug, not a condition. Carries a fixed label so crash records group.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InternalError {
    pub what: &'static str,
    pub detail: OsDetail,
}

/// Every failure the core reports, grouped by the family that decides recovery.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum QcmError {
    Config(ConfigError),
    Profile(ProfileError),
    Device(DeviceError),
    Storage(StorageError),
    Confirmation(ConfirmationError),
    /// The user asked to stop. A result, not a fault.
    Cancelled,
    Internal(InternalError),
}

impl QcmError {
    #[must_use]
    pub fn code(&self) -> ErrorCode {
        match self {
            Self::Config(error) => match error {
                ConfigError::Unreadable => ErrorCode::ConfigUnreadable,
                ConfigError::TooLarge { .. } => ErrorCode::ConfigTooLarge,
                ConfigError::HasBlockingProblems { .. } => ErrorCode::ConfigHasBlockingProblems,
            },
            Self::Profile(error) => match error {
                ProfileError::UnknownSession => ErrorCode::ProfileUnknownSession,
                ProfileError::RevisionConflict { .. } => ErrorCode::ProfileRevisionConflict,
                ProfileError::NothingToUndo => ErrorCode::ProfileNothingToUndo,
                ProfileError::OperationRejected { .. } => ErrorCode::ProfileOperationRejected,
                ProfileError::NeedsSaveTarget => ErrorCode::ProfileNeedsSaveTarget,
                ProfileError::SaveTargetOnDevice => ErrorCode::ProfileSaveTargetOnDevice,
            },
            Self::Device(error) => device_code(error),
            Self::Storage(error) => match error {
                StorageError::Device(error) => device_code(error),
                StorageError::ReadOnly { .. } => ErrorCode::StorageReadOnly,
                StorageError::Full { .. } => ErrorCode::StorageFull,
                StorageError::PermissionDenied { .. } => ErrorCode::StoragePermissionDenied,
                StorageError::RemovedDuringOperation { .. } => ErrorCode::DeviceRemovedDuringWrite,
                StorageError::VerifyFailed => ErrorCode::StorageVerifyFailed,
                StorageError::BackupFailed { .. } => ErrorCode::StorageBackupFailed,
                StorageError::RestoreFailed { .. } => ErrorCode::StorageRestoreFailed,
                StorageError::SwapFailed { .. } => ErrorCode::StorageSwapFailed,
                StorageError::NameRejected { .. } => ErrorCode::StorageNameRejected,
                StorageError::ProtectedFile { .. } => ErrorCode::StorageProtectedFile,
                StorageError::FileNotFound { .. } => ErrorCode::StorageFileNotFound,
                StorageError::Io { .. } => ErrorCode::StorageIo,
            },
            Self::Confirmation(error) => match error {
                ConfirmationError::Missing { .. } => ErrorCode::ConfirmationRequired,
                ConfirmationError::Unknown => ErrorCode::ConfirmationUnknown,
                ConfirmationError::Expired => ErrorCode::ConfirmationExpired,
                ConfirmationError::Mismatch => ErrorCode::ConfirmationMismatch,
                ConfirmationError::AlreadyUsed => ErrorCode::ConfirmationAlreadyUsed,
            },
            Self::Cancelled => ErrorCode::Cancelled,
            Self::Internal(_) => ErrorCode::Internal,
        }
    }

    /// False only where the user cannot get out of it without a new build.
    #[must_use]
    pub fn recoverable(&self) -> bool {
        !matches!(self, Self::Internal(_))
    }

    /// The single next thing to offer. Every family answers, including the ones
    /// where the answer is "report it", so no screen is left with a dead end.
    #[must_use]
    pub fn action(&self) -> RecoveryAction {
        match self {
            Self::Config(error) => match error {
                ConfigError::Unreadable | ConfigError::TooLarge { .. } => {
                    RecoveryAction::ChooseAnotherFile
                }
                ConfigError::HasBlockingProblems { .. } => RecoveryAction::FixProfileProblems,
            },
            Self::Profile(error) => match error {
                ProfileError::UnknownSession | ProfileError::RevisionConflict { .. } => {
                    RecoveryAction::ReopenProfile
                }
                ProfileError::NothingToUndo | ProfileError::OperationRejected { .. } => {
                    RecoveryAction::Retry
                }
                ProfileError::NeedsSaveTarget | ProfileError::SaveTargetOnDevice => {
                    RecoveryAction::ChooseSaveLocation
                }
            },
            Self::Device(error) => device_action(error),
            Self::Storage(error) => match error {
                StorageError::Device(error) => device_action(error),
                StorageError::ReadOnly { .. } => RecoveryAction::MakeDeviceWritable,
                StorageError::Full { .. } => RecoveryAction::FreeSpaceOnDevice,
                StorageError::PermissionDenied { .. } => RecoveryAction::MakeDeviceWritable,
                StorageError::RemovedDuringOperation { .. } => RecoveryAction::ReconnectDevice,
                StorageError::VerifyFailed | StorageError::Io { .. } => RecoveryAction::Retry,
                StorageError::BackupFailed { .. } => RecoveryAction::Retry,
                StorageError::RestoreFailed { .. } | StorageError::SwapFailed { .. } => {
                    RecoveryAction::RestoreBackupByHand
                }
                StorageError::NameRejected { .. } => RecoveryAction::ChooseAnotherName,
                StorageError::ProtectedFile { .. } | StorageError::FileNotFound { .. } => {
                    RecoveryAction::RefreshDevices
                }
            },
            Self::Confirmation(error) => match error {
                ConfirmationError::Missing { .. }
                | ConfirmationError::Unknown
                | ConfirmationError::Expired
                | ConfirmationError::Mismatch
                | ConfirmationError::AlreadyUsed => RecoveryAction::ConfirmAgain,
            },
            Self::Cancelled => RecoveryAction::Retry,
            Self::Internal(_) => RecoveryAction::ReportBug,
        }
    }

    /// Fallback English. The window localizes from [`QcmError::code`]; this is
    /// what a log or an unlocalized surface prints. Built from structured
    /// fields only, so it cannot carry a path.
    #[must_use]
    pub fn message(&self) -> String {
        match self {
            Self::Config(error) => match error {
                ConfigError::Unreadable => "That file could not be read as a profile.".to_owned(),
                ConfigError::TooLarge { limit_bytes } => {
                    format!("That file is larger than the {limit_bytes} byte limit.")
                }
                ConfigError::HasBlockingProblems { errors } => {
                    format!("This profile has {errors} problems that must be fixed first.")
                }
            },
            Self::Profile(error) => match error {
                ProfileError::UnknownSession => "That profile is no longer open.".to_owned(),
                ProfileError::RevisionConflict { expected, actual } => format!(
                    "This profile changed since the edit was made (expected revision {expected}, found {actual})."
                ),
                ProfileError::NothingToUndo => "There is nothing left to undo.".to_owned(),
                ProfileError::OperationRejected { index, op } => format!(
                    "That change could not be made, so none of the batch was applied (operation {index}, {op})."
                ),
                ProfileError::NeedsSaveTarget => {
                    "This profile has not been saved anywhere yet.".to_owned()
                }
                ProfileError::SaveTargetOnDevice => {
                    "This profile lives on the QuadStick. Saving writes to your computer; use Install to put it back on the device."
                        .to_owned()
                }
            },
            Self::Device(error) => device_message(error),
            Self::Storage(error) => Self::storage_message(error),
            Self::Confirmation(error) => match error {
                ConfirmationError::Missing { kind } => {
                    format!("This needs to be confirmed first: {}.", kind.as_str())
                }
                ConfirmationError::Unknown => "That confirmation is not on record.".to_owned(),
                ConfirmationError::Expired => "That confirmation timed out.".to_owned(),
                ConfirmationError::Mismatch => {
                    "That confirmation was for a different change.".to_owned()
                }
                ConfirmationError::AlreadyUsed => {
                    "That confirmation was already used once.".to_owned()
                }
            },
            Self::Cancelled => "Cancelled.".to_owned(),
            Self::Internal(_) => "Something went wrong inside the app.".to_owned(),
        }
    }

    fn storage_message(error: &StorageError) -> String {
        match error {
            StorageError::Device(error) => device_message(error),
            StorageError::ReadOnly { .. } => "The QuadStick drive is read only.".to_owned(),
            StorageError::Full { .. } => "The QuadStick drive is full.".to_owned(),
            StorageError::PermissionDenied { .. } => {
                "This computer would not let the app write to the QuadStick.".to_owned()
            }
            StorageError::RemovedDuringOperation { target, .. } => format!(
                "The QuadStick was disconnected during the write. The profile on the device is {}.",
                target.as_str()
            ),
            StorageError::VerifyFailed => {
                "The copy on the device did not read back the same, so nothing was replaced."
                    .to_owned()
            }
            StorageError::BackupFailed { .. } => {
                "The backup could not be made, so nothing was changed on the device.".to_owned()
            }
            StorageError::RestoreFailed { backup, .. } => match backup {
                Some(location) => format!(
                    "Writing failed and the old profile could not be put back. A copy of it is in {location}."
                ),
                None => "Writing failed and the old profile could not be put back.".to_owned(),
            },
            StorageError::SwapFailed { target, backup, .. } => match backup {
                Some(location) => format!(
                    "Writing failed while replacing the file. The profile on the device is {}. A copy of the old one is in {location}.",
                    target.as_str()
                ),
                None => format!(
                    "Writing failed while replacing the file. The profile on the device is {}.",
                    target.as_str()
                ),
            },
            StorageError::NameRejected { reason } => format!(
                "The QuadStick cannot hold a file with that name ({}).",
                reason.as_str()
            ),
            StorageError::ProtectedFile { name } => {
                format!("{name} is a device file and is not deleted this way.")
            }
            StorageError::FileNotFound { name } => {
                format!("{name} is no longer on the QuadStick.")
            }
            StorageError::Io { stage, target, .. } => format!(
                "The QuadStick could not be written ({}). The profile on the device is {}.",
                stage.as_str(),
                target.as_str()
            ),
        }
    }
}

fn device_code(error: &DeviceError) -> ErrorCode {
    match error {
        DeviceError::NotFound => ErrorCode::DeviceNotFound,
        DeviceError::Stale { .. } => ErrorCode::DeviceStale,
        DeviceError::NotQuadStick => ErrorCode::DeviceNotQuadStick,
        DeviceError::Busy { .. } => ErrorCode::DeviceBusy,
    }
}

fn device_action(error: &DeviceError) -> RecoveryAction {
    match error {
        DeviceError::NotFound | DeviceError::NotQuadStick => RecoveryAction::ReconnectDevice,
        DeviceError::Stale { .. } => RecoveryAction::RefreshDevices,
        DeviceError::Busy { .. } => RecoveryAction::WaitForCurrentOperation,
    }
}

fn device_message(error: &DeviceError) -> String {
    match error {
        DeviceError::NotFound => "That QuadStick is no longer connected.".to_owned(),
        DeviceError::Stale { .. } => {
            "That drive is not the one this window was showing.".to_owned()
        }
        DeviceError::NotQuadStick => {
            "That folder does not look like a QuadStick: default.csv is missing.".to_owned()
        }
        DeviceError::Busy { .. } => "The QuadStick is busy with another operation.".to_owned(),
    }
}

impl fmt::Display for QcmError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}: {}", self.code(), self.message())
    }
}

impl std::error::Error for QcmError {}

impl From<ConfigError> for QcmError {
    fn from(error: ConfigError) -> Self {
        Self::Config(error)
    }
}

impl From<StorageError> for QcmError {
    fn from(error: StorageError) -> Self {
        Self::Storage(error)
    }
}

impl From<DeviceError> for QcmError {
    fn from(error: DeviceError) -> Self {
        Self::Device(error)
    }
}

impl From<DeviceError> for StorageError {
    fn from(error: DeviceError) -> Self {
        Self::Device(error)
    }
}

impl From<ProfileError> for QcmError {
    fn from(error: ProfileError) -> Self {
        Self::Profile(error)
    }
}

impl From<ConfirmationError> for QcmError {
    fn from(error: ConfirmationError) -> Self {
        Self::Confirmation(error)
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RecoveryActionDto {
    pub kind: String,
}

/// The only error shape a window ever sees.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct QcmErrorDto {
    pub code: String,
    pub message: String,
    pub recoverable: bool,
    pub action: Option<RecoveryActionDto>,
    pub operation_id: Option<String>,
    /// What is on the device now, where the failure can prove it. Beyond the
    /// original sketch on purpose: a window cannot say "restored" or "we cannot
    /// tell" without it, and the failure matrix requires it to say one of them.
    pub target_state: Option<String>,
    /// Where the rescue copy is, in a form the user can find and we can print.
    pub backup: Option<String>,
}

impl QcmErrorDto {
    /// The redaction boundary. Everything here comes from typed fields that
    /// cannot hold a host path; [`scrub`] is the net under a future field that
    /// forgets. [`OsDetail`] is deliberately not read.
    #[must_use]
    pub fn new(error: &QcmError, operation: Option<OperationId>) -> Self {
        let storage = match error {
            QcmError::Storage(storage) => Some(storage),
            _ => None,
        };
        Self {
            code: error.code().as_str().to_owned(),
            message: scrub(&error.message()),
            recoverable: error.recoverable(),
            action: Some(RecoveryActionDto {
                kind: error.action().as_str().to_owned(),
            }),
            operation_id: operation.map(|id| id.to_string()),
            target_state: storage
                .and_then(StorageError::target_state)
                .map(|state| state.as_str().to_owned()),
            backup: storage
                .and_then(StorageError::backup)
                .map(|location| scrub(location.as_str())),
        }
    }
}

impl From<&QcmError> for QcmErrorDto {
    fn from(error: &QcmError) -> Self {
        Self::new(error, None)
    }
}

/// True for text that names a place on this machine.
///
/// Used by the redaction tests and by [`scrub`]. It is deliberately eager: a
/// false positive costs a word in a message, a false negative ships someone's
/// home directory to a screenshot in a bug report.
#[must_use]
pub fn looks_like_absolute_path(token: &str) -> bool {
    if token.starts_with("~/") || token.starts_with("\\\\") {
        return true;
    }
    if token.starts_with('/') && token[1..].contains('/') {
        return true;
    }
    let mut chars = token.chars();
    match (chars.next(), chars.next(), chars.next()) {
        (Some(drive), Some(':'), Some('\\' | '/')) => drive.is_ascii_alphabetic(),
        _ => false,
    }
}

/// Replace anything that looks like a place on this machine with its last
/// component. Defense in depth only: the conversion above is built so there is
/// nothing for this to find. Splitting on whitespace means a path with a space
/// in it loses only its leading directories, which is the safe direction.
#[must_use]
pub fn scrub(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    let mut rest = text;
    while !rest.is_empty() {
        let end = rest.find(char::is_whitespace).unwrap_or(rest.len());
        let (token, tail) = rest.split_at(end);
        if looks_like_absolute_path(token) {
            out.push_str(last_component(token));
        } else {
            out.push_str(token);
        }
        let cut = tail
            .char_indices()
            .find(|(_, c)| !c.is_whitespace())
            .map_or(tail.len(), |(index, _)| index);
        out.push_str(&tail[..cut]);
        rest = &tail[cut..];
    }
    out
}

fn last_component(token: &str) -> &str {
    let trimmed = token.trim_end_matches(['/', '\\']);
    match trimmed.rsplit(['/', '\\']).next() {
        Some(name) if !name.is_empty() => name,
        _ => "<path>",
    }
}
