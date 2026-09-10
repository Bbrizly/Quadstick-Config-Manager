//! Discovery: what the app is allowed to show, and what it must prove again.
//!
//! No hardware. The fake models the one thing discovery is really about, a
//! volume that proves it is a QuadStick by holding `default.csv`, and every way
//! that can stop being true while the window is open.

use qcm_core::clock::ManualClock;
use qcm_core::devices::{DEFAULT_SCAN_TTL, Devices};
use qcm_core::error::{DeviceError, StorageError, StorageStage};
use qcm_core::ports::storage::StorageDeviceId;
use qcm_testkit::{FakeBackupStore, FakeQuadStick, Fault};
use std::time::Duration;

type Service<'a> = Devices<&'a FakeQuadStick, FakeBackupStore, &'a ManualClock>;

fn service<'a>(device: &'a FakeQuadStick, clock: &'a ManualClock) -> Service<'a> {
    Devices::new(device, FakeBackupStore::new(), clock)
}

#[test]
fn nothing_plugged_in_is_an_empty_list_and_not_an_error() {
    let fake = FakeQuadStick::new();
    let clock = ManualClock::new();
    let mut devices = service(&fake, &clock);

    let scan = devices.refresh_devices().expect("discovery");

    assert!(scan.devices.is_empty());
    assert!(scan.changed, "the first scan is always news");
}

#[test]
fn one_quadstick_comes_back_with_an_opaque_id_and_a_printable_name() {
    let fake = FakeQuadStick::new();
    let id = fake.plug("QUADSTICK");
    let clock = ManualClock::new();
    let mut devices = service(&fake, &clock);

    let found = devices.list_devices().expect("discovery");

    assert_eq!(found.len(), 1);
    assert_eq!(found[0].id, id);
    assert_eq!(found[0].display_name.as_str(), "QUADSTICK");
    assert!(found[0].writable);
}

#[test]
fn two_devices_are_told_apart() {
    let fake = FakeQuadStick::new();
    let first = fake.plug("QUADSTICK");
    let second = fake.plug("QUADSTICK");
    let clock = ManualClock::new();
    let mut devices = service(&fake, &clock);

    let found = devices.list_devices().expect("discovery");

    assert_eq!(found.len(), 2);
    assert_ne!(found[0].id, found[1].id);
    assert!(found.iter().any(|row| row.id == first));
    assert!(found.iter().any(|row| row.id == second));
}

// The marker is the whole test for what a QuadStick is. A backup drive with the
// same label is not one.
#[test]
fn a_volume_without_the_marker_is_not_a_candidate() {
    let fake = FakeQuadStick::new();
    let id = fake.plug_without_marker("QUADSTICK");
    let clock = ManualClock::new();
    let mut devices = service(&fake, &clock);

    assert!(devices.list_devices().expect("discovery").is_empty());
    assert_eq!(
        devices.resolve_device(id),
        Err(StorageError::Device(DeviceError::NotQuadStick))
    );
}

// The name never carries a mount point, whatever the adapter hands over. A
// device row copied into a bug report must not spell out the user's drives.
#[test]
fn a_display_name_is_sanitized_on_the_way_through() {
    let fake = FakeQuadStick::new();
    fake.plug("/Volumes/QUAD\u{7}STICK");
    let clock = ManualClock::new();
    let mut devices = service(&fake, &clock);

    let found = devices.list_devices().expect("discovery");

    let shown = found[0].display_name.as_str();
    assert_eq!(shown, "VolumesQUADSTICK");
    assert!(!shown.contains('/'));
    assert!(!shown.chars().any(char::is_control));
}

// Ported from `Device.FindCandidatesCached`. Refresh, save and undo all ask for
// the list, and a live scan each time stats every volume on the thread the user
// is waiting on.
#[test]
fn a_burst_of_lookups_enumerates_once() {
    let fake = FakeQuadStick::new();
    fake.plug("QUADSTICK");
    let clock = ManualClock::new();
    let mut devices = service(&fake, &clock);

    for _ in 0..5 {
        devices.list_devices().expect("discovery");
    }

    assert_eq!(fake.discover_count(), 1);
    assert!(devices.device_cache_is_fresh());
}

#[test]
fn the_cache_lets_go_after_its_window() {
    let fake = FakeQuadStick::new();
    fake.plug("QUADSTICK");
    let clock = ManualClock::new();
    let mut devices = service(&fake, &clock);

    devices.list_devices().expect("discovery");
    clock.advance(DEFAULT_SCAN_TTL - Duration::from_millis(1));
    devices.list_devices().expect("discovery");
    assert_eq!(fake.discover_count(), 1, "still inside the window");

    clock.advance(Duration::from_millis(2));
    devices.list_devices().expect("discovery");

    assert_eq!(fake.discover_count(), 2);
}

// An explicit Refresh must not wait out the window: a user who has just plugged
// a stick in is looking at the screen.
#[test]
fn invalidating_the_cache_forces_the_next_lookup_to_enumerate() {
    let fake = FakeQuadStick::new();
    fake.plug("QUADSTICK");
    let clock = ManualClock::new();
    let mut devices = service(&fake, &clock);

    devices.list_devices().expect("discovery");
    devices.invalidate_device_cache();
    assert!(!devices.device_cache_is_fresh());
    devices.list_devices().expect("discovery");

    assert_eq!(fake.discover_count(), 2);
}

