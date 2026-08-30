use qcm_config::ProfileFile;
use qcm_core::clock::ManualClock;
use qcm_core::devices::Devices;
use qcm_core::error::ErrorCode;
use qcm_core::QcmError;
use qcm_tauri_lib::adapters::device_picker::DeviceFolderPicker;
use qcm_tauri_lib::device_shell::DeviceShell;
use qcm_testkit::{FakeBackupStore, FakeQuadStick};
use serde_json::json;
use std::time::Duration;

#[derive(Debug, Clone, Copy)]
struct CancelledPicker;

impl DeviceFolderPicker for CancelledPicker {
    fn pick_device_folder(&self) -> Result<bool, QcmError> {
        Ok(false)
    }
}

type TestShell<'a> =
    DeviceShell<&'a FakeQuadStick, FakeBackupStore, &'a ManualClock, CancelledPicker>;

fn shell<'a>(drive: &'a FakeQuadStick, clock: &'a ManualClock) -> TestShell<'a> {
    DeviceShell::new(
        Devices::new(drive, FakeBackupStore::new(), clock),
        CancelledPicker,
    )
}

fn profile(name: &str) -> ProfileFile {
    ProfileFile::load(&format!(
        "Profile Name,,L\n{name}\nOutputs,Function,usb\nx,normal,lip\n"
    ))
}

fn code(error: &QcmError) -> ErrorCode {
    error.code()
}

#[test]
fn discovery_returns_only_opaque_device_identity() {
    let (drive, device) = FakeQuadStick::with_device();
    let clock = ManualClock::new();
    let shell = shell(&drive, &clock);

    let snapshot = shell.refresh_devices().expect("discover");

    assert_eq!(snapshot.devices.len(), 1);
    assert_eq!(snapshot.devices[0].device_id, device.to_string());
    assert_eq!(snapshot.devices[0].generation, drive.generation(device).unwrap().raw());
    let json = serde_json::to_string(&snapshot).unwrap();
    assert!(!json.contains('/'));
    assert!(!json.contains("\\\\"));
}

#[test]
fn a_forged_device_id_never_becomes_a_path() {
    let (drive, _) = FakeQuadStick::with_device();
    let clock = ManualClock::new();
    let shell = shell(&drive, &clock);

    let error = shell
        .get_device_library(json!({ "deviceId": "dev-999999" }))
        .unwrap_err();

    assert_eq!(code(&error), ErrorCode::DeviceNotFound);
}

#[test]
fn a_stale_generation_is_refused_before_a_file_is_read() {
    let (drive, device) = FakeQuadStick::with_device();
    drive.put_file(device, "racing.csv", b"old");
    let clock = ManualClock::new();
    let shell = shell(&drive, &clock);
    let old = drive.generation(device).unwrap().raw();

    drive.unplug(device);
    drive.replug(device);
    let error = shell
        .prepare_delete(json!({
            "deviceId": device.to_string(),
            "expectedGeneration": old,
            "name": "racing.csv"
        }))
        .unwrap_err();

    assert_eq!(code(&error), ErrorCode::DeviceStale);
    assert!(drive.file(device, "racing.csv").is_some());
}

#[test]
fn traversal_is_rejected_before_delete_planning() {
    let (drive, device) = FakeQuadStick::with_device();
    drive.put_file(device, "racing.csv", b"old");
    let clock = ManualClock::new();
    let shell = shell(&drive, &clock);
    let generation = drive.generation(device).unwrap().raw();

    let error = shell
        .prepare_delete(json!({
            "deviceId": device.to_string(),
            "expectedGeneration": generation,
            "name": "../racing.csv"
        }))
        .unwrap_err();

    assert_eq!(code(&error), ErrorCode::StorageNameRejected);
    assert!(drive.file(device, "racing.csv").is_some());
}

#[test]
fn an_expired_delete_confirmation_does_not_delete_the_profile() {
    let (drive, device) = FakeQuadStick::with_device();
    drive.put_file(device, "racing.csv", b"old");
    let clock = ManualClock::new();
    let shell = shell(&drive, &clock);
    let generation = drive.generation(device).unwrap().raw();
    let plan = shell
        .prepare_delete(json!({
            "deviceId": device.to_string(),
            "expectedGeneration": generation,
            "name": "racing.csv"
        }))
        .unwrap();

    clock.advance(Duration::from_secs(121));
    let failure = shell
        .commit_delete(json!({
            "planId": plan.plan_id,
            "confirmationId": plan.confirmation.confirmation_id
        }))
        .unwrap_err();

    assert_eq!(code(&failure.error), ErrorCode::ConfirmationExpired);
    assert!(drive.file(device, "racing.csv").is_some());
}

