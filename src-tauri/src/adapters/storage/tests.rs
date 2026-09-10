//! The adapter against a real filesystem.
//!
//! Same trick the shipped `DeviceTests` used: a temp directory with a
//! `default.csv` in it is a QuadStick as far as every rule in the app is
//! concerned, because `IsInstallTarget` was only ever that one check. No
//! hardware, and the file operations are the real ones.

use super::volumes::FixedVolumes;
use super::{
    FileSystemBackupStore, FileSystemDeviceStorage, civil_from_days, unique_backup_name, utc_stamp,
};
use qcm_config::ProfileFile;
use qcm_core::clock::SystemClock;
use qcm_core::devices::Devices;
use qcm_core::error::{DeviceError, StorageError, TargetState};
use qcm_core::ports::storage::{
    BackupStore, DeviceFileName, DeviceStorage, SafeDeviceFileName, StorageDeviceId,
};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

/// A temp directory that removes itself. `std` has no such thing and this needs
/// no dependency to write.
struct Scratch {
    path: PathBuf,
}

impl Scratch {
    fn new(tag: &str) -> Self {
        static NEXT: AtomicU64 = AtomicU64::new(0);
        let unique = format!(
            "qcm-{tag}-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::SeqCst)
        );
        let path = std::env::temp_dir().join(unique);
        fs::create_dir_all(&path).expect("a scratch directory");
        Self { path }
    }

    fn path(&self) -> &Path {
        &self.path
    }
}

impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.path);
    }
}

/// A fake QuadStick: a directory with the marker in it.
fn quadstick(scratch: &Scratch) -> PathBuf {
    let root = scratch.path().join("QUADSTICK");
    fs::create_dir_all(&root).expect("a device root");
    fs::write(root.join("default.csv"), b"QuadStick Configuration File,\n").expect("the marker");
    root
}

fn storage(roots: Vec<PathBuf>) -> FileSystemDeviceStorage<FixedVolumes> {
    FileSystemDeviceStorage::new(FixedVolumes::new(roots))
}

fn safe(name: &str) -> SafeDeviceFileName {
    SafeDeviceFileName::new(name).expect("a writable name")
}

fn plain(name: &str) -> DeviceFileName {
    DeviceFileName::new(name).expect("a plain name")
}

fn only_device(adapter: &FileSystemDeviceStorage<FixedVolumes>) -> StorageDeviceId {
    let found = adapter.discover().expect("discovery");
    assert_eq!(found.len(), 1, "expected exactly one device");
    found[0].id
}

// ------------------------------------------------------------------ discovery

#[test]
fn a_folder_with_the_marker_is_a_quadstick_and_one_without_is_not() {
    let scratch = Scratch::new("discover");
    let device = quadstick(&scratch);
    let plain_folder = scratch.path().join("BACKUPS");
    fs::create_dir_all(&plain_folder).expect("a plain folder");

    let adapter = storage(vec![device.clone(), plain_folder]);
    let found = adapter.discover().expect("discovery");

    assert_eq!(found.len(), 1);
    assert_eq!(found[0].display_name.as_str(), "QUADSTICK");
}

#[test]
fn a_root_that_stays_put_keeps_its_id_and_generation() {
    let scratch = Scratch::new("stable");
    let adapter = storage(vec![quadstick(&scratch)]);

    let first = adapter.discover().expect("discovery");
    let second = adapter.discover().expect("discovery");

    assert_eq!(first[0].id, second[0].id);
    assert_eq!(first[0].generation, second[0].generation);
}

// The OS can hand the same mount point to an unrelated volume, so a root that
// went away and came back answers at a new generation and any plan made against
// the old one is refused.
#[test]
fn a_root_that_goes_away_and_comes_back_answers_at_a_new_generation() {
    let scratch = Scratch::new("remount");
    let root = quadstick(&scratch);
    let adapter = storage(vec![root.clone()]);
    let before = adapter.discover().expect("discovery")[0].clone();

    fs::remove_file(root.join("default.csv")).expect("unmount");
    assert!(adapter.discover().expect("discovery").is_empty());
    fs::write(root.join("default.csv"), b"QuadStick Configuration File,\n").expect("remount");
    let after = adapter.discover().expect("discovery")[0].clone();

    assert_eq!(before.id, after.id);
    assert_ne!(before.generation, after.generation);
    assert_eq!(
        adapter.read_file(after.id, before.generation, &plain("default.csv")),
        Err(StorageError::Device(DeviceError::Stale {
            expected: before.generation,
            actual: after.generation,
        }))
    );
}

