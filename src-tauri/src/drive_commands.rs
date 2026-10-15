use crate::drive::{
    ConflictChoice, DriveBackupOutcomeDto, DriveFileDto, DriveResolution, DriveService,
    DriveShareDto, RestoreSummaryDto,
};
use crate::ipc::{SessionRevisionRequest, parse};
use crate::shell::ShellState;
use crate::telemetry::ProfileSource;
use crate::workbook_shell::{WorkbookImportReviewDto, WorkbookShellState};
use qcm_core::error::{InternalError, OsDetail, QcmError, QcmErrorDto, RequestError};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::sync::Arc;
use tauri::State;

type Failure = Box<QcmErrorDto>;

fn redact<T>(result: Result<T, QcmError>) -> Result<T, Failure> {
    result.map_err(|error| Box::new(QcmErrorDto::from(&error)))
}
fn join_error(error: impl std::fmt::Display) -> Failure {
    Box::new(QcmErrorDto::from(&QcmError::Internal(InternalError {
        what: "Drive worker",
        detail: OsDetail::new(error.to_string()),
    })))
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ResolveDriveRequest {
    resolution_id: String,
    choice: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct CloudRefRequest {
    cloud_ref: String,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", tag = "kind")]
pub enum DriveResolutionDto {
    Finished { result: DriveBackupOutcomeDto },
    Review { review: WorkbookImportReviewDto },
}

#[tauri::command]
pub async fn backup_profile_to_drive(
    profiles: State<'_, ShellState>,
    drive: State<'_, Arc<DriveService>>,
    request: Value,
) -> Result<DriveBackupOutcomeDto, Failure> {
    let profile = redact(profiles.profile_for_drive(request))?;
    let service = Arc::clone(drive.inner());
    let outcome = tauri::async_runtime::spawn_blocking(move || service.backup(profile))
        .await
        .map_err(join_error)
        .and_then(redact)?;
    crate::telemetry::global().feature_used(crate::telemetry::AppFeature::DriveBackup);
    Ok(outcome)
}

#[tauri::command]
pub async fn resolve_drive_conflict(
    drive: State<'_, Arc<DriveService>>,
    workbook: State<'_, WorkbookShellState>,
    request: Value,
) -> Result<DriveResolutionDto, Failure> {
    let request: ResolveDriveRequest = redact(parse(request, "resolve_drive_conflict request"))?;
    let choice = match request.choice.as_str() {
        "replace_with_mine" => ConflictChoice::ReplaceWithMine,
        "keep_online" => ConflictChoice::KeepOnline,
        "recreate" => ConflictChoice::Recreate,
        "disable" => ConflictChoice::Disable,
        _ => {
            return Err(Box::new(QcmErrorDto::from(&QcmError::Request(
                RequestError::OutOfRange {
                    what: "Drive conflict choice",
                },
            ))));
        }
    };
    let service = Arc::clone(drive.inner());
    let resolution_id = request.resolution_id;
    let resolution =
        tauri::async_runtime::spawn_blocking(move || service.resolve(&resolution_id, choice))
            .await
            .map_err(join_error)
            .and_then(redact)?;
    match resolution {
        DriveResolution::Finished(result) => Ok(DriveResolutionDto::Finished { result }),
        DriveResolution::KeepRemote { name, bytes } => {
            let review = redact(workbook.import_bytes(&name, bytes, ProfileSource::Drive))?;
            Ok(DriveResolutionDto::Review { review })
        }
    }
}

#[tauri::command]
pub async fn list_drive_backups(
    drive: State<'_, Arc<DriveService>>,
) -> Result<Vec<DriveFileDto>, Failure> {
    let service = Arc::clone(drive.inner());
    tauri::async_runtime::spawn_blocking(move || service.list_backups())
        .await
        .map_err(join_error)
        .and_then(redact)
}

#[tauri::command]
pub async fn restore_drive_backup(
    drive: State<'_, Arc<DriveService>>,
    workbook: State<'_, WorkbookShellState>,
    request: Value,
) -> Result<WorkbookImportReviewDto, Failure> {
    let request: CloudRefRequest = redact(parse(request, "restore_drive_backup request"))?;
    let service = Arc::clone(drive.inner());
    let restored =
        tauri::async_runtime::spawn_blocking(move || service.restore_workbook(&request.cloud_ref))
            .await
            .map_err(join_error)
            .and_then(redact)?;
    let review = redact(workbook.import_bytes(&restored.0, restored.1, ProfileSource::Drive))?;
    crate::telemetry::global().feature_used(crate::telemetry::AppFeature::DriveRestore);
    Ok(review)
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct CloudRefsRequest {
    cloud_refs: Vec<String>,
}

/// Bulk restore into the profile library (`DriveBackup.RestoreAsync`).
#[tauri::command]
pub async fn restore_drive_backups(
    drive: State<'_, Arc<DriveService>>,
    request: Value,
) -> Result<RestoreSummaryDto, Failure> {
    let request: CloudRefsRequest = redact(parse(request, "restore_drive_backups request"))?;
    if request.cloud_refs.len() > 500 {
        return Err(Box::new(QcmErrorDto::from(&QcmError::Request(
            RequestError::TooLarge {
                what: "Drive restore picks",
                limit: 500,
                actual: request.cloud_refs.len(),
            },
        ))));
    }
    let service = Arc::clone(drive.inner());
    let summary = tauri::async_runtime::spawn_blocking(move || {
        service.restore_into_library(&request.cloud_refs, &crate::drive::library_dir())
    })
    .await
    .map_err(join_error)
    .and_then(redact)?;
    // Opening the picker is not using restore; only an import is.
    if !summary.imported.is_empty() {
        crate::telemetry::global().feature_used(crate::telemetry::AppFeature::DriveRestore);
    }
    Ok(summary)
}

#[tauri::command]
pub async fn share_drive_profile(
    profiles: State<'_, ShellState>,
    drive: State<'_, Arc<DriveService>>,
    request: Value,
) -> Result<DriveShareDto, Failure> {
    let raw: SessionRevisionRequest =
        redact(parse(request.clone(), "share_drive_profile request"))?;
    let profile = redact(profiles.profile_for_drive(request))?;
    if profile.revision != raw.expected_revision {
        return Err(Box::new(QcmErrorDto::from(&QcmError::Request(
            RequestError::OutOfRange {
                what: "Drive profile revision",
            },
        ))));
    }
    let key = profile.persistent_key;
    let service = Arc::clone(drive.inner());
    let share = tauri::async_runtime::spawn_blocking(move || service.share(&key))
        .await
        .map_err(join_error)
        .and_then(redact)?;
    crate::telemetry::global().feature_used(crate::telemetry::AppFeature::ShareLink);
    Ok(share)
}
