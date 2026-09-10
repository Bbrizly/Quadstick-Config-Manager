//! The command surface, driven the way a window drives it.
//!
//! Every command body is called directly with the JSON a window would send.
//! There is no dialog to automate and no window to build: the picker is a port,
//! so a cancel is a value a test hands back rather than a key somebody has to
//! press.
//!
//! What these hold is the two things a window cannot be trusted to do for
//! itself. A request made against a revision that has moved on is refused rather
//! than applied, and a refusal always arrives as a code the UI can switch on,
//! never as a sentence.

use qcm_core::QcmError;
use qcm_core::error::{QcmErrorDto, looks_like_absolute_path};
use qcm_core::ports::local::LocalProfileRef;
use qcm_core::profiles::EditorSnapshot;
use qcm_tauri_lib::adapters::picker::ProfilePicker;
use qcm_tauri_lib::shell::Shell;
use qcm_testkit::{FakeProfileLibrary, FakeSettingsFile};
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};

const SAMPLE: &str = "Profile Name,,Left Joystick,,,,,,,,Comments\n\
racing.csv,,,,,,,,,,\n\
PlayStation Outputs,Function,usb,,,,,,,,\n\
cross,normal,sip,,,,,,,,\n";

struct StubPicker {
    library: Arc<FakeProfileLibrary>,
    open: Mutex<Vec<Option<LocalProfileRef>>>,
    save_as: Mutex<Vec<Option<LocalProfileRef>>>,
    opened: Mutex<usize>,
}

impl StubPicker {
    fn new(library: Arc<FakeProfileLibrary>) -> Self {
        Self {
            library,
            open: Mutex::new(Vec::new()),
            save_as: Mutex::new(Vec::new()),
            opened: Mutex::new(0),
        }
    }

    fn will_open(&self, name: &str, text: &str) {
        let target = self.library.add(name, text);
        self.open.lock().expect("lock").push(Some(target));
    }

    fn will_save_as(&self, name: &str) -> LocalProfileRef {
        let target = self.library.slot(name);
        self.save_as
            .lock()
            .expect("lock")
            .push(Some(target.clone()));
        target
    }

    fn will_cancel_open(&self) {
        self.open.lock().expect("lock").push(None);
    }

    fn will_cancel_save_as(&self) {
        self.save_as.lock().expect("lock").push(None);
    }

    fn times_opened(&self) -> usize {
        *self.opened.lock().expect("lock")
    }
}

impl ProfilePicker for StubPicker {
    fn pick_open(&self) -> Result<Option<LocalProfileRef>, QcmError> {
        *self.opened.lock().expect("lock") += 1;
        Ok(self.open.lock().expect("lock").remove(0))
    }

    fn pick_save_as(&self, _suggested: &str) -> Result<Option<LocalProfileRef>, QcmError> {
        *self.opened.lock().expect("lock") += 1;
        Ok(self.save_as.lock().expect("lock").remove(0))
    }
}

type TestShell = Shell<FakeProfileLibrary, Arc<StubPicker>, FakeSettingsFile>;

struct Harness {
    shell: TestShell,
    picker: Arc<StubPicker>,
    library: Arc<FakeProfileLibrary>,
}

fn harness() -> Harness {
    let library = Arc::new(FakeProfileLibrary::new());
    let picker = Arc::new(StubPicker::new(Arc::clone(&library)));
    let shell = Shell::new(
        Arc::clone(&library),
        Arc::clone(&picker),
        FakeSettingsFile::new(),
    );
    Harness {
        shell,
        picker,
        library,
    }
}

fn code(error: &QcmErrorDto) -> &str {
    &error.code
}

fn set_cell(row: usize, value: &str) -> Value {
    json!({ "op": "set_cell", "row": row, "col": 0, "value": value })
}

fn new_profile(shell: &TestShell, name: &str) -> EditorSnapshot {
    shell
        .new_profile(json!({ "name": name }))
        .map_err(|error| QcmErrorDto::from(&error))
        .expect("a new profile opens")
}

fn dto(error: QcmError) -> QcmErrorDto {
    QcmErrorDto::from(&error)
}

#[test]
fn a_new_profile_opens_clean_with_nothing_to_undo() {
    let app = harness();
    let snapshot = new_profile(&app.shell, "racing.csv");
    assert!(!snapshot.dirty);
    assert!(!snapshot.can_undo);
    assert!(snapshot.save_target.is_none());
    assert!(snapshot.session_id.starts_with("session-"));
    assert!(!snapshot.grid.is_empty());
}

