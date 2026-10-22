//! What a host app gets from the shell's Rust, with a generic host.

use qcm_tauri_lib::adapters::library::FileSystemProfileLibrary;
use qcm_tauri_lib::adapters::picker::NativeProfilePicker;
use qcm_tauri_lib::adapters::settings::SettingsFile;
use qcm_tauri_lib::adapters::storage::volumes::PlatformVolumes;
use qcm_tauri_lib::shell::{Shell, ShellState};
use serde_json::json;
use std::fs;
use std::path::PathBuf;
use std::sync::Arc;

const SAMPLE: &str = "Profile Name,,Left Joystick,,,,,,,,Comments\n\
working.csv,,,,,,,,,,\n\
PlayStation Outputs,Function,usb,,,,,,,,\n\
cross,normal,sip,,,,,,,,\n";

fn scratch(tag: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!("qcm-host-{tag}-{}", std::process::id()));
    let _ = fs::remove_dir_all(&dir);
    fs::create_dir_all(&dir).expect("a scratch directory");
    dir
}

fn host_shell(dir: &std::path::Path) -> ShellState {
    let library = Arc::new(FileSystemProfileLibrary::new(PlatformVolumes));
    let picker = NativeProfilePicker::new(Arc::clone(&library));
    Shell::new(library, picker, SettingsFile::at(dir.join("settings.json")))
}

#[test]
fn a_hosted_file_opens_without_a_picker_and_saves_back_to_itself() {
    let dir = scratch("open");
    let working = dir.join("working.csv");
    fs::write(&working, SAMPLE).expect("the working file");
    let shell = host_shell(&dir);

    let opened = shell.open_hosted(&working).expect("the hosted file opens");
    assert!(!opened.dirty);
    let edited = shell
        .apply_editor_ops(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
            "ops": [{ "op": "set_cell", "row": 4, "col": 0, "value": "circle" }],
        }))
        .expect("edit succeeds");
    shell
        .save_profile(json!({
            "sessionId": edited.session_id,
            "expectedRevision": edited.revision,
        }))
        .expect("save succeeds");

    let saved = fs::read_to_string(&working).expect("the working file is still there");
    assert!(saved.contains("circle"), "{saved}");
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn a_missing_hosted_file_is_refused() {
    let dir = scratch("missing");
    let shell = host_shell(&dir);
    assert!(shell.open_hosted(&dir.join("nope.csv")).is_err());
    let _ = fs::remove_dir_all(&dir);
}
