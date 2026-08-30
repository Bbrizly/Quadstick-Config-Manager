//! The install transaction, failed at every stage on purpose.
//!
//! Ported from `EditInstallTests.DeviceTests` and `InstallCleanupTests`, which
//! fake a device with a temp directory and a `default.csv` marker. The fake here
//! does the same job in memory and can also pull the stick out mid-swap, which
//! the legacy tests could not.
//!
//! Every case asserts what is on the device afterwards, not just what was
//! returned. A transaction that reports the right thing and leaves the wrong
//! thing on the drive is the failure that hurts somebody.

use qcm_config::ProfileFile;
use qcm_core::cancel::CancelSignal;
use qcm_core::clock::ManualClock;
use qcm_core::confirmation::{ConfirmationId, ConfirmationKind};
use qcm_core::devices::Devices;
use qcm_core::error::{
    ConfigError, ConfirmationError, DeviceError, ErrorCode, NameRejection, QcmError, QcmErrorDto,
    StorageError, StorageStage, TargetState,
};
use qcm_core::ports::storage::StorageDeviceId;
use qcm_testkit::{FakeBackupStore, FakeQuadStick, Fault};
use std::cell::Cell;

const OLD_PROFILE: &[u8] = b"an older racing profile\n";

type Service<'a> = Devices<&'a FakeQuadStick, FakeBackupStore, &'a ManualClock>;

struct Rig {
    fake: FakeQuadStick,
    clock: ManualClock,
    device: StorageDeviceId,
}

impl Rig {
    fn new() -> Self {
        let (fake, device) = FakeQuadStick::with_device();
        Self {
            fake,
            clock: ManualClock::new(),
            device,
        }
    }

    /// A stick that already holds `racing.csv`, so every install has something
    /// to back up and something to lose.
    fn with_existing() -> Self {
        let rig = Self::new();
        rig.fake.put_file(rig.device, "racing.csv", OLD_PROFILE);
        rig
    }

    fn devices(&self) -> Service<'_> {
        Devices::new(&self.fake, FakeBackupStore::new(), &self.clock)
    }

    fn file(&self, name: &str) -> Option<Vec<u8>> {
        self.fake.file(self.device, name)
    }

    fn strays(&self) -> Vec<String> {
        self.fake.stray_temp_names(self.device)
    }
}

fn profile(name: &str) -> ProfileFile {
    ProfileFile::load(&format!(
        "Profile Name,,L\n{name}\nOutputs,Function,usb\nx,normal,lip\n"
    ))
}

/// Plan and run in one go, for the cases where the confirmation is not the
/// point. Panics if the profile needs one, which is what a caller wanting the
/// short path should get.
fn install(rig: &Rig, devices: &mut Service<'_>, file: &ProfileFile) -> InstallOutcome {
    let plan = match devices.plan_install(rig.device, file) {
        Ok(plan) => plan,
        Err(error) => return InstallOutcome::Refused(error),
    };
    assert!(
        !plan.needs_confirmation(),
        "this helper is for profiles that need no acknowledgement"
    );
    match devices.install(plan, None) {
        Ok(receipt) => InstallOutcome::Installed(Box::new(receipt)),
        Err(failure) => InstallOutcome::Failed(Box::new(failure)),
    }
}

enum InstallOutcome {
    /// Rejected before the device was reached.
    Refused(QcmError),
    Installed(Box<qcm_core::devices::InstallReceipt>),
    Failed(Box<qcm_core::devices::InstallFailure>),
}

impl InstallOutcome {
    fn installed(self) -> qcm_core::devices::InstallReceipt {
        match self {
            Self::Installed(receipt) => *receipt,
            Self::Refused(error) => panic!("refused: {error}"),
            Self::Failed(failure) => panic!("failed: {}", failure.error),
        }
    }

    fn failed(self) -> qcm_core::devices::InstallFailure {
        match self {
            Self::Failed(failure) => *failure,
            Self::Installed(_) => panic!("the install was supposed to fail"),
            Self::Refused(error) => panic!("refused before the device: {error}"),
        }
    }

