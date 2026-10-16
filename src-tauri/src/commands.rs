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
use crate::drive::DriveService;
use crate::drive_auto::{BackupMode, DriveBackupHub, fire};
use crate::ipc::{AppSnapshotDto, CloseOutcomeDto, parse};
use crate::preference_ipc::{PreferenceCatalogDto, preference_catalog};
use crate::shell::ShellState;
use crate::streaming::{
    DeviceInvalidationDto, DeviceInvalidationHub, LiveRuntime, LiveSnapshotDto, SubscriptionDto,
};
use crate::workbook_shell::{
    WorkbookExportReceiptDto, WorkbookImportReviewDto, WorkbookShellState,
};
use qcm_core::error::{ProfileError, QcmError, QcmErrorDto, StorageStage};
use qcm_core::profiles::{EditorSnapshot, SaveReceiptDto};
use qcm_core::settings::AppSettingsDto;
use serde::Serialize;
use serde_json::Value;
use std::sync::Arc;
use tauri::State;
use tauri::ipc::Channel;

use crate::telemetry::{self, AppFeature, InstallFailure, ProfileSource};
use qcm_core::ErrorCode;

type Failure = Box<QcmErrorDto>;

type Drive<'a> = State<'a, Arc<DriveService>>;
type Backups<'a> = State<'a, Arc<DriveBackupHub>>;

fn request_session(request: &Value) -> Option<&str> {
    request.get("sessionId").and_then(Value::as_str)
}

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
    let opened = redact(state.new_profile(request))?;
    telemetry::global().opened(ProfileSource::New, None);
    Ok(opened)
}

#[tauri::command]
pub fn choose_and_open_profile(
    state: State<'_, ShellState>,
    drive: Drive<'_>,
    backups: Backups<'_>,
) -> Result<Option<EditorSnapshot>, Failure> {
    let opened = redact(state.choose_and_open_profile())?;
    if let Some(snapshot) = &opened {
        telemetry::global().opened(ProfileSource::File, None);
        // A profile opened from a path retries its backup if it was left dirty.
        let profile = state.drive_snapshot(&snapshot.session_id);
        fire(&drive, &backups, profile, BackupMode::RetryIfDirty);
    }
    Ok(opened)
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
    drive: Drive<'_>,
    backups: Backups<'_>,
    request: Value,
) -> Result<SaveReceiptDto, Failure> {
    let session = request_session(&request).map(str::to_owned);
    let saved = redact(state.save_profile(request))?;
    telemetry::global().saved();
    // `FireBackupPush`: the local save is done; the push never holds it up.
    let profile = session.and_then(|session| state.drive_snapshot(&session));
    fire(&drive, &backups, profile, BackupMode::AfterSave);
    Ok(saved)
}

#[tauri::command]
pub fn save_profile_as(
    state: State<'_, ShellState>,
    drive: Drive<'_>,
    backups: Backups<'_>,
    request: Value,
) -> Result<Option<SaveReceiptDto>, Failure> {
    let session = request_session(&request).map(str::to_owned);
    let saved = redact(state.save_profile_as(request))?;
    if saved.is_some() {
        telemetry::global().saved();
        let profile = session.and_then(|session| state.drive_snapshot(&session));
        fire(&drive, &backups, profile, BackupMode::AfterSave);
    }
    Ok(saved)
}

