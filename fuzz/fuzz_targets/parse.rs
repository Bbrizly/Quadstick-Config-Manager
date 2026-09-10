#![no_main]

use libfuzzer_sys::fuzz_target;
use qcm_config::{parse_csv, write_csv};

const MAX_INPUT: usize = 64 * 1024;

fuzz_target!(|data: &[u8]| {
    if data.len() > MAX_INPUT {
        return;
    }
    let Ok(text) = std::str::from_utf8(data) else {
        return;
    };

    let parsed = parse_csv(text);
    let written = write_csv(&parsed);
    let _ = parse_csv(&written);
});
