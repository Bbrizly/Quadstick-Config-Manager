//! Soft diagnostics: redaction, feedback, telemetry allowlist, crash rescue.
//!
//! Soft does not ship PostHog wiring. Events no-op when the token is empty,
//! usage analytics is off, or `QSCM_TELEMETRY=0`. Signed network send stays
//! behind a later gate; Soft proves the gates refuse the right things.

use crate::ipc::parse;
use crate::shell::ShellState;
use qcm_core::error::{QcmError, QcmErrorDto, RequestError};
use serde::Deserialize;
use serde_json::Value;
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
    fn pending_crash_list_discard_and_consent() {
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
}
