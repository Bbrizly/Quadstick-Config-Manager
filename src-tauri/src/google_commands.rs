use crate::google_auth::{GoogleAuthService, GoogleAuthStatusDto};
use qcm_core::error::{InternalError, OsDetail, QcmError, QcmErrorDto};
use std::sync::Arc;
use tauri::State;

type Failure = Box<QcmErrorDto>;

fn redact<T>(result: Result<T, QcmError>) -> Result<T, Failure> {
    result.map_err(|error| Box::new(QcmErrorDto::from(&error)))
}

fn join_error(error: impl std::fmt::Display) -> Failure {
    Box::new(QcmErrorDto::from(&QcmError::Internal(InternalError {
        what: "Google authentication worker",
        detail: OsDetail::new(error.to_string()),
    })))
}

#[tauri::command]
pub fn get_google_auth_status(
    state: State<'_, Arc<GoogleAuthService>>,
) -> Result<GoogleAuthStatusDto, Failure> {
    redact(state.status())
}

#[tauri::command]
pub async fn connect_google(
    state: State<'_, Arc<GoogleAuthService>>,
) -> Result<GoogleAuthStatusDto, Failure> {
    let auth = Arc::clone(state.inner());
    tauri::async_runtime::spawn_blocking(move || auth.connect())
        .await
        .map_err(join_error)
        .and_then(redact)
}

#[tauri::command]
pub fn disconnect_google(
    state: State<'_, Arc<GoogleAuthService>>,
) -> Result<GoogleAuthStatusDto, Failure> {
    redact(state.disconnect())
}
