use std::{thread, time::Duration};

use hidapi::{HidApi, HidDevice};
use qs_bridge::{PRODUCT_ID_X360CE, Pad, VENDOR_ID, decode};

const USAGE: &str = "usage: qs-bridge [--print]

Copies a QuadStick in emulation mode 2 (x360ce) onto a virtual Xbox 360 pad.
--print  show each report and what it decodes to, and make no pad";

fn main() {
    let print = match std::env::args().nth(1).as_deref() {
        None => false,
        Some("--print") => true,
        Some(_) => {
            eprintln!("{USAGE}");
            std::process::exit(2);
        }
    };
    if !print && !cfg!(target_os = "linux") {
        eprintln!("qs-bridge: only Linux can make the virtual pad so far. --print works here.");
        std::process::exit(2);
    }
    let mut api = HidApi::new().unwrap_or_else(|e| fail(&format!("cannot start HID: {e}")));
    let mut waiting = false;
    loop {
        match open_stick(&mut api) {
            Some(stick) => {
                waiting = false;
                eprintln!("qs-bridge: QuadStick found");
                if print {
                    pump(&stick, |raw, pad| println!("{raw:02x?}\n  {pad:?}"));
                } else {
                    bridge(&stick);
                }
                eprintln!("qs-bridge: QuadStick gone, all buttons released");
            }
            None if !waiting => {
                waiting = true;
                eprintln!("qs-bridge: waiting for a QuadStick in emulation mode 2 (16d0:092c)");
            }
            None => {}
        }
        thread::sleep(Duration::from_secs(1));
    }
}

#[cfg(target_os = "linux")]
fn bridge(stick: &HidDevice) {
    let mut pad = qs_bridge::uinput::VirtualPad::create().unwrap_or_else(|e| {
        fail(&format!(
            "cannot make the virtual pad: {e}. Install 70-quadstick.rules (see README) and plug the stick in again."
        ))
    });
    pump(stick, |_, p| {
        if let Err(e) = pad.send(p) {
            eprintln!("qs-bridge: write to the virtual pad failed: {e}");
        }
    });
    // Dropping `pad` sends a neutral report and removes the device.
}

#[cfg(not(target_os = "linux"))]
fn bridge(_: &HidDevice) {}

/// Reads until the stick goes. Every state change reaches `send`, and a report
/// that cannot be read is sent as neutral so nothing stays held on a bad read.
fn pump(stick: &HidDevice, mut send: impl FnMut(&[u8], &Pad)) {
    let mut buf = [0u8; 64];
    let mut last = None;
    loop {
        let n = match stick.read_timeout(&mut buf, 1000) {
            Ok(0) => continue,
            Ok(n) => n,
            Err(_) => return,
        };
        let pad = decode(&buf[..n]).unwrap_or_default();
        if last != Some(pad) {
            send(&buf[..n], &pad);
            last = Some(pad);
        }
    }
}

/// The gamepad interface only. Mode 2 also brings keyboard, mouse and drive.
fn open_stick(api: &mut HidApi) -> Option<HidDevice> {
    api.refresh_devices().ok()?;
    let ours =
        |d: &&hidapi::DeviceInfo| d.vendor_id() == VENDOR_ID && d.product_id() == PRODUCT_ID_X360CE;
    let info = api
        .device_list()
        .filter(ours)
        .find(|d| d.usage_page() == 0x01 && d.usage() == 0x05)?;
    match info.open_device(api) {
        Ok(d) => Some(d),
        Err(e) => {
            eprintln!(
                "qs-bridge: found the QuadStick but cannot open it: {e}. On Linux, install 70-quadstick.rules."
            );
            None
        }
    }
}

fn fail(msg: &str) -> ! {
    eprintln!("qs-bridge: {msg}");
    std::process::exit(1);
}