#[test]
fn a_name_past_the_limit_is_refused_rather_than_shortened() {
    let app = harness();
    let long = "a".repeat(129);
    let error = dto(app
        .shell
        .new_profile(json!({ "name": long }))
        .expect_err("129 is past the limit"));
    assert_eq!(code(&error), "QCM_REQUEST_TOO_LARGE");
    assert!(error.message.contains("128"));
}

#[test]
fn a_payload_the_command_cannot_read_is_still_a_code() {
    let app = harness();
    for malformed in [
        json!({}),
        json!({ "name": 5 }),
        json!("racing"),
        Value::Null,
    ] {
        let error = dto(app
            .shell
            .new_profile(malformed.clone())
            .expect_err("malformed request"));
        assert_eq!(code(&error), "QCM_REQUEST_MALFORMED", "{malformed}");
        assert_eq!(
            error.action.as_ref().map(|a| a.kind.as_str()),
            Some("report_bug")
        );
    }
}

#[test]
fn a_cancelled_open_dialog_opens_nothing_and_is_not_an_error() {
    let app = harness();
    app.picker.will_cancel_open();
    assert!(
        app.shell
            .choose_and_open_profile()
            .expect("a cancel is a result")
            .is_none()
    );
}

#[test]
fn a_chosen_file_opens_with_its_own_grid() {
    let app = harness();
    app.picker.will_open("Racing.csv", SAMPLE);
    let snapshot = app
        .shell
        .choose_and_open_profile()
        .expect("the file opens")
        .expect("not cancelled");
    assert_eq!(snapshot.save_target.as_deref(), Some("Racing.csv"));
    assert_eq!(snapshot.grid[3][0], "cross");
    assert!(!snapshot.dirty);
}

#[test]
fn an_edit_made_against_a_stale_revision_is_refused_and_changes_nothing() {
    let app = harness();
    let opened = new_profile(&app.shell, "racing.csv");
    let session = opened.session_id.clone();
    let edited = app
        .shell
        .apply_editor_ops(json!({
            "sessionId": session,
            "expectedRevision": opened.revision,
            "ops": [set_cell(4, "circle")],
        }))
        .expect("the first edit lands");
    let error = dto(app
        .shell
        .apply_editor_ops(json!({
            "sessionId": session,
            "expectedRevision": opened.revision,
            "ops": [set_cell(4, "square")],
        }))
        .expect_err("the second edit was stale"));
    assert_eq!(code(&error), "QCM_PROFILE_REVISION_CONFLICT");
    assert_eq!(
        error.action.as_ref().map(|a| a.kind.as_str()),
        Some("reopen_profile")
    );
    let now = app
        .shell
        .apply_editor_ops(json!({
            "sessionId": session,
            "expectedRevision": edited.revision,
            "ops": [],
        }))
        .expect("read back");
    assert_eq!(now.grid, edited.grid);
}

#[test]
fn a_batch_with_one_bad_operation_applies_none_of_it() {
    let app = harness();
    let opened = new_profile(&app.shell, "racing.csv");
    let before = opened.grid.clone();
    let error = dto(app
        .shell
        .apply_editor_ops(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
            "ops": [set_cell(4, "circle"), set_cell(0, "nowhere")],
        }))
        .expect_err("row zero is not a row"));
    assert_eq!(code(&error), "QCM_PROFILE_OPERATION_REJECTED");
    let now = app
        .shell
        .apply_editor_ops(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
            "ops": [],
        }))
        .expect("nothing moved");
    assert_eq!(now.grid, before);
    assert!(!now.dirty);
}

#[test]
fn a_batch_past_the_limit_is_refused_before_anything_is_applied() {
    let app = harness();
    let opened = new_profile(&app.shell, "racing.csv");
    let ops: Vec<Value> = (0..257).map(|_| set_cell(4, "circle")).collect();
    let error = dto(app
        .shell
        .apply_editor_ops(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
            "ops": ops,
        }))
        .expect_err("257 is past limit"));
    assert_eq!(code(&error), "QCM_REQUEST_TOO_LARGE");
    let now = app
        .shell
        .apply_editor_ops(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
            "ops": [],
        }))
        .expect("profile unchanged");
    assert!(!now.dirty);
}

