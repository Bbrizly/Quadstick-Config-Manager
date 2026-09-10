use qcm_config::{IssueKind, Severity, parse_and_validate};
use serde_json::{Value, json};
use std::fs;
use std::path::{Path, PathBuf};

#[test]
fn validation_issues_match_csharp_oracle() {
    let required = std::env::var_os("QCM_REQUIRE_VALIDATION_ORACLE").is_some();
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
        let oracle_path = repo(format!("fixtures/oracle/{id}.inspect.json"));
        if !oracle_path.exists() {
            assert!(
                !required,
                "required C# validation oracle artifact is missing: {}",
                oracle_path.display()
            );
            continue;
        }

        let expected: Value = serde_json::from_str(
            &fs::read_to_string(&oracle_path).expect("read C# inspect oracle"),
        )
        .expect("C# inspect oracle must be JSON");
        let expected_issues = expected["snapshot"]["issues"]
            .as_array()
            .expect("inspect snapshot issues must be an array")
            .iter()
            .map(stable_expected_issue)
            .collect::<Vec<_>>();

        let csv = fs::read_to_string(repo(fixture_path)).expect("read profile fixture");
        let (_, actual_issues) = parse_and_validate(&csv);
        let actual = actual_issues
            .iter()
            .map(|issue| {
                json!({
                    "severity": severity_name(issue.severity),
                    "cell": issue.cell,
                    "kind": kind_name(issue.kind),
                })
            })
            .collect::<Vec<_>>();

        assert_eq!(
            actual, expected_issues,
            "C# validation issue parity failed for {id}"
        );
    }
}

fn stable_expected_issue(issue: &Value) -> Value {
    json!({
        "severity": issue["severity"],
        "cell": issue["cell"],
        "kind": issue["kind"],
    })
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
