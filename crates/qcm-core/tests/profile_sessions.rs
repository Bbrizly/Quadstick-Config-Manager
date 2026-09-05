//! The session manager, driven the way a window drives it.
//!
//! Every save here goes through a fake library, because the failures that
//! matter (a full disk, a target that turned into a QuadStick, a write that
//! never landed) cannot be produced on demand against a real folder.

use qcm_config::EditorOp;
use qcm_core::error::{ErrorCode, ProfileError, QcmError, QcmErrorDto, looks_like_absolute_path};
use qcm_core::ports::local::{LocalProfileRef, LocalProfileStore};
use qcm_core::profiles::{
    CloseOutcome, CloseRequest, EditorSnapshot, ProfileSessions, SaveReceiptDto, SessionId,
};
use qcm_core::{DeviceFileName, DeviceGeneration, StorageDeviceId};
use qcm_testkit::FakeProfileLibrary;

/// Two modes and no version header, so a save has to normalize on the way out.
const SAMPLE: &str = "Profile Name,,Left Joystick,,,,,,,,Comments\n\
racing.csv,,,,,,,,,,\n\
PlayStation Outputs,Function,usb,,,,,,,,\n\
cross,normal,sip,,,,,,,,\n\
\n\
Profile Name,,Left Joystick,,,,,,,,Comments\n\
racing.csv,,,,,,,,,,\n\
PlayStation Outputs,Function,usb,,,,,,,,\n\
circle,normal,puff,,,,,,,,\n";

fn sessions() -> ProfileSessions<FakeProfileLibrary> {
    ProfileSessions::new(FakeProfileLibrary::new())
}

fn id(snapshot: &EditorSnapshot) -> SessionId {
    snapshot.session_id.parse().expect("session id round trip")
}

fn rename_first_output(value: &str) -> EditorOp {
    EditorOp::SetCell {
        row: 4,
        col: 0,
        value: value.to_owned(),
    }
}

fn code(error: &QcmError) -> ErrorCode {
    error.code()
}

#[test]
fn two_profiles_open_at_once_never_touch_each_others_state() {
    let mut open = sessions();
    let racing = open.store().add("Racing.csv", SAMPLE);
    let flying = open.store().add("Flying.csv", SAMPLE);

    let first = open.open_local(racing).expect("open racing");
    let second = open.open_local(flying).expect("open flying");
    assert_ne!(first.session_id, second.session_id);
    assert_eq!(open.open_count(), 2);

    let first_id = id(&first);
    let second_id = id(&second);
    let edited = open
        .apply_ops(first_id, first.revision, &[rename_first_output("square")])
        .expect("edit the first profile");

    assert!(edited.dirty);
    assert_eq!(edited.revision, first.revision + 1);
    let untouched = open.snapshot(second_id).expect("second still open");
    assert!(!untouched.dirty);
    assert_eq!(untouched.revision, second.revision);
    assert_eq!(untouched.grid, second.grid);
    assert!(open.any_dirty());
}

#[test]
fn an_edit_made_against_a_revision_that_moved_on_is_refused() {
    let mut open = sessions();
    let target = open.store().add("Racing.csv", SAMPLE);
    let opened = open.open_local(target).expect("open");
    let session = id(&opened);

    open.apply_ops(session, opened.revision, &[rename_first_output("square")])
        .expect("first edit");

    let stale = open
        .apply_ops(session, opened.revision, &[rename_first_output("triangle")])
        .expect_err("an edit against the old picture must not land");
    assert_eq!(code(&stale), ErrorCode::ProfileRevisionConflict);
    assert!(matches!(
        stale,
        QcmError::Profile(ProfileError::RevisionConflict {
            expected: 0,
            actual: 1
        })
    ));

    // The refused edit changed nothing: the first one is still what is there.
    let now = open.snapshot(session).expect("still open");
    assert_eq!(now.revision, 1);
    assert_eq!(now.grid[3][0], "square");
}

#[test]
fn of_two_edits_made_from_the_same_picture_only_the_first_lands() {
    let mut open = sessions();
    let target = open.store().add("Racing.csv", SAMPLE);
    let opened = open.open_local(target).expect("open");
    let session = id(&opened);

    // Two windows, or a window and an agent, both holding revision 0.
    let seen_by_both = opened.revision;
    let winner = open
        .apply_ops(session, seen_by_both, &[rename_first_output("square")])
        .expect("the first edit lands");
    let loser = open
        .apply_ops(session, seen_by_both, &[rename_first_output("triangle")])
        .expect_err("the second must not silently win");

    assert_eq!(code(&loser), ErrorCode::ProfileRevisionConflict);
    assert_eq!(winner.grid[3][0], "square");
    assert_eq!(
        open.snapshot(session).expect("open").grid[3][0],
        "square",
        "last write must not win"
    );
}

