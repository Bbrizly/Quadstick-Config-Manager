//! The device library: listing, ordering, the light guide, and delete.
//!
//! Ported from `DeviceFileManagementTests`, which cares about one thing above
//! all: nothing is removed unless every rule passed and a copy already exists
//! off the drive.

use qcm_core::clock::ManualClock;
use qcm_core::confirmation::{ConfirmationId, ConfirmationKind};
use qcm_core::devices::{Devices, GuideEntry, LedColour, led_pattern, selection_order};
use qcm_core::error::{
    ConfirmationError, DeviceError, NameRejection, QcmError, StorageError, StorageStage,
};
use qcm_core::ports::storage::{DeviceFileName, StorageDeviceId};
use qcm_testkit::{FakeBackupStore, FakeQuadStick, Fault};

type Service<'a> = Devices<&'a FakeQuadStick, FakeBackupStore, &'a ManualClock>;

fn plain(name: &str) -> DeviceFileName {
    DeviceFileName::new(name).expect("a plain device file name")
}

fn names(list: &[&str]) -> Vec<DeviceFileName> {
    list.iter().map(|name| plain(name)).collect()
}

fn shown(ordered: &[DeviceFileName]) -> Vec<&str> {
    ordered.iter().map(DeviceFileName::as_str).collect()
}

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

    fn devices(&self) -> Service<'_> {
        Devices::new(&self.fake, FakeBackupStore::new(), &self.clock)
    }
}

// ------------------------------------------------------------------ ordering

// Ported from `SelectionOrder_puts_default_first_drops_prefs_and_sorts_the_rest`.
#[test]
fn selection_order_puts_default_first_drops_prefs_and_sorts_the_rest() {
    let given = names(&[
        "Zelda.csv",
        "prefs.csv",
        "apex.csv",
        "default.csv",
        "Battlefield.csv",
        "cod.CSV",
    ]);

    assert_eq!(
        shown(&selection_order(&given)),
        vec![
            "default.csv",
            "apex.csv",
            "Battlefield.csv",
            "cod.CSV",
            "Zelda.csv"
        ]
    );
}

#[test]
fn selection_order_matches_the_protected_names_whatever_the_case() {
    let given = names(&["game.csv", "PREFS.CSV", "Default.csv", "notes.txt"]);

    assert_eq!(
        shown(&selection_order(&given)),
        vec!["Default.csv", "game.csv"]
    );
}

#[test]
fn selection_order_of_nothing_is_empty() {
    assert!(selection_order(&[]).is_empty());
    assert!(selection_order(&names(&["prefs.csv"])).is_empty());
}

// Found on a real FAT volume. A QuadStick drive is FAT, so macOS leaves
// ._Racing.csv beside every file it copies there. Listing them would put phantom
// entries in the light guide and offer ._prefs.csv for deletion, since it does
// not match the exact protected name.
#[test]
fn selection_order_ignores_the_sidecars_macos_writes_to_fat_drives() {
    let given = names(&[
        "default.csv",
        "._default.csv",
        "._prefs.csv",
        "._Racing.csv",
        "Racing.csv",
    ]);

    assert_eq!(
        shown(&selection_order(&given)),
        vec!["default.csv", "Racing.csv"]
    );
}

#[test]
fn a_name_that_is_not_a_visible_csv_is_not_a_profile() {
    for name in ["._Racing.csv", "._prefs.csv", ".hidden.csv", "notes.txt"] {
        assert!(!plain(name).is_profile(), "{name}");
    }
    for name in ["Racing.csv", "default.csv", "prefs.csv", "cod.CSV"] {
        assert!(plain(name).is_profile(), "{name}");
    }
}

// ---------------------------------------------------------------- the lights