#[tauri::command]
pub fn close_profile(
    state: State<'_, ShellState>,
    drive: Drive<'_>,
    backups: Backups<'_>,
    request: Value,
) -> Result<CloseOutcomeDto, Failure> {
    // The session is gone once it closes, so what a save on the way out
    // writes is taken first, and pushed only if that save happened.
    let saving = request.get("disposition").and_then(Value::as_str) == Some("save");
    let profile = request_session(&request)
        .filter(|_| saving)
        .and_then(|session| state.drive_snapshot_before_close(session));
    let outcome = redact(state.close_profile(request))?;
    if matches!(outcome, CloseOutcomeDto::SavedAndClosed { .. }) {
        fire(&drive, &backups, profile, BackupMode::AfterSave);
    }
    Ok(outcome)
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
    let opened = redact(profiles.open_workbook_copy(&accepted.name, &accepted.csv))?;
    // Remembered against the file once it is saved, so Get the latest can
    // ask the same sheet again later.
    if let Some(link) = accepted.follow_link {
        profiles.set_pending_follow(&opened.session_id, link);
    }
    // A community profile is a Google Sheet import, as in the shipped app:
    // counted only once it actually opened.
    if accepted.source == ProfileSource::Sheets {
        telemetry::global().feature_used(AppFeature::SheetsImport);
    }
    telemetry::global().opened(accepted.source, None);
    Ok(opened)
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
pub fn get_preference_catalog() -> PreferenceCatalogDto {
    preference_catalog()
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
pub fn device_location(
    state: State<'_, DeviceShellState>,
    request: Value,
) -> Result<Option<String>, Failure> {
    redact(state.device_location(request))
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
    let telemetry = telemetry::global();
    telemetry.install_attempted();
    let file = profile_state.profile_for_install(&request.session_id);
    if let Err(QcmError::Profile(ProfileError::UnknownSession)) = &file {
        telemetry.install_failed(InstallFailure::NoProfile);
    }
    let file = redact(file)?;
    let plan = device_state.prepare_install(&request.device_id, &file);
    if let Err(error) = &plan {
        match error.code() {
            ErrorCode::ConfigHasBlockingProblems => {
                telemetry.install_failed(InstallFailure::HasErrors);
            }
            ErrorCode::DeviceNotQuadStick | ErrorCode::DeviceNotFound => {
                telemetry.install_failed(InstallFailure::NotAQuadstick);
            }
            _ => {}
        }
    }
    redact(plan)
}

/// The person backed out of an install: `InstallFlow.cs` reports these as
/// `install_failed` with a `cancelled_*` reason. Only those four are taken.
#[tauri::command]
pub fn record_install_cancelled(request: Value) -> Result<bool, Failure> {
    #[derive(serde::Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct Request {
        reason: String,
    }
    let request: Request = redact(parse(request, "record_install_cancelled request"))?;
    let reason = InstallFailure::cancelled_from_wire(&request.reason).ok_or_else(|| {
        Box::new(QcmErrorDto::from(&QcmError::Request(
            qcm_core::error::RequestError::OutOfRange {
                what: "install cancel reason",
            },
        )))
    })?;
    let telemetry = telemetry::global();
    if reason.before_prepare() {
        telemetry.install_attempted();
    }
    Ok(telemetry.install_failed(reason))
}

#[tauri::command]
pub fn commit_install(
    state: State<'_, DeviceShellState>,
    invalidation: State<'_, DeviceInvalidationHub>,
    request: Value,
    progress: Channel<InstallProgressDto>,
) -> Result<InstallReceiptDto, Failure> {
    let result = state.commit_install_with_progress(request, |stage| {
        let _ = progress.send(InstallProgressDto::from(stage));
    });
    match &result {
        Ok(_) => {
            telemetry::global().installed();
        }
        // Storage and device failures only: a refused confirmation or a
        // malformed request is not an I/O error.
        Err(failure) if matches!(failure.error, QcmError::Storage(_) | QcmError::Device(_)) => {
            telemetry::global().install_failed(InstallFailure::IoError);
        }
        Err(_) => {}
    }
    let receipt = redact_operation(result)?;
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
    let snapshot = profile_state.open_device_copy(
        opened.device,
        opened.generation,
        opened.name,
        &opened.csv_text,
    );
    telemetry::global().opened(ProfileSource::Device, None);
    Ok(snapshot)
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

// ---- Home library and templates (`library_shell.rs`) ----

#[tauri::command]
pub fn list_library_profiles(
    state: State<'_, ShellState>,
    cards: State<'_, crate::library_shell::LibraryCards>,
) -> Vec<crate::library_shell::LibraryCardDto> {
    state.library_cards(&cards, &crate::drive::library_dir())
}

#[tauri::command]
pub fn open_library_profile(
    state: State<'_, ShellState>,
    cards: State<'_, crate::library_shell::LibraryCards>,
    drive: Drive<'_>,
    backups: Backups<'_>,
    request: Value,
) -> Result<EditorSnapshot, Failure> {
    let opened = redact(state.open_library_card(&cards, request))?;
    telemetry::global().opened(ProfileSource::Library, None);
    let profile = state.drive_snapshot(&opened.session_id);
    fire(&drive, &backups, profile, BackupMode::RetryIfDirty);
    Ok(opened)
}

#[tauri::command]
pub fn delete_library_profile(
    state: State<'_, ShellState>,
    cards: State<'_, crate::library_shell::LibraryCards>,
    request: Value,
) -> Result<(), Failure> {
    redact(state.delete_library_card(&cards, request))
}

#[tauri::command]
pub fn list_templates(
    state: State<'_, ShellState>,
    cards: State<'_, crate::library_shell::LibraryCards>,
) -> Vec<crate::library_shell::TemplateDto> {
    state.template_list(&cards, &crate::library_shell::templates_dir())
}

#[tauri::command]
pub fn open_template(
    state: State<'_, ShellState>,
    cards: State<'_, crate::library_shell::LibraryCards>,
    request: Value,
) -> Result<EditorSnapshot, Failure> {
    let opened = redact(state.open_template_card(&cards, request))?;
    telemetry::global().opened(ProfileSource::New, None);
    Ok(opened)
}

#[tauri::command]
pub fn rename_template(
    cards: State<'_, crate::library_shell::LibraryCards>,
    request: Value,
) -> Result<crate::library_shell::TemplateRenameDto, Failure> {
    redact(crate::library_shell::rename_template_card(
        &cards,
        &crate::library_shell::templates_dir(),
        request,
    ))
}

#[tauri::command]
pub fn delete_template(
    cards: State<'_, crate::library_shell::LibraryCards>,
    request: Value,
) -> Result<(), Failure> {
    redact(crate::library_shell::delete_template_card(&cards, request))
}

#[tauri::command]
pub fn save_template(
    state: State<'_, ShellState>,
    request: Value,
) -> Result<crate::library_shell::TemplateReceiptDto, Failure> {
    redact(state.save_template(&crate::library_shell::templates_dir(), request))
}

// ---- Custom output names not used yet (`names_shell.rs`) ----

#[tauri::command]
pub fn get_custom_name_drafts(
    state: State<'_, ShellState>,
    request: Value,
) -> Result<crate::names_shell::CustomNameDraftsDto, Failure> {
    redact(state.custom_name_drafts(request))
}

#[tauri::command]
pub fn set_custom_name_drafts(
    state: State<'_, ShellState>,
    request: Value,
) -> Result<crate::names_shell::CustomNameDraftsDto, Failure> {
    redact(state.set_custom_name_drafts(request))
}
