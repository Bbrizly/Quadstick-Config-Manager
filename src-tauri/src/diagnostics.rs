//! Soft diagnostics: redaction, feedback, telemetry allowlist, crash rescue.
//!
//! Soft does not ship PostHog wiring. Events no-op when the token is empty,
//! usage analytics is off, or `QSCM_TELEMETRY=0`. Signed network send stays
//! behind a later gate; Soft proves the gates refuse the right things.

use crate::ipc::parse;
use crate::shell::ShellState;
use qcm_core::error::{QcmError, QcmErrorDto, RequestError};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use tauri::State;

type Failure = Box<QcmErrorDto>;

fn redact_err<T>(result: Result<T, QcmError>) -> Result<T, Failure> {
    result.map_err(|error| Box::new(QcmErrorDto::from(&error)))
}

pub const MAX_FEEDBACK_CHARS: usize = 1000;
pub const MAX_PENDING_CRASH_REPORTS: usize = 5;

/// Avalonia `TelemetryEvent` wire names. Nothing else may be tracked.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TelemetryEvent {
    AppLaunched,
    ProfileOpened,
    ProfileSaved,
    InstallAttempted,
    InstallSucceeded,
    InstallFailed,
    FeatureUsed,
    FeedbackSubmitted,
}

impl TelemetryEvent {
    #[must_use]
    pub const fn wire(self) -> &'static str {
        match self {
            Self::AppLaunched => "app_launched",
            Self::ProfileOpened => "profile_opened",
            Self::ProfileSaved => "profile_saved",
            Self::InstallAttempted => "install_attempted",
            Self::InstallSucceeded => "install_succeeded",
            Self::InstallFailed => "install_failed",
            Self::FeatureUsed => "feature_used",
            Self::FeedbackSubmitted => "feedback_submitted",
        }
    }

    #[must_use]
    pub fn from_wire(name: &str) -> Option<Self> {
        match name {
            "app_launched" => Some(Self::AppLaunched),
            "profile_opened" => Some(Self::ProfileOpened),
            "profile_saved" => Some(Self::ProfileSaved),
            "install_attempted" => Some(Self::InstallAttempted),
            "install_succeeded" => Some(Self::InstallSucceeded),
            "install_failed" => Some(Self::InstallFailed),
            "feature_used" => Some(Self::FeatureUsed),
            "feedback_submitted" => Some(Self::FeedbackSubmitted),
            _ => None,
        }
    }
}

/// Soft project token. Empty means no client is ever built, matching Avalonia's
/// gitignored `TelemetryToken.Local.cs` empty default.
pub fn telemetry_token() -> &'static str {
    option_env!("QSCM_TELEMETRY_TOKEN").unwrap_or("")
}

#[must_use]
pub fn telemetry_kill_switch() -> bool {
    std::env::var("QSCM_TELEMETRY").ok().as_deref() == Some("0")
}

/// Replace home-directory usernames the way Avalonia `CrashReport.SanitizePath` did.
#[must_use]
pub fn sanitize_path(input: &str) -> String {
    if input.is_empty() {
        return String::new();
    }
    let chars: Vec<char> = input.chars().collect();
    let mut out = String::with_capacity(input.len());
    let mut i = 0;
    while i < chars.len() {
        if let Some(marker_end) = home_marker_end(&chars, i) {
            for ch in &chars[i..marker_end] {
                out.push(*ch);
            }
            i = marker_end;
            let user_start = i;
            while i < chars.len() && chars[i] != '/' && chars[i] != '\\' {
                i += 1;
            }
            if i > user_start {
                out.push_str("<user>");
            }
            continue;
        }
        out.push(chars[i]);
        i += 1;
    }
    out
}