    fn refused(self) -> QcmError {
        match self {
            Self::Refused(error) => error,
            Self::Installed(_) => panic!("the install was supposed to be refused"),
            Self::Failed(failure) => panic!("reached the device: {}", failure.error),
        }
    }
}

// ---------------------------------------------------------------- happy path

#[test]
fn a_clean_install_replaces_the_profile_and_backs_up_what_was_there() {
    let rig = Rig::with_existing();
    let mut devices = rig.devices();

    let receipt = install(&rig, &mut devices, &profile("racing.csv")).installed();

    assert!(receipt.confirmed_on_device);
    assert!(receipt.backup.is_some());
    assert_eq!(devices.backups().entries().len(), 1);
    assert_eq!(devices.backups().entries()[0].1, OLD_PROFILE);
    let installed = rig.file("racing.csv").expect("the new profile");
    assert_eq!(installed, receipt.bytes_written_for_test());
    assert!(rig.strays().is_empty());
    assert!(rig.file("default.csv").is_some(), "the marker is untouched");
}

#[test]
fn a_first_install_of_a_name_makes_no_backup() {
    let rig = Rig::new();
    let mut devices = rig.devices();

    let receipt = install(&rig, &mut devices, &profile("racing.csv")).installed();

    assert!(receipt.backup.is_none());
    assert!(devices.backups().is_empty());
    assert!(rig.file("racing.csv").is_some());
}

// Ported from `Install_adds_the_QMP_version_header_when_missing`.
#[test]
fn the_installed_file_carries_the_version_header() {
    let rig = Rig::new();
    let mut devices = rig.devices();

    install(&rig, &mut devices, &profile("racing.csv")).installed();

    let bytes = rig.file("racing.csv").expect("installed");
    let text = String::from_utf8(bytes).expect("utf-8");
    assert!(text.starts_with("QuadStick Configuration,Version 1.5"));
    let reloaded = ProfileFile::load(&text);
    assert!(reloaded.document.has_version_header);
    assert!(
        !reloaded
            .issues
            .iter()
            .any(|issue| issue.severity == qcm_config::Severity::Error)
    );
}

// Ported from `Install_does_not_mutate_the_open_file`. Normalization happens on
// a copy: the editor the user is looking at must not gain a header or a dirty
// flag because they pressed Install.
#[test]
fn installing_does_not_touch_the_profile_that_is_open() {
    let rig = Rig::new();
    let mut devices = rig.devices();
    let file = profile("racing.csv");
    assert!(!file.document.has_version_header);

    install(&rig, &mut devices, &file).installed();

    assert!(!file.document.has_version_header);
    assert!(!file.dirty());
}

#[test]
fn an_install_leaves_no_scratch_files_on_the_device() {
    let rig = Rig::with_existing();
    let mut devices = rig.devices();

    install(&rig, &mut devices, &profile("racing.csv")).installed();

    assert!(rig.strays().is_empty());
    assert!(
        !rig.fake
            .file_names(rig.device)
            .iter()
            .any(|name| name.contains(".qscm-"))
    );
}

#[test]
fn a_successful_install_drops_the_device_cache() {
    let rig = Rig::new();
    let mut devices = rig.devices();
    devices.list_devices().expect("discovery");
    assert!(devices.device_cache_is_fresh());

    install(&rig, &mut devices, &profile("racing.csv")).installed();

    assert!(!devices.device_cache_is_fresh());
}

// --------------------------------------------------------------- name rules

// Ported from `A_name_too_long_for_the_device_is_a_problem_and_never_installs`.
// Firmware 2373 keeps each root file name in a 31 character slot, so a longer
// name copies on fine, then cannot be opened, and the name after it in the
// device's own list prints as garbage too.
#[test]
fn a_name_too_long_for_the_device_never_reaches_it() {
    let rig = Rig::new();
    let mut devices = rig.devices();
    let too_long = format!("{}.csv", "a".repeat(28));

    let error = install(&rig, &mut devices, &profile(&too_long)).refused();

    assert_eq!(
        error,
        QcmError::Storage(StorageError::NameRejected {
            reason: NameRejection::TooLongForDevice
        })
    );
    assert_eq!(rig.fake.file_names(rig.device), vec!["default.csv"]);
}

