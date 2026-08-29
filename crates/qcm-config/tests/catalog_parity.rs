use qcm_config::{canonical_catalog, load_preferences, load_validation};
use serde_json::Value;
use std::fs;
use std::path::{Path, PathBuf};

#[test]
fn embedded_catalog_sources_are_valid() {
    let validation = load_validation().expect("validation.json must remain parseable");
    assert!(!validation.inputs.is_empty());
    assert!(!validation.outputs_ps3.is_empty());
    assert!(!validation.outputs_xbox.is_empty());
    assert!(!validation.functions.is_empty());

    let preferences = load_preferences().expect("preferences.json must remain valid");
    assert!(!preferences.is_empty());
}

#[test]
fn catalog_matches_csharp_oracle() {
    let artifact = repo("fixtures/oracle/catalog-canonical.txt");
    if !artifact.exists() {
        assert!(
            std::env::var_os("QCM_REQUIRE_CATALOG_ORACLE").is_none(),
            "required C# catalog oracle artifact is missing: {}",
            artifact.display()
        );
        return;
    }

    let expected: Value = serde_json::from_str(
        &fs::read_to_string(&artifact).expect("read C# catalog oracle artifact"),
    )
    .expect("C# catalog oracle must be JSON");
    let actual = canonical_catalog().expect("Rust catalog must load");
    assert_eq!(actual, expected, "C# catalog parity failed");
}

fn repo(path: impl AsRef<Path>) -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../..")
        .join(path)
}
