use qcm_config::{EditorOp, ProfileFile};

fn profile() -> ProfileFile {
    ProfileFile::load(
        "Profile Name,,One\nconfig.csv\nOutputs,Function,usb\nx,normal,lip\n\nProfile Name,,Two\n\nOutputs,Function,usb\ncircle,normal,puff\n",
    )
}

#[test]
fn typed_duplicate_move_and_delete_use_whole_sheet_semantics() {
    let mut profile = profile();

    assert!(profile.apply_editor_op(&EditorOp::DuplicateMode {
        sheet: 0,
        name: "Copy".to_owned(),
    }));
    assert_eq!(profile.document.sheets.len(), 3);
    assert_eq!(profile.document.sheets[2].mode_name, "Copy");

    assert!(profile.apply_editor_op(&EditorOp::MoveMode {
        sheet: 2,
        delta: -1,
    }));
    assert_eq!(profile.document.sheets[1].mode_name, "Copy");

    assert!(profile.apply_editor_op(&EditorOp::DeleteMode { sheet: 0 }));
    assert_eq!(profile.document.sheets[0].mode_name, "Copy");
    assert_eq!(profile.document.csv_file_name(), Some("config.csv"));
}

#[test]
fn typed_delete_refuses_to_remove_the_last_mode() {
    let mut profile =
        ProfileFile::load("Profile Name,,Only\nconfig.csv\nOutputs,Function,usb\nx,normal,lip\n");
    assert!(!profile.apply_editor_op(&EditorOp::DeleteMode { sheet: 0 }));
    assert_eq!(profile.document.sheets.len(), 1);
}