#[test]
fn a_stick_plugged_in_after_a_scan_shows_up_once_the_cache_is_dropped() {
    let fake = FakeQuadStick::new();
    let clock = ManualClock::new();
    let mut devices = service(&fake, &clock);
    assert!(devices.list_devices().expect("discovery").is_empty());

    fake.plug("QUADSTICK");

    assert!(
        devices.list_devices().expect("discovery").is_empty(),
        "the cached answer stands until it is dropped or ages out"
    );
    devices.invalidate_device_cache();
    assert_eq!(devices.list_devices().expect("discovery").len(), 1);
}

// A global event on every polling tick is what the discovery spec forbids.
#[test]
fn an_unchanged_set_is_not_reported_as_news() {
    let fake = FakeQuadStick::new();
    fake.plug("QUADSTICK");
    let clock = ManualClock::new();
    let mut devices = service(&fake, &clock);

    assert!(devices.refresh_devices().expect("discovery").changed);
    assert!(!devices.refresh_devices().expect("discovery").changed);
}

#[test]
fn a_drive_turning_read_only_counts_as_a_change() {
    let fake = FakeQuadStick::new();
    let id = fake.plug("QUADSTICK");
    let clock = ManualClock::new();
    let mut devices = service(&fake, &clock);
    devices.refresh_devices().expect("discovery");

    fake.set_read_only(id, true);
    let scan = devices.refresh_devices().expect("discovery");

    assert!(scan.changed);
    assert!(!scan.devices[0].writable);
}

// Free space moves on its own while the firmware writes. Waking the window for
// it would be an event every tick.
#[test]
fn free_space_moving_on_its_own_is_not_a_change() {
    let fake = FakeQuadStick::new();
    let id = fake.plug("QUADSTICK");
    let clock = ManualClock::new();
    let mut devices = service(&fake, &clock);
    fake.set_capacity(id, Some(4096));
    devices.refresh_devices().expect("discovery");

    fake.put_file(id, "racing.csv", b"a profile\n");
    let scan = devices.refresh_devices().expect("discovery");

    assert!(!scan.changed);
    assert!(scan.devices[0].free_bytes.is_some_and(|free| free < 4096));
}

#[test]
fn a_device_unplugged_is_a_change_and_then_not_found() {
    let fake = FakeQuadStick::new();
    let id = fake.plug("QUADSTICK");
    let clock = ManualClock::new();
    let mut devices = service(&fake, &clock);
    devices.refresh_devices().expect("discovery");

    fake.unplug(id);

    assert!(devices.refresh_devices().expect("discovery").changed);
    assert_eq!(
        devices.resolve_device(id),
        Err(StorageError::Device(DeviceError::NotFound))
    );
}

// The mount point may have been handed to an unrelated volume in between, so
// the same id comes back at a new generation and the old one is refused.
#[test]
fn a_replugged_device_answers_at_a_new_generation() {
    let fake = FakeQuadStick::new();
    let id = fake.plug("QUADSTICK");
    let clock = ManualClock::new();
    let mut devices = service(&fake, &clock);
    let before = devices.resolve_device(id).expect("mounted");

    fake.unplug(id);
    fake.replug(id);
    let after = devices.resolve_device(id).expect("mounted");

    assert_ne!(before.generation, after.generation);
    assert_eq!(before.device, after.device);
}

// Proving the marker once at discovery is not enough. A user can reformat a
// stick with the window open.
#[test]
fn a_marker_removed_after_discovery_fails_the_next_lookup() {
    let fake = FakeQuadStick::new();
    let id = fake.plug("QUADSTICK");
    let clock = ManualClock::new();
    let mut devices = service(&fake, &clock);
    devices.resolve_device(id).expect("mounted");

    fake.remove_marker(id);

    assert_eq!(
        devices.resolve_device(id),
        Err(StorageError::Device(DeviceError::NotQuadStick))
    );
}

#[test]
fn an_id_that_was_never_handed_out_is_not_found() {
    let fake = FakeQuadStick::new();
    let clock = ManualClock::new();
    let mut devices = service(&fake, &clock);

    assert_eq!(
        devices.resolve_device(StorageDeviceId::from_raw(4242)),
        Err(StorageError::Device(DeviceError::NotFound))
    );
}

// A volume the OS will not let us read is skipped, not turned into a crash.
#[test]
fn a_scan_that_the_platform_refuses_is_reported_not_swallowed() {
    let fake = FakeQuadStick::new();
    fake.plug("QUADSTICK");
    let clock = ManualClock::new();
    let mut devices = service(&fake, &clock);
    fake.fail_at(StorageStage::Discover, Fault::PermissionDenied);

    assert_eq!(
        devices.refresh_devices().err(),
        Some(StorageError::PermissionDenied {
            stage: StorageStage::Discover
        })
    );
    // And the failure did not poison the next one.
    assert_eq!(
        devices.refresh_devices().expect("discovery").devices.len(),
        1
    );
}

// A failed scan must not be cached as "no devices".
#[test]
fn a_failed_scan_leaves_no_cache_behind() {
    let fake = FakeQuadStick::new();
    fake.plug("QUADSTICK");
    let clock = ManualClock::new();
    let mut devices = service(&fake, &clock);
    fake.fail_at(StorageStage::Discover, Fault::Io);

    assert!(devices.refresh_devices().is_err());

    assert!(!devices.device_cache_is_fresh());
    assert_eq!(devices.list_devices().expect("discovery").len(), 1);
}

#[test]
fn a_volume_that_will_not_report_its_size_is_not_shown_as_empty() {
    let fake = FakeQuadStick::new();
    let id = fake.plug("QUADSTICK");
    fake.set_capacity(id, None);
    let clock = ManualClock::new();
    let mut devices = service(&fake, &clock);

    let found = devices.list_devices().expect("discovery");

    assert_eq!(found[0].free_bytes, None);
}
