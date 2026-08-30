//! The registered commands.
//!
//! Each one is a wrapper and nothing else: read the state, call the method,
//! redact the error. The work is in [`crate::shell`] so a test can drive the
//! whole surface without a window.
//!
//! They are synchronous on purpose. Tauri runs a synchronous command on the main
//! thread, which is where a native file dialog has to be opened on macOS. An
//! async command would move the picker onto a worker and the modal would have to
//! be marshalled back. Nothing here does enough work to be worth that: the files
//! are a few kilobytes of CSV.
//!
//! Nothing here takes a path, and nothing here returns one. The window names a
//! profile by an opaque session id and a file by a display name, and the only
//! thing that can turn either into a place on the machine is an adapter.

use crate::ipc::{AppSnapshotDto, CloseOutcomeDto};
use crate::shell::ShellState;
use qcm_core::error::QcmErrorDto;
use qcm_core::profiles::{EditorSnapshot, SaveReceiptDto};
use qcm_core::settings::AppSettingsDto;
use serde_json::Value;
use tauri::State;

/// Every command answers with this shape on failure. A raw string never
/// crosses: the window switches on `code`, and `message` is the fallback text.
///
/// Boxed because the DTO is the wider of the two arms and every command returns
/// it. `Box` is transparent to serde, so the JSON a window receives is the same
/// object either way.
type Failure = Box<QcmErrorDto>;

fn redact<T>(result: Result<T, qcm_core::QcmError>) -> Result<T, Failure> {
    result.map_err(|error| Box::new(QcmErrorDto::from(&error)))
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
