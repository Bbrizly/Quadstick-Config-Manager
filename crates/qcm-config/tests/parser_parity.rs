use qcm_config::{ProfileDocument, SheetType, parse_structure};
use serde_json::{Value, json};
use std::fs;
use std::path::{Path, PathBuf};

const SCHEMA_VERSION: &str = "qcm-parity-1";
const LEGACY_BASE: &str = "f7783944387202bcafaeb7ff3f67789098fa6a4e";

#[test]
fn profile_structure_matches_csharp_oracle() {
    let required = std::env::var_os("QCM_REQUIRE_PARSER_ORACLE").is_some();
    let manifest: Value = serde_json::from_str(
        &fs::read_to_string(repo("fixtures/manifest.json")).expect("read fixture manifest"),
    )
    .expect("fixture manifest must be JSON");

    for fixture in manifest["fixtures"]
        .as_array()
        .expect("fixtures must be an array")
    {
        if fixture["kind"] != "profile-csv" {
            continue;
        }
        let id = fixture["id"].as_str().expect("fixture id must be text");
        let fixture_path = fixture["path"].as_str().expect("fixture path must be text");
        let oracle_path = repo(format!("fixtures/oracle/{id}.parser-structure.txt"));
        if !oracle_path.exists() {
            assert!(
                !required,
                "required C# parser oracle artifact is missing: {}",
                oracle_path.display()
            );
            continue;
        }

        let expected: Value = serde_json::from_str(
            &fs::read_to_string(&oracle_path).expect("read C# parser oracle artifact"),
        )
        .expect("C# parser oracle must be JSON");
        let csv = fs::read_to_string(repo(fixture_path)).expect("read profile fixture");
        let actual = canonical_structure(id, &parse_structure(&csv));
        assert_eq!(
            actual, expected,
            "C# parser structure parity failed for {id}"
        );
    }
}

fn canonical_structure(fixture_id: &str, document: &ProfileDocument) -> Value {
    let sheets = document
        .sheets
        .iter()
        .map(|sheet| {
            json!({
                "type": sheet_type_name(sheet.sheet_type),
                "modeName": sheet.mode_name,
                "csvFileName": sheet.csv_file_name,
                "headerLabel": sheet.header_label,
                "channel": sheet.channel,
                "startRow": sheet.start_row,
            })
        })
        .collect::<Vec<_>>();

    json!({
        "schemaVersion": SCHEMA_VERSION,
        "legacyBase": LEGACY_BASE,
        "fixtureId": fixture_id,
        "document": {
            "csvFileName": document.csv_file_name(),
            "fileNameCellRow": document.file_name_cell_row(),
            "hasVersionHeader": document.has_version_header,
            "headerVersion": document.header_version,
            "headerSource": document.header_source,
            "headerName": document.header_name,
            "title": document.title(),
            "isDefaultConfig": document.is_default_config(),
            "isDevicePreferences": document.is_device_preferences(),
            "sheets": sheets,
        }
    })
}

fn sheet_type_name(sheet_type: SheetType) -> &'static str {
    match sheet_type {
        SheetType::ProfileName => "ProfileName",
        SheetType::Preferences => "Preferences",
        SheetType::Infrared => "Infrared",
    }
}

fn repo(path: impl AsRef<Path>) -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../..")
        .join(path)
}