// The other side of the boundary: 31 characters including ".csv" still installs.
#[test]
fn a_name_that_exactly_fills_the_device_slot_installs() {
    let rig = Rig::new();
    let mut devices = rig.devices();
    let exact = format!("{}.csv", "a".repeat(27));

    install(&rig, &mut devices, &profile(&exact)).installed();

    assert!(rig.file(&exact).is_some());
}

// Ported from `A_name_the_device_cannot_hold_is_a_problem_and_never_installs`.
// Windows resolves NUL and friends to a device whatever extension follows, so
// the write appears to work and the read back comes back empty.
#[test]
fn a_name_the_device_cannot_hold_is_refused_and_nothing_is_written() {
    for name in ["NUL.csv", "con.csv", "LPT1.csv", "my\u{0}game.csv"] {
        let rig = Rig::new();
        let mut devices = rig.devices();

        let error = install(&rig, &mut devices, &profile(name)).refused();

        assert!(
            matches!(
                error,
                QcmError::Config(ConfigError::HasBlockingProblems { .. })
                    | QcmError::Storage(StorageError::NameRejected { .. })
            ),
            "{name}: {error}"
        );
        assert_eq!(
            rig.fake.file_names(rig.device),
            vec!["default.csv"],
            "{name}"
        );
        assert!(rig.strays().is_empty(), "{name}");
    }
}

// Ported from `Install_refuses_profiles_with_errors`. A row past the device's
// 1023 character line buffer comes back as if it were the next row, so the
// damage reaches past that row and ends the mode early.
#[test]
fn a_profile_with_blocking_problems_never_reaches_the_device() {
    let rig = Rig::new();
    let mut devices = rig.devices();
    let overflow = format!(
        "Profile Name,,L\nracing.csv\nOutputs,Function,usb\nx,normal,lip,,,,,,,,{}\n",
        "c".repeat(1100)
    );
    let bad = ProfileFile::load(&overflow);

    let error = install(&rig, &mut devices, &bad).refused();

    assert!(matches!(
        error,
        QcmError::Config(ConfigError::HasBlockingProblems { .. })
    ));
    assert!(rig.file("racing.csv").is_none());
}

#[test]
fn a_profile_with_no_declared_name_is_refused() {
    let rig = Rig::new();
    let mut devices = rig.devices();
    let nameless = ProfileFile::load("Profile Name,,L\n\nOutputs,Function,usb\nx,normal,lip\n");

    let error = install(&rig, &mut devices, &nameless).refused();

    assert!(matches!(
        error,
        QcmError::Config(ConfigError::HasBlockingProblems { .. })
            | QcmError::Storage(StorageError::NameRejected {
                reason: NameRejection::Empty
            })
    ));
    assert_eq!(rig.fake.file_names(rig.device), vec!["default.csv"]);
}

// Ported from `Install_refuses_non_quadstick_folder`.
#[test]
fn a_folder_that_is_not_a_quadstick_is_refused_and_left_alone() {
    let fake = FakeQuadStick::new();
    let id = fake.plug_without_marker("BACKUP DRIVE");
    let clock = ManualClock::new();
    let mut devices: Service<'_> = Devices::new(&fake, FakeBackupStore::new(), &clock);

    let error = devices
        .plan_install(id, &profile("racing.csv"))
        .expect_err("not a QuadStick");

    assert_eq!(
        error,
        QcmError::Storage(StorageError::Device(DeviceError::NotQuadStick))
    );
    assert!(fake.file_names(id).is_empty());
}

// ------------------------------------------------------------ the two gates

