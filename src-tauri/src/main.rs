// A release build on Windows must not open a console window behind the app.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    qcm_tauri_lib::run();
}
