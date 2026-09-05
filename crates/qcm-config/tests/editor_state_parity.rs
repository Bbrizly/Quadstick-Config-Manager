use qcm_config::ProfileFile;
use serde_json::{Value, json};
use std::fs;
use std::path::{Path, PathBuf};

#[test]
fn undo_dirty_revision_sequence_matches_csharp_oracle() {
    let required = std::env::var_os("QCM_REQUIRE_EDITOR_STATE_ORACLE").is_some();
    let oracle_path = repo("fixtures/oracle/task16-state.apply.json");
    if !oracle_path.exists() {
        assert!(
            !required,
            "required C# editor-state oracle artifact is missing: {}",
            oracle_path.display()
        );
        return;
    }

    let expected: Value = serde_json::from_str(
        &fs::read_to_string(&oracle_path).expect("read C# editor-state oracle artifact"),
    )
    .expect("C# editor-state oracle must be JSON");
    let operations: Value = serde_json::from_str(
        &fs::read_to_string(repo("fixtures/ops/task16-state.json"))
            .expect("read editor-state operations"),
    )
    .expect("editor-state operations must be JSON");
    let csv = fs::read_to_string(repo("fixtures/profiles/profile-headerless.csv"))
        .expect("read editor-state seed profile");
    let mut actual = ProfileFile::load(&csv);

    let results = operations
        .as_array()
        .expect("editor-state ops root must be an array")
        .iter()
        .enumerate()
        .map(|(index, op)| {
            let kind = op["op"].as_str().expect("op name must be text");
            let applied = match kind {
                "set_cell" => actual.set_cell(
                    usize_field(op, "row"),
                    usize_field(op, "col"),
                    string_field(op, "value").to_owned(),
                ),
                "set_output" => actual.set_output(
                    usize_field(op, "row"),
                    string_field(op, "token"),
                    op.get("action").and_then(Value::as_str).unwrap_or(""),
                ),
                "normalize" => actual.normalize_for_device_csv(),
                "undo" => actual.undo(),
                other => panic!("unsupported editor-state op {other}"),
            };
            json!({ "index": index, "op": kind, "applied": applied })
        })
        .collect::<Vec<_>>();

    assert_eq!(
        Value::Array(results),
        expected["operations"],
        "editor-state operation-result parity failed"
    );
    assert_eq!(
        serde_json::to_value(&actual.grid).expect("serialize Rust raw grid"),
        expected["snapshot"]["rawGrid"],
        "undo must restore the exact raw grid"
    );
    assert_eq!(
        actual.to_csv_text(),
        expected["snapshot"]["serialized"]["text"]
            .as_str()
            .expect("oracle serialized text must be text"),
        "editor-state serialized-byte parity failed"
    );
    assert_eq!(
        json!({
            "dirty": actual.dirty(),
            "revision": actual.revision(),
            "canUndo": actual.can_undo(),
        }),
        expected["snapshot"]["editor"],
        "dirty/revision/undo parity failed"
    );
}

fn usize_field(value: &Value, name: &str) -> usize {
    value[name]
        .as_u64()
        .and_then(|number| usize::try_from(number).ok())
        .unwrap_or_else(|| panic!("{name} must be a non-negative integer"))
}

fn string_field<'a>(value: &'a Value, name: &str) -> &'a str {
    value[name]
        .as_str()
        .unwrap_or_else(|| panic!("{name} must be text"))
}

fn repo(path: impl AsRef<Path>) -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../..")
        .join(path)
}
