//! HID scanning from threads that come and go, as the live-input worker does
//! (one thread per session). macOS hidapi schedules its device manager on the
//! run loop of whichever thread first touched HID; once that thread exited,
//! the next scan added devices to a dead run loop and the app crashed with
//! SIGTRAP in `CFRunLoopAddSource`. The smoke test hit it opening a profile
//! a second time.

use qcm_core::ports::live_input::LiveInputPort;
use qcm_tauri_lib::adapters::hid::HidLiveInput;

#[test]
fn scanning_from_short_lived_threads_does_not_crash() {
    for _ in 0..4 {
        std::thread::spawn(|| {
            let _ = HidLiveInput::new().candidates();
        })
        .join()
        .expect("a HID scan thread panicked");
    }
}