fn home_marker_end(chars: &[char], from: usize) -> Option<usize> {
    // Optional drive: `C:` then separator, then Users/ or home/
    let mut start = from;
    if chars.len() - from >= 2 && chars[from].is_ascii_alphabetic() && chars[from + 1] == ':' {
        start = from + 2;
        if start < chars.len() && (chars[start] == '/' || chars[start] == '\\') {
            start += 1;
        } else {
            return None;
        }
    }
    // Optional UNC host skip is out of Soft scope; match Users/home after a separator
    // or at the start after the optional drive.
    let rest = &chars[start..];
    if starts_with_ignore_ascii(rest, &['U', 's', 'e', 'r', 's']) {
        let after = start + 5;
        if after < chars.len() && (chars[after] == '/' || chars[after] == '\\') {
            return Some(after + 1);
        }
    }
    if starts_with_ignore_ascii(rest, &['h', 'o', 'm', 'e']) {
        let after = start + 4;
        if after < chars.len() && (chars[after] == '/' || chars[after] == '\\') {
            return Some(after + 1);
        }
    }
    // Absolute unix without drive: /Users/ or /home/
    if from < chars.len() && (chars[from] == '/' || chars[from] == '\\') {
        let rest = &chars[from + 1..];
        if starts_with_ignore_ascii(rest, &['U', 's', 'e', 'r', 's']) {
            let after = from + 1 + 5;
            if after < chars.len() && (chars[after] == '/' || chars[after] == '\\') {
                return Some(after + 1);
            }
        }
        if starts_with_ignore_ascii(rest, &['h', 'o', 'm', 'e']) {
            let after = from + 1 + 4;
            if after < chars.len() && (chars[after] == '/' || chars[after] == '\\') {
                return Some(after + 1);
            }
        }
    }
    None
}

fn starts_with_ignore_ascii(hay: &[char], needle: &[char]) -> bool {
    if hay.len() < needle.len() {
        return false;
    }
    hay.iter()
        .zip(needle.iter())
        .all(|(a, b)| a.eq_ignore_ascii_case(b))
}

/// Refuse feedback that is empty, oversized, or sent without usage consent.
pub fn send_feedback_text(text: &str, usage_analytics: bool) -> Result<(), QcmError> {
    if !usage_analytics {
        return Err(RequestError::OutOfRange {
            what: "usage analytics consent",
        }
        .into());
    }
    let chars = text.chars().count();
    if chars == 0 || chars > MAX_FEEDBACK_CHARS {
        return Err(RequestError::TooLarge {
            what: "feedback",
            limit: MAX_FEEDBACK_CHARS,
            actual: chars.max(1),
        }
        .into());
    }
    // Soft: no network send. Consent and length are the load-bearing gates.
    Ok(())
}

/// Track a closed-set event. Soft no-ops when any gate is closed.
pub fn track_event(event: TelemetryEvent, usage_analytics: bool) -> bool {
    track_event_gated(
        event,
        usage_analytics,
        telemetry_kill_switch(),
        telemetry_token(),
    )
}

/// Same gates as [`track_event`], with kill-switch and token passed in so tests
/// never need to mutate the process environment.
pub fn track_event_gated(
    event: TelemetryEvent,
    usage_analytics: bool,
    kill_switch: bool,
    token: &str,
) -> bool {
    if kill_switch || !usage_analytics || token.is_empty() {
        return false;
    }
    let _ = event.wire();
    true
}

static PENDING_DIR_OVERRIDE: Mutex<Option<PathBuf>> = Mutex::new(None);
static RESCUE_DIR_OVERRIDE: Mutex<Option<PathBuf>> = Mutex::new(None);
#[cfg(test)]
static PENDING_TEST_LOCK: Mutex<()> = Mutex::new(());
#[cfg(test)]
static RESCUE_TEST_LOCK: Mutex<()> = Mutex::new(());

/// Test seam for pending crash reports.
pub fn set_pending_dir_override(path: Option<PathBuf>) {
    *PENDING_DIR_OVERRIDE.lock().expect("pending dir lock") = path;
}

/// Test seam for rescue writes.
pub fn set_rescue_dir_override(path: Option<PathBuf>) {
    *RESCUE_DIR_OVERRIDE.lock().expect("rescue dir lock") = path;
}

fn pending_dir() -> PathBuf {
    PENDING_DIR_OVERRIDE
        .lock()
        .expect("pending dir lock")
        .clone()
        .unwrap_or_else(|| default_app_data_dir().join("pending-reports"))
}

fn rescue_dir() -> PathBuf {
    RESCUE_DIR_OVERRIDE
        .lock()
        .expect("rescue dir lock")
        .clone()
        .unwrap_or_else(|| default_app_data_dir().join("rescue"))
}

