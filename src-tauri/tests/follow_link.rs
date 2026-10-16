//! Port of `FollowLinkTests.cs` at the native shell boundary: a profile
//! imported from a Sheets link follows it once saved, and Get the latest asks
//! the same sheet again, shows what changed, and replaces only after a yes.

use qcm_core::QcmError;
use qcm_core::ports::local::LocalProfileRef;
use qcm_tauri_lib::adapters::picker::ProfilePicker;
use qcm_tauri_lib::follow::{FetchFailure, FollowLinks, SheetFetcher, SheetRefusal, fetch_sheet};
use qcm_tauri_lib::follow_shell::{ChangeDto, LatestBegin, LatestCheckDto, LatestReplaceDto};
use qcm_tauri_lib::shell::Shell;
use qcm_testkit::{FakeProfileLibrary, FakeSettingsFile};
use serde_json::json;
use std::path::PathBuf;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, OnceLock};

const LINK: &str = "https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/edit";

fn profile(modes: &[&str]) -> String {
    modes
        .iter()
        .map(|m| {
            format!(
                "Profile Name,,{m}\r\ncatalog.csv\r\nPlayStation Outputs,Function,usb\r\ndpad_N,normal,right_sip\r\n"
            )
        })
        .collect::<Vec<_>>()
        .join("\r\n")
}

/// Answers like a sheet whose workbook export is unavailable, so the import
/// takes its CSV fallback. The body can be swapped between presses.
struct Sheet {
    csv: Mutex<String>,
    requests: AtomicUsize,
}
impl Sheet {
    fn new(csv: &str) -> Self {
        Self {
            csv: Mutex::new(csv.to_owned()),
            requests: AtomicUsize::new(0),
        }
    }
    fn set(&self, csv: &str) {
        *self.csv.lock().expect("sheet") = csv.to_owned();
    }
    fn requests(&self) -> usize {
        self.requests.load(Ordering::SeqCst)
    }
}
impl SheetFetcher for Sheet {
    fn get(&self, url: &str) -> Result<Vec<u8>, FetchFailure> {
        self.requests.fetch_add(1, Ordering::SeqCst);
        Ok(if url.contains("format=csv") {
            self.csv.lock().expect("sheet").clone().into_bytes()
        } else {
            b"not a workbook".to_vec()
        })
    }
}

#[derive(Default)]
struct QueuePicker {
    open: Mutex<Vec<LocalProfileRef>>,
    save: Mutex<Vec<LocalProfileRef>>,
}
impl ProfilePicker for QueuePicker {
    fn pick_open(&self) -> Result<Option<LocalProfileRef>, QcmError> {
        Ok(self.open.lock().expect("picker").pop())
    }
    fn pick_save_as(&self, _suggested: &str) -> Result<Option<LocalProfileRef>, QcmError> {
        Ok(self.save.lock().expect("picker").pop())
    }
}

type TestShell = Shell<FakeProfileLibrary, Arc<QueuePicker>, FakeSettingsFile>;

