//! What a window is shown must never say where anything lives on this machine.
//!
//! Bug reports carry screenshots. An error that prints `/Users/<name>/...` puts
//! the person's name in every one of them, and an OS message is where that gets
//! in. These tests walk every error family with a poisoned path in every field
//! that could hold one and check what comes out the other side.

use qcm_core::confirmation::ConfirmationKind;
use qcm_core::error::{
    BackupLocationDisplay, ConfigError, ConfirmationError, DeviceError, InternalError,
    NameRejection, OsDetail, ProfileError, QcmError, QcmErrorDto, RequestError, StorageError,
    StorageStage, TargetState, looks_like_absolute_path, scrub,
};
use qcm_core::operation::{OperationId, OperationIds};
use qcm_core::ports::storage::{DeviceFileName, DeviceGeneration};

const UNIX_POISON: &str = "/Users/bassam/Documents/GitHub/quadstick/default.csv";
const WINDOWS_POISON: &str = "C:\\Users\\Bassam\\AppData\\Roaming\\prefs.csv";
const SECRETS: [&str; 6] = [
    "/Users/",
    "bassam",
    "Bassam",
    "C:\\",
    "AppData",
    "/Volumes/",
];

fn poisoned_detail() -> OsDetail {
    OsDetail::new(format!(
        "Access to the path {UNIX_POISON} is denied (os error 13); tried {WINDOWS_POISON}"
    ))
}

fn poisoned_backup() -> Option<BackupLocationDisplay> {
    Some(BackupLocationDisplay::new(
        "/Users/bassam/QuadStickBackups",
        "/Users/bassam/QuadStickBackups/20260829-101010-123-racing.csv",
    ))
}

fn name(value: &str) -> DeviceFileName {
    DeviceFileName::new(value).expect("plain device file name")
}

/// One of every family, every one carrying whatever path it could carry.
fn every_error() -> Vec<QcmError> {
    vec![
        QcmError::Config(ConfigError::Unreadable),
        QcmError::Config(ConfigError::TooLarge {
            limit_bytes: 4_194_304,
        }),
        QcmError::Config(ConfigError::HasBlockingProblems { errors: 3 }),
        QcmError::Profile(ProfileError::UnknownSession),
        QcmError::Profile(ProfileError::RevisionConflict {
            expected: 4,
            actual: 6,
        }),
        QcmError::Profile(ProfileError::NothingToUndo),
        QcmError::Profile(ProfileError::OperationRejected {
            index: 2,
            op: "delete_row",
        }),
        QcmError::Profile(ProfileError::NeedsSaveTarget),
        QcmError::Profile(ProfileError::SaveTargetOnDevice),
        QcmError::Device(DeviceError::NotFound),
        QcmError::Device(DeviceError::Stale {
            expected: DeviceGeneration::from_raw(2),
            actual: DeviceGeneration::from_raw(3),
        }),
        QcmError::Device(DeviceError::NotQuadStick),
        QcmError::Device(DeviceError::Busy {
            operation: OperationId::from_raw(9),
        }),
        QcmError::Storage(StorageError::Device(DeviceError::NotQuadStick)),
        QcmError::Storage(StorageError::ReadOnly {
            stage: StorageStage::TempCreate,
        }),
        QcmError::Storage(StorageError::Full {
            stage: StorageStage::TempWrite,
            target: TargetState::Unchanged,
        }),
        QcmError::Storage(StorageError::PermissionDenied {
            stage: StorageStage::Backup,
        }),
        QcmError::Storage(StorageError::RemovedDuringOperation {
            stage: StorageStage::ReplaceAfterDisplace,
            target: TargetState::Uncertain,
        }),
        QcmError::Storage(StorageError::VerifyFailed),
        QcmError::Storage(StorageError::BackupFailed {
            detail: poisoned_detail(),
        }),
        QcmError::Storage(StorageError::RestoreFailed {
            backup: poisoned_backup(),
            detail: poisoned_detail(),
        }),
        QcmError::Storage(StorageError::SwapFailed {
            target: TargetState::Restored,
            backup: poisoned_backup(),
            detail: poisoned_detail(),
        }),
        QcmError::Storage(StorageError::NameRejected {
            reason: NameRejection::TooLongForDevice,
        }),
        QcmError::Storage(StorageError::ProtectedFile {
            name: name("default.csv"),
        }),
        QcmError::Storage(StorageError::FileNotFound {
            name: name("racing.csv"),
        }),
        QcmError::Storage(StorageError::Io {
            stage: StorageStage::ReplaceBeforeDisplace,
            target: TargetState::Unchanged,
            detail: poisoned_detail(),
        }),
        QcmError::Confirmation(ConfirmationError::Missing {
            kind: ConfirmationKind::OverwriteDefaultCsv,
        }),
        QcmError::Confirmation(ConfirmationError::Unknown),
        QcmError::Confirmation(ConfirmationError::Expired),
        QcmError::Confirmation(ConfirmationError::Mismatch),
        QcmError::Confirmation(ConfirmationError::AlreadyUsed),
        QcmError::Request(RequestError::Malformed {
            what: "apply_editor_ops request",
        }),
        QcmError::Request(RequestError::TooLarge {
            what: "profile name",
            limit: 128,
            actual: 4096,
        }),
        QcmError::Request(RequestError::OutOfRange { what: "theme" }),
        QcmError::Cancelled,
        QcmError::Internal(InternalError {
            what: "editor snapshot",
            detail: poisoned_detail(),
        }),
    ]
}