// Ported from `LedPattern_rows_match_the_audited_table`.
#[test]
fn the_light_rows_match_the_audited_table() {
    use LedColour::{Blue, Grey, Purple, Red};
    let cases: [(usize, [LedColour; 5]); 10] = [
        (1, [Purple, Grey, Grey, Grey, Grey]),
        (5, [Grey, Grey, Grey, Grey, Purple]),
        (10, [Grey, Grey, Grey, Grey, Blue]),
        (15, [Grey, Grey, Grey, Grey, Red]),
        (19, [Grey, Grey, Grey, Purple, Red]),
        (20, [Blue, Blue, Blue, Blue, Blue]),
        (26, [Purple, Blue, Blue, Blue, Purple]),
        (30, [Red, Red, Red, Red, Purple]),
        (31, [Purple, Red, Red, Red, Red]),
        (32, [Red, Purple, Red, Red, Red]),
    ];
    for (number, expected) in cases {
        assert_eq!(led_pattern(number), expected, "file number {number}");
    }
}

// Nothing past 32 is documented, so nothing is extrapolated.
#[test]
fn a_file_number_outside_the_table_has_no_lights() {
    for number in [0, 33, 100, usize::MAX] {
        assert!(led_pattern(number).is_empty(), "{number}");
    }
}

#[test]
fn every_row_in_the_table_is_five_named_colours() {
    for number in 1..=32 {
        assert_eq!(led_pattern(number).len(), 5, "file number {number}");
    }
    // Named, not coloured. A guide read aloud has to say the word.
    assert_eq!(led_pattern(1)[0].as_str(), "purple");
    assert_eq!(led_pattern(1)[1].as_str(), "grey");
    assert_eq!(led_pattern(10)[4].as_str(), "blue");
    assert_eq!(led_pattern(15)[4].as_str(), "red");
}

// ----------------------------------------------------------------- listing

#[test]
fn the_list_is_the_selection_order_with_its_numbers_and_lights() {
    let rig = Rig::new();
    rig.fake.put_file(rig.device, "Zelda.csv", b"a\n");
    rig.fake.put_file(rig.device, "apex.csv", b"b\n");
    rig.fake.put_file(rig.device, "prefs.csv", b"settings\n");
    let mut devices = rig.devices();

    let (guide, unnameable) = devices.list_profiles(rig.device).expect("listing");

    assert_eq!(unnameable, 0);
    assert_eq!(
        guide,
        vec![
            GuideEntry {
                file_number: 1,
                name: plain("default.csv"),
                lights: led_pattern(1),
            },
            GuideEntry {
                file_number: 2,
                name: plain("apex.csv"),
                lights: led_pattern(2),
            },
            GuideEntry {
                file_number: 3,
                name: plain("Zelda.csv"),
                lights: led_pattern(3),
            },
        ]
    );
}

// A list that quietly hides part of a drive is the bug this app keeps being
// reported for. What cannot be named is counted, not dropped.
#[test]
fn the_list_counts_what_it_cannot_name_instead_of_hiding_it() {
    let rig = Rig::new();
    rig.fake.put_file(rig.device, "Racing.csv", b"a\n");
    rig.fake.put_file(rig.device, "bad\u{0}name.csv", b"c\n");
    let mut devices = rig.devices();

    let (guide, unnameable) = devices.list_profiles(rig.device).expect("listing");

    assert_eq!(unnameable, 1);
    assert_eq!(guide.len(), 2);
}

#[test]
fn listing_a_folder_that_stopped_being_a_quadstick_fails() {
    let rig = Rig::new();
    rig.fake.remove_marker(rig.device);
    let mut devices = rig.devices();

    assert_eq!(
        devices.list_profiles(rig.device),
        Err(QcmError::Storage(StorageError::Device(
            DeviceError::NotQuadStick
        )))
    );
}

// ------------------------------------------------------------------ delete

// Ported from `Protected_names_are_rejected_before_any_backup_or_delete`.
#[test]
fn a_protected_name_is_refused_before_any_backup_or_delete() {
    for name in [
        "default.csv",
        "Default.CSV",
        "DEFAULT.csv",
        "prefs.csv",
        "Prefs.Csv",
        "PREFS.CSV",
    ] {
        let rig = Rig::new();
        rig.fake.put_file(rig.device, name, b"keep me\n");
        let mut devices = rig.devices();

        let error = devices
            .plan_delete(rig.device, &plain(name))
            .expect_err("protected");

        assert!(
            matches!(error, QcmError::Storage(StorageError::ProtectedFile { .. })),
            "{name}: {error}"
        );
        assert!(
            rig.fake.file(rig.device, name).is_some(),
            "{name} must still be on the device"
        );
        assert!(
            devices.backups().is_empty(),
            "no backup work may start for {name}"
        );
    }
}