// Ported from `Install_refuses_default_csv_without_confirmation_then_allows_with_it`.
#[test]
fn default_csv_needs_its_own_acknowledgement() {
    let rig = Rig::new();
    let mut devices = rig.devices();

    let plan = devices
        .plan_install(rig.device, &profile("default.csv"))
        .expect("planned");
    let required = plan.confirmation().expect("a gate").clone();
    assert_eq!(required.kind, ConfirmationKind::OverwriteDefaultCsv);

    let failure = devices.install(plan, None).expect_err("no acknowledgement");
    assert_eq!(
        failure.error,
        QcmError::Confirmation(ConfirmationError::Missing {
            kind: ConfirmationKind::OverwriteDefaultCsv
        })
    );
    assert_eq!(failure.target, TargetState::Unchanged);
    assert!(devices.backups().is_empty(), "nothing was copied either");

    let plan = devices
        .plan_install(rig.device, &profile("default.csv"))
        .expect("planned");
    let id = plan.confirmation().expect("a gate").id;
    let receipt = devices.install(plan, Some(id)).expect("confirmed");
    assert_eq!(receipt.target.as_str(), "default.csv");
    let _ = required;
}

// Ported from `Install_refuses_prefs_csv_without_confirmation_then_allows_with_it`.
// prefs.csv is the device's own settings, so writing it changes every profile at
// once. The gate lives in the core so no caller can write one by accident.
#[test]
fn prefs_csv_needs_its_own_acknowledgement() {
    let rig = Rig::new();
    let mut devices = rig.devices();

    let plan = devices
        .plan_install(rig.device, &profile("prefs.csv"))
        .expect("planned");
    assert_eq!(
        plan.confirmation().expect("a gate").kind,
        ConfirmationKind::OverwriteDevicePreferences
    );

    let failure = devices.install(plan, None).expect_err("no acknowledgement");
    assert_eq!(failure.target, TargetState::Unchanged);
    assert!(rig.file("prefs.csv").is_none());
    assert!(rig.strays().is_empty());
    assert!(devices.backups().is_empty());

    let plan = devices
        .plan_install(rig.device, &profile("prefs.csv"))
        .expect("planned");
    let id = plan.confirmation().expect("a gate").id;
    devices.install(plan, Some(id)).expect("confirmed");
    assert!(rig.file("prefs.csv").is_some());
}

// Ported from `The_prefs_and_default_confirmations_do_not_unlock_each_other`.
#[test]
fn one_acknowledgement_never_unlocks_the_other_file() {
    let rig = Rig::new();
    let mut devices = rig.devices();

    let prefs = devices
        .plan_install(rig.device, &profile("prefs.csv"))
        .expect("planned");
    let prefs_id = prefs.confirmation().expect("a gate").id;

    let fallback = devices
        .plan_install(rig.device, &profile("default.csv"))
        .expect("planned");
    let failure = devices
        .install(fallback, Some(prefs_id))
        .expect_err("the wrong acknowledgement");

    assert_eq!(
        failure.error,
        QcmError::Confirmation(ConfirmationError::Mismatch)
    );
    assert!(rig.file("default.csv").is_some(), "the marker still stands");
    assert_eq!(
        rig.file("default.csv").as_deref(),
        Some(&b"QuadStick Configuration File,\n"[..]),
        "and it was not replaced"
    );
    // And the prefs acknowledgement is still good for its own write.
    let _ = devices
        .install(prefs, Some(prefs_id))
        .expect("its own file");
}

#[test]
fn an_acknowledgement_cannot_be_spent_twice() {
    let rig = Rig::new();
    let mut devices = rig.devices();
    let first = devices
        .plan_install(rig.device, &profile("default.csv"))
        .expect("planned");
    let id = first.confirmation().expect("a gate").id;
    devices.install(first, Some(id)).expect("confirmed");

    let second = devices
        .plan_install(rig.device, &profile("default.csv"))
        .expect("planned");
    let failure = devices
        .install(second, Some(id))
        .expect_err("already spent");

    assert_eq!(
        failure.error,
        QcmError::Confirmation(ConfirmationError::Unknown)
    );
}

#[test]
fn an_acknowledgement_left_open_over_lunch_times_out() {
    let rig = Rig::new();
    let mut devices = rig.devices();
    let plan = devices
        .plan_install(rig.device, &profile("default.csv"))
        .expect("planned");
    let id = plan.confirmation().expect("a gate").id;

    rig.clock
        .advance(qcm_core::DEFAULT_CONFIRMATION_TTL + std::time::Duration::from_secs(1));

    let failure = devices.install(plan, Some(id)).expect_err("timed out");
    assert_eq!(
        failure.error,
        QcmError::Confirmation(ConfirmationError::Expired)
    );
}

