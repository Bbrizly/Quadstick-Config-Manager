//! The device-safety tests that used to need a stick and a steady hand.
//!
//! `rehearse_install` below is not the install transaction. TASK-024 owns that.
//! It is the shortest sequence that walks the whole port, so these tests prove
//! the port has the operations the real transaction needs and the fake can fail
//! at every stage of it.

use qcm_core::error::{DeviceError, NameRejection, StorageError, StorageStage, TargetState};
use qcm_core::ports::storage::{
    BackupStore, CommitFailure, DeviceFileName, DeviceGeneration, DeviceStorage,
    SafeDeviceFileName, StorageDeviceId,
};
use qcm_testkit::{FakeBackupStore, FakeQuadStick, Fault};

const PROFILE: &[u8] = b"QuadStick Configuration,Version 1.5\nracing.csv\n";
const OLD_PROFILE: &[u8] = b"old profile\n";

fn safe(name: &str) -> SafeDeviceFileName {
    SafeDeviceFileName::new(name).expect("a name this app may write")
}

fn plain(name: &str) -> DeviceFileName {
    DeviceFileName::new(name).expect("a plain device file name")
}

/// What the install transaction has to do, in the order `Device.Install` does
/// it. Reports what is on the device when it fails, which is the only thing the
/// user actually needs to know.
#[derive(Debug, PartialEq, Eq)]
struct Rehearsal {
    error: StorageError,
    target: TargetState,
    backed_up: bool,
}

fn rehearse_install(
    device: &FakeQuadStick,
    backups: &FakeBackupStore,
    id: StorageDeviceId,
    target: &SafeDeviceFileName,
    bytes: &[u8],
) -> Result<(), Rehearsal> {
    let fail = |error: StorageError, backed_up: bool| Rehearsal {
        target: error.target_state().unwrap_or(TargetState::Unchanged),
        error,
        backed_up,
    };

    let probe = device.revalidate(id).map_err(|error| fail(error, false))?;
    let generation = probe.generation;
    let name = target.as_device_name().clone();

    let existing = match device.read_file(id, generation, &name) {
        Ok(bytes) => Some(bytes),
        Err(StorageError::FileNotFound { .. }) => None,
        Err(error) => return Err(fail(error, false)),
    };

    // Backup before anything on the device moves. A failure here has to abort
    // while the old profile is still whole.
    let mut backed_up = false;
    if let Some(old) = &existing {
        backups
            .store(&name, old)
            .map_err(|error| fail(error, false))?;
        backed_up = true;
    }

    let staged = device
        .stage_write(id, generation, target, bytes)
        .map_err(|error| fail(error, backed_up))?;

    if let Err(error) = device.verify_staged(&staged, bytes) {
        let _ = device.discard_staged(staged);
        return Err(fail(error, backed_up));
    }

    match device.commit_staged(staged) {
        Ok(()) => Ok(()),
        Err(CommitFailure { error, staged }) => {
            if let Some(staged) = staged {
                // Never leave a stray temp beside a profile.
                let _ = device.discard_staged(staged);
            }
            let reported = error.target_state().unwrap_or(TargetState::Uncertain);
            if reported != TargetState::Missing {
                return Err(fail(error, backed_up));
            }
            // The old entry is gone and we can still see the drive, so put the
            // backed-up bytes back the safe way.
            let Some(old) = existing else {
                return Err(Rehearsal {
                    error,
                    target: TargetState::Missing,
                    backed_up,
                });
            };
            match device.restore_file(id, generation, &name, &old) {
                Ok(()) => Err(Rehearsal {
                    error,
                    target: TargetState::Restored,
                    backed_up,
                }),
                Err(restore) => Err(Rehearsal {
                    error: restore,
                    target: TargetState::Uncertain,
                    backed_up,
                }),
            }
        }
    }
}

fn device_with_existing_profile() -> (FakeQuadStick, FakeBackupStore, StorageDeviceId) {
    let (device, id) = FakeQuadStick::with_device();
    device.put_file(id, "racing.csv", OLD_PROFILE);
    (device, FakeBackupStore::new(), id)
}