// Ported from `Non_csv_file_is_rejected`.
#[test]
fn something_that_is_not_a_profile_is_refused() {
    let rig = Rig::new();
    rig.fake.put_file(rig.device, "readme.txt", b"notes\n");
    let mut devices = rig.devices();

    let error = devices
        .plan_delete(rig.device, &plain("readme.txt"))
        .expect_err("not a profile");

    assert_eq!(
        error,
        QcmError::Storage(StorageError::NameRejected {
            reason: NameRejection::NotCsv
        })
    );
    assert!(rig.fake.file(rig.device, "readme.txt").is_some());
    assert!(devices.backups().is_empty());
}

// Ported from `Path_traversal_is_rejected`, `Subdirectory_target_is_rejected`
// and `Absolute_path_is_rejected`. Here the type is the guard: a name that is
// not a plain direct child cannot be built at all, so it cannot be passed in.
#[test]
fn a_name_that_could_walk_out_of_the_root_cannot_even_be_built() {
    for attempt in [
        "../outside.csv",
        "sub/game.csv",
        "sub\\game.csv",
        "/Users/b/game.csv",
        "C:\\Users\\b\\game.csv",
    ] {
        assert!(
            DeviceFileName::new(attempt).is_err(),
            "{attempt} must be refused"
        );
    }
}

// Ported from `Missing_file_is_rejected`.
#[test]
fn deleting_something_that_is_already_gone_says_so_and_backs_nothing_up() {
    let rig = Rig::new();
    let mut devices = rig.devices();

    let error = devices
        .plan_delete(rig.device, &plain("gone.csv"))
        .expect_err("not there");

    assert_eq!(
        error,
        QcmError::Storage(StorageError::FileNotFound {
            name: plain("gone.csv")
        })
    );
    assert!(devices.backups().is_empty());
}

// Ported from `Root_without_default_csv_is_rejected`.
#[test]
fn deleting_from_a_folder_that_is_not_a_quadstick_is_refused() {
    let rig = Rig::new();
    rig.fake.put_file(rig.device, "game.csv", b"body\n");
    rig.fake.remove_marker(rig.device);
    let mut devices = rig.devices();

    let error = devices
        .plan_delete(rig.device, &plain("game.csv"))
        .expect_err("not a QuadStick");

    assert_eq!(
        error,
        QcmError::Storage(StorageError::Device(DeviceError::NotQuadStick))
    );
    assert!(rig.fake.file(rig.device, "game.csv").is_some());
}

// Ported from `Successful_delete_backs_up_and_removes_only_the_exact_target`.
#[test]
fn a_delete_backs_up_and_removes_only_the_exact_target() {
    let rig = Rig::new();
    rig.fake.put_file(rig.device, "game.csv", b"game body\n");
    rig.fake.put_file(rig.device, "other.csv", b"other body\n");
    let mut devices = rig.devices();

    let plan = devices
        .plan_delete(rig.device, &plain("game.csv"))
        .expect("planned");
    let id = plan.confirmation().id;
    let receipt = devices.delete_profile(plan, id).expect("deleted");

    assert_eq!(receipt.name, plain("game.csv"));
    assert!(receipt.backup.as_str().ends_with("game.csv"));
    assert_eq!(devices.backups().entries()[0].1, b"game body\n");
    assert!(rig.fake.file(rig.device, "game.csv").is_none());
    assert!(
        rig.fake.file(rig.device, "other.csv").is_some(),
        "other profiles are untouched"
    );
    assert!(
        rig.fake.file(rig.device, "default.csv").is_some(),
        "the marker is untouched"
    );
}

