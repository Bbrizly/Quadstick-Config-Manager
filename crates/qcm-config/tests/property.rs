use proptest::prelude::*;
use qcm_config::{Grid, ProfileFile, Severity, parse_csv, write_csv};
use std::fs;
use std::path::{Path, PathBuf};

const CASES: u32 = 128;

fn cell(max_len: usize) -> impl Strategy<Value = String> {
    prop::collection::vec(
        prop_oneof![
            (b'a'..=b'z'),
            (b'A'..=b'Z'),
            (b'0'..=b'9'),
            Just(b' '),
            Just(b'_'),
            Just(b'-'),
            Just(b';'),
            Just(b','),
            Just(b'"'),
        ],
        0..=max_len,
    )
    .prop_map(|bytes| String::from_utf8(bytes).expect("ASCII generator"))
}

fn grid() -> impl Strategy<Value = Grid> {
    prop::collection::vec(prop::collection::vec(cell(24), 0..=18), 0..=40)
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(CASES))]

    #[test]
    fn csv_parse_write_parse_is_stable(g in grid()) {
        let text = write_csv(&g);
        prop_assert_eq!(parse_csv(&text), g);
    }

    #[test]
    fn device_serialization_is_idempotent(g in grid()) {
        let text = write_csv(&g);
        let first = ProfileFile::load(&text).to_csv_text();
        let second = ProfileFile::load(&first).to_csv_text();
        prop_assert_eq!(second, first);
    }

    #[test]
    fn normalize_twice_is_a_noop(g in grid()) {
        let text = write_csv(&g);
        let mut profile = ProfileFile::load(&text);
        let _ = profile.normalize_for_device_csv();
        let once = profile.to_csv_text();
        let once_grid = profile.grid.clone();
        let changed_again = profile.normalize_for_device_csv();
        prop_assert!(!changed_again);
        prop_assert_eq!(&profile.grid, &once_grid);
        prop_assert_eq!(profile.to_csv_text(), once);
    }
}

#[test]
fn committed_profiles_survive_parse_and_normalize_without_panicking() {
    for path in corpus_profiles() {
        let text = fs::read_to_string(&path).expect("read committed profile seed");
        let profile = ProfileFile::load(&text);
        assert!(profile.issues.iter().all(|issue| matches!(issue.severity, Severity::Error | Severity::Warning)));
        let mut normalized = profile.clone();
        let _ = normalized.normalize_for_device_csv();
        let once = normalized.to_csv_text();
        let _ = normalized.normalize_for_device_csv();
        assert_eq!(normalized.to_csv_text(), once, "normalization drift: {}", path.display());
    }
}

fn corpus_profiles() -> Vec<PathBuf> {
    let dir = repo("fixtures/profiles");
    let mut paths = fs::read_dir(dir)
        .expect("read fixture profile directory")
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .filter(|path| path.extension().is_some_and(|ext| ext == "csv"))
        .collect::<Vec<_>>();
    paths.sort();
    paths
}

fn repo(path: impl AsRef<Path>) -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../..")
        .join(path)
}