// The marker is checked again on every lookup, not once at discovery: a user can
// reformat a stick with the window open.
#[test]
fn a_marker_removed_after_discovery_fails_every_later_call() {
    let scratch = Scratch::new("marker");
    let root = quadstick(&scratch);
    let adapter = storage(vec![root.clone()]);
    let id = only_device(&adapter);
    let generation = adapter.revalidate(id).expect("mounted").generation;
    fs::write(root.join("racing.csv"), b"old\n").expect("a profile");

    fs::remove_file(root.join("default.csv")).expect("reformat");

    let gone = StorageError::Device(DeviceError::NotQuadStick);
    assert_eq!(adapter.revalidate(id), Err(gone.clone()));
    assert_eq!(adapter.list_files(id, generation).err(), Some(gone.clone()));
    assert_eq!(
        adapter
            .stage_write(id, generation, &safe("racing.csv"), b"new\n")
            .err(),
        Some(gone.clone())
    );
    assert_eq!(
        adapter
            .delete_file(id, generation, &plain("racing.csv"))
            .err(),
        Some(gone)
    );
    assert_eq!(
        fs::read(root.join("racing.csv")).expect("still there"),
        b"old\n"
    );
}

#[test]
fn an_id_that_was_never_handed_out_is_not_found() {
    let scratch = Scratch::new("unknown");
    let adapter = storage(vec![quadstick(&scratch)]);

    assert_eq!(
        adapter.revalidate(StorageDeviceId::from_raw(4242)),
        Err(StorageError::Device(DeviceError::NotFound))
    );
}

// ------------------------------------------------------------------- listing

#[test]
fn the_listing_counts_what_it_cannot_name() {
    let scratch = Scratch::new("listing");
    let root = quadstick(&scratch);
    fs::write(root.join("Racing.csv"), b"a\n").expect("a profile");
    fs::write(root.join("._Racing.csv"), b"sidecar\n").expect("a sidecar");
    fs::write(root.join("notes.txt"), b"b\n").expect("notes");
    let adapter = storage(vec![root]);
    let id = only_device(&adapter);
    let generation = adapter.revalidate(id).expect("mounted").generation;

    let listing = adapter.list_files(id, generation).expect("listing");

    let names: Vec<&str> = listing
        .files
        .iter()
        .map(|entry| entry.name.as_str())
        .collect();
    assert_eq!(
        names,
        vec!["._Racing.csv", "Racing.csv", "default.csv", "notes.txt"]
    );
    assert_eq!(listing.unnameable, 0);
}

// ------------------------------------------------------- the whole transaction

#[test]
fn an_install_writes_the_profile_backs_up_the_old_one_and_leaves_no_scratch() {
    let scratch = Scratch::new("install");
    let root = quadstick(&scratch);
    fs::write(root.join("racing.csv"), b"an older profile\n").expect("a profile");
    let backups = Scratch::new("backups");

    let adapter = storage(vec![root.clone()]);
    let store = FileSystemBackupStore::new(backups.path().join("QuadStickBackups"));
    let clock = SystemClock::new();
    let mut devices = Devices::new(&adapter, store, &clock);
    let id = devices.list_devices().expect("discovery")[0].id;

    let file =
        ProfileFile::load("Profile Name,,L\nracing.csv\nOutputs,Function,usb\nx,normal,lip\n");
    let plan = devices.plan_install(id, &file).expect("planned");
    let receipt = devices.install(plan, None).expect("installed");

    assert!(receipt.confirmed_on_device);
    let written = fs::read_to_string(root.join("racing.csv")).expect("installed");
    assert!(written.starts_with("QuadStick Configuration,Version 1.5"));

    // Nothing scratch is left on the drive.
    let left: Vec<String> = fs::read_dir(&root)
        .expect("listing")
        .filter_map(Result::ok)
        .map(|entry| entry.file_name().to_string_lossy().into_owned())
        .filter(|name| name.contains(".qscm-"))
        .collect();
    assert!(left.is_empty(), "left behind: {left:?}");

    // And the old profile is off the device, where the firmware cannot delete it.
    let saved: Vec<PathBuf> = fs::read_dir(backups.path().join("QuadStickBackups"))
        .expect("the backup folder")
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .collect();
    assert_eq!(saved.len(), 1);
    assert_eq!(
        fs::read(&saved[0]).expect("the backup"),
        b"an older profile\n"
    );
    assert!(
        saved[0]
            .file_name()
            .and_then(|name| name.to_str())
            .is_some_and(|name| name.ends_with("-racing.csv"))
    );
}

