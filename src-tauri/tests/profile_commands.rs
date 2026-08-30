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

/// Whatever the test says the user did in the dialog.
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

    /// How many times a dialog was actually put in front of the user. A refusal
    /// that could have been made first must not cost somebody a file picker.
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

// A payload the command cannot read is still a typed refusal. The window
// switches on the code, and a framework-level string would be neither
// switchable nor covered by the redaction rule.
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
            .expect_err("{malformed} is not a new_profile request"));
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
    let opened = app
        .shell
        .choose_and_open_profile()
        .expect("a cancel is a result");
    assert!(opened.is_none());
}

#[test]
fn a_chosen_file_opens_with_its_own_grid() {
    let app = harness();
    app.picker.will_open("Racing.csv", SAMPLE);
    let snapshot = app
        .shell
        .choose_and_open_profile()
        .expect("the file opens")
        .expect("the user did not cancel");
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
        .expect_err("the second edit was made against a revision that moved on"));
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
        .expect("an empty batch just reads the profile back");
    assert_eq!(now.grid, edited.grid);
}

// One rejected operation takes the whole batch down. Half an applied batch is
// the state nobody can reason about, and the revision would be past where it
// started with no way back.
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
        .expect("nothing moved, so the original revision is still current");
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
        .expect_err("257 is past the batch limit"));
    assert_eq!(code(&error), "QCM_REQUEST_TOO_LARGE");

    let now = app
        .shell
        .apply_editor_ops(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
            "ops": [],
        }))
        .expect("the profile is where it was");
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
        .expect_err("4097 characters is past the limit"));
    assert_eq!(code(&error), "QCM_REQUEST_TOO_LARGE");
}

// Guessing a session id is not an exploit, because every mutation still carries
// a revision and every device write still carries a confirmation. It must still
// be a clean refusal rather than a panic.
#[test]
fn a_forged_session_id_is_an_unknown_session_and_not_a_crash() {
    let app = harness();
    for forged in ["session-999", "not-an-id", "", "session--1"] {
        let error = dto(app
            .shell
            .undo_editor(json!({ "sessionId": forged, "expectedRevision": 1 }))
            .expect_err("nothing is open under that id"));
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
        .expect_err("a new profile has nothing to undo"));
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
        .expect("the edit lands");
    let undone = app
        .shell
        .undo_editor(json!({
            "sessionId": opened.session_id,
            "expectedRevision": edited.revision,
        }))
        .expect("undo lands");
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
        .expect_err("there is nowhere to write yet"));
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
        .expect("a cancel is a result");
    assert!(receipt.is_none());

    // Still nowhere to save, so the next plain Save has to ask again.
    let error = dto(app
        .shell
        .save_profile(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
        }))
        .expect_err("cancelling did not adopt a target"));
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
        .expect("the save lands")
        .expect("the user did not cancel");

    assert_eq!(receipt.name, "Racing.csv");
    assert!(receipt.bytes > 0);
    assert!(!looks_like_absolute_path(&receipt.name));
    let written = app
        .library
        .text(&target)
        .expect("bytes reached the library");
    assert_eq!(written.len(), receipt.bytes);
    let json = serde_json::to_string(&receipt).expect("receipt serializes");
    assert!(!json.contains("/Users/"), "{json}");
}

// A refusal that can be made before the dialog is made before the dialog.
// Asking somebody to pick a file and then telling them it was pointless is the
// kind of small cruelty this app is written to avoid.
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
        .expect("the edit lands");

    let error = dto(app
        .shell
        .save_profile_as(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
        }))
        .expect_err("the profile moved on"));
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
        .expect("save as lands")
        .expect("not cancelled");

    let edited = app
        .shell
        .apply_editor_ops(json!({
            "sessionId": opened.session_id,
            "expectedRevision": first.revision,
            "ops": [set_cell(4, "circle")],
        }))
        .expect("the edit lands");

    app.shell
        .save_profile(json!({
            "sessionId": opened.session_id,
            "expectedRevision": edited.revision,
        }))
        .expect("the second save needs no dialog");

    assert_eq!(app.picker.times_opened(), 1);
    assert_eq!(app.library.writes(&target), 2);
    assert!(
        app.library
            .text(&target)
            .expect("bytes on disk")
            .contains("circle")
    );
}