#[test]
fn an_invented_acknowledgement_is_refused() {
    let rig = Rig::new();
    let mut devices = rig.devices();
    let plan = devices
        .plan_install(rig.device, &profile("prefs.csv"))
        .expect("planned");

    let failure = devices
        .install(plan, Some(ConfirmationId::from_raw(4242)))
        .expect_err("not on record");

    assert_eq!(
        failure.error,
        QcmError::Confirmation(ConfirmationError::Unknown)
    );
    assert!(rig.file("prefs.csv").is_none());
}

// Ported from `Install_of_a_normal_profile_needs_no_prefs_confirmation`.
#[test]
fn an_ordinary_profile_needs_no_acknowledgement_at_all() {
    let rig = Rig::new();
    let mut devices = rig.devices();

    let plan = devices
        .plan_install(rig.device, &profile("racing.csv"))
        .expect("planned");

    assert!(!plan.needs_confirmation());
    devices.install(plan, None).expect("installed");
}

// ---------------------------------------------------- faults, stage by stage

// The whole failure matrix in one place: every stage before the swap leaves the
// old profile whole and provably untouched.
#[test]
fn every_stage_before_the_swap_leaves_the_profile_untouched() {
    let stages = [
        StorageStage::Revalidate,
        StorageStage::ReadFile,
        StorageStage::TempCreate,
        StorageStage::TempWrite,
        StorageStage::TempFlush,
        StorageStage::TempReadBack,
        StorageStage::ReplaceBeforeDisplace,
    ];
    for stage in stages {
        let rig = Rig::with_existing();
        let mut devices = rig.devices();
        // Armed after planning, because planning revalidates too and a fault
        // spent there would be testing the wrong call.
        let plan = devices
            .plan_install(rig.device, &profile("racing.csv"))
            .expect("planned");
        rig.fake.fail_at(stage, Fault::Io);

        let failure = devices.install(plan, None).expect_err("the injected fault");

        assert_eq!(
            failure.target,
            TargetState::Unchanged,
            "{stage:?} is before the swap"
        );
        assert_eq!(
            rig.file("racing.csv").as_deref(),
            Some(OLD_PROFILE),
            "{stage:?} changed the profile"
        );
        assert!(rig.strays().is_empty(), "{stage:?} left a temp behind");
    }
}

// Ported from the legacy read-only directory test. A stick that has gone
// read-only fails before anything destructive.
#[test]
fn a_read_only_drive_fails_before_the_destructive_stage() {
    let rig = Rig::with_existing();
    rig.fake.set_read_only(rig.device, true);
    let mut devices = rig.devices();

    let failure = install(&rig, &mut devices, &profile("racing.csv")).failed();

    assert_eq!(
        failure.error,
        QcmError::Storage(StorageError::ReadOnly {
            stage: StorageStage::TempCreate
        })
    );
    assert_eq!(failure.target, TargetState::Unchanged);
    assert_eq!(rig.file("racing.csv").as_deref(), Some(OLD_PROFILE));
    assert!(rig.strays().is_empty());
}

#[test]
fn a_full_volume_fails_before_the_profile_is_touched() {
    let rig = Rig::with_existing();
    rig.fake.set_capacity(rig.device, Some(40));
    let mut devices = rig.devices();

    let failure = install(&rig, &mut devices, &profile("racing.csv")).failed();

    assert!(matches!(
        failure.error,
        QcmError::Storage(StorageError::Full {
            stage: StorageStage::TempWrite,
            target: TargetState::Unchanged
        })
    ));
    assert_eq!(rig.file("racing.csv").as_deref(), Some(OLD_PROFILE));
    assert!(rig.strays().is_empty());
}

