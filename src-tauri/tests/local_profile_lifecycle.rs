//! End-to-end local CSV lifecycle at the native shell boundary.
//!
//! This is the TASK-040A loop without a real dialog: the picker queues an
//! opaque `LocalProfileRef`, while the shell owns parsing, editing, undo, save,
//! close and reopen. No host path crosses the command-facing boundary.

use qcm_core::QcmError;
use qcm_core::ports::local::LocalProfileRef;
use qcm_tauri_lib::adapters::picker::ProfilePicker;
use qcm_tauri_lib::shell::Shell;
use qcm_testkit::{FakeProfileLibrary, FakeSettingsFile};
use serde_json::json;
use std::sync::{Arc, Mutex};

const SAMPLE: &str = "Profile Name,,Left Joystick,,,,,,,,Comments\n\
racing.csv,,,,,,,,,,\n\
PlayStation Outputs,Function,usb,,,,,,,,\n\
cross,normal,sip,,,,,,,,\n";

struct QueuePicker {
    open: Mutex<Vec<Option<LocalProfileRef>>>,
}

impl QueuePicker {
    fn new() -> Self {
        Self {
            open: Mutex::new(Vec::new()),
        }
    }

    fn queue_open(&self, target: LocalProfileRef) {
        self.open.lock().expect("picker lock").push(Some(target));
    }
}

impl ProfilePicker for QueuePicker {
    fn pick_open(&self) -> Result<Option<LocalProfileRef>, QcmError> {
        Ok(self.open.lock().expect("picker lock").remove(0))
    }

    fn pick_save_as(&self, _suggested: &str) -> Result<Option<LocalProfileRef>, QcmError> {
        Ok(None)
    }
}

#[test]
fn local_csv_edit_undo_save_close_reopen_preserves_canonical_state() {
    let library = Arc::new(FakeProfileLibrary::new());
    let target = library.add("Racing.csv", SAMPLE);
    let picker = Arc::new(QueuePicker::new());
    picker.queue_open(target.clone());
    let shell = Shell::new(
        Arc::clone(&library),
        Arc::clone(&picker),
        FakeSettingsFile::new(),
    );

    let opened = shell
        .choose_and_open_profile()
        .expect("open succeeds")
        .expect("picker did not cancel");
    let original = opened.grid.clone();

    let edited = shell
        .apply_editor_ops(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
            "ops": [{ "op": "set_cell", "row": 4, "col": 0, "value": "circle" }],
        }))
        .expect("edit succeeds");
    assert!(edited.dirty);
    assert_ne!(edited.grid, original);

    let undone = shell
        .undo_editor(json!({
            "sessionId": edited.session_id,
            "expectedRevision": edited.revision,
        }))
        .expect("undo succeeds");
    assert_eq!(undone.grid, original);

    let edited_again = shell
        .apply_editor_ops(json!({
            "sessionId": undone.session_id,
            "expectedRevision": undone.revision,
            "ops": [{ "op": "set_cell", "row": 4, "col": 0, "value": "square" }],
        }))
        .expect("second edit succeeds");

    let receipt = shell
        .save_profile(json!({
            "sessionId": edited_again.session_id,
            "expectedRevision": edited_again.revision,
        }))
        .expect("save succeeds");
    assert_eq!(receipt.name, "Racing.csv");

    let saved = shell
        .get_profile_snapshot(json!({ "sessionId": edited_again.session_id }))
        .expect("saved session remains readable");
    assert!(!saved.dirty);

    let closed = shell
        .close_profile(json!({
            "sessionId": saved.session_id,
            "disposition": "if_clean",
        }))
        .expect("clean close succeeds");
    assert_eq!(
        serde_json::to_value(closed).expect("close serializes"),
        json!({ "kind": "closed" })
    );

    picker.queue_open(target);
    let reopened = shell
        .choose_and_open_profile()
        .expect("reopen succeeds")
        .expect("picker did not cancel");

    assert_eq!(reopened.grid, saved.grid);
    assert_eq!(reopened.grid[3][0], "square");
    assert!(!reopened.dirty);
    assert!(!reopened.can_undo);
    assert_ne!(reopened.session_id, saved.session_id);
}