// Ported from `Install_refuses_default_csv_without_confirmation_then_allows_with_it`,
// run against a real directory.
#[test]
fn overwriting_the_fallback_profile_needs_its_acknowledgement() {
    let scratch = Scratch::new("fallback");
    let root = quadstick(&scratch);
    let backups = Scratch::new("fallback-backups");
    let adapter = storage(vec![root.clone()]);
    let store = FileSystemBackupStore::new(backups.path().join("QuadStickBackups"));
    let clock = SystemClock::new();
    let mut devices = Devices::new(&adapter, store, &clock);
    let id = devices.list_devices().expect("discovery")[0].id;
    let file =
        ProfileFile::load("Profile Name,,L\ndefault.csv\nOutputs,Function,usb\nx,normal,lip\n");

    let plan = devices.plan_install(id, &file).expect("planned");
    let failure = devices.install(plan, None).expect_err("no acknowledgement");
    assert_eq!(failure.target, TargetState::Unchanged);
    assert_eq!(
        fs::read(root.join("default.csv")).expect("untouched"),
        b"QuadStick Configuration File,\n"
    );

    let plan = devices.plan_install(id, &file).expect("planned");
    let confirmation = plan.confirmation().expect("a gate").id;
    devices
        .install(plan, Some(confirmation))
        .expect("confirmed");
    assert!(
        fs::read_to_string(root.join("default.csv"))
            .expect("replaced")
            .starts_with("QuadStick Configuration,Version 1.5")
    );
}

// A stick that has gone read-only fails before anything destructive, which is
// the shipped `Install_failure_leaves_the_existing_file_untouched` test: it made
// the device directory read-only and asserted the old profile survived whole.
//
// Unix only, for the same reason the shipped test was. A read-only directory
// does not stop a create on Windows, so that failure has to come from the fake
// or from hardware.
#[cfg(unix)]
#[test]
fn a_read_only_drive_fails_before_the_profile_is_touched() {
    use std::os::unix::fs::PermissionsExt;

    let scratch = Scratch::new("readonly");
    let root = quadstick(&scratch);
    fs::write(root.join("racing.csv"), b"an older profile\n").expect("a profile");
    let backups = Scratch::new("readonly-backups");
    let adapter = storage(vec![root.clone()]);
    let store = FileSystemBackupStore::new(backups.path().join("QuadStickBackups"));
    let clock = SystemClock::new();
    let mut devices = Devices::new(&adapter, store, &clock);
    let id = devices.list_devices().expect("discovery")[0].id;
    let file =
        ProfileFile::load("Profile Name,,L\nracing.csv\nOutputs,Function,usb\nx,normal,lip\n");
    let plan = devices.plan_install(id, &file).expect("planned");

    // Read and traverse only: nothing may be created or removed in here.
    fs::set_permissions(&root, fs::Permissions::from_mode(0o500)).expect("lock the folder");
    let failure = devices.install(plan, None).expect_err("read only");
    fs::set_permissions(&root, fs::Permissions::from_mode(0o700)).expect("unlock the folder");

    assert_eq!(failure.target, TargetState::Unchanged);
    assert_eq!(
        fs::read(root.join("racing.csv")).expect("untouched"),
        b"an older profile\n"
    );
    let left: Vec<String> = fs::read_dir(&root)
        .expect("listing")
        .filter_map(Result::ok)
        .map(|entry| entry.file_name().to_string_lossy().into_owned())
        .filter(|name| name.contains(".qscm-"))
        .collect();
    assert!(left.is_empty(), "left behind: {left:?}");
}