#[test]
fn a_confirmation_for_one_delete_cannot_authorize_another() {
    let (drive, device) = FakeQuadStick::with_device();
    drive.put_file(device, "one.csv", b"one");
    drive.put_file(device, "two.csv", b"two");
    let clock = ManualClock::new();
    let shell = shell(&drive, &clock);
    let generation = drive.generation(device).unwrap().raw();

    let one = shell
        .prepare_delete(json!({
            "deviceId": device.to_string(),
            "expectedGeneration": generation,
            "name": "one.csv"
        }))
        .unwrap();
    let two = shell
        .prepare_delete(json!({
            "deviceId": device.to_string(),
            "expectedGeneration": generation,
            "name": "two.csv"
        }))
        .unwrap();

    let failure = shell
        .commit_delete(json!({
            "planId": two.plan_id,
            "confirmationId": one.confirmation.confirmation_id
        }))
        .unwrap_err();

    assert_eq!(code(&failure.error), ErrorCode::ConfirmationMismatch);
    assert!(drive.file(device, "one.csv").is_some());
    assert!(drive.file(device, "two.csv").is_some());
}

#[test]
fn a_committed_plan_is_one_shot() {
    let (drive, device) = FakeQuadStick::with_device();
    drive.put_file(device, "racing.csv", b"old");
    let clock = ManualClock::new();
    let shell = shell(&drive, &clock);
    let generation = drive.generation(device).unwrap().raw();
    let plan = shell
        .prepare_delete(json!({
            "deviceId": device.to_string(),
            "expectedGeneration": generation,
            "name": "racing.csv"
        }))
        .unwrap();
    let plan_id = plan.plan_id.clone();
    let confirmation_id = plan.confirmation.confirmation_id.clone();

    shell
        .commit_delete(json!({
            "planId": plan_id,
            "confirmationId": confirmation_id
        }))
        .expect("first commit");
    let second = shell
        .commit_delete(json!({
            "planId": plan.plan_id,
            "confirmationId": plan.confirmation.confirmation_id
        }))
        .unwrap_err();

    assert_eq!(code(&second.error), ErrorCode::RequestOutOfRange);
    assert!(drive.file(device, "racing.csv").is_none());
}

#[test]
fn removing_the_marker_after_install_planning_aborts_the_write() {
    let (drive, device) = FakeQuadStick::with_device();
    let clock = ManualClock::new();
    let shell = shell(&drive, &clock);
    let plan = shell
        .prepare_install(&device.to_string(), &profile("racing.csv"))
        .expect("plan");

    drive.remove_marker(device);
    let failure = shell
        .commit_install(json!({ "planId": plan.plan_id }))
        .unwrap_err();

    assert_eq!(code(&failure.error), ErrorCode::DeviceNotQuadStick);
    assert!(drive.file(device, "racing.csv").is_none());
}

#[test]
fn a_clean_install_commits_only_the_native_prepared_plan() {
    let (drive, device) = FakeQuadStick::with_device();
    let clock = ManualClock::new();
    let shell = shell(&drive, &clock);
    let plan = shell
        .prepare_install(&device.to_string(), &profile("racing.csv"))
        .expect("plan");

    let receipt = shell
        .commit_install(json!({ "planId": plan.plan_id }))
        .expect("install");

    assert_eq!(receipt.target, "racing.csv");
    assert!(receipt.confirmed_on_device);
    assert!(drive.file(device, "racing.csv").is_some());
    assert!(drive.stray_temp_names(device).is_empty());
}

#[test]
fn cancelling_the_folder_picker_changes_nothing() {
    let (drive, _) = FakeQuadStick::with_device();
    let clock = ManualClock::new();
    let shell = shell(&drive, &clock);
    let before = shell.refresh_devices().unwrap();

    let selected = shell.choose_device_folder().unwrap();
    let after = shell.list_devices().unwrap();

    assert!(selected.is_none());
    assert_eq!(before.devices, after.devices);
}