fn assert_clean(text: &str, context: &str) {
    for secret in SECRETS {
        assert!(
            !text.contains(secret),
            "{context} leaked {secret:?}: {text:?}"
        );
    }
    for token in text.split_whitespace() {
        assert!(
            !looks_like_absolute_path(token),
            "{context} leaked a path in {token:?}"
        );
    }
}

#[test]
fn no_error_shows_a_place_on_this_machine() {
    for error in every_error() {
        let dto = QcmErrorDto::new(&error, Some(OperationId::from_raw(1)));
        assert_clean(&dto.message, dto.code.as_str());
        if let Some(backup) = &dto.backup {
            assert_clean(backup, dto.code.as_str());
        }
        assert_clean(&dto.code, "code");
    }
}

// The same errors keep their detail for a log. Redaction is a conversion, not
// an amnesia: a crash record still has to say which path failed.
#[test]
fn the_os_detail_survives_for_diagnostics() {
    let error = QcmError::Storage(StorageError::BackupFailed {
        detail: poisoned_detail(),
    });
    assert!(format!("{error:?}").contains(UNIX_POISON));
    assert!(!QcmErrorDto::from(&error).message.contains(UNIX_POISON));
}

#[test]
fn every_error_carries_a_code_a_recovery_and_a_next_step() {
    for error in every_error() {
        let dto = QcmErrorDto::new(&error, None);
        assert!(dto.code.starts_with("QCM_"), "{dto:?}");
        assert!(dto.action.is_some(), "{dto:?}");
        assert!(!dto.message.is_empty(), "{dto:?}");
        assert_eq!(dto.recoverable, error.recoverable());
    }
}

// The acceptance criterion for TASK-021: a window recovers from anything except
// a bug in the app without ever seeing an operating-system error.
#[test]
fn only_an_internal_bug_is_unrecoverable() {
    for error in every_error() {
        let expected = !matches!(error, QcmError::Internal(_));
        assert_eq!(error.recoverable(), expected, "{error:?}");
    }
}

#[test]
fn a_failure_past_the_swap_never_claims_the_file_is_unchanged() {
    let error = StorageError::RemovedDuringOperation {
        stage: StorageStage::ReplaceAfterDisplace,
        target: TargetState::Uncertain,
    };
    let dto = QcmErrorDto::new(&QcmError::Storage(error), None);
    assert_eq!(dto.target_state.as_deref(), Some("uncertain"));
    assert!(dto.message.contains("uncertain"));

    let before = StorageError::RemovedDuringOperation {
        stage: StorageStage::TempWrite,
        target: TargetState::Unchanged,
    };
    let dto = QcmErrorDto::new(&QcmError::Storage(before), None);
    assert_eq!(dto.target_state.as_deref(), Some("unchanged"));
}