// No backup, no install. The copy comes off the device before anything moves,
// so a failure here aborts while the old profile is still whole.
#[test]
fn a_backup_that_fails_stops_the_install_before_anything_moves() {
    let rig = Rig::with_existing();
    let mut devices = rig.devices();
    devices.backups().fail_next();

    let failure = install(&rig, &mut devices, &profile("racing.csv")).failed();

    assert!(matches!(
        failure.error,
        QcmError::Storage(StorageError::BackupFailed { .. })
    ));
    assert_eq!(failure.stage, StorageStage::Backup);
    assert_eq!(failure.target, TargetState::Unchanged);
    assert!(failure.backup.is_none());
    assert_eq!(rig.file("racing.csv").as_deref(), Some(OLD_PROFILE));
    assert!(rig.strays().is_empty());
}

// The temp file is compared byte for byte before the target is touched. This is
// the read-back the whole transaction rests on.
#[test]
fn a_temp_that_does_not_read_back_the_same_leaves_the_target_alone() {
    let rig = Rig::with_existing();
    let mut devices = rig.devices();
    rig.fake.fail_at(StorageStage::TempReadBack, Fault::Io);

    let failure = install(&rig, &mut devices, &profile("racing.csv")).failed();

    assert_eq!(failure.stage, StorageStage::TempReadBack);
    assert_eq!(failure.target, TargetState::Unchanged);
    assert_eq!(rig.file("racing.csv").as_deref(), Some(OLD_PROFILE));
    assert!(rig.strays().is_empty(), "the rejected temp was cleaned up");
}

// Ported from `Install_move_failure_with_no_backup_still_cleans_up_tmp`: a swap
// that throws with no backup available, which the restore path cannot help, is
// exactly the case the cleanup must still catch.
#[test]
fn a_swap_that_fails_with_no_backup_still_cleans_up_the_temp() {
    let rig = Rig::new();
    let mut devices = rig.devices();
    rig.fake
        .fail_at(StorageStage::ReplaceBeforeDisplace, Fault::Io);

    let failure = install(&rig, &mut devices, &profile("racing.csv")).failed();

    assert_eq!(failure.target, TargetState::Unchanged);
    assert!(failure.backup.is_none());
    assert!(rig.strays().is_empty());
    assert!(rig.file("racing.csv").is_none());
}

// The one the restore path exists for. The old directory entry is gone, the
// drive is still answering, so the backed-up bytes go back the safe way: beside
// the target then into place, never a plain copy that a full volume could cut
// short into a profile the device would load without complaining.
#[test]
fn a_failure_after_the_old_entry_is_gone_restores_it_and_says_so() {
    let rig = Rig::with_existing();
    let mut devices = rig.devices();
    rig.fake
        .fail_at(StorageStage::ReplaceAfterDisplace, Fault::Io);

    let failure = install(&rig, &mut devices, &profile("racing.csv")).failed();

    assert_eq!(failure.target, TargetState::Restored);
    assert_eq!(failure.stage, StorageStage::ReplaceAfterDisplace);
    assert!(failure.backup.is_some());
    assert_eq!(rig.file("racing.csv").as_deref(), Some(OLD_PROFILE));
    assert!(rig.strays().is_empty());
}

// Two failures deep. What is under the name cannot be proven, so the app says
// exactly that and points at the rescue copy rather than guessing.
#[test]
fn a_failed_restore_says_uncertain_and_names_the_backup() {
    let rig = Rig::with_existing();
    let mut devices = rig.devices();
    rig.fake
        .fail_at(StorageStage::ReplaceAfterDisplace, Fault::Io);
    rig.fake.fail_at(StorageStage::RestoreWrite, Fault::Io);

    let failure = install(&rig, &mut devices, &profile("racing.csv")).failed();

    assert_eq!(failure.target, TargetState::Uncertain);
    assert!(matches!(
        failure.error,
        QcmError::Storage(StorageError::RestoreFailed { .. })
    ));
    let backup = failure.backup.expect("the way out of this one");
    assert!(backup.as_str().ends_with("racing.csv"));
}

// A first install has nothing to put back, so Missing is the honest answer: the
// name the user asked for is not there.
#[test]
fn a_swap_that_breaks_with_nothing_to_restore_reports_missing() {
    let rig = Rig::new();
    let mut devices = rig.devices();
    rig.fake
        .fail_at(StorageStage::ReplaceAfterDisplace, Fault::Io);

    let failure = install(&rig, &mut devices, &profile("racing.csv")).failed();

    assert_eq!(failure.target, TargetState::Missing);
    assert!(failure.backup.is_none());
    assert!(rig.file("racing.csv").is_none());
    assert!(rig.strays().is_empty());
}

