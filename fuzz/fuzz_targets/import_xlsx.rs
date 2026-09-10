#![no_main]

use libfuzzer_sys::fuzz_target;
use qcm_config::{export_xlsx, import_xlsx, ProfileFile};

const MAX_INPUT: usize = 256 * 1024;

fuzz_target!(|data: &[u8]| {
    if data.len() > MAX_INPUT {
        return;
    }
    if let Ok(import) = import_xlsx(data) {
        let profile = ProfileFile::load(&import.csv);
        if let Ok(bytes) = export_xlsx(&profile) {
            let _ = import_xlsx(&bytes);
        }
    }
});
