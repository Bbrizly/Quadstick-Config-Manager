//! Registered Tauri commands.
//!
//! Every wrapper does three things only: parse through our stable request
//! boundary, call a shell method, and redact the error. No command accepts or
//! returns a host path.

use crate::device_ipc::{
    DeletePlanDto, DeleteReceiptDto, DeviceLibrarySnapshotDto, DevicePresenceSnapshotDto,
    InstallPlanDto, InstallReceiptDto, PrepareInstallRequest,
};
use crate::device_shell::{DeviceOperationError, DeviceShellState};
use crate::ipc::{AppSnapshotDto, CloseOutcomeDto, parse};
use crate::shell::ShellState;
use qcm_core::error::QcmErrorDto;
use qcm_core::profiles::{EditorSnapshot, SaveReceiptDto};
use qcm_core::settings::AppSettingsDto;
use serde_json::Value;
use tauri::State;

type Failure = Box<QcmErrorDto>;

fn redact<T>(result: Result<T, qcm_core::QcmError>) -> Result<T, Failure> {
    result.map_err(|error| Box::new(QcmErrorDto::from(&error)))
}

fn redact_operation<T>(result: Result<T, DeviceOperationError>) -> Result<T, Failure> {
    result.map_err(|failure| {
        Box::new(QcmErrorDto::new(&failure.error, failure.operation))
    })
}

#[tauri::command]
pub fn get_app_snapshot(state: State<'_, ShellState>) -> AppSnapshotDto {
    state.app_snapshot()
}

#[tauri::command]
pub fn get_settings(state: State<'_, ShellState>) -> AppSettingsDto {
    state.get_settings()
}

#[tauri::command]
pub fn update_settings(
    state: State<'_, ShellState>,
    request: Value,
) -> Result<AppSettingsDto, Failure> {
    redact(state.update_settings(request))
}

#[tauri::command]
pub fn new_profile(
    state: State<'_, ShellState>,
    request: Value,
) -> Result<EditorSnapshot, Failure> {
    redact(state.new_profile(request))
}

#[tauri::command]
pub fn choose_and_open_profile(
    state: State<'_, ShellState>,
) -> Result<Option<EditorSnapshot>, Failure> {
    redact(state.choose_and_open_profile())
}

#[tauri::command]
pub fn apply_editor_ops(
    state: State<'_, ShellState>,
    request: Value,
) -> Result<EditorSnapshot, Failure> {
    redact(state.apply_editor_ops(request))
}

#[tauri::command]
pub fn undo_editor(
    state: State<'_, ShellState>,
    request: Value,
) -> Result<EditorSnapshot, Failure> {
    redact(state.undo_editor(request))
}

#[tauri::command]
pub fn save_profile(
    state: State<'_, ShellState>,
    request: Value,
) -> Result<SaveReceiptDto, Failure> {
    redact(state.save_profile(request))
}

#[tauri::command]
pub fn save_profile_as(
    state: State<'_, ShellState>,
    request: Value,
) -> Result<Option<SaveReceiptDto>, Failure> {
    redact(state.save_profile_as(request))
}

#[tauri::command]
pub fn close_profile(
    state: State<'_, ShellState>,
    request: Value,
) -> Result<CloseOutcomeDto, Failure> {
    redact(state.close_profile(request))
}

#[tauri::command]
pub fn list_devices(
    state: State<'_, DeviceShellState>,
) -> Result<DevicePresenceSnapshotDto, Failure> {
    redact(state.list_devices())
}

#[tauri::command]
pub fn refresh_devices(
    state: State<'_, DeviceShellState>,
) -> Result<DevicePresenceSnapshotDto, Failure> {
    redact(state.refresh_devices())
}

#[tauri::command]
pub fn choose_device_folder(
    state: State<'_, DeviceShellState>,
) -> Result<Option<DevicePresenceSnapshotDto>, Failure> {
    redact(state.choose_device_folder())
}

#[tauri::command]
pub fn get_device_library(
    state: State<'_, DeviceShellState>,
    request: Value,
) -> Result<DeviceLibrarySnapshotDto, Failure> {
    redact(state.get_device_library(request))
}

#[tauri::command]
pub fn prepare_install(
    profile_state: State<'_, ShellState>,
    device_state: State<'_, DeviceShellState>,
    request: Value,
) -> Result<InstallPlanDto, Failure> {
    let request: PrepareInstallRequest =
        redact(parse(request, "prepare_install request"))?;
    let file = redact(profile_state.profile_for_install(&request.session_id))?;
    redact(device_state.prepare_install(&request.device_id, &file))
}

#[tauri::command]
pub fn commit_install(
    state: State<'_, DeviceShellState>,
    request: Value,
) -> Result<InstallReceiptDto, Failure> {
    redact_operation(state.commit_install(request))
}

#[tauri::command]
pub fn prepare_delete_device_profile(
    state: State<'_, DeviceShellState>,
    request: Value,
) -> Result<DeletePlanDto, Failure> {
    redact(state.prepare_delete(request))
}

#[tauri::command]
pub fn commit_delete_device_profile(
    state: State<'_, DeviceShellState>,
    request: Value,
) -> Result<DeleteReceiptDto, Failure> {
    redact_operation(state.commit_delete(request))
}

#[tauri::command]
pub fn open_device_profile(
    profile_state: State<'_, ShellState>,
    device_state: State<'_, DeviceShellState>,
    request: Value,
) -> Result<EditorSnapshot, Failure> {
    let opened = redact(device_state.open_device_profile(request))?;
    Ok(profile_state.open_device_copy(
        opened.device,
        opened.generation,
        opened.name,
        &opened.csv_text,
    ))
}

#[tauri::command]
pub fn open_device_preferences(
    profile_state: State<'_, ShellState>,
    device_state: State<'_, DeviceShellState>,
    request: Value,
) -> Result<EditorSnapshot, Failure> {
    let opened = redact(device_state.open_device_preferences(request))?;
    Ok(profile_state.open_device_copy(
        opened.device,
        opened.generation,
        opened.name,
        &opened.csv_text,
    ))
}
