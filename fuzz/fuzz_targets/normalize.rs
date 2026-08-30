#![no_main]

use libfuzzer_sys::fuzz_target;
use qcm_config::ProfileFile;

const MAX_INPUT: usize = 64 * 1024;

fuzz_target!(|data: &[u8]| {
    if data.len() > MAX_INPUT {
        return;
    }
    let Ok(text) = std::str::from_utf8(data) else {
        return;
    };

    let mut profile = ProfileFile::load(text);
    let _ = profile.normalize_for_device_csv();
    let once = profile.grid.clone();
    let _ = profile.to_csv_text();
    let second_changed = profile.normalize_for_device_csv();
    assert!(!second_changed);
    assert_eq!(profile.grid, once);
});