// A drive that turns read-only during the swap is a dying drive, not an early
// refusal, and it may not be reported as one.
#[test]
fn a_read_only_failure_reports_the_state_its_stage_can_prove() {
    let early = QcmError::Storage(StorageError::ReadOnly {
        stage: StorageStage::TempCreate,
    });
    assert_eq!(
        QcmErrorDto::new(&early, None).target_state.as_deref(),
        Some("unchanged")
    );
    let late = QcmError::Storage(StorageError::PermissionDenied {
        stage: StorageStage::ReplaceAfterDisplace,
    });
    assert_eq!(
        QcmErrorDto::new(&late, None).target_state.as_deref(),
        Some("uncertain")
    );
}

#[test]
fn a_stage_before_the_swap_is_the_only_one_allowed_to_say_unchanged() {
    assert!(StorageStage::TempReadBack.is_before_swap());
    assert!(StorageStage::Backup.is_before_swap());
    assert!(!StorageStage::ReplaceBeforeDisplace.is_before_swap());
    assert!(!StorageStage::ReplaceAfterDisplace.is_before_swap());
    assert!(!StorageStage::RestoreWrite.is_before_swap());
    assert!(!StorageStage::Cleanup.is_before_swap());
}

#[test]
fn a_backup_location_is_findable_without_naming_the_home_directory() {
    let location = BackupLocationDisplay::new(
        "/Users/bassam/QuadStickBackups",
        "20260829-101010-123-racing.csv",
    );
    assert_eq!(
        location.as_str(),
        "QuadStickBackups/20260829-101010-123-racing.csv"
    );
    assert_clean(location.as_str(), "backup location");
}

#[test]
fn the_scrubber_catches_a_path_a_future_field_forgets() {
    assert_eq!(
        scrub("Access to /Users/bassam/quadstick/default.csv is denied"),
        "Access to default.csv is denied"
    );
    assert_eq!(
        scrub("tried C:\\Users\\Bassam\\prefs.csv twice"),
        "tried prefs.csv twice"
    );
    assert_eq!(
        scrub("saved to ~/QuadStickBackups"),
        "saved to QuadStickBackups"
    );
    assert_eq!(scrub("\\\\nas\\share\\game.csv"), "game.csv");
    assert_eq!(scrub("racing.csv is fine"), "racing.csv is fine");
    assert_eq!(scrub("a/b is relative"), "a/b is relative");
    assert_eq!(scrub(""), "");
    assert_eq!(scrub("  spaced   out  "), "  spaced   out  ");
}

#[test]
fn the_path_detector_is_eager_but_not_blind() {
    assert!(looks_like_absolute_path("/Users/bassam/x.csv"));
    assert!(looks_like_absolute_path("/Volumes/QUADSTICK/default.csv"));
    assert!(looks_like_absolute_path("C:\\Users\\b"));
    assert!(looks_like_absolute_path("d:/temp"));
    assert!(looks_like_absolute_path("\\\\nas\\share"));
    assert!(looks_like_absolute_path("~/backups"));
    assert!(!looks_like_absolute_path("racing.csv"));
    assert!(!looks_like_absolute_path("/tmp"));
    assert!(!looks_like_absolute_path("a/b"));
    assert!(!looks_like_absolute_path(""));
}

#[test]
fn the_dto_survives_the_trip_a_window_would_take_it_on() {
    let error = QcmError::Storage(StorageError::SwapFailed {
        target: TargetState::Restored,
        backup: poisoned_backup(),
        detail: poisoned_detail(),
    });
    let ids = OperationIds::new();
    let dto = QcmErrorDto::new(&error, Some(ids.mint()));
    let json = serde_json::to_string(&dto).expect("dto serializes");
    assert_clean(&json, "serialized dto");
    let back: QcmErrorDto = serde_json::from_str(&json).expect("dto deserializes");
    assert_eq!(back, dto);
    assert_eq!(back.operation_id.as_deref(), Some("op-1"));
}