#[test]
fn a_batch_that_cannot_be_applied_in_full_applies_none_of_it() {
    let mut open = sessions();
    let target = open.store().add("Racing.csv", SAMPLE);
    let opened = open.open_local(target).expect("open");
    let session = id(&opened);

    let refused = open
        .apply_ops(
            session,
            opened.revision,
            &[
                rename_first_output("square"),
                EditorOp::DeleteRow { row: 9_999 },
                rename_first_output("triangle"),
            ],
        )
        .expect_err("a batch with an impossible step must be refused whole");

    assert_eq!(code(&refused), ErrorCode::ProfileOperationRejected);
    assert!(matches!(
        refused,
        QcmError::Profile(ProfileError::OperationRejected {
            index: 1,
            op: "delete_row"
        })
    ));
    let now = open.snapshot(session).expect("still open");
    assert_eq!(now.revision, opened.revision);
    assert!(!now.dirty);
    assert_eq!(now.grid, opened.grid);
}

#[test]
fn every_applied_operation_stays_its_own_undo_step() {
    let mut open = sessions();
    let target = open.store().add("Racing.csv", SAMPLE);
    let opened = open.open_local(target).expect("open");
    let session = id(&opened);

    let after = open
        .apply_ops(
            session,
            opened.revision,
            &[
                rename_first_output("square"),
                rename_first_output("triangle"),
            ],
        )
        .expect("both edits land");
    assert_eq!(after.revision, opened.revision + 2);

    let undone = open.undo(session, after.revision).expect("undo one step");
    assert_eq!(undone.grid[3][0], "square");
    // Undo after a save diverges memory from disk again, so it dirties and
    // moves the revision on rather than winding it back.
    assert_eq!(undone.revision, after.revision + 1);
    assert!(undone.dirty);
}

#[test]
fn a_save_writes_exactly_the_bytes_the_plan_carried() {
    let mut open = sessions();
    let target = open.store().add("Racing.csv", SAMPLE);
    let opened = open.open_local(target.clone()).expect("open");
    let session = id(&opened);
    let edited = open
        .apply_ops(session, opened.revision, &[rename_first_output("square")])
        .expect("edit");

    let plan = open
        .prepare_save(session, edited.revision)
        .expect("prepare a save");
    assert_eq!(plan.session(), session);
    assert_eq!(plan.target_name().as_str(), "Racing.csv");
    assert_eq!(
        open.store().text(&target).as_deref(),
        Some(SAMPLE),
        "preparing must not write"
    );
    let planned = plan.text().to_owned();

    let receipt = open.commit_save(plan).expect("commit the save");
    assert_eq!(
        open.store().text(&target).as_deref(),
        Some(planned.as_str())
    );
    assert_eq!(receipt.bytes, planned.len());
    assert_eq!(receipt.revision, planned_revision(&open, session));
    assert_eq!(SaveReceiptDto::from(&receipt).name, "Racing.csv");

    let after = open.snapshot(session).expect("still open");
    assert!(!after.dirty, "a saved profile is clean");
    assert!(after.can_undo, "saving does not take away undo");
    assert!(planned.contains("QuadStick Configuration"));
    assert!(planned.contains("square"));
}

fn planned_revision(open: &ProfileSessions<FakeProfileLibrary>, session: SessionId) -> u64 {
    open.snapshot(session).expect("open").revision
}

#[test]
fn normalizing_on_the_way_out_is_an_edit_the_receipt_owns_up_to() {
    let mut open = sessions();
    let target = open.store().add("Racing.csv", SAMPLE);
    let opened = open.open_local(target).expect("open");
    let session = id(&opened);

    let receipt = open.save(session, opened.revision).expect("save");
    assert!(
        receipt.revision > opened.revision,
        "the header this save inserted has to move the revision on"
    );
    let after = open.snapshot(session).expect("open");
    assert_eq!(after.revision, receipt.revision);
    assert!(
        after.can_undo,
        "the normalization is undoable like any edit"
    );
}

#[test]
fn a_clean_profile_closes_without_anyone_being_asked() {
    let mut open = sessions();
    let target = open.store().add("Racing.csv", SAMPLE);
    let opened = open.open_local(target.clone()).expect("open");
    let session = id(&opened);

    assert_eq!(
        open.close(session, CloseRequest::IfClean),
        Ok(CloseOutcome::Closed)
    );
    assert_eq!(open.open_count(), 0);
    assert_eq!(open.store().writes(&target), 0);
    assert_eq!(
        code(&open.snapshot(session).expect_err("gone")),
        ErrorCode::ProfileUnknownSession
    );
}

