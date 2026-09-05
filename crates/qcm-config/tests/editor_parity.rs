use qcm_config::{EditorOp, IssueKind, ProfileFile, Severity, SheetType};
use serde_json::{Value, json};
use std::fs;
use std::path::{Path, PathBuf};

#[test]
fn typed_editor_sequence_matches_csharp_oracle() {
    let required = std::env::var_os("QCM_REQUIRE_EDITOR_ORACLE").is_some();
    let oracle_path = repo("fixtures/oracle/task15-core.apply.json");
    if !oracle_path.exists() {
        assert!(
            !required,
            "required C# editor oracle artifact is missing: {}",
            oracle_path.display()
        );
        return;
    }

    let expected: Value = serde_json::from_str(
        &fs::read_to_string(&oracle_path).expect("read C# editor oracle artifact"),
    )
    .expect("C# editor oracle must be JSON");
    let ops: Vec<EditorOp> = serde_json::from_str(
        &fs::read_to_string(repo("fixtures/ops/task15-core.json")).expect("read editor ops"),
    )
    .expect("editor ops must match the typed Rust command schema");
    let csv = fs::read_to_string(repo("fixtures/profiles/profile-headerless.csv"))
        .expect("read editor seed profile");
    let mut actual = ProfileFile::load(&csv);

    let results = ops
        .iter()
        .enumerate()
        .map(|(index, op)| {
            json!({
                "index": index,
                "op": op.name(),
                "applied": actual.apply_editor_op(op),
            })
        })
        .collect::<Vec<_>>();

    assert_eq!(
        Value::Array(results),
        expected["operations"],
        "operation-result parity failed"
    );
    assert_eq!(
        serde_json::to_value(&actual.grid).expect("serialize Rust raw grid"),
        expected["snapshot"]["rawGrid"],
        "editor raw-grid parity failed"
    );
    assert_eq!(
        canonical_document(&actual),
        expected["snapshot"]["document"],
        "editor parsed-document parity failed"
    );

    let actual_issues = actual
        .issues
        .iter()
        .map(|issue| {
            json!({
                "severity": severity_name(issue.severity),
                "cell": issue.cell,
                "kind": kind_name(issue.kind),
            })
        })
        .collect::<Vec<_>>();
    let expected_issues = expected["snapshot"]["issues"]
        .as_array()
        .expect("oracle issues must be an array")
        .iter()
        .map(|issue| {
            json!({
                "severity": issue["severity"],
                "cell": issue["cell"],
                "kind": issue["kind"],
            })
        })
        .collect::<Vec<_>>();
    assert_eq!(actual_issues, expected_issues, "editor issue parity failed");

    assert_eq!(
        actual.to_csv_text(),
        expected["snapshot"]["serialized"]["text"]
            .as_str()
            .expect("oracle serialized text must be text"),
        "editor serialized-byte parity failed"
    );
}

fn canonical_document(profile: &ProfileFile) -> Value {
    let document = &profile.document;
    json!({
        "csvFileName": document.csv_file_name(),
        "fileNameCellRow": document.file_name_cell_row(),
        "hasVersionHeader": document.has_version_header,
        "headerVersion": document.header_version,
        "headerSource": document.header_source,
        "headerName": document.header_name,
        "title": document.title(),
        "isDefaultConfig": document.is_default_config(),
        "isDevicePreferences": document.is_device_preferences(),
        "sheets": document.sheets.iter().enumerate().map(|(index, sheet)| {
            json!({
                "index": index,
                "type": sheet_type_name(sheet.sheet_type),
                "modeName": sheet.mode_name,
                "csvFileName": sheet.csv_file_name,
                "headerLabel": sheet.header_label,
                "channel": sheet.channel,
                "startRow": sheet.start_row,
                "bindings": sheet.bindings.iter().map(|binding| {
                    json!({
                        "row": binding.row,
                        "output": binding.output,
                        "function": binding.function,
                        "inputs": binding.inputs,
                        "inputCols": binding.input_cols,
                        "actionName": binding.action_name,
                    })
                }).collect::<Vec<_>>(),
            })
        }).collect::<Vec<_>>(),
    })
}

fn sheet_type_name(sheet_type: SheetType) -> &'static str {
    match sheet_type {
        SheetType::ProfileName => "ProfileName",
        SheetType::Preferences => "Preferences",
        SheetType::Infrared => "Infrared",
    }
}

fn severity_name(severity: Severity) -> &'static str {
    match severity {
        Severity::Error => "Error",
        Severity::Warning => "Warning",
    }
}

fn kind_name(kind: IssueKind) -> &'static str {
    match kind {
        IssueKind::None => "None",
        IssueKind::UnknownInput => "UnknownInput",
    }
}

fn repo(path: impl AsRef<Path>) -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../..")
        .join(path)
}