#[test]
fn a_temp_that_will_not_be_committed_is_removed() {
    let scratch = Scratch::new("discard");
    let root = quadstick(&scratch);
    let adapter = storage(vec![root.clone()]);
    let id = only_device(&adapter);
    let generation = adapter.revalidate(id).expect("mounted").generation;

    let staged = adapter
        .stage_write(id, generation, &safe("racing.csv"), b"new bytes\n")
        .expect("staged");
    assert!(root.join("racing.csv.qscm-tmp").is_file());
    assert!(!root.join("racing.csv").exists(), "the target is untouched");

    adapter.discard_staged(staged).expect("cleanup");

    assert!(!root.join("racing.csv.qscm-tmp").exists());
}

#[test]
fn a_temp_that_does_not_read_back_the_same_is_refused() {
    let scratch = Scratch::new("verify");
    let root = quadstick(&scratch);
    let adapter = storage(vec![root.clone()]);
    let id = only_device(&adapter);
    let generation = adapter.revalidate(id).expect("mounted").generation;
    let staged = adapter
        .stage_write(id, generation, &safe("racing.csv"), b"new bytes\n")
        .expect("staged");

    // Something else changed the temp under us.
    fs::write(root.join("racing.csv.qscm-tmp"), b"tampered\n").expect("overwrite");

    assert_eq!(
        adapter.verify_staged(&staged, b"new bytes\n"),
        Err(StorageError::VerifyFailed)
    );
    adapter.discard_staged(staged).expect("cleanup");
}

// Beside then into place. The target is the old file or nothing, never a half
// written profile the device would load without complaining.
#[test]
fn a_restore_leaves_the_target_whole_and_no_scratch_behind() {
    let scratch = Scratch::new("restore");
    let root = quadstick(&scratch);
    let adapter = storage(vec![root.clone()]);
    let id = only_device(&adapter);
    let generation = adapter.revalidate(id).expect("mounted").generation;

    adapter
        .restore_file(id, generation, &plain("racing.csv"), b"the old profile\n")
        .expect("restored");

    assert_eq!(
        fs::read(root.join("racing.csv")).expect("restored"),
        b"the old profile\n"
    );
    assert!(!root.join("racing.csv.qscm-restore").exists());
}

// -------------------------------------------------------------------- delete

#[test]
fn delete_takes_a_copy_off_the_device_and_removes_only_the_target() {
    let scratch = Scratch::new("delete");
    let root = quadstick(&scratch);
    fs::write(root.join("game.csv"), b"game body\n").expect("a profile");
    fs::write(root.join("other.csv"), b"other body\n").expect("a neighbour");
    let backups = Scratch::new("delete-backups");
    let adapter = storage(vec![root.clone()]);
    let store = FileSystemBackupStore::new(backups.path().join("QuadStickBackups"));
    let clock = SystemClock::new();
    let mut devices = Devices::new(&adapter, store, &clock);
    let id = devices.list_devices().expect("discovery")[0].id;

    let plan = devices
        .plan_delete(id, &plain("game.csv"))
        .expect("planned");
    let confirmation = plan.confirmation().id;
    devices.delete_profile(plan, confirmation).expect("deleted");

    assert!(!root.join("game.csv").exists());
    assert!(root.join("other.csv").is_file());
    assert!(root.join("default.csv").is_file());
    let saved: Vec<PathBuf> = fs::read_dir(backups.path().join("QuadStickBackups"))
        .expect("the backup folder")
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .collect();
    assert_eq!(fs::read(&saved[0]).expect("the backup"), b"game body\n");
}

#[test]
fn the_device_own_files_are_never_deleted_this_way() {
    let scratch = Scratch::new("protected");
    let root = quadstick(&scratch);
    fs::write(root.join("prefs.csv"), b"settings\n").expect("settings");
    let adapter = storage(vec![root.clone()]);
    let id = only_device(&adapter);
    let generation = adapter.revalidate(id).expect("mounted").generation;

    for protected in ["default.csv", "prefs.csv"] {
        assert!(matches!(
            adapter.delete_file(id, generation, &plain(protected)),
            Err(StorageError::ProtectedFile { .. })
        ));
        assert!(root.join(protected).is_file(), "{protected}");
    }
}

// A directory named like a profile is not a profile. Removing it would take
// whatever is inside with it.
#[test]
fn a_directory_named_like_a_profile_is_not_deleted() {
    let scratch = Scratch::new("directory");
    let root = quadstick(&scratch);
    fs::create_dir(root.join("racing.csv")).expect("a directory in the way");
    let adapter = storage(vec![root.clone()]);
    let id = only_device(&adapter);
    let generation = adapter.revalidate(id).expect("mounted").generation;

    assert!(matches!(
        adapter.delete_file(id, generation, &plain("racing.csv")),
        Err(StorageError::FileNotFound { .. })
    ));
    assert!(root.join("racing.csv").is_dir());
}