#[test]
fn one_operation_carrying_a_wall_of_text_is_refused() {
    let app = harness();
    let opened = new_profile(&app.shell, "racing.csv");
    let wall = "x".repeat(4097);
    let error = dto(app
        .shell
        .apply_editor_ops(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
            "ops": [set_cell(4, &wall)],
        }))
        .expect_err("too large"));
    assert_eq!(code(&error), "QCM_REQUEST_TOO_LARGE");
}

#[test]
fn a_forged_session_id_is_an_unknown_session_and_not_a_crash() {
    let app = harness();
    for forged in ["session-999", "not-an-id", "", "session--1"] {
        let error = dto(app
            .shell
            .undo_editor(json!({ "sessionId": forged, "expectedRevision": 1 }))
            .expect_err("unknown"));
        assert_eq!(code(&error), "QCM_PROFILE_UNKNOWN_SESSION", "{forged}");
    }
}

#[test]
fn undo_with_nothing_to_take_back_says_so() {
    let app = harness();
    let opened = new_profile(&app.shell, "racing.csv");
    let error = dto(app
        .shell
        .undo_editor(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
        }))
        .expect_err("nothing to undo"));
    assert_eq!(code(&error), "QCM_PROFILE_NOTHING_TO_UNDO");
}

#[test]
fn undo_takes_back_the_last_edit() {
    let app = harness();
    let opened = new_profile(&app.shell, "racing.csv");
    let before = opened.grid.clone();
    let edited = app
        .shell
        .apply_editor_ops(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
            "ops": [set_cell(4, "circle")],
        }))
        .expect("edit");
    let undone = app
        .shell
        .undo_editor(json!({
            "sessionId": opened.session_id,
            "expectedRevision": edited.revision,
        }))
        .expect("undo");
    assert_eq!(undone.grid, before);
}

#[test]
fn saving_a_profile_that_has_never_been_saved_asks_for_a_place_first() {
    let app = harness();
    let opened = new_profile(&app.shell, "racing.csv");
    let error = dto(app
        .shell
        .save_profile(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
        }))
        .expect_err("nowhere to write"));
    assert_eq!(code(&error), "QCM_PROFILE_NEEDS_SAVE_TARGET");
    assert_eq!(
        error.action.as_ref().map(|a| a.kind.as_str()),
        Some("choose_save_location")
    );
}

#[test]
fn a_cancelled_save_as_leaves_the_profile_exactly_as_it_was() {
    let app = harness();
    let opened = new_profile(&app.shell, "racing.csv");
    app.picker.will_cancel_save_as();
    let receipt = app
        .shell
        .save_profile_as(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
        }))
        .expect("cancel result");
    assert!(receipt.is_none());
    let error = dto(app
        .shell
        .save_profile(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
        }))
        .expect_err("still nowhere"));
    assert_eq!(code(&error), "QCM_PROFILE_NEEDS_SAVE_TARGET");
}

#[test]
fn save_as_writes_the_bytes_and_the_receipt_names_no_place() {
    let app = harness();
    let opened = new_profile(&app.shell, "racing.csv");
    let target = app.picker.will_save_as("Racing.csv");
    let receipt = app
        .shell
        .save_profile_as(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
        }))
        .expect("save")
        .expect("not cancelled");
    assert_eq!(receipt.name, "Racing.csv");
    assert!(receipt.bytes > 0);
    assert!(!looks_like_absolute_path(&receipt.name));
    let written = app.library.text(&target).expect("bytes reached library");
    assert_eq!(written.len(), receipt.bytes);
    let json = serde_json::to_string(&receipt).expect("serializes");
    assert!(!json.contains("/Users/"), "{json}");
}

#[test]
fn a_stale_save_as_is_refused_without_a_dialog_ever_opening() {
    let app = harness();
    let opened = new_profile(&app.shell, "racing.csv");
    app.shell
        .apply_editor_ops(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
            "ops": [set_cell(4, "circle")],
        }))
        .expect("edit");
    let error = dto(app
        .shell
        .save_profile_as(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
        }))
        .expect_err("stale"));
    assert_eq!(code(&error), "QCM_PROFILE_REVISION_CONFLICT");
    assert_eq!(app.picker.times_opened(), 0);
}

#[test]
fn a_plain_save_writes_back_to_the_place_save_as_named() {
    let app = harness();
    let opened = new_profile(&app.shell, "racing.csv");
    let target = app.picker.will_save_as("Racing.csv");
    let first = app
        .shell
        .save_profile_as(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
        }))
        .expect("save as")
        .expect("not cancelled");
    let edited = app
        .shell
        .apply_editor_ops(json!({
            "sessionId": opened.session_id,
            "expectedRevision": first.revision,
            "ops": [set_cell(4, "circle")],
        }))
        .expect("edit");
    app.shell
        .save_profile(json!({
            "sessionId": opened.session_id,
            "expectedRevision": edited.revision,
        }))
        .expect("save");
    assert_eq!(app.picker.times_opened(), 1);
    assert_eq!(app.library.writes(&target), 2);
    assert!(app.library.text(&target).expect("bytes").contains("circle"));
}

