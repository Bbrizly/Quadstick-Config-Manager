#![no_main]

use libfuzzer_sys::fuzz_target;
use qcm_config::ProfileFile;

const MAX_INPUT: usize = 64 * 1024;
const MAX_OPS: usize = 64;
const TOKENS: [&str; 4] = ["triangle", "circle", "mouse_left", ""];
const VALUES: [&str; 5] = ["", "lip", "normal", "note", "é中"];

fuzz_target!(|data: &[u8]| {
    if data.len() > MAX_INPUT || data.is_empty() {
        return;
    }

    let split = 1 + (usize::from(data[0]) * data.len().saturating_sub(1) / 255);
    let split = split.min(data.len());
    let Ok(text) = std::str::from_utf8(&data[1..split]) else {
        return;
    };
    let mut profile = ProfileFile::load(text);

    for chunk in data[split..].chunks(4).take(MAX_OPS) {
        let op = chunk.first().copied().unwrap_or_default() % 7;
        let a = chunk.get(1).copied().unwrap_or_default();
        let b = chunk.get(2).copied().unwrap_or_default();
        let c = chunk.get(3).copied().unwrap_or_default();
        match op {
            0 => {
                let row = usize::from(a % 32) + 1;
                let col = usize::from(b % 20);
                let value = VALUES[usize::from(c) % VALUES.len()].to_owned();
                let _ = profile.set_cell(row, col, value);
            }
            1 => {
                let row = usize::from(a % 32) + 1;
                let token = TOKENS[usize::from(b) % TOKENS.len()];
                let _ = profile.set_output(row, token, "");
            }
            2 => {
                let _ = profile.normalize_for_device_csv();
            }
            3 => {
                let _ = profile.undo();
            }
            4 => {
                let _ = profile.add_mode_sheet(VALUES[usize::from(c) % VALUES.len()]);
            }
            5 => {
                if !profile.grid.is_empty() {
                    let row = usize::from(a) % profile.grid.len() + 1;
                    let _ = profile.delete_row(row);
                }
            }
            _ => {
                if !profile.document.sheets.is_empty() {
                    let sheet = usize::from(a) % profile.document.sheets.len();
                    let _ = profile.set_mode_channel(sheet, TOKENS[usize::from(b) % TOKENS.len()]);
                }
            }
        }
        let _ = profile.to_csv_text();
    }
});