fn generation(device: &FakeQuadStick, id: StorageDeviceId) -> DeviceGeneration {
    device.generation(id).expect("a mounted device")
}

#[test]
fn a_clean_install_replaces_the_profile_and_leaves_no_scratch_behind() {
    let (device, backups, id) = device_with_existing_profile();

    rehearse_install(&device, &backups, id, &safe("racing.csv"), PROFILE).expect("install");

    assert_eq!(device.file(id, "racing.csv").as_deref(), Some(PROFILE));
    assert!(device.stray_temp_names(id).is_empty());
    assert_eq!(backups.entries().len(), 1);
    assert_eq!(backups.entries()[0].1, OLD_PROFILE);
    assert!(device.file(id, "default.csv").is_some(), "marker untouched");
}

#[test]
fn a_first_install_of_a_name_makes_no_backup() {
    let (device, id) = FakeQuadStick::with_device();
    let backups = FakeBackupStore::new();

    rehearse_install(&device, &backups, id, &safe("racing.csv"), PROFILE).expect("install");

    assert!(backups.is_empty());
    assert_eq!(device.file(id, "racing.csv").as_deref(), Some(PROFILE));
}

// The whole point of the fake: every stage in the testing strategy's fault list
// can be failed on its own, and each one reports the truth about the target.
#[test]
fn every_stage_can_fail_on_its_own() {
    let stages_before_the_swap = [
        StorageStage::Revalidate,
        StorageStage::ReadFile,
        StorageStage::TempCreate,
        StorageStage::TempWrite,
        StorageStage::TempFlush,
        StorageStage::TempReadBack,
        StorageStage::ReplaceBeforeDisplace,
    ];
    for stage in stages_before_the_swap {
        let (device, backups, id) = device_with_existing_profile();
        device.fail_at(stage, Fault::Io);

        let outcome = rehearse_install(&device, &backups, id, &safe("racing.csv"), PROFILE)
            .expect_err("the injected fault must be reported");

        assert_eq!(
            outcome.target,
            TargetState::Unchanged,
            "{stage:?} is before the swap, so the profile is provably untouched"
        );
        assert_eq!(
            device.file(id, "racing.csv").as_deref(),
            Some(OLD_PROFILE),
            "{stage:?}"
        );
        assert!(
            device.stray_temp_names(id).is_empty(),
            "{stage:?} left a temp behind"
        );
    }
}

#[test]
fn a_failure_after_the_old_entry_is_gone_restores_it_and_says_so() {
    let (device, backups, id) = device_with_existing_profile();
    device.fail_at(StorageStage::ReplaceAfterDisplace, Fault::Io);

    let outcome = rehearse_install(&device, &backups, id, &safe("racing.csv"), PROFILE)
        .expect_err("the swap failed");

    assert_eq!(outcome.target, TargetState::Restored);
    assert!(outcome.backed_up);
    assert_eq!(device.file(id, "racing.csv").as_deref(), Some(OLD_PROFILE));
    assert!(device.stray_temp_names(id).is_empty());
}

#[test]
fn a_failed_restore_says_uncertain_rather_than_guessing() {
    let (device, backups, id) = device_with_existing_profile();
    device.fail_at(StorageStage::ReplaceAfterDisplace, Fault::Io);
    device.fail_at(StorageStage::RestoreWrite, Fault::Io);

    let outcome = rehearse_install(&device, &backups, id, &safe("racing.csv"), PROFILE)
        .expect_err("the swap failed");

    assert_eq!(outcome.target, TargetState::Uncertain);
    assert!(outcome.backed_up, "the backup is the way out of this one");
}

#[test]
fn a_stick_pulled_during_the_temp_write_leaves_the_profile_alone() {
    let (device, backups, id) = device_with_existing_profile();
    device.fail_at(StorageStage::TempWrite, Fault::Unplug);

    let outcome = rehearse_install(&device, &backups, id, &safe("racing.csv"), PROFILE)
        .expect_err("the stick was pulled");

    assert!(matches!(
        outcome.error,
        StorageError::RemovedDuringOperation {
            stage: StorageStage::TempWrite,
            target: TargetState::Unchanged,
        }
    ));
    assert_eq!(outcome.target, TargetState::Unchanged);
}