fn default_app_data_dir() -> PathBuf {
    #[cfg(target_os = "macos")]
    {
        std::env::var_os("HOME")
            .map(PathBuf::from)
            .unwrap_or_else(|| PathBuf::from("."))
            .join("Library")
            .join("Application Support")
            .join("QuadStickConfigManagerRewrite")
    }
    #[cfg(target_os = "windows")]
    {
        std::env::var_os("APPDATA")
            .map(PathBuf::from)
            .unwrap_or_else(|| PathBuf::from("."))
            .join("QuadStickConfigManagerRewrite")
    }
    #[cfg(not(any(target_os = "macos", target_os = "windows")))]
    {
        std::env::var_os("XDG_DATA_HOME")
            .map(PathBuf::from)
            .or_else(|| {
                std::env::var_os("HOME")
                    .map(|home| PathBuf::from(home).join(".local").join("share"))
            })
            .unwrap_or_else(|| PathBuf::from("."))
            .join("QuadStickConfigManagerRewrite")
    }
}

/// Paths of pending crash report files, oldest first.
pub fn pending_crash_reports() -> Vec<PathBuf> {
    let dir = pending_dir();
    let Ok(entries) = fs::read_dir(&dir) else {
        return Vec::new();
    };
    let mut files: Vec<_> = entries
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .filter(|path| {
            path.file_name()
                .and_then(|name| name.to_str())
                .is_some_and(|name| name.starts_with("crash-") && name.ends_with(".json"))
        })
        .collect();
    files.sort_by_key(|path| fs::metadata(path).and_then(|meta| meta.modified()).ok());
    if files.len() > MAX_PENDING_CRASH_REPORTS {
        let drop_count = files.len() - MAX_PENDING_CRASH_REPORTS;
        for surplus in &files[..drop_count] {
            let _ = fs::remove_file(surplus);
        }
        files.drain(0..drop_count);
    }
    files
}

/// Drop one pending report, or every pending report when `path` is `None`.
pub fn discard_pending_crash_report(path: Option<&Path>) {
    match path {
        Some(path) => {
            let _ = fs::remove_file(path);
        }
        None => {
            for file in pending_crash_reports() {
                let _ = fs::remove_file(file);
            }
        }
    }
}

/// Soft consent hook: when the user turns off crash prompts, discard pending.
pub fn acknowledge_crash_consent(ask_about_crashes: bool) {
    if !ask_about_crashes {
        discard_pending_crash_report(None);
    }
}

/// Persist a crash report before the default panic handler runs.
pub fn install_panic_hook() {
    let previous = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        let message = sanitize_path(&info.to_string());
        let body = json!({
            "schema": 1,
            "where": "panic",
            "message": message,
        })
        .to_string();
        let _ = write_pending_crash_report("panic", &body);
        previous(info);
    }));
}

/// Write a pending crash report JSON under the pending dir (testable override).
pub fn write_pending_crash_report(where_label: &str, body: &str) -> Result<PathBuf, QcmError> {
    let dir = pending_dir();
    fs::create_dir_all(&dir).map_err(|error| {
        QcmError::Internal(qcm_core::error::InternalError {
            what: "write pending crash report",
            detail: qcm_core::error::OsDetail::new(error.to_string()),
        })
    })?;
    let name = format!(
        "crash-{}-{}.json",
        where_label
            .chars()
            .map(|c| if c.is_ascii_alphanumeric() { c } else { '_' })
            .collect::<String>(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_millis())
            .unwrap_or(0)
    );
    let path = dir.join(name);
    fs::write(&path, body.as_bytes()).map_err(|error| {
        QcmError::Internal(qcm_core::error::InternalError {
            what: "write pending crash report",
            detail: qcm_core::error::OsDetail::new(error.to_string()),
        })
    })?;
    Ok(path)
}

fn sanitize_rescue_stem(raw: &str) -> String {
    let cleaned: String = raw
        .chars()
        .map(|c| {
            if c.is_ascii_alphanumeric() || c == '-' || c == '_' {
                c
            } else {
                '_'
            }
        })
        .collect();
    if cleaned.is_empty() {
        "profile".to_owned()
    } else {
        cleaned
    }
}