#[test]
fn a_stick_pulled_during_the_temp_write_leaves_the_profile_alone() {
    let rig = Rig::with_existing();
    let mut devices = rig.devices();
    rig.fake.fail_at(StorageStage::TempWrite, Fault::Unplug);

    let failure = install(&rig, &mut devices, &profile("racing.csv")).failed();

    assert_eq!(
        failure.error,
        QcmError::Storage(StorageError::RemovedDuringOperation {
            stage: StorageStage::TempWrite,
            target: TargetState::Unchanged
        })
    );
    assert_eq!(failure.target, TargetState::Unchanged);
    assert_eq!(rig.file("racing.csv").as_deref(), Some(OLD_PROFILE));
}

// The comment this whole branch exists for. A mount point going away does not
// prove the old directory entry survived: the replace may already have removed
// it before the volume disappeared. So the app says what is certainly true,
// keeps the backup, and asks the user to look, rather than promising them
// nothing happened.
#[test]
fn a_stick_pulled_during_the_swap_never_claims_nothing_happened() {
    let rig = Rig::with_existing();
    let mut devices = rig.devices();
    rig.fake
        .fail_at(StorageStage::ReplaceAfterDisplace, Fault::Unplug);

    let failure = install(&rig, &mut devices, &profile("racing.csv")).failed();

    assert_eq!(failure.target, TargetState::Uncertain);
    assert_ne!(failure.target, TargetState::Unchanged);
    assert!(failure.backup.is_some());
    let dto = QcmErrorDto::new(&failure.error, Some(failure.operation));
    assert_eq!(dto.target_state.as_deref(), Some("uncertain"));
    assert_eq!(dto.code, ErrorCode::DeviceRemovedDuringWrite.as_str());
}

// A stick pulled and pushed back between the dialog and the button comes back at
// a new generation, and the plan made against the old one is refused.
#[test]
fn a_device_replugged_between_planning_and_writing_is_refused() {
    let rig = Rig::with_existing();
    let mut devices = rig.devices();
    let plan = devices
        .plan_install(rig.device, &profile("racing.csv"))
        .expect("planned");

    rig.fake.unplug(rig.device);
    rig.fake.replug(rig.device);

    let failure = devices.install(plan, None).expect_err("a different mount");

    assert!(matches!(
        failure.error,
        QcmError::Storage(StorageError::Device(DeviceError::Stale { .. }))
    ));
    assert_eq!(failure.target, TargetState::Unchanged);
    assert_eq!(rig.file("racing.csv").as_deref(), Some(OLD_PROFILE));
}

// A user reformatting a stick with the window open.
#[test]
fn a_marker_that_vanishes_between_planning_and_writing_stops_the_write() {
    let rig = Rig::with_existing();
    let mut devices = rig.devices();
    let plan = devices
        .plan_install(rig.device, &profile("racing.csv"))
        .expect("planned");

    rig.fake.remove_marker(rig.device);

    let failure = devices.install(plan, None).expect_err("not a QuadStick");

    assert_eq!(
        failure.error,
        QcmError::Storage(StorageError::Device(DeviceError::NotQuadStick))
    );
    assert_eq!(rig.file("racing.csv").as_deref(), Some(OLD_PROFILE));
}

#[test]
fn a_one_shot_fault_lets_the_retry_through() {
    let rig = Rig::with_existing();
    let mut devices = rig.devices();
    rig.fake.fail_at(StorageStage::TempWrite, Fault::Io);

    install(&rig, &mut devices, &profile("racing.csv")).failed();
    let receipt = install(&rig, &mut devices, &profile("racing.csv")).installed();

    assert_eq!(
        rig.file("racing.csv"),
        Some(receipt.bytes_written_for_test())
    );
    assert!(rig.strays().is_empty());
}

// --------------------------------------------------------------- cancelling