// The mount going away does not prove the old directory entry survived. A real
// adapter cannot look, so it may not say "nothing happened".
#[test]
fn a_stick_pulled_during_the_swap_never_claims_nothing_happened() {
    let (device, backups, id) = device_with_existing_profile();
    device.fail_at(StorageStage::ReplaceAfterDisplace, Fault::Unplug);

    let outcome = rehearse_install(&device, &backups, id, &safe("racing.csv"), PROFILE)
        .expect_err("the stick was pulled");

    assert_eq!(outcome.target, TargetState::Uncertain);
    assert!(outcome.backed_up);
}

#[test]
fn a_full_volume_fails_before_the_profile_is_touched() {
    let (device, backups, id) = device_with_existing_profile();
    device.set_capacity(id, Some(40));

    let outcome =
        rehearse_install(&device, &backups, id, &safe("racing.csv"), PROFILE).expect_err("no room");

    assert!(matches!(
        outcome.error,
        StorageError::Full {
            stage: StorageStage::TempWrite,
            target: TargetState::Unchanged,
        }
    ));
    assert_eq!(device.file(id, "racing.csv").as_deref(), Some(OLD_PROFILE));
    assert!(device.stray_temp_names(id).is_empty());
}

#[test]
fn a_volume_that_will_not_report_its_size_is_not_treated_as_empty() {
    let (device, backups, id) = device_with_existing_profile();
    device.set_capacity(id, None);

    rehearse_install(&device, &backups, id, &safe("racing.csv"), PROFILE).expect("install");

    let probe = device.revalidate(id).expect("mounted");
    assert_eq!(probe.capabilities.free_bytes, None);
}

#[test]
fn a_read_only_drive_fails_before_the_destructive_stage() {
    let (device, backups, id) = device_with_existing_profile();
    device.set_read_only(id, true);

    let outcome = rehearse_install(&device, &backups, id, &safe("racing.csv"), PROFILE)
        .expect_err("read only");

    assert!(matches!(
        outcome.error,
        StorageError::ReadOnly {
            stage: StorageStage::TempCreate
        }
    ));
    assert_eq!(device.file(id, "racing.csv").as_deref(), Some(OLD_PROFILE));
    let probe = device.revalidate(id).expect("mounted");
    assert!(!probe.capabilities.writable);
}

#[test]
fn a_backup_that_fails_stops_the_install_before_anything_moves() {
    let (device, backups, id) = device_with_existing_profile();
    backups.fail_next();

    let outcome = rehearse_install(&device, &backups, id, &safe("racing.csv"), PROFILE)
        .expect_err("no backup, no install");

    assert!(matches!(outcome.error, StorageError::BackupFailed { .. }));
    assert!(!outcome.backed_up);
    assert_eq!(device.file(id, "racing.csv").as_deref(), Some(OLD_PROFILE));
    assert!(device.stray_temp_names(id).is_empty());
}

#[test]
fn a_temp_that_does_not_read_back_the_same_leaves_the_target_alone() {
    let (device, backups, id) = device_with_existing_profile();
    let probe = device.revalidate(id).expect("mounted");
    let staged = device
        .stage_write(id, probe.generation, &safe("racing.csv"), PROFILE)
        .expect("staged");

    assert_eq!(
        device.verify_staged(&staged, b"different bytes"),
        Err(StorageError::VerifyFailed)
    );
    assert_eq!(device.file(id, "racing.csv").as_deref(), Some(OLD_PROFILE));

    device.discard_staged(staged).expect("cleanup");
    assert!(device.stray_temp_names(id).is_empty());
    let _ = backups;
}