#[test]
fn closing_a_dirty_profile_without_an_answer_keeps_it_open() {
    let app = harness();
    let opened = new_profile(&app.shell, "racing.csv");
    app.shell
        .apply_editor_ops(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
            "ops": [set_cell(4, "circle")],
        }))
        .expect("edit");
    let outcome = app
        .shell
        .close_profile(json!({
            "sessionId": opened.session_id,
            "disposition": "if_clean",
        }))
        .expect("ask");
    assert_eq!(
        serde_json::to_value(&outcome).expect("serializes"),
        json!({ "kind": "keptOpenUnsavedChanges" })
    );
    let discarded = app
        .shell
        .close_profile(json!({
            "sessionId": opened.session_id,
            "disposition": "discard",
        }))
        .expect("discard");
    assert_eq!(
        serde_json::to_value(&discarded).expect("serializes"),
        json!({ "kind": "closed" })
    );
}

#[test]
fn a_disposition_the_app_does_not_offer_is_refused() {
    let app = harness();
    let opened = new_profile(&app.shell, "racing.csv");
    let error = dto(app
        .shell
        .close_profile(json!({
            "sessionId": opened.session_id,
            "disposition": "whatever",
        }))
        .expect_err("no fourth answer"));
    assert_eq!(code(&error), "QCM_REQUEST_OUT_OF_RANGE");
}

#[test]
fn closing_with_save_writes_first_and_reports_the_receipt() {
    let app = harness();
    let opened = new_profile(&app.shell, "racing.csv");
    let target = app.picker.will_save_as("Racing.csv");
    app.shell
        .save_profile_as(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
        }))
        .expect("save")
        .expect("not cancelled");
    let outcome = app
        .shell
        .close_profile(json!({
            "sessionId": opened.session_id,
            "disposition": "save",
        }))
        .expect("save and close");
    let value = serde_json::to_value(&outcome).expect("serializes");
    assert_eq!(value["kind"], "savedAndClosed");
    assert_eq!(value["receipt"]["name"], "Racing.csv");
    assert!(app.library.text(&target).is_some());
}

#[test]
fn a_settings_value_the_app_does_not_offer_is_refused_not_rounded() {
    let app = harness();
    let before = app.shell.get_settings();
    let error = dto(app
        .shell
        .update_settings(json!({
            "expectedRevision": before.revision,
            "patch": { "interfaceScalePercent": 137 },
        }))
        .expect_err("invalid scale"));
    assert_eq!(code(&error), "QCM_REQUEST_OUT_OF_RANGE");
    assert!(error.message.contains("interface scale"));
    assert_eq!(app.shell.get_settings(), before);
}

#[test]
fn a_settings_change_moves_the_revision_and_nothing_else() {
    let app = harness();
    let before = app.shell.get_settings();
    let after = app
        .shell
        .update_settings(json!({
            "expectedRevision": before.revision,
            "patch": { "theme": "dark", "interfaceScalePercent": 150 },
        }))
        .expect("legal values");
    assert_eq!(after.theme, "dark");
    assert_eq!(after.interface_scale_percent, 150);
    assert_eq!(after.revision, before.revision + 1);
    assert_eq!(after.model, before.model);
}

#[test]
fn a_settings_change_made_against_a_stale_revision_is_refused() {
    let app = harness();
    let before = app.shell.get_settings();
    app.shell
        .update_settings(json!({
            "expectedRevision": before.revision,
            "patch": { "reduceMotion": true },
        }))
        .expect("first change");
    let error = dto(app
        .shell
        .update_settings(json!({
            "expectedRevision": before.revision,
            "patch": { "theme": "dark" },
        }))
        .expect_err("stale"));
    assert_eq!(code(&error), "QCM_PROFILE_REVISION_CONFLICT");
    assert_eq!(app.shell.get_settings().theme, "system");
}