/// A signal that flips once it has been read `after` times, so a test can put
/// the stop request exactly where it wants it.
struct CancelsAfter {
    reads: Cell<usize>,
    after: usize,
}

impl CancelsAfter {
    const fn new(after: usize) -> Self {
        Self {
            reads: Cell::new(0),
            after,
        }
    }

    fn reads(&self) -> usize {
        self.reads.get()
    }
}

impl CancelSignal for CancelsAfter {
    fn cancelled(&self) -> bool {
        let seen = self.reads.get();
        self.reads.set(seen + 1);
        seen >= self.after
    }
}

#[test]
fn a_stop_before_anything_moves_is_honoured() {
    let rig = Rig::with_existing();
    let mut devices = rig.devices();
    let plan = devices
        .plan_install(rig.device, &profile("racing.csv"))
        .expect("planned");

    let failure = devices
        .install_with_cancel(plan, None, &CancelsAfter::new(0))
        .expect_err("stopped");

    assert_eq!(failure.error, QcmError::Cancelled);
    assert_eq!(failure.target, TargetState::Unchanged);
    assert_eq!(rig.file("racing.csv").as_deref(), Some(OLD_PROFILE));
    assert!(devices.backups().is_empty());
}

// The rule the swap window exists for. A stop that arrives once the temp file is
// about to be written is not honoured, because a cancel inside the replace would
// leave a disabled user with nothing under that name.
#[test]
fn a_stop_inside_the_swap_window_is_not_honoured() {
    let rig = Rig::with_existing();
    let mut devices = rig.devices();
    let plan = devices
        .plan_install(rig.device, &profile("racing.csv"))
        .expect("planned");
    let cancel = CancelsAfter::new(3);

    let receipt = devices
        .install_with_cancel(plan, None, &cancel)
        .expect("the swap runs to its end");

    assert_eq!(
        cancel.reads(),
        3,
        "the signal is read three times and no more"
    );
    assert_eq!(
        rig.file("racing.csv"),
        Some(receipt.bytes_written_for_test())
    );
    assert!(rig.strays().is_empty());
}

// ------------------------------------------------------------- determinism

#[test]
fn the_transaction_is_deterministic() {
    let run = || {
        let rig = Rig::with_existing();
        let mut devices = rig.devices();
        rig.fake
            .fail_at(StorageStage::ReplaceAfterDisplace, Fault::Io);
        let failure = install(&rig, &mut devices, &profile("racing.csv")).failed();
        (
            failure.target,
            failure.stage,
            failure.stages.clone(),
            rig.fake.file_names(rig.device),
            devices.backups().entries(),
        )
    };
    assert_eq!(run(), run());
}

/// The stage receipt has to describe what really ran, so a support conversation
/// can start from what the app did rather than from what it meant to do.
#[test]
fn the_stage_receipt_lists_what_actually_ran() {
    let rig = Rig::with_existing();
    let mut devices = rig.devices();

    let receipt = install(&rig, &mut devices, &profile("racing.csv")).installed();

    assert_eq!(
        receipt.stages,
        vec![
            StorageStage::Revalidate,
            StorageStage::ReadFile,
            StorageStage::Backup,
            StorageStage::TempWrite,
            StorageStage::TempReadBack,
            StorageStage::ReplaceAfterDisplace,
        ]
    );
}

#[test]
fn a_first_install_records_no_backup_stage() {
    let rig = Rig::new();
    let mut devices = rig.devices();

    let receipt = install(&rig, &mut devices, &profile("racing.csv")).installed();

    assert!(!receipt.stages.contains(&StorageStage::Backup));
}

/// Test-only helper: the bytes the receipt says were written.
trait ReceiptBytes {
    fn bytes_written_for_test(&self) -> Vec<u8>;
}

impl ReceiptBytes for qcm_core::devices::InstallReceipt {
    fn bytes_written_for_test(&self) -> Vec<u8> {
        // Rebuilt from the same normalization the plan used, so a change to
        // either side breaks the test rather than passing quietly.
        let mut file = profile(self.target.as_str());
        file.normalize_for_device_csv();
        file.to_csv_text().into_bytes()
    }
}