#[test]
fn a_marker_that_is_gone_stops_every_write() {
    let (device, backups, id) = device_with_existing_profile();
    device.remove_marker(id);

    let outcome = rehearse_install(&device, &backups, id, &safe("racing.csv"), PROFILE)
        .expect_err("not a QuadStick");

    assert_eq!(
        outcome.error,
        StorageError::Device(DeviceError::NotQuadStick)
    );
    assert_eq!(device.file(id, "racing.csv").as_deref(), Some(OLD_PROFILE));
}

// Discovery proving the marker once is not enough. A user can reformat a stick
// with the app open, so every scoped call checks again.
#[test]
fn a_marker_that_vanishes_mid_session_stops_the_scoped_calls_too() {
    let (device, id) = FakeQuadStick::with_device();
    device.put_file(id, "racing.csv", OLD_PROFILE);
    let now = generation(&device, id);
    device.remove_marker(id);

    let gone = StorageError::Device(DeviceError::NotQuadStick);
    assert_eq!(device.list_files(id, now).err(), Some(gone.clone()));
    assert_eq!(
        device
            .stage_write(id, now, &safe("racing.csv"), PROFILE)
            .err(),
        Some(gone.clone())
    );
    assert_eq!(
        device.read_file(id, now, &plain("racing.csv")).err(),
        Some(gone.clone())
    );
    assert_eq!(
        device.delete_file(id, now, &plain("racing.csv")).err(),
        Some(gone)
    );
    assert_eq!(device.file(id, "racing.csv").as_deref(), Some(OLD_PROFILE));
}

#[test]
fn a_folder_that_never_had_a_marker_is_not_a_candidate() {
    let device = FakeQuadStick::new();
    let id = device.plug_without_marker("BACKUP DRIVE");

    assert!(device.discover().expect("discovery").is_empty());
    assert_eq!(
        device.revalidate(id),
        Err(StorageError::Device(DeviceError::NotQuadStick))
    );
}

#[test]
fn a_stale_generation_is_refused_rather_than_written_to() {
    let (device, id) = FakeQuadStick::with_device();
    let stale = generation(&device, id);
    device.unplug(id);
    device.replug(id);

    assert_ne!(generation(&device, id), stale);
    assert_eq!(
        device.stage_write(id, stale, &safe("racing.csv"), PROFILE),
        Err(StorageError::Device(DeviceError::Stale {
            expected: stale,
            actual: generation(&device, id),
        }))
    );
    assert!(device.stray_temp_names(id).is_empty());
}

#[test]
fn an_unplugged_device_is_not_found_rather_than_written_to() {
    let (device, id) = FakeQuadStick::with_device();
    let known = generation(&device, id);
    device.unplug(id);

    assert_eq!(
        device.revalidate(id),
        Err(StorageError::Device(DeviceError::NotFound))
    );
    assert_eq!(
        device.stage_write(id, known, &safe("racing.csv"), PROFILE),
        Err(StorageError::Device(DeviceError::NotFound))
    );
    assert!(device.discover().expect("discovery").is_empty());
}

#[test]
fn two_devices_are_told_apart() {
    let device = FakeQuadStick::new();
    let first = device.plug("QUADSTICK");
    let second = device.plug("QUADSTICK");
    device.put_file(first, "racing.csv", b"first\n");

    let found = device.discover().expect("discovery");
    assert_eq!(found.len(), 2);
    assert_ne!(found[0].id, found[1].id);
    assert_ne!(found[0].generation, found[1].generation);
    assert!(device.file(second, "racing.csv").is_none());
}