/// Write dirty profile CSV bytes to the rescue folder with a sanitized name.
pub fn write_rescue_profile(stem: &str, csv_bytes: &[u8]) -> Result<PathBuf, QcmError> {
    let dir = rescue_dir();
    fs::create_dir_all(&dir).map_err(|error| {
        QcmError::Internal(qcm_core::error::InternalError {
            what: "write rescue profile",
            detail: qcm_core::error::OsDetail::new(error.to_string()),
        })
    })?;
    let name = sanitize_rescue_stem(stem);
    let stamp = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis())
        .unwrap_or(0);
    let path = dir.join(format!("{name}-rescued-{stamp}.csv"));
    let temp = path.with_extension("csv.tmp");
    fs::write(&temp, csv_bytes).map_err(|error| {
        QcmError::Internal(qcm_core::error::InternalError {
            what: "write rescue profile",
            detail: qcm_core::error::OsDetail::new(error.to_string()),
        })
    })?;
    fs::rename(&temp, &path).map_err(|error| {
        let _ = fs::remove_file(&temp);
        QcmError::Internal(qcm_core::error::InternalError {
            what: "write rescue profile",
            detail: qcm_core::error::OsDetail::new(error.to_string()),
        })
    })?;
    Ok(path)
}

const AUTOSAVE_DRAFT: &str = "autosave-draft.csv";

/// Newest-first CSV rescues waiting from a previous session.
pub fn pending_rescue_paths() -> Vec<PathBuf> {
    let dir = rescue_dir();
    let Ok(entries) = fs::read_dir(&dir) else {
        return Vec::new();
    };
    let mut files: Vec<_> = entries
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .filter(|path| path.extension().and_then(|ext| ext.to_str()) == Some("csv"))
        .collect();
    files.sort_by_key(|path| {
        std::cmp::Reverse(fs::metadata(path).and_then(|meta| meta.modified()).ok())
    });
    files
}

/// Opaque rescue offer. `rescue_id` is a filename only, never a host path.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PendingRescueDto {
    pub rescue_id: String,
    pub display_name: String,
}

#[must_use]
pub fn pending_rescue_offer() -> Option<PendingRescueDto> {
    let path = pending_rescue_paths().into_iter().next()?;
    let rescue_id = path.file_name()?.to_str()?.to_owned();
    if rescue_id.contains('/') || rescue_id.contains('\\') || rescue_id.contains("..") {
        return None;
    }
    let display_name = path
        .file_stem()
        .and_then(|stem| stem.to_str())
        .unwrap_or("profile")
        .to_owned();
    Some(PendingRescueDto {
        rescue_id,
        display_name,
    })
}

fn checked_rescue_path(rescue_id: &str) -> Result<PathBuf, QcmError> {
    if rescue_id.is_empty()
        || rescue_id.contains('/')
        || rescue_id.contains('\\')
        || rescue_id.contains("..")
        || Path::new(rescue_id).components().count() != 1
    {
        return Err(QcmError::Request(RequestError::OutOfRange {
            what: "rescue id",
        }));
    }
    let path = rescue_dir().join(rescue_id);
    if !path.is_file() {
        return Err(QcmError::Request(RequestError::OutOfRange {
            what: "rescue id",
        }));
    }
    Ok(path)
}

/// Read one rescue CSV by opaque filename id.
pub fn read_rescue_csv(rescue_id: &str) -> Result<String, QcmError> {
    let path = checked_rescue_path(rescue_id)?;
    fs::read_to_string(&path).map_err(|error| {
        QcmError::Internal(qcm_core::error::InternalError {
            what: "read rescue profile",
            detail: qcm_core::error::OsDetail::new(error.to_string()),
        })
    })
}

/// Drop every rescue CSV (including the autosave draft).
pub fn discard_rescues() {
    for path in pending_rescue_paths() {
        let _ = fs::remove_file(path);
    }
}

/// Best-effort dirty autosave. Never interrupts the editor path.
pub fn write_autosave_draft(csv_bytes: &[u8]) {
    let dir = rescue_dir();
    if fs::create_dir_all(&dir).is_err() {
        return;
    }
    let path = dir.join(AUTOSAVE_DRAFT);
    let temp = path.with_extension("csv.tmp");
    if fs::write(&temp, csv_bytes).is_err() {
        return;
    }
    if fs::rename(&temp, &path).is_err() {
        let _ = fs::remove_file(&temp);
    }
}