fn rescue_dir() -> &'static PathBuf {
    static DIR: OnceLock<PathBuf> = OnceLock::new();
    DIR.get_or_init(|| {
        let dir = std::env::temp_dir().join(format!("qcm-follow-rescue-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        qcm_tauri_lib::diagnostics::set_rescue_dir_override(Some(dir.clone()));
        dir
    })
}

fn rescued(stem: &str) -> Vec<String> {
    let mut found: Vec<String> = std::fs::read_dir(rescue_dir())
        .map(|entries| {
            entries
                .filter_map(Result::ok)
                .map(|e| e.file_name().to_string_lossy().into_owned())
                .filter(|n| n.starts_with(&format!("{stem}-before-latest-")))
                .collect()
        })
        .unwrap_or_default();
    found.sort();
    found
}

struct World {
    library: Arc<FakeProfileLibrary>,
    picker: Arc<QueuePicker>,
    shell: TestShell,
}

fn world(links: FollowLinks) -> World {
    rescue_dir();
    let library = Arc::new(FakeProfileLibrary::new());
    let picker = Arc::new(QueuePicker::default());
    let shell = Shell::new(
        Arc::clone(&library),
        Arc::clone(&picker),
        FakeSettingsFile::new(),
    )
    .with_follow_links(links);
    World {
        library,
        picker,
        shell,
    }
}

impl World {
    /// Import from the link and Save As `name`: the profile now follows it.
    fn import_and_save(&self, name: &str, csv: &str) -> (String, LocalProfileRef) {
        let opened = self.shell.open_workbook_copy(name, csv).expect("opens");
        self.shell
            .set_pending_follow(&opened.session_id, LINK.to_owned());
        let slot = self.library.slot(name);
        self.picker.save.lock().expect("picker").push(slot.clone());
        self.shell
            .save_profile_as(
                json!({ "sessionId": opened.session_id, "expectedRevision": opened.revision }),
            )
            .expect("saves")
            .expect("not cancelled");
        (opened.session_id, slot)
    }

    fn follows(&self, session: &str) -> bool {
        self.shell
            .follow_state(json!({ "sessionId": session }))
            .expect("state")
            .follows
    }

    fn revision(&self, session: &str) -> u64 {
        self.shell
            .get_profile_snapshot(json!({ "sessionId": session }))
            .expect("snapshot")
            .revision
    }

    fn check(&self, session: &str, sheet: &Sheet) -> LatestCheckDto {
        let begin = self
            .shell
            .latest_begin(
                json!({ "sessionId": session, "expectedRevision": self.revision(session) }),
            )
            .expect("begin");
        match begin {
            LatestBegin::Done(answer) => answer,
            LatestBegin::Fetch {
                session,
                revision,
                link,
            } => self
                .shell
                .latest_compare(session, revision, fetch_sheet(&link, sheet))
                .expect("compare"),
        }
    }

    fn replace(&self, plan_id: &str) -> LatestReplaceDto {
        self.shell
            .replace_with_latest(json!({ "planId": plan_id }))
            .expect("replace")
    }
}

fn plan(answer: &LatestCheckDto) -> (String, Vec<ChangeDto>) {
    match answer {
        LatestCheckDto::Changes {
            plan_id, changes, ..
        } => (plan_id.clone(), changes.clone()),
        other => panic!("expected changes, got {other:?}"),
    }
}

#[test]
fn an_imported_profile_remembers_its_link_once_saved_and_after_a_restart() {
    let file = std::env::temp_dir().join(format!("qcm-follow-links-{}.json", std::process::id()));
    let _ = std::fs::remove_file(&file);
    let w = world(FollowLinks::file(file.clone()));
    let opened = w
        .shell
        .open_workbook_copy("game.csv", &profile(&["Walking"]))
        .expect("opens");
    w.shell
        .set_pending_follow(&opened.session_id, LINK.to_owned());
    assert!(!w.follows(&opened.session_id)); // not until there is a file
    let slot = w.library.slot("game.csv");
    w.picker.save.lock().expect("picker").push(slot.clone());
    w.shell
        .save_profile_as(
            json!({ "sessionId": opened.session_id, "expectedRevision": opened.revision }),
        )
        .expect("saves");
    assert!(w.follows(&opened.session_id));

    // A restart: a new shell reads the same file of links.
    let again = Shell::new(
        Arc::clone(&w.library),
        Arc::clone(&w.picker),
        FakeSettingsFile::new(),
    )
    .with_follow_links(FollowLinks::file(file.clone()));
    w.picker.open.lock().expect("picker").push(slot);
    let reopened = again
        .choose_and_open_profile()
        .expect("opens")
        .expect("picked");
    assert!(
        again
            .follow_state(json!({ "sessionId": reopened.session_id }))
            .expect("state")
            .follows
    );
    let _ = std::fs::remove_file(&file);
}

// Opening some other profile between the import and the save is not that
// import being saved, and must not hand it the link.
#[test]
fn a_profile_that_was_not_imported_does_not_pick_up_a_link() {
    let w = world(FollowLinks::in_memory());
    let imported = w
        .shell
        .open_workbook_copy("game.csv", &profile(&["Walking"]))
        .expect("opens");
    w.shell
        .set_pending_follow(&imported.session_id, LINK.to_owned());
    let other = w.library.add("other.csv", &profile(&["Flying"]));
    w.picker.open.lock().expect("picker").push(other);
    let opened = w
        .shell
        .choose_and_open_profile()
        .expect("opens")
        .expect("picked");
    w.shell
        .apply_editor_ops(
            json!({ "sessionId": opened.session_id, "expectedRevision": opened.revision,
            "ops": [{ "op": "set_cell", "row": 4, "col": 2, "value": "left_sip" }] }),
        )
        .expect("edits");
    let revision = w.revision(&opened.session_id);
    w.shell
        .save_profile(json!({ "sessionId": opened.session_id, "expectedRevision": revision }))
        .expect("saves");
    assert!(!w.follows(&opened.session_id));
}

#[test]
fn the_latest_replaces_the_profile_only_after_a_yes() {
    let w = world(FollowLinks::in_memory());
    let (session, target) = w.import_and_save("yes.csv", &profile(&["Walking"]));
    let before = w.library.text(&target).expect("saved");
    let sheet = Sheet::new(&profile(&["Walking", "Driving"]));

    let answer = w.check(&session, &sheet);
    let (plan_id, changes) = plan(&answer);
    assert!(changes.contains(&ChangeDto::ModeAdded {
        mode: "Driving".into()
    }));
    assert_eq!(w.library.text(&target).as_deref(), Some(before.as_str())); // not yet

    let LatestReplaceDto::Replaced { snapshot } = w.replace(&plan_id) else {
        panic!("replaced");
    };
    let now = w.library.text(&target).expect("written");
    assert!(now.contains("Driving"));
    assert!(!snapshot.dirty);
    assert_eq!(rescued("yes").len(), 1);

    // A second replace in the same second keeps both rescue copies.
    sheet.set(&profile(&["Walking", "Driving", "Flying"]));
    let (plan_id, _) = plan(&w.check(&snapshot.session_id, &sheet));
    assert!(matches!(
        w.replace(&plan_id),
        LatestReplaceDto::Replaced { .. }
    ));
    let copies = rescued("yes");
    assert_eq!(copies.len(), 2);
    assert_ne!(copies[0], copies[1]);
    let first = std::fs::read_to_string(rescue_dir().join(&copies[0])).expect("rescue");
    let second = std::fs::read_to_string(rescue_dir().join(&copies[1])).expect("rescue");
    assert!([first.as_str(), second.as_str()].contains(&before.as_str()));
}

#[test]
fn saying_keep_mine_changes_nothing_and_an_unchanged_sheet_is_up_to_date() {
    let w = world(FollowLinks::in_memory());
    let (session, target) = w.import_and_save("keep.csv", &profile(&["Walking"]));
    let before = w.library.text(&target).expect("saved");
    let sheet = Sheet::new(&profile(&["Walking"]).replace("right_sip", "left_puff"));
    let _ = plan(&w.check(&session, &sheet)); // asked, then the person says Keep mine
    assert_eq!(w.library.text(&target).as_deref(), Some(before.as_str()));
    assert!(rescued("keep").is_empty());

    sheet.set(&before);
    assert_eq!(w.check(&session, &sheet), LatestCheckDto::UpToDate);
}

// The same validation as Import: a sign-in page or a sheet with no profile tab
// never gets as far as the question, let alone the file.
#[test]
fn a_sheet_that_would_not_import_is_refused() {
    let w = world(FollowLinks::in_memory());
    let (session, target) = w.import_and_save("refused.csv", &profile(&["Walking"]));
    let before = w.library.text(&target).expect("saved");
    for answer in ["<!doctype html><html>Sign in</html>", "just,notes\r\n"] {
        let sheet = Sheet::new(answer);
        assert!(matches!(
            w.check(&session, &sheet),
            LatestCheckDto::Refused {
                refusal: SheetRefusal::WebPage | SheetRefusal::NoProfileTab { .. }
            }
        ));
    }
    assert_eq!(w.library.text(&target).as_deref(), Some(before.as_str()));
}

// Unsaved work is asked about first; nothing goes out before it is saved.
#[test]
fn an_unsaved_edit_is_never_thrown_away() {
    let w = world(FollowLinks::in_memory());
    let (session, _) = w.import_and_save("unsaved.csv", &profile(&["Walking"]));
    w.shell
        .apply_editor_ops(
            json!({ "sessionId": session, "expectedRevision": w.revision(&session),
            "ops": [{ "op": "set_cell", "row": 4, "col": 2, "value": "left_sip" }] }),
        )
        .expect("edits");
    let sheet = Sheet::new(&profile(&["Walking", "Driving"]));
    assert_eq!(w.check(&session, &sheet), LatestCheckDto::Unsaved);
    assert_eq!(sheet.requests(), 0);
}

// An edit made while the question is up was never in the list the person
// said yes to, and neither was a change on disk.
#[test]
fn an_edit_or_a_disk_change_made_while_the_question_is_up_is_not_replaced() {
    let w = world(FollowLinks::in_memory());
    let (session, target) = w.import_and_save("meanwhile.csv", &profile(&["Walking"]));
    let sheet = Sheet::new(&profile(&["Walking", "Driving"]));

    let (plan_id, _) = plan(&w.check(&session, &sheet));
    w.shell
        .apply_editor_ops(
            json!({ "sessionId": session, "expectedRevision": w.revision(&session),
            "ops": [{ "op": "set_cell", "row": 4, "col": 2, "value": "left_sip" }] }),
        )
        .expect("edits");
    assert!(matches!(
        w.replace(&plan_id),
        LatestReplaceDto::ChangedMeanwhile
    ));
    assert!(!w.library.text(&target).expect("kept").contains("Driving"));
    let revision = w.revision(&session);
    w.shell
        .save_profile(json!({ "sessionId": session, "expectedRevision": revision }))
        .expect("saves");

    let (plan_id, _) = plan(&w.check(&session, &sheet));
    w.library.set_text(&target, &profile(&["Elsewhere"]));
    assert!(matches!(
        w.replace(&plan_id),
        LatestReplaceDto::ChangedMeanwhile
    ));
    assert!(w.library.text(&target).expect("kept").contains("Elsewhere"));
    assert!(rescued("meanwhile").is_empty());
}

// Opening the profile and asking whether it follows makes no request.
#[test]
fn nothing_is_asked_for_until_the_button_is_pressed() {
    let w = world(FollowLinks::in_memory());
    let (session, target) = w.import_and_save("quiet.csv", &profile(&["Walking"]));
    let sheet = Sheet::new(&profile(&["Walking"]));
    w.picker.open.lock().expect("picker").push(target);
    let reopened = w
        .shell
        .choose_and_open_profile()
        .expect("opens")
        .expect("picked");
    assert!(w.follows(&session));
    assert!(w.follows(&reopened.session_id));
    assert_eq!(sheet.requests(), 0);
}

#[test]
fn forgetting_the_link_stops_following_and_leaves_the_file() {
    let w = world(FollowLinks::in_memory());
    let (session, target) = w.import_and_save("forget.csv", &profile(&["Walking"]));
    let before = w.library.text(&target).expect("saved");
    assert!(
        !w.shell
            .forget_follow_link(json!({ "sessionId": session }))
            .expect("forgets")
            .follows
    );
    assert!(!w.follows(&session));
    let sheet = Sheet::new(&profile(&["Walking", "Driving"]));
    assert_eq!(w.check(&session, &sheet), LatestCheckDto::NotFollowed);
    assert_eq!(sheet.requests(), 0);
    assert_eq!(w.library.text(&target).as_deref(), Some(before.as_str()));
}

#[test]
fn unused_custom_names_wait_for_a_file_then_last_with_it() {
    let w = world(FollowLinks::in_memory());
    let opened = w
        .shell
        .open_workbook_copy("names.csv", &profile(&["Walking"]))
        .expect("opens");
    let drafts = json!({ "sessionId": opened.session_id, "drafts": [["Shoot", "r2"]] });
    // Untitled: nowhere to keep them yet, so the window holds them.
    assert!(
        !w.shell
            .set_custom_name_drafts(drafts.clone())
            .expect("set")
            .kept
    );
    let slot = w.library.slot("names.csv");
    w.picker.save.lock().expect("picker").push(slot);
    w.shell
        .save_profile_as(
            json!({ "sessionId": opened.session_id, "expectedRevision": opened.revision }),
        )
        .expect("saves")
        .expect("not cancelled");
    assert!(w.shell.set_custom_name_drafts(drafts).expect("set").kept);
    let read = w
        .shell
        .custom_name_drafts(json!({ "sessionId": opened.session_id }))
        .expect("read");
    assert_eq!(read.drafts, vec![("Shoot".to_owned(), "r2".to_owned())]);
    // An empty list forgets them; a blank name is refused.
    assert!(
        w.shell
            .set_custom_name_drafts(json!({ "sessionId": opened.session_id, "drafts": [] }))
            .expect("clear")
            .kept
    );
    assert!(
        w.shell
            .custom_name_drafts(json!({ "sessionId": opened.session_id }))
            .expect("read")
            .drafts
            .is_empty()
    );
    assert!(
        w.shell
            .set_custom_name_drafts(
                json!({ "sessionId": opened.session_id, "drafts": [[" ", "x"]] })
            )
            .is_err()
    );
}
