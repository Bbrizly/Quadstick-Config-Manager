//! The self-driving smoke test's native half. Compiled only with the `e2e`
//! cargo feature, so a shipped build has none of it.
//!
//! The walk runs in the WebView (`src/e2e/`). This side hands it a place to
//! write its report, answers the native dialogs it cannot click, and quits
//! when it is done. Every path comes from the environment the runner script
//! sets, and the run refuses to start unless HOME is a throwaway folder, so
//! the person's real profiles, settings and rescue draft are never touched.

use std::io::Write as _;
use std::path::PathBuf;

const HOME_PREFIX: &str = "qcm-e2e-home";

/// Where the report goes. Required.
fn report_path() -> Option<PathBuf> {
    std::env::var_os("QCM_E2E_REPORT").map(PathBuf::from)
}

/// A folder holding `default.csv`, offered when the walk asks for a drive.
#[must_use]
pub fn device_dir() -> Option<PathBuf> {
    std::env::var_os("QCM_E2E_DEVICE_DIR").map(PathBuf::from)
}

/// Stop before any window or store exists unless HOME is a throwaway folder
/// inside the temp directory (symlinks resolved) and the report goes inside it.
pub fn guard() {
    let canonical = |path: PathBuf| std::fs::canonicalize(path).ok();
    let home = std::env::var_os("HOME")
        .map(PathBuf::from)
        .and_then(canonical);
    let temp = canonical(std::env::temp_dir());
    let report_dir = report_path()
        .and_then(|report| report.parent().map(PathBuf::from))
        .and_then(canonical);
    let throwaway = match (&home, &temp, &report_dir) {
        (Some(home), Some(temp), Some(report_dir)) => {
            home.starts_with(temp)
                && home != temp
                && report_dir.starts_with(home)
                && home
                    .file_name()
                    .and_then(|name| name.to_str())
                    .is_some_and(|name| name.starts_with(HOME_PREFIX))
        }
        _ => false,
    };
    if !throwaway {
        eprintln!(
            "e2e build: HOME must be a {HOME_PREFIX}* folder in the temp directory, with QCM_E2E_REPORT inside it; refusing to run"
        );
        std::process::exit(2);
    }
}

/// One progress line, so a hang still leaves a trail.
#[tauri::command]
pub fn e2e_log(line: String) {
    let Some(report) = report_path() else { return };
    let log = report.with_extension("log");
    if let Ok(mut file) = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(log)
    {
        let _ = writeln!(file, "{line}");
    }
}

/// Bring the test window forward and make the app active, so WebKit treats
/// the page as visible. Only this window is touched.
#[tauri::command]
pub fn e2e_bring_forward(app: tauri::AppHandle) {
    use tauri::Manager;
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.unminimize();
        let _ = window.show();
        let _ = window.set_focus();
    }
}

/// What the native window says about itself, for the visibility log.
#[tauri::command]
pub fn e2e_window_state(app: tauri::AppHandle) -> serde_json::Value {
    use tauri::Manager;
    let Some(window) = app.get_webview_window("main") else {
        return serde_json::json!({ "window": false });
    };
    serde_json::json!({
        "window": true,
        "visible": window.is_visible().ok(),
        "minimized": window.is_minimized().ok(),
        "focused": window.is_focused().ok(),
        "fullscreen": window.is_fullscreen().ok(),
        "screenLocked": session_flag("ioreg", &["-n", "Root", "-d1"], "\"CGSSessionScreenIsLocked\"=Yes"),
        "displayAsleep": session_flag("system_profiler", &["SPDisplaysDataType"], "Display Asleep: Yes"),
    })
}

/// Whether a macOS tool's output says `needle`. `None` when it could not run.
fn session_flag(tool: &str, args: &[&str], needle: &str) -> Option<bool> {
    let out = std::process::Command::new(tool).args(args).output().ok()?;
    Some(String::from_utf8_lossy(&out.stdout).contains(needle))
}

/// A real native close request on the main window, as the red button sends.
#[tauri::command]
pub fn e2e_request_close(app: tauri::AppHandle) {
    use tauri::Manager;
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.close();
    }
}

/// Write the report. The walk then quits through the app's own close path.
#[tauri::command]
pub fn e2e_report(report: String) {
    if let Some(path) = report_path() {
        let _ = std::fs::write(path, report);
    }
}

/// Quit at once, for a walk that could not reach the app's own quit.
#[tauri::command]
pub fn e2e_exit(app: tauri::AppHandle, code: i32) {
    app.cleanup_before_exit();
    std::process::exit(code);
}