#[test]
fn the_app_snapshot_claims_only_what_is_wired() {
    let app = harness();
    let snapshot = app.shell.app_snapshot();
    assert!(snapshot.capabilities.profile_editing);
    assert!(snapshot.capabilities.device_install);
    assert!(snapshot.capabilities.live_input);
    assert!(!snapshot.capabilities.community_catalog);
    assert!(!snapshot.capabilities.google_backup);
    assert!(!snapshot.capabilities.agent);
    assert!(!snapshot.version.is_empty());
    assert_eq!(snapshot.settings.revision, 1);
}

#[test]
fn an_editor_snapshot_never_names_a_place_on_this_machine() {
    let app = harness();
    app.picker
        .will_open("/Users/bassam/Documents/Racing.csv", SAMPLE);
    let snapshot = app
        .shell
        .choose_and_open_profile()
        .expect("open")
        .expect("not cancelled");
    assert_eq!(snapshot.save_target.as_deref(), Some("Racing.csv"));
    let json = serde_json::to_string(&snapshot).expect("serializes");
    assert!(!json.contains("/Users/"), "{json}");
    assert!(!json.contains("bassam"), "{json}");
}

#[test]
fn a_failed_save_reports_the_failure_without_the_path_in_it() {
    let library = Arc::new(FakeProfileLibrary::new());
    let picker = Arc::new(StubPicker::new(Arc::clone(&library)));
    let shell = Shell::new(
        Arc::clone(&library),
        Arc::clone(&picker),
        FakeSettingsFile::new(),
    );
    let opened = shell
        .new_profile(json!({ "name": "racing.csv" }))
        .expect("open");
    picker.will_save_as("Racing.csv");
    library.fail_next_write();
    let error = dto(shell
        .save_profile_as(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
        }))
        .expect_err("write failed"));
    assert_eq!(error.code, "QCM_STORAGE_FULL");
    assert_eq!(error.target_state.as_deref(), Some("unchanged"));
    for token in error.message.split_whitespace() {
        assert!(!looks_like_absolute_path(token), "{}", error.message);
    }
}

#[test]
fn no_command_takes_a_path() {
    let app = harness();
    let opened = new_profile(&app.shell, "racing.csv");
    let error = dto(app
        .shell
        .save_profile(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
            "path": "/Users/bassam/Documents/Racing.csv",
        }))
        .expect_err("extra field changes nothing"));
    assert_eq!(code(&error), "QCM_PROFILE_NEEDS_SAVE_TARGET");
}

#[test]
fn every_command_this_build_registers_is_on_the_list() {
    let registered = qcm_tauri_lib::registered_commands();
    assert_eq!(registered.len(), 25);
    for name in [
        "get_app_snapshot",
        "get_settings",
        "update_settings",
        "new_profile",
        "choose_and_open_profile",
        "get_profile_snapshot",
        "apply_editor_ops",
        "undo_editor",
        "save_profile",
        "save_profile_as",
        "close_profile",
        "list_devices",
        "refresh_devices",
        "choose_device_folder",
        "get_device_library",
        "prepare_install",
        "commit_install",
        "prepare_delete_device_profile",
        "commit_delete_device_profile",
        "open_device_profile",
        "open_device_preferences",
        "start_live_input",
        "stop_live_input",
        "subscribe_devices_changed",
        "unsubscribe_devices_changed",
    ] {
        assert!(registered.contains(&name), "{name}");
    }
    for later in ["rename_device_profile", "reorder_device_profiles"] {
        assert!(!registered.contains(&later), "{later}");
    }
}

#[test]
fn a_batch_of_edits_is_one_call_and_many_undo_steps() {
    let app = harness();
    let opened = new_profile(&app.shell, "racing.csv");
    let after = app
        .shell
        .apply_editor_ops(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
            "ops": [set_cell(4, "circle"), set_cell(4, "square")],
        }))
        .expect("both land");
    assert_eq!(after.revision, opened.revision + 2);
    assert!(after.can_undo);
    assert!(after.dirty);
    let once = app
        .shell
        .undo_editor(json!({
            "sessionId": opened.session_id,
            "expectedRevision": after.revision,
        }))
        .expect("undo");
    assert_eq!(once.grid[3][0], "circle");
}

#[test]
fn an_unknown_operation_is_a_malformed_request_and_not_a_silent_skip() {
    let app = harness();
    let opened = new_profile(&app.shell, "racing.csv");
    let error = dto(app
        .shell
        .apply_editor_ops(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
            "ops": [{ "op": "delete_everything" }],
        }))
        .expect_err("unknown operation"));
    assert_eq!(code(&error), "QCM_REQUEST_MALFORMED");
}