// -------------------------------------------------------------------- backups

#[test]
fn two_backups_of_one_name_in_the_same_instant_both_survive() {
    let backups = Scratch::new("naming");
    let store = FileSystemBackupStore::new(backups.path().join("QuadStickBackups"));
    let name = plain("racing.csv");

    let first = store.store(&name, b"first\n").expect("backup");
    let second = store.store(&name, b"second\n").expect("backup");

    assert_ne!(first.location, second.location);
    let saved: Vec<PathBuf> = fs::read_dir(backups.path().join("QuadStickBackups"))
        .expect("the backup folder")
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .collect();
    assert_eq!(saved.len(), 2);
}

// The location a user is shown is two plain names, so it can never spell out the
// home directory it sits in.
// Two backups of one name inside the same millisecond is the case the counter
// exists for, and it cannot be forced through the clock, so the naming rule is
// exercised on its own with a fixed stamp.
#[test]
fn a_backup_name_never_lands_on_one_that_is_already_there() {
    let folder = Scratch::new("naming-rule");
    let stamp = "20260829-143012-045";

    let first = unique_backup_name(folder.path(), stamp, "racing.csv");
    assert_eq!(first, "20260829-143012-045-racing.csv");
    fs::write(folder.path().join(&first), b"first\n").expect("the first backup");

    let second = unique_backup_name(folder.path(), stamp, "racing.csv");
    assert_eq!(second, "20260829-143012-045-2-racing.csv");
    fs::write(folder.path().join(&second), b"second\n").expect("the second backup");

    let third = unique_backup_name(folder.path(), stamp, "racing.csv");
    assert_eq!(third, "20260829-143012-045-3-racing.csv");
    assert_eq!(
        fs::read(folder.path().join(&first)).expect("still there"),
        b"first\n"
    );
}

#[test]
fn the_backup_location_shown_to_a_user_is_not_a_path() {
    let backups = Scratch::new("display");
    let store = FileSystemBackupStore::new(backups.path().join("QuadStickBackups"));

    let receipt = store
        .store(&plain("racing.csv"), b"body\n")
        .expect("backup");

    let shown = receipt.location.as_str();
    assert!(shown.starts_with("QuadStickBackups/"));
    assert!(shown.ends_with("-racing.csv"));
    assert_eq!(shown.matches('/').count(), 1);
}

#[test]
fn a_backup_area_that_cannot_be_made_is_reported_rather_than_skipped() {
    let scratch = Scratch::new("blocked");
    // The parent is a file, so the folder cannot be created, which is how the
    // shipped test injected this failure too.
    let blocker = scratch.path().join("not-a-folder");
    fs::write(&blocker, b"x").expect("the blocker");
    let store = FileSystemBackupStore::new(blocker.join("QuadStickBackups"));

    assert!(matches!(
        store.store(&plain("racing.csv"), b"body\n"),
        Err(StorageError::BackupFailed { .. })
    ));
}

#[test]
fn the_backup_stamp_is_a_sortable_date_and_time() {
    let stamp = utc_stamp();
    assert_eq!(stamp.len(), "yyyymmdd-hhmmss-mmm".len());
    let parts: Vec<&str> = stamp.split('-').collect();
    assert_eq!(parts.len(), 3);
    assert!(
        parts
            .iter()
            .all(|part| part.chars().all(|c| c.is_ascii_digit()))
    );
    // Anchored so a broken calendar shows up as a date in the wrong century
    // rather than as a name that merely looks plausible.
    assert!(&stamp[..4] >= "2026", "{stamp}");
}

#[test]
fn the_calendar_arithmetic_matches_known_days() {
    assert_eq!(civil_from_days(0), (1970, 1, 1));
    assert_eq!(civil_from_days(59), (1970, 3, 1));
    // 2000 was a leap year, 1900 was not: the two the naive rule gets wrong.
    assert_eq!(civil_from_days(11_016), (2000, 2, 29));
    assert_eq!(civil_from_days(20_313), (2025, 8, 13));
}