// Ported from `Backup_failure_leaves_the_source_file_untouched`. No backup means
// no delete.
#[test]
fn a_backup_that_fails_leaves_the_file_on_the_device() {
    let rig = Rig::new();
    rig.fake.put_file(rig.device, "game.csv", b"keep me\n");
    let mut devices = rig.devices();
    let plan = devices
        .plan_delete(rig.device, &plain("game.csv"))
        .expect("planned");
    let id = plan.confirmation().id;
    devices.backups().fail_next();

    let error = devices.delete_profile(plan, id).expect_err("no backup");

    assert!(matches!(
        error,
        QcmError::Storage(StorageError::BackupFailed { .. })
    ));
    assert_eq!(
        rig.fake.file(rig.device, "game.csv").as_deref(),
        Some(&b"keep me\n"[..])
    );
}

// Ported from `Two_deletes_of_the_same_name_keep_two_distinct_backups`.
#[test]
fn two_deletes_of_one_name_keep_two_distinct_backups() {
    let rig = Rig::new();
    let mut devices = rig.devices();

    rig.fake.put_file(rig.device, "game.csv", b"first\n");
    let plan = devices
        .plan_delete(rig.device, &plain("game.csv"))
        .expect("planned");
    let id = plan.confirmation().id;
    let first = devices.delete_profile(plan, id).expect("deleted");

    rig.fake.put_file(rig.device, "game.csv", b"second\n");
    let plan = devices
        .plan_delete(rig.device, &plain("game.csv"))
        .expect("planned");
    let id = plan.confirmation().id;
    let second = devices.delete_profile(plan, id).expect("deleted");

    assert_ne!(first.backup, second.backup);
    let stored = devices.backups().entries();
    assert_eq!(stored.len(), 2);
    assert_eq!(stored[0].1, b"first\n");
    assert_eq!(stored[1].1, b"second\n");
}

// Deleting from a disabled user's device cannot be undone from the device, so
// the acknowledgement is part of the operation and not something the window
// remembers to ask.
#[test]
fn a_delete_without_its_acknowledgement_does_nothing() {
    let rig = Rig::new();
    rig.fake.put_file(rig.device, "game.csv", b"body\n");
    let mut devices = rig.devices();
    let plan = devices
        .plan_delete(rig.device, &plain("game.csv"))
        .expect("planned");
    assert_eq!(
        plan.confirmation().kind,
        ConfirmationKind::DeleteDeviceProfile
    );

    let error = devices
        .delete_profile(plan, ConfirmationId::from_raw(4242))
        .expect_err("not on record");

    assert_eq!(error, QcmError::Confirmation(ConfirmationError::Unknown));
    assert!(rig.fake.file(rig.device, "game.csv").is_some());
    assert!(devices.backups().is_empty());
}

// An answer given about one file must never remove another.
#[test]
fn a_delete_acknowledgement_cannot_be_spent_on_another_file() {
    let rig = Rig::new();
    rig.fake.put_file(rig.device, "game.csv", b"one\n");
    rig.fake.put_file(rig.device, "other.csv", b"two\n");
    let mut devices = rig.devices();
    let first = devices
        .plan_delete(rig.device, &plain("game.csv"))
        .expect("planned");
    let second = devices
        .plan_delete(rig.device, &plain("other.csv"))
        .expect("planned");
    let first_id = first.confirmation().id;

    let error = devices
        .delete_profile(second, first_id)
        .expect_err("the wrong acknowledgement");

    assert_eq!(error, QcmError::Confirmation(ConfirmationError::Mismatch));
    assert!(rig.fake.file(rig.device, "other.csv").is_some());
    // And the answer is still good for the file it was given about.
    devices
        .delete_profile(first, first_id)
        .expect("its own file");
    assert!(rig.fake.file(rig.device, "game.csv").is_none());
}

#[test]
fn a_device_replugged_between_planning_and_deleting_is_refused() {
    let rig = Rig::new();
    rig.fake.put_file(rig.device, "game.csv", b"body\n");
    let mut devices = rig.devices();
    let plan = devices
        .plan_delete(rig.device, &plain("game.csv"))
        .expect("planned");
    let id = plan.confirmation().id;

    rig.fake.unplug(rig.device);
    rig.fake.replug(rig.device);

    let error = devices.delete_profile(plan, id).expect_err("a new mount");

    assert!(matches!(
        error,
        QcmError::Storage(StorageError::Device(DeviceError::Stale { .. }))
    ));
    assert!(rig.fake.file(rig.device, "game.csv").is_some());
}

