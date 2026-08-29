use qcm_config::ProfileFile;
use serde_json::Value;
use std::fs;
use std::path::{Path, PathBuf};

#[test]
fn normalized_grid_and_serialized_bytes_match_csharp_oracle() {
    let required = std::env::var_os("QCM_REQUIRE_NORMALIZE_ORACLE").is_some();
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
        let oracle_path = repo(format!("fixtures/oracle/{id}.normalize.json"));
        if !oracle_path.exists() {
            assert!(
                !required,
                "required C# normalization oracle artifact is missing: {}",
                oracle_path.display()
            );
            continue;
        }

        let expected: Value = serde_json::from_str(
            &fs::read_to_string(&oracle_path).expect("read C# normalization oracle artifact"),
        )
        .expect("C# normalization oracle must be JSON");
        let csv = fs::read_to_string(repo(fixture_path)).expect("read profile fixture");
        let mut actual = ProfileFile::load(&csv);
        actual.normalize_for_device_csv();

        assert_eq!(
            serde_json::to_value(&actual.grid).expect("serialize Rust raw grid"),
            expected["snapshot"]["rawGrid"],
            "normalized raw-grid parity failed for {id}"
        );
        assert_eq!(
            actual.to_csv_text(),
            expected["snapshot"]["serialized"]["text"]
                .as_str()
                .expect("oracle serialized text must be text"),
            "normalized serialized byte parity failed for {id}"
        );

        let once_grid = actual.grid.clone();
        let once_text = actual.to_csv_text();
        assert!(
            !actual.normalize_for_device_csv(),
            "second normalization unexpectedly changed {id}"
        );
        assert_eq!(actual.grid, once_grid, "normalization grid is not idempotent for {id}");
        assert_eq!(
            actual.to_csv_text(),
            once_text,
            "normalization bytes are not idempotent for {id}"
        );
    }
}

fn repo(path: impl AsRef<Path>) -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../..")
        .join(path)
}