#[test]
fn a_dirty_profile_stays_open_until_the_caller_answers_for_it() {
    let mut open = sessions();
    let target = open.store().add("Racing.csv", SAMPLE);
    let opened = open.open_local(target.clone()).expect("open");
    let session = id(&opened);
    open.apply_ops(session, opened.revision, &[rename_first_output("square")])
        .expect("edit");

    assert_eq!(
        open.close(session, CloseRequest::IfClean),
        Ok(CloseOutcome::KeptOpenUnsavedChanges)
    );
    assert!(open.is_open(session), "the work is still on screen");
    // Asking twice is the same question, not a second answer to it.
    assert_eq!(
        open.close(session, CloseRequest::IfClean),
        Ok(CloseOutcome::KeptOpenUnsavedChanges)
    );
    assert!(open.snapshot(session).expect("open").dirty);
    assert_eq!(open.store().writes(&target), 0);
}

#[test]
fn discarding_drops_the_work_and_writes_nothing() {
    let mut open = sessions();
    let target = open.store().add("Racing.csv", SAMPLE);
    let opened = open.open_local(target.clone()).expect("open");
    let session = id(&opened);
    open.apply_ops(session, opened.revision, &[rename_first_output("square")])
        .expect("edit");

    assert_eq!(
        open.close(session, CloseRequest::Discard),
        Ok(CloseOutcome::Closed)
    );
    assert_eq!(open.open_count(), 0);
    assert_eq!(open.store().writes(&target), 0);
    assert_eq!(open.store().text(&target).as_deref(), Some(SAMPLE));
}

#[test]
fn a_save_that_never_reached_disk_does_not_earn_the_close() {
    let mut open = sessions();
    let target = open.store().add("Racing.csv", SAMPLE);
    let opened = open.open_local(target.clone()).expect("open");
    let session = id(&opened);
    open.apply_ops(session, opened.revision, &[rename_first_output("square")])
        .expect("edit");

    open.store().fail_next_write();
    let failed = open
        .close(session, CloseRequest::Save)
        .expect_err("a full disk must not close the profile");
    assert_eq!(code(&failed), ErrorCode::StorageFull);
    assert!(open.is_open(session), "the user's work stays on screen");
    assert!(open.snapshot(session).expect("open").dirty);
    assert_eq!(open.store().text(&target).as_deref(), Some(SAMPLE));

    let outcome = open.close(session, CloseRequest::Save).expect("second try");
    let CloseOutcome::SavedAndClosed(receipt) = outcome else {
        panic!("expected a saved close, got {outcome:?}");
    };
    assert_eq!(receipt.session, session);
    assert!(!open.is_open(session));
    assert_eq!(open.store().writes(&target), 1);
    let written = open.store().text(&target).expect("written");
    assert!(written.contains("square"));
}

#[test]
fn a_working_copy_read_off_a_device_has_nowhere_to_save_yet() {
    let mut open = sessions();
    let opened = open.open_device_copy(
        StorageDeviceId::from_raw(7),
        DeviceGeneration::from_raw(2),
        DeviceFileName::new("Racing.csv").expect("plain name"),
        SAMPLE,
    );
    let session = id(&opened);
    assert!(opened.save_target.is_none());

    let refused = open
        .save(session, opened.revision)
        .expect_err("save must not write back to the device");
    assert_eq!(code(&refused), ErrorCode::ProfileNeedsSaveTarget);

    let slot = open.store().slot("Racing copy.csv");
    let receipt = open
        .save_as(session, opened.revision, slot.clone())
        .expect("save as into the library");
    assert_eq!(receipt.name.as_str(), "Racing copy.csv");
    assert!(open.store().text(&slot).is_some());
    assert_eq!(
        open.snapshot(session).expect("open").save_target.as_deref(),
        Some("Racing copy.csv")
    );
}

#[test]
fn a_target_that_turned_into_a_quadstick_is_refused_and_forgotten() {
    let mut open = sessions();
    let target = open.store().add("Racing.csv", SAMPLE);
    let opened = open.open_local(target.clone()).expect("open");
    let session = id(&opened);
    open.store().set_on_quadstick(&target, true);

    let refused = open
        .save(session, opened.revision)
        .expect_err("save never writes to the device");
    assert_eq!(code(&refused), ErrorCode::ProfileSaveTargetOnDevice);
    assert_eq!(open.store().writes(&target), 0);
    assert!(
        open.snapshot(session).expect("open").save_target.is_none(),
        "the next save has to ask where to go instead of pointing at the stick"
    );
}