// The legacy leave prompt had exactly three answers. Closing something dirty
// without giving one keeps the profile open rather than guessing.
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
        .expect("the edit lands");

    let outcome = app
        .shell
        .close_profile(json!({
            "sessionId": opened.session_id,
            "disposition": "if_clean",
        }))
        .expect("asking is not an error");
    assert_eq!(
        serde_json::to_value(&outcome).expect("outcome serializes"),
        json!({ "kind": "keptOpenUnsavedChanges" })
    );

    let discarded = app
        .shell
        .close_profile(json!({
            "sessionId": opened.session_id,
            "disposition": "discard",
        }))
        .expect("an explicit discard closes it");
    assert_eq!(
        serde_json::to_value(&discarded).expect("outcome serializes"),
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
        .expect_err("there is no fourth answer"));
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
        .expect("save as lands")
        .expect("not cancelled");

    let outcome = app
        .shell
        .close_profile(json!({
            "sessionId": opened.session_id,
            "disposition": "save",
        }))
        .expect("save and close");
    let value = serde_json::to_value(&outcome).expect("outcome serializes");
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
        .expect_err("137 is not a size this app offers"));
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
        .expect("both are legal values");
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
        .expect("the first change lands");
    let error = dto(app
        .shell
        .update_settings(json!({
            "expectedRevision": before.revision,
            "patch": { "theme": "dark" },
        }))
        .expect_err("settings moved on"));
    assert_eq!(code(&error), "QCM_PROFILE_REVISION_CONFLICT");
    assert_eq!(app.shell.get_settings().theme, "system");
}

// A capability that is true before its commands exist is a button that does
// nothing. Each flag belongs to the task that registers its commands.
#[test]
fn the_app_snapshot_claims_only_what_is_wired() {
    let app = harness();
    let snapshot = app.shell.app_snapshot();
    assert!(snapshot.capabilities.profile_editing);
    assert!(!snapshot.capabilities.device_install);
    assert!(!snapshot.capabilities.live_input);
    assert!(!snapshot.capabilities.community_catalog);
    assert!(!snapshot.capabilities.google_backup);
    assert!(!snapshot.capabilities.agent);
    assert!(!snapshot.version.is_empty());
    assert_eq!(snapshot.settings.revision, 1);
}

// Nothing a window is shown may say where anything lives on this machine, and
// an editor snapshot is the largest thing it is shown.
#[test]
fn an_editor_snapshot_never_names_a_place_on_this_machine() {
    let app = harness();
    app.picker
        .will_open("/Users/bassam/Documents/Racing.csv", SAMPLE);
    let snapshot = app
        .shell
        .choose_and_open_profile()
        .expect("the file opens")
        .expect("not cancelled");
    assert_eq!(snapshot.save_target.as_deref(), Some("Racing.csv"));
    let json = serde_json::to_string(&snapshot).expect("snapshot serializes");
    assert!(!json.contains("/Users/"), "{json}");
    assert!(!json.contains("bassam"), "{json}");
}

// The failure that carries a path is a write that failed, because the operating
// system puts the path in its own message.
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
        .expect("a new profile opens");
    picker.will_save_as("Racing.csv");
    library.fail_next_write();

    let error = dto(shell
        .save_profile_as(json!({
            "sessionId": opened.session_id,
            "expectedRevision": opened.revision,
        }))
        .expect_err("the write failed"));
    assert_eq!(error.code, "QCM_STORAGE_FULL");
    assert_eq!(error.target_state.as_deref(), Some("unchanged"));
    for token in error.message.split_whitespace() {
        assert!(!looks_like_absolute_path(token), "{}", error.message);
    }
}

// The picker is the only thing that mints a target, so a session whose file the
// user never chose has nothing to write to. This is the port doing its job:
// there is no request shape that names a path.
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
        .expect_err("the extra field changes nothing"));
    assert_eq!(code(&error), "QCM_PROFILE_NEEDS_SAVE_TARGET");
}

#[test]
fn every_command_this_build_registers_is_on_the_list() {
    let registered = qcm_tauri_lib::registered_commands();
    assert_eq!(registered.len(), 10);
    for name in [
        "get_app_snapshot",
        "get_settings",
        "update_settings",
        "new_profile",
        "choose_and_open_profile",
        "apply_editor_ops",
        "undo_editor",
        "save_profile",
        "save_profile_as",
        "close_profile",
    ] {
        assert!(registered.contains(&name), "{name}");
    }
    // Nothing device-shaped is registered yet. TASK-033 owns those, and a
    // command that exists before its confirmation plan does is the failure mode
    // the preparation/commit split was designed to prevent.
    for later in [
        "commit_install",
        "delete_device_profile",
        "start_live_input",
    ] {
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
        .expect("one step back");
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
        .expect_err("there is no such operation"));
    assert_eq!(code(&error), "QCM_REQUEST_MALFORMED");
}