#[test]
fn the_file_list_counts_what_it_cannot_name_instead_of_hiding_it() {
    let (device, id) = FakeQuadStick::with_device();
    device.put_file(id, "Racing.csv", b"a\n");
    device.put_file(id, "._Racing.csv", b"sidecar\n");
    device.put_file(id, "notes.txt", b"b\n");
    device.put_file(id, "bad\u{0}name.csv", b"c\n");

    let listing = device
        .list_files(id, generation(&device, id))
        .expect("listing");

    assert_eq!(listing.unnameable, 1);
    let names: Vec<&str> = listing
        .files
        .iter()
        .map(|entry| entry.name.as_str())
        .collect();
    // The fake lists in one stable order. Selection order is the device
    // library's rule, not the port's, and TASK-025 ports it.
    assert_eq!(
        names,
        vec!["._Racing.csv", "Racing.csv", "default.csv", "notes.txt"]
    );
    let profiles: Vec<&str> = listing
        .files
        .iter()
        .filter(|entry| entry.name.is_profile())
        .map(|entry| entry.name.as_str())
        .collect();
    assert_eq!(profiles, vec!["Racing.csv", "default.csv"]);
}

#[test]
fn delete_refuses_the_device_own_files_and_keeps_them() {
    let (device, id) = FakeQuadStick::with_device();
    device.put_file(id, "prefs.csv", b"settings\n");
    let now = generation(&device, id);

    for protected in ["default.csv", "prefs.csv"] {
        assert!(matches!(
            device.delete_file(id, now, &plain(protected)),
            Err(StorageError::ProtectedFile { .. })
        ));
        assert!(device.file(id, protected).is_some());
    }
}

#[test]
fn delete_removes_only_the_named_profile() {
    let (device, id) = FakeQuadStick::with_device();
    device.put_file(id, "racing.csv", b"a\n");
    device.put_file(id, "apex.csv", b"b\n");
    let now = generation(&device, id);

    device
        .delete_file(id, now, &plain("racing.csv"))
        .expect("delete");

    assert!(device.file(id, "racing.csv").is_none());
    assert!(device.file(id, "apex.csv").is_some());
    assert!(device.file(id, "default.csv").is_some());
}

#[test]
fn delete_refuses_anything_that_is_not_a_profile() {
    let (device, id) = FakeQuadStick::with_device();
    device.put_file(id, "notes.txt", b"b\n");
    let now = generation(&device, id);

    assert_eq!(
        device.delete_file(id, now, &plain("notes.txt")),
        Err(StorageError::NameRejected {
            reason: NameRejection::NotCsv
        })
    );
    assert!(device.file(id, "notes.txt").is_some());
}

#[test]
fn delete_of_something_that_is_already_gone_says_so() {
    let (device, id) = FakeQuadStick::with_device();
    let now = generation(&device, id);

    assert_eq!(
        device.delete_file(id, now, &plain("gone.csv")),
        Err(StorageError::FileNotFound {
            name: plain("gone.csv")
        })
    );
}

#[test]
fn two_backups_of_one_name_both_survive() {
    let backups = FakeBackupStore::new();
    let name = plain("racing.csv");

    let first = backups.store(&name, b"first\n").expect("backup");
    let second = backups.store(&name, b"second\n").expect("backup");

    assert_ne!(first.location, second.location);
    assert_eq!(backups.entries().len(), 2);
    assert_eq!(backups.entries()[0].1, b"first\n");
    assert_eq!(backups.entries()[1].1, b"second\n");
}

#[test]
fn a_one_shot_fault_lets_the_retry_through() {
    let (device, backups, id) = device_with_existing_profile();
    device.fail_at(StorageStage::TempWrite, Fault::Io);

    rehearse_install(&device, &backups, id, &safe("racing.csv"), PROFILE)
        .expect_err("the first attempt fails");
    rehearse_install(&device, &backups, id, &safe("racing.csv"), PROFILE)
        .expect("the retry succeeds");

    assert_eq!(device.file(id, "racing.csv").as_deref(), Some(PROFILE));
    assert!(device.stray_temp_names(id).is_empty());
}

#[test]
fn the_fake_is_deterministic() {
    let run = || {
        let (device, backups, id) = device_with_existing_profile();
        device.fail_at(StorageStage::ReplaceAfterDisplace, Fault::Io);
        let outcome = rehearse_install(&device, &backups, id, &safe("racing.csv"), PROFILE)
            .expect_err("the swap failed");
        (outcome, device.file_names(id), backups.entries())
    };
    assert_eq!(run(), run());
}