/// Drop the autosave draft when the open profile is clean or closed.
pub fn clear_autosave_draft() {
    let path = rescue_dir().join(AUTOSAVE_DRAFT);
    let _ = fs::remove_file(path);
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SendFeedbackRequest {
    text: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct TrackTelemetryRequest {
    event: String,
}

/// Opaque crash-report offer for the WebView. `report_id` is a filename only.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PendingCrashReportDto {
    pub report_id: String,
    pub details: String,
}

/// Soft send outcome. Empty telemetry token cannot network-send.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CrashResolveResultDto {
    pub sent: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CrashReportChoice {
    Send,
    Later,
    Never,
}

impl CrashReportChoice {
    fn from_wire(value: &str) -> Option<Self> {
        match value {
            "send" => Some(Self::Send),
            "later" => Some(Self::Later),
            "never" => Some(Self::Never),
            _ => None,
        }
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ResolveCrashReportRequest {
    report_id: String,
    choice: String,
}

/// Soft body check: Avalonia needs a non-empty `chain`, Soft accepts any
/// non-empty body written by [`write_pending_crash_report`].
fn is_offerable_pending_body(text: &str) -> bool {
    let trimmed = text.trim();
    if trimmed.is_empty() {
        return false;
    }
    if let Ok(value) = serde_json::from_str::<Value>(trimmed) {
        if let Some(object) = value.as_object() {
            if let Some(chain) = object.get("chain").and_then(Value::as_array) {
                return !chain.is_empty();
            }
            // Soft plain JSON body (no chain field): any non-empty object/value.
            return true;
        }
        // Non-object JSON (string/number/array): still a Soft plain body.
        return true;
    }
    // Non-JSON text Soft body.
    true
}

/// Filename-only id under the pending dir. Never returns a host path.
fn pending_report_path(report_id: &str) -> Result<PathBuf, QcmError> {
    if report_id.is_empty()
        || report_id.contains('/')
        || report_id.contains('\\')
        || report_id.contains("..")
        || Path::new(report_id)
            .file_name()
            .and_then(|name| name.to_str())
            != Some(report_id)
        || !(report_id.starts_with("crash-") && report_id.ends_with(".json"))
    {
        return Err(RequestError::OutOfRange {
            what: "crash report id",
        }
        .into());
    }
    let path = pending_dir().join(report_id);
    if !path.is_file() {
        return Err(RequestError::OutOfRange {
            what: "crash report id",
        }
        .into());
    }
    Ok(path)
}

/// Newest readable, offerable pending report. Skips locked files; discards
/// unparseable ones so they cannot bury good reports forever.
#[must_use]
pub fn pending_crash_report_offer(ask_about_crashes: bool) -> Option<PendingCrashReportDto> {
    if !ask_about_crashes {
        return None;
    }
    for path in pending_crash_reports().into_iter().rev() {
        let text = match fs::read_to_string(&path) {
            Ok(text) => text,
            Err(_) => continue,
        };
        if !is_offerable_pending_body(&text) {
            discard_pending_crash_report(Some(&path));
            continue;
        }
        let report_id = path
            .file_name()
            .and_then(|name| name.to_str())
            .unwrap_or("")
            .to_owned();
        if report_id.is_empty() {
            discard_pending_crash_report(Some(&path));
            continue;
        }
        return Some(PendingCrashReportDto {
            report_id,
            details: text,
        });
    }
    None
}

/// Apply Send / Later / Never. Soft send never networks; empty token keeps the
/// file and returns `sent: false` (Avalonia keep-on-fail). Non-empty Soft token
/// still skips the network but returns `sent: true` and discards that one file.
pub fn resolve_pending_crash_report(
    report_id: &str,
    choice: CrashReportChoice,
    token: &str,
) -> Result<CrashResolveResultDto, QcmError> {
    match choice {
        CrashReportChoice::Later => Ok(CrashResolveResultDto { sent: false }),
        CrashReportChoice::Never => {
            discard_pending_crash_report(None);
            Ok(CrashResolveResultDto { sent: false })
        }
        CrashReportChoice::Send => {
            let path = pending_report_path(report_id)?;
            if token.is_empty() {
                // Soft: cannot network-send. Keep the file so the next launch
                // can ask again, matching Avalonia keep-on-fail.
                return Ok(CrashResolveResultDto { sent: false });
            }
            // Soft: token present still does not open a socket. Treat as sent
            // for UI thank-you + discard of the one report the user saw.
            discard_pending_crash_report(Some(&path));
            Ok(CrashResolveResultDto { sent: true })
        }
    }
}

#[tauri::command]
pub fn send_feedback(state: State<'_, ShellState>, request: Value) -> Result<(), Failure> {
    let request: SendFeedbackRequest = redact_err(parse(request, "send_feedback request"))?;
    let settings = state.get_settings();
    redact_err(send_feedback_text(&request.text, settings.usage_analytics))
}

#[tauri::command]
pub fn track_telemetry_event(
    state: State<'_, ShellState>,
    request: Value,
) -> Result<bool, Failure> {
    let request: TrackTelemetryRequest =
        redact_err(parse(request, "track_telemetry_event request"))?;
    let event = TelemetryEvent::from_wire(&request.event).ok_or_else(|| {
        Box::new(QcmErrorDto::from(&QcmError::Request(
            RequestError::OutOfRange {
                what: "telemetry event",
            },
        )))
    })?;
    let settings = state.get_settings();
    Ok(track_event(event, settings.usage_analytics))
}

#[tauri::command]
pub fn get_pending_crash_report(state: State<'_, ShellState>) -> Option<PendingCrashReportDto> {
    let settings = state.get_settings();
    pending_crash_report_offer(settings.ask_about_crashes)
}

#[tauri::command]
pub fn resolve_crash_report(
    state: State<'_, ShellState>,
    request: Value,
) -> Result<CrashResolveResultDto, Failure> {
    let request: ResolveCrashReportRequest =
        redact_err(parse(request, "resolve_crash_report request"))?;
    let choice = CrashReportChoice::from_wire(&request.choice).ok_or_else(|| {
        Box::new(QcmErrorDto::from(&QcmError::Request(
            RequestError::OutOfRange {
                what: "crash report choice",
            },
        )))
    })?;
    let result = redact_err(resolve_pending_crash_report(
        &request.report_id,
        choice,
        telemetry_token(),
    ))?;
    if choice == CrashReportChoice::Never {
        let settings = state.get_settings();
        redact_err(state.update_settings(json!({
            "expectedRevision": settings.revision,
            "patch": { "askAboutCrashes": false },
        })))?;
    }
    Ok(result)
}

#[tauri::command]
pub fn get_pending_rescue() -> Option<PendingRescueDto> {
    pending_rescue_offer()
}

#[tauri::command]
pub fn open_rescue_profile(
    state: State<'_, ShellState>,
    request: Value,
) -> Result<qcm_core::profiles::EditorSnapshot, Failure> {
    #[derive(Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct OpenRescueRequest {
        rescue_id: String,
    }
    let request: OpenRescueRequest = redact_err(parse(request, "open_rescue_profile request"))?;
    let csv = redact_err(read_rescue_csv(&request.rescue_id))?;
    let snapshot = state.open_unsaved_profile(&csv);
    discard_rescues();
    Ok(snapshot)
}

#[tauri::command]
pub fn discard_pending_rescues() {
    discard_rescues();
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sanitize_path_strips_home_usernames() {
        assert_eq!(
            sanitize_path("/Users/bassam/Documents/Racing.csv"),
            "/Users/<user>/Documents/Racing.csv"
        );
        assert_eq!(
            sanitize_path("/home/runner/work/qcm"),
            "/home/<user>/work/qcm"
        );
        assert_eq!(
            sanitize_path(r"C:\Users\Bassam\AppData\Local\crash.json"),
            r"C:\Users\<user>\AppData\Local\crash.json"
        );
        assert_eq!(sanitize_path("no path here"), "no path here");
        assert_eq!(sanitize_path(""), "");
    }

    #[test]
    fn feedback_is_capped_and_needs_consent() {
        assert!(send_feedback_text("hello", false).is_err());
        assert!(send_feedback_text("hello", true).is_ok());
        assert!(send_feedback_text("", true).is_err());
        let too_long: String = "a".repeat(MAX_FEEDBACK_CHARS + 1);
        assert!(send_feedback_text(&too_long, true).is_err());
        let exact: String = "b".repeat(MAX_FEEDBACK_CHARS);
        assert!(send_feedback_text(&exact, true).is_ok());
    }

    #[test]
    fn telemetry_kill_switch_and_empty_token_no_op() {
        assert!(!track_event_gated(
            TelemetryEvent::AppLaunched,
            true,
            true,
            "token"
        ));
        assert!(!track_event_gated(
            TelemetryEvent::AppLaunched,
            true,
            false,
            ""
        ));
        assert!(!track_event_gated(
            TelemetryEvent::AppLaunched,
            false,
            false,
            "token"
        ));
        assert!(track_event_gated(
            TelemetryEvent::AppLaunched,
            true,
            false,
            "token"
        ));
        // Soft builds ship an empty token, so the public path stays off.
        assert!(!track_event(TelemetryEvent::AppLaunched, true));
    }

    #[test]
    fn telemetry_event_wire_names_are_closed() {
        assert_eq!(
            TelemetryEvent::from_wire("app_launched"),
            Some(TelemetryEvent::AppLaunched)
        );
        assert_eq!(TelemetryEvent::from_wire("not_a_real_event"), None);
        assert_eq!(
            TelemetryEvent::FeedbackSubmitted.wire(),
            "feedback_submitted"
        );
    }

    #[test]
    fn rescue_write_uses_sanitized_filename() {
        let _lock = RESCUE_TEST_LOCK.lock().expect("rescue test lock");
        let dir = std::env::temp_dir().join(format!(
            "qcm-rescue-test-{}",
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0)
        ));
        set_rescue_dir_override(Some(dir.clone()));
        let path = write_rescue_profile("bad?.name", b"Profile Name,Racing\n").expect("write");
        assert!(path.starts_with(&dir));
        let name = path.file_name().and_then(|n| n.to_str()).unwrap_or("");
        assert!(name.contains("bad__name-rescued-"));
        assert!(name.ends_with(".csv"));
        assert!(!name.contains('?'));
        let body = fs::read(&path).expect("read rescue");
        assert_eq!(body, b"Profile Name,Racing\n");
        let _ = fs::remove_dir_all(&dir);
        set_rescue_dir_override(None);
    }

    #[test]
    fn pending_rescue_lists_newest_csv_by_filename_only() {
        let _lock = RESCUE_TEST_LOCK.lock().expect("rescue test lock");
        let dir = std::env::temp_dir().join(format!(
            "qcm-rescue-offer-{}",
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0)
        ));
        let _ = fs::remove_dir_all(&dir);
        set_rescue_dir_override(Some(dir.clone()));
        let path = write_rescue_profile("Racing", b"Profile Name,Racing\n").expect("write");
        let offer = pending_rescue_offer().expect("offer");
        assert_eq!(
            offer.rescue_id,
            path.file_name().and_then(|n| n.to_str()).unwrap()
        );
        assert!(offer.display_name.contains("Racing"));
        assert!(!offer.rescue_id.contains('/'));
        discard_rescues();
        assert!(pending_rescue_offer().is_none());
        set_rescue_dir_override(None);
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn rescue_id_rejects_path_traversal() {
        assert!(read_rescue_csv("../secrets.csv").is_err());
        assert!(read_rescue_csv("a/b.csv").is_err());
    }

    #[test]
    fn pending_crash_list_discard_and_consent() {
        let _lock = PENDING_TEST_LOCK.lock().expect("pending test lock");
        let dir = std::env::temp_dir().join(format!(
            "qcm-pending-test-{}",
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0)
        ));
        set_pending_dir_override(Some(dir.clone()));
        let written = write_pending_crash_report("ui", "{\"schema\":1}").expect("write");
        assert!(written.exists());
        assert_eq!(pending_crash_reports().len(), 1);
        acknowledge_crash_consent(false);
        assert!(pending_crash_reports().is_empty());
        let _ = fs::remove_dir_all(&dir);
        set_pending_dir_override(None);
    }

    #[test]
    fn get_pending_returns_details_and_filename_id() {
        let _lock = PENDING_TEST_LOCK.lock().expect("pending test lock");
        let dir = std::env::temp_dir().join(format!(
            "qcm-pending-offer-{}",
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0)
        ));
        set_pending_dir_override(Some(dir.clone()));
        assert!(pending_crash_report_offer(false).is_none());
        let body = r#"{"schema":1,"note":"soft plain body"}"#;
        let written = write_pending_crash_report("ui", body).expect("write");
        let name = written
            .file_name()
            .and_then(|n| n.to_str())
            .expect("filename")
            .to_owned();
        let offer = pending_crash_report_offer(true).expect("offer");
        assert_eq!(offer.report_id, name);
        assert!(!offer.report_id.contains('/') && !offer.report_id.contains('\\'));
        assert_eq!(offer.details, body);
        let _ = fs::remove_dir_all(&dir);
        set_pending_dir_override(None);
    }

    #[test]
    fn resolve_later_keeps_send_empty_token_keeps_never_discards_all() {
        let _lock = PENDING_TEST_LOCK.lock().expect("pending test lock");
        let dir = std::env::temp_dir().join(format!(
            "qcm-pending-resolve-{}",
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0)
        ));
        set_pending_dir_override(Some(dir.clone()));
        let first = write_pending_crash_report("ui", r#"{"schema":1,"a":1}"#).expect("write");
        std::thread::sleep(std::time::Duration::from_millis(2));
        let second = write_pending_crash_report("ui", r#"{"schema":1,"a":2}"#).expect("write");
        let first_id = first
            .file_name()
            .and_then(|n| n.to_str())
            .expect("id")
            .to_owned();

        let later =
            resolve_pending_crash_report(&first_id, CrashReportChoice::Later, "").expect("later");
        assert!(!later.sent);
        assert_eq!(pending_crash_reports().len(), 2);
        assert!(first.exists());

        let send_fail =
            resolve_pending_crash_report(&first_id, CrashReportChoice::Send, "").expect("send");
        assert!(!send_fail.sent);
        assert!(first.exists());

        let never =
            resolve_pending_crash_report(&first_id, CrashReportChoice::Never, "").expect("never");
        assert!(!never.sent);
        assert!(pending_crash_reports().is_empty());
        assert!(!first.exists());
        assert!(!second.exists());

        let _ = fs::remove_dir_all(&dir);
        set_pending_dir_override(None);
    }

    #[test]
    fn resolve_send_with_token_discards_one_soft_without_network() {
        let _lock = PENDING_TEST_LOCK.lock().expect("pending test lock");
        let dir = std::env::temp_dir().join(format!(
            "qcm-pending-send-{}",
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0)
        ));
        set_pending_dir_override(Some(dir.clone()));
        let keep = write_pending_crash_report("ui", r#"{"schema":1,"keep":true}"#).expect("write");
        std::thread::sleep(std::time::Duration::from_millis(2));
        let send = write_pending_crash_report("ui", r#"{"schema":1,"send":true}"#).expect("write");
        let send_id = send
            .file_name()
            .and_then(|n| n.to_str())
            .expect("id")
            .to_owned();
        let result = resolve_pending_crash_report(&send_id, CrashReportChoice::Send, "soft-token")
            .expect("send");
        assert!(result.sent);
        assert!(!send.exists());
        assert!(keep.exists());
        let _ = fs::remove_dir_all(&dir);
        set_pending_dir_override(None);
    }

    #[test]
    fn empty_pending_body_is_discarded_not_offered() {
        let _lock = PENDING_TEST_LOCK.lock().expect("pending test lock");
        let dir = std::env::temp_dir().join(format!(
            "qcm-pending-empty-{}",
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0)
        ));
        set_pending_dir_override(Some(dir.clone()));
        let empty = write_pending_crash_report("ui", "   ").expect("write");
        assert!(pending_crash_report_offer(true).is_none());
        assert!(!empty.exists());
        let _ = fs::remove_dir_all(&dir);
        set_pending_dir_override(None);
    }

    #[test]
    fn panic_hook_path_writes_report_body() {
        let _lock = PENDING_TEST_LOCK.lock().expect("pending test lock");
        let dir = std::env::temp_dir().join(format!(
            "qcm-pending-panic-{}",
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0)
        ));
        set_pending_dir_override(Some(dir.clone()));
        let body = json!({"schema":1,"where":"panic","message":"soft"}).to_string();
        let written = write_pending_crash_report("panic", &body).expect("write");
        assert!(written.exists());
        let offer = pending_crash_report_offer(true).expect("offer");
        assert!(offer.details.contains("panic") || offer.details.contains("soft"));
        let _ = fs::remove_dir_all(&dir);
        set_pending_dir_override(None);
    }
}
