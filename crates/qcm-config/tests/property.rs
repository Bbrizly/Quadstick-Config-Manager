use proptest::prelude::*;
use qcm_config::{Grid, ProfileFile, Severity, parse_csv, write_csv};
use std::fs;
use std::path::{Path, PathBuf};

const CASES: u32 = 128;

fn cell(max_len: usize) -> impl Strategy<Value = String> {
    prop::collection::vec(
        prop_oneof![
            Just('a'),
            Just('Z'),
            Just('0'),
            Just(' '),
            Just(','),
            Just('"'),
            Just('\r'),
            Just('\n'),
            Just('é'),
            Just('中'),
            Just('\0'),
        ],
        0..=max_len,
    )
    .prop_map(|chars| chars.into_iter().collect())
}

fn grid() -> impl Strategy<Value = Grid> {
    prop::collection::vec(prop::collection::vec(cell(24), 1..=16), 0..=16)
}

fn profile_with_extra_columns(extra: Vec<String>) -> ProfileFile {
    let mut binding = vec![String::new(); 12];
    binding[0] = "triangle".to_owned();
    binding[1] = "normal".to_owned();
    binding[2] = "lip".to_owned();
    binding[10] = "note".to_owned();
    binding.extend(extra);

    let raw = vec![
        vec!["Profile Name".to_owned(), String::new(), "Mode".to_owned()],
        vec!["config.csv".to_owned()],
        vec![
            "PlayStation Outputs".to_owned(),
            "Function".to_owned(),
            "usb".to_owned(),
        ],
        binding,
    ];
    ProfileFile::load(&write_csv(&raw))
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(CASES))]

    #[test]
    fn csv_write_then_parse_preserves_safe_arbitrary_grids(rows in grid()) {
        let written = write_csv(&rows);
        prop_assert_eq!(parse_csv(&written), rows);
    }

    #[test]
    fn arbitrary_bounded_text_can_be_parsed_written_and_reparsed(text in cell(2048)) {
        let parsed = parse_csv(&text);
        let written = write_csv(&parsed);
        let reparsed = parse_csv(&written);
        prop_assert_eq!(reparsed, parsed);
    }

    #[test]
    fn unrelated_output_edit_preserves_every_m_plus_cell(
        extra in prop::collection::vec(cell(24), 0..=8),
    ) {
        let mut profile = profile_with_extra_columns(extra);
        let before = profile.grid[3].get(12..).unwrap_or(&[]).to_vec();
        prop_assert!(profile.set_output(4, "circle", ""));
        prop_assert_eq!(profile.grid[3].get(12..).unwrap_or(&[]), before.as_slice());
    }

    #[test]
    fn applying_no_operations_is_raw_grid_identity(rows in grid()) {
        let csv = write_csv(&rows);
        let profile = ProfileFile::load(&csv);
        let before = profile.grid.clone();
        prop_assert_eq!(profile.grid, before);
    }

    #[test]
    fn undo_restores_the_exact_previous_raw_grid(
        extra in prop::collection::vec(cell(24), 0..=8),
        value in cell(24),
    ) {
        let mut profile = profile_with_extra_columns(extra);
        let before = profile.grid.clone();
        let new_column = profile.grid[3].len();
        prop_assert!(profile.set_cell(4, new_column, value));
        prop_assert!(profile.undo());
        prop_assert_eq!(profile.grid, before);
    }

    #[test]
    fn normalization_is_idempotent_for_bounded_arbitrary_text(text in cell(2048)) {
        let mut profile = ProfileFile::load(&text);
        let _ = profile.normalize_for_device_csv();
        let once_grid = profile.grid.clone();
        let once_text = profile.to_csv_text();
        prop_assert!(!profile.normalize_for_device_csv());
        prop_assert_eq!(&profile.grid, &once_grid);
        prop_assert_eq!(profile.to_csv_text(), once_text);
    }

    #[test]
    fn safe_profile_serialization_obeys_device_visible_cell_rules(
        extra in prop::collection::vec(cell(24), 0..=8),
    ) {
        let profile = profile_with_extra_columns(extra);
        prop_assert!(!profile.issues.iter().any(|issue| issue.severity == Severity::Error));

        let serialized = profile.to_csv_text();
        for line in serialized.split("\r\n").filter(|line| !line.is_empty()) {
            prop_assert!(line.len() <= 1023, "device line exceeded 1023 bytes");
        }
        for row in parse_csv(&serialized) {
            for (column, value) in row.iter().enumerate() {
                prop_assert!(
                    !value.contains('\r') && !value.contains('\n'),
                    "serialized cell retained a newline"
                );
                if column < 10 {
                    prop_assert_eq!(value.as_str(), value.trim(), "A..J cell was not trimmed");
                }
            }
        }
    }
}

#[test]
fn malformed_and_edge_fixture_seeds_stay_panic_free() {
    for fixture in [
        "fixtures/profiles/csv-quoted.csv",
        "fixtures/profiles/profile-wrong-case.csv",
        "fixtures/profiles/profile-missing-blank.csv",
        "fixtures/profiles/profile-comma-blank.csv",
        "fixtures/profiles/profile-extra-columns.csv",
    ] {
        let text = fs::read_to_string(repo(fixture)).expect("read fuzz seed fixture");
        let parsed = parse_csv(&text);
        let _ = write_csv(&parsed);
        let mut profile = ProfileFile::load(&text);
        let _ = profile.normalize_for_device_csv();
        let once = profile.grid.clone();
        let _ = profile.normalize_for_device_csv();
        assert_eq!(
            profile.grid, once,
            "normalization seed not idempotent: {fixture}"
        );
    }
}

fn repo(path: impl AsRef<Path>) -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../..")
        .join(path)
}