#[test]
fn a_save_as_onto_a_quadstick_leaves_the_old_target_where_it_was() {
    let mut open = sessions();
    let target = open.store().add("Racing.csv", SAMPLE);
    let opened = open.open_local(target).expect("open");
    let session = id(&opened);

    let on_device = open.store().slot("Racing.csv");
    open.store().set_on_quadstick(&on_device, true);
    let refused = open
        .save_as(session, opened.revision, on_device.clone())
        .expect_err("refused");
    assert_eq!(code(&refused), ErrorCode::ProfileSaveTargetOnDevice);
    assert_eq!(open.store().writes(&on_device), 0);
    assert_eq!(
        open.snapshot(session).expect("open").save_target.as_deref(),
        Some("Racing.csv")
    );
}

#[test]
fn a_new_profile_opens_clean_with_nothing_to_undo() {
    let mut open = sessions();
    let opened = open.open_new("racing.csv");
    assert!(!opened.dirty);
    assert!(!opened.can_undo);
    assert!(opened.save_target.is_none());
    assert!(
        opened.grid.iter().any(|row| row
            .first()
            .is_some_and(|cell| cell.eq_ignore_ascii_case("racing.csv"))),
        "the name the user gave has to be in the file"
    );
}

#[test]
fn the_modes_a_window_lists_are_numbered_the_way_the_firmware_counts_them() {
    let mut open = sessions();
    let opened = open.open_community("silas-racing", SAMPLE);
    let numbers: Vec<Option<usize>> = opened.modes.iter().map(|mode| mode.number).collect();
    assert_eq!(numbers, vec![Some(1), Some(2)]);
    assert_eq!(opened.modes[1].index, 1);
}

#[test]
fn a_command_for_a_closed_profile_is_refused_rather_than_landing_elsewhere() {
    let mut open = sessions();
    let target = open.store().add("Racing.csv", SAMPLE);
    let first = open.open_local(target).expect("open");
    let closed = id(&first);
    open.close(closed, CloseRequest::IfClean).expect("close");

    let second = open.store().add("Flying.csv", SAMPLE);
    let reopened = open.open_local(second).expect("open another");
    assert_ne!(reopened.session_id, first.session_id, "ids are not reused");

    for error in [
        open.apply_ops(closed, 0, &[rename_first_output("square")])
            .expect_err("apply"),
        open.undo(closed, 0).expect_err("undo"),
        open.save(closed, 0).expect_err("save"),
    ] {
        assert_eq!(code(&error), ErrorCode::ProfileUnknownSession);
    }
}

#[test]
fn a_read_that_fails_opens_nothing() {
    let mut open = sessions();
    let target = open.store().add("Racing.csv", SAMPLE);
    open.store().fail_next_read();
    let failed = open.open_local(target).expect_err("read failed");
    assert_eq!(code(&failed), ErrorCode::StoragePermissionDenied);
    assert_eq!(open.open_count(), 0);
}

#[test]
fn nothing_a_window_can_see_names_a_place_on_this_machine() {
    let mut open = sessions();
    let picked = open
        .store()
        .add("/Users/tester/Documents/Racing.csv", SAMPLE);
    let local = open.open_local(picked.clone()).expect("open");
    let device = open.open_device_copy(
        StorageDeviceId::from_raw(7),
        DeviceGeneration::from_raw(2),
        DeviceFileName::new("Racing.csv").expect("plain name"),
        SAMPLE,
    );

    for snapshot in [&local, &device] {
        let json = serde_json::to_string(snapshot).expect("snapshot serializes");
        assert!(!json.contains("/Users/tester"), "{json}");
        for token in json.split(['"', ',', ' ']) {
            assert!(!looks_like_absolute_path(token), "{token:?} in {json}");
        }
    }
    assert_eq!(local.save_target.as_deref(), Some("Racing.csv"));

    let session = id(&local);
    open.store().set_on_quadstick(&picked, true);
    let refused = open.save(session, local.revision).expect_err("refused");
    let dto = QcmErrorDto::new(&refused, None);
    assert!(!dto.message.contains("/Users/tester"));
}

#[test]
fn the_port_hands_out_no_path_in_either_direction() {
    let library = FakeProfileLibrary::new();
    let target: LocalProfileRef = library.add("/Volumes/QUADSTICK/Racing.csv", SAMPLE);
    assert_eq!(target.display_name().as_str(), "Racing.csv");
    assert!(!looks_like_absolute_path(&target.id().to_string()));
    assert_eq!(library.read(&target).expect("read"), SAMPLE);
}