#[test]
fn a_stick_pulled_before_the_delete_lands_leaves_the_file_alone() {
    let rig = Rig::new();
    rig.fake.put_file(rig.device, "game.csv", b"body\n");
    let mut devices = rig.devices();
    let plan = devices
        .plan_delete(rig.device, &plain("game.csv"))
        .expect("planned");
    let id = plan.confirmation().id;
    rig.fake.fail_at(StorageStage::Delete, Fault::Unplug);

    let error = devices.delete_profile(plan, id).expect_err("pulled out");

    assert!(matches!(
        error,
        QcmError::Storage(StorageError::RemovedDuringOperation { .. })
    ));
    assert!(rig.fake.file(rig.device, "game.csv").is_some());
    // The backup was taken first, so the user still has the file either way.
    assert_eq!(devices.backups().entries().len(), 1);
}

#[test]
fn a_delete_drops_the_device_cache() {
    let rig = Rig::new();
    rig.fake.put_file(rig.device, "game.csv", b"body\n");
    let mut devices = rig.devices();
    devices.list_devices().expect("discovery");
    let plan = devices
        .plan_delete(rig.device, &plain("game.csv"))
        .expect("planned");
    let id = plan.confirmation().id;

    devices.delete_profile(plan, id).expect("deleted");

    assert!(!devices.device_cache_is_fresh());
}

// ------------------------------------------------------------- reading files

#[test]
fn the_device_preferences_can_be_read_back() {
    let rig = Rig::new();
    rig.fake
        .put_file(rig.device, "prefs.csv", b"Preferences,,\nvolume,5\n");
    let mut devices = rig.devices();

    let read = devices.read_preferences(rig.device).expect("read");

    assert_eq!(read.csv_text, "Preferences,,\nvolume,5\n");
    assert_eq!(read.device, rig.device);
}

#[test]
fn a_device_with_no_preferences_file_says_so() {
    let rig = Rig::new();
    let mut devices = rig.devices();

    assert_eq!(
        devices.read_preferences(rig.device),
        Err(QcmError::Storage(StorageError::FileNotFound {
            name: plain("prefs.csv")
        }))
    );
}

#[test]
fn the_fallback_profile_can_be_read_back() {
    let rig = Rig::new();
    let mut devices = rig.devices();

    let read = devices.read_default_config(rig.device).expect("read");

    assert_eq!(read.csv_text, "QuadStick Configuration File,\n");
}

#[test]
fn one_profile_can_be_read_off_the_device() {
    let rig = Rig::new();
    rig.fake.put_file(rig.device, "Racing.csv", b"a profile\n");
    let mut devices = rig.devices();

    let read = devices
        .read_profile(rig.device, &plain("Racing.csv"))
        .expect("read");

    assert_eq!(read.csv_text, "a profile\n");
}

// The sidecars macOS leaves on a FAT drive are binary metadata. Opening one as a
// profile would show the user nonsense.
#[test]
fn a_sidecar_is_not_readable_as_a_profile() {
    let rig = Rig::new();
    rig.fake
        .put_file(rig.device, "._Racing.csv", b"\x00\x05\x16\x07");
    let mut devices = rig.devices();

    assert_eq!(
        devices.read_profile(rig.device, &plain("._Racing.csv")),
        Err(QcmError::Storage(StorageError::NameRejected {
            reason: NameRejection::NotCsv
        }))
    );
}

// Never rewrite a value the user did not type. A settings file this app cannot
// read is one it must not rewrite either, so it says so instead of repairing it.
#[test]
fn preferences_that_are_not_text_are_reported_rather_than_repaired() {
    let rig = Rig::new();
    rig.fake
        .put_file(rig.device, "prefs.csv", &[0xff, 0xfe, 0x00]);
    let mut devices = rig.devices();

    assert_eq!(
        devices.read_preferences(rig.device),
        Err(QcmError::Config(qcm_core::error::ConfigError::Unreadable))
    );
    assert_eq!(
        rig.fake.file(rig.device, "prefs.csv").as_deref(),
        Some(&[0xff, 0xfe, 0x00][..]),
        "and it was left exactly as it was"
    );
}
