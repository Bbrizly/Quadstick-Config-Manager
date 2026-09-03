//! Registered Tauri commands.
//!
//! Every wrapper does three things only: parse through our stable request
//! boundary, call a shell method, and redact the error. No command accepts or
//! returns a host path. Streaming is caller-scoped through typed IPC channels;
//! there are no global telemetry-frame events.

use crate::device_ipc::{
    DeletePlanDto, DeleteReceiptDto, DeviceLibrarySnapshotDto, DevicePresenceSnapshotDto,
    InstallPlanDto, InstallReceiptDto, PrepareInstallRequest,
};
use crate::device_rename_ipc::RenameDeviceProfileReceiptDto;
use crate::device_shell::{DeviceOperationError, DeviceShellState};
use crate::ipc::{AppSnapshotDto, CloseOutcomeDto, parse};
use crate::shell::ShellState;
use crate::streaming::{
    DeviceInvalidationDto, DeviceInvalidationHub, LiveRuntime, LiveSnapshotDto, SubscriptionDto,
};
use crate::workbook_shell::{
    WorkbookExportReceiptDto, WorkbookImportReviewDto, WorkbookShellState,
};
use qcm_core::error::{QcmErrorDto, StorageStage};
use qcm_core::profiles::{EditorSnapshot, SaveReceiptDto};
use qcm_core::settings::AppSettingsDto;
use serde::Serialize;
use serde_json::Value;
use tauri::State;
use tauri::ipc::Channel;

type Failure = Box<QcmErrorDto>;

fn redact<T>(result: Result<T, qcm_core::QcmError>) -> Result<T, Failure> {
    result.map_err(|error| Box::new(QcmErrorDto::from(&error)))
}

fn redact_operation<T>(result: Result<T, DeviceOperationError>) -> Result<T, Failure> {
    result.map_err(|failure| Box::new(QcmErrorDto::new(&failure.error, failure.operation)))
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct InstallProgressDto {
    pub stage: String,
}

impl From<StorageStage> for InstallProgressDto {
    fn from(stage: StorageStage) -> Self {
        Self {
            stage: stage.as_str().to_owned(),
        }
    }
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
pub fn get_profile_snapshot(
    state: State<'_, ShellState>,
    request: Value,
) -> Result<EditorSnapshot, Failure> {
    redact(state.get_profile_snapshot(request))
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
pub fn choose_and_import_workbook(
    state: State<'_, WorkbookShellState>,
) -> Result<Option<WorkbookImportReviewDto>, Failure> {
    redact(state.choose_import())
}

#[tauri::command]
pub fn repair_workbook_tab(
    state: State<'_, WorkbookShellState>,
    request: Value,
) -> Result<WorkbookImportReviewDto, Failure> {
    redact(state.repair_tab(request))
}

#[tauri::command]
pub fn accept_workbook_import(
    workbook: State<'_, WorkbookShellState>,
    profiles: State<'_, ShellState>,
    request: Value,
) -> Result<EditorSnapshot, Failure> {
    let accepted = redact(workbook.accept_import(request))?;
    redact(profiles.open_workbook_copy(&accepted.name, &accepted.csv))
}

#[tauri::command]
pub fn cancel_workbook_import(
    state: State<'_, WorkbookShellState>,
    request: Value,
) -> Result<(), Failure> {
    redact(state.cancel_import(request))
}

#[tauri::command]
pub fn export_profile_xlsx(
    profiles: State<'_, ShellState>,
    workbook: State<'_, WorkbookShellState>,
    request: Value,
) -> Result<Option<WorkbookExportReceiptDto>, Failure> {
    let (profile, suggested) = redact(profiles.profile_for_export(request))?;
    redact(workbook.export_profile(&profile, &suggested))
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
    invalidation: State<'_, DeviceInvalidationHub>,
) -> Result<DevicePresenceSnapshotDto, Failure> {
    let snapshot = redact(state.refresh_devices())?;
    if snapshot.changed {
        invalidation.notify();
    }
    Ok(snapshot)
}

#[tauri::command]
pub fn choose_device_folder(
    state: State<'_, DeviceShellState>,
    invalidation: State<'_, DeviceInvalidationHub>,
) -> Result<Option<DevicePresenceSnapshotDto>, Failure> {
    let snapshot = redact(state.choose_device_folder())?;
    if snapshot.as_ref().is_some_and(|value| value.changed) {
        invalidation.notify();
    }
    Ok(snapshot)
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
    let request: PrepareInstallRequest = redact(parse(request, "prepare_install request"))?;
    let file = redact(profile_state.profile_for_install(&request.session_id))?;
    redact(device_state.prepare_install(&request.device_id, &file))
}

#[tauri::command]
pub fn commit_install(
    state: State<'_, DeviceShellState>,
    invalidation: State<'_, DeviceInvalidationHub>,
    request: Value,
    progress: Channel<InstallProgressDto>,
) -> Result<InstallReceiptDto, Failure> {
    let receipt = redact_operation(state.commit_install_with_progress(request, |stage| {
        let _ = progress.send(InstallProgressDto::from(stage));
    }))?;
    invalidation.notify();
    Ok(receipt)
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
    invalidation: State<'_, DeviceInvalidationHub>,
    request: Value,
) -> Result<DeleteReceiptDto, Failure> {
    let receipt = redact_operation(state.commit_delete(request))?;
    invalidation.notify();
    Ok(receipt)
}

#[tauri::command]
pub fn rename_device_profile(
    state: State<'_, DeviceShellState>,
    invalidation: State<'_, DeviceInvalidationHub>,
    request: Value,
) -> Result<RenameDeviceProfileReceiptDto, Failure> {
    let receipt = redact(state.rename_profile(request))?;
    invalidation.notify();
    Ok(receipt)
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

#[tauri::command]
pub fn start_live_input(
    state: State<'_, LiveRuntime>,
    on_frame: Channel<LiveSnapshotDto>,
) -> SubscriptionDto {
    state.subscribe(on_frame)
}

#[tauri::command]
pub fn stop_live_input(state: State<'_, LiveRuntime>, subscription_id: String) {
    state.unsubscribe(&subscription_id);
}

#[tauri::command]
pub fn subscribe_devices_changed(
    state: State<'_, DeviceInvalidationHub>,
    on_changed: Channel<DeviceInvalidationDto>,
) -> SubscriptionDto {
    state.subscribe(on_changed)
}

#[tauri::command]
pub fn unsubscribe_devices_changed(
    state: State<'_, DeviceInvalidationHub>,
    subscription_id: String,
) {
    state.unsubscribe(&subscription_id);
}
