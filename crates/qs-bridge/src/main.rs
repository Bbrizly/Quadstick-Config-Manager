use std::io::{self, Write};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::{thread, time::Duration};

use hidapi::{HidApi, HidDevice};
use qs_bridge::{PRODUCT_ID_X360CE, Pad, Status, VENDOR_ID, decode};

const USAGE: &str = "usage: qs-bridge [--json | --print]

Copies a QuadStick in emulation mode 2 (x360ce) onto a virtual Xbox 360 pad.
--json   also print one status line per change on stdout, and stop when
         stdin closes (how the Config Manager runs it)
--print  show each report and what it decodes to, and make no pad";

/// How long a read or a wait may run before the stop flag is checked again.
/// Reads return as soon as a report arrives, so this is not input latency.
const TICK: Duration = Duration::from_millis(250);

fn main() {
    let (json, print) = match std::env::args().nth(1).as_deref() {
        None => (false, false),
        Some("--json") => (true, false),
        Some("--print") => (false, true),
        Some(_) => {
            eprintln!("{USAGE}");
            std::process::exit(2);
        }
    };
    let stop = Arc::new(AtomicBool::new(false));
    if json {
        // The host holds stdin open for as long as it wants the pad. EOF means
        // it closed it or died, and either way the pad has to go with it.
        let stop = Arc::clone(&stop);
        thread::spawn(move || {
            let _ = io::copy(&mut io::stdin().lock(), &mut io::sink());
            stop.store(true, Ordering::SeqCst);
        });
    }
    let mut status = Reporter { json, last: None };
    if !print && !cfg!(target_os = "linux") {
        status.set(Status::Unsupported);
        std::process::exit(2);
    }
    let mut api = HidApi::new().unwrap_or_else(|e| {
        eprintln!("qs-bridge: cannot start HID: {e}");
        std::process::exit(1);
    });
    while !stop.load(Ordering::SeqCst) {
        match open_stick(&mut api) {
            Ok(Some(stick)) if print => {
                eprintln!("qs-bridge: QuadStick found");
                pump(&stick, &stop, |raw, pad| println!("{raw:02x?}\n  {pad:?}"));
            }
            Ok(Some(stick)) => {
                let after = bridge(&stick, &stop, &mut status);
                status.set(after);
            }
            Ok(None) => status.set(Status::Waiting),
            Err(()) => status.set(Status::NoAccess),
        }
        // A failed open retries each second, so installing the udev rule and
        // replugging fixes it without restarting anything.
        for _ in 0..4 {
            if stop.load(Ordering::SeqCst) {
                break;
            }
            thread::sleep(TICK);
        }
    }
}

/// Prints a status once per change: to stderr for a person, and to stdout as
/// a line for the host when `--json` is on.
struct Reporter {
    json: bool,
    last: Option<Status>,
}

impl Reporter {
    fn set(&mut self, s: Status) {
        if self.last == Some(s) {
            return;
        }
        self.last = Some(s);
        eprintln!("qs-bridge: {}", say(s));
        if self.json {
            // A host that has gone away closes the pipe; stdin EOF stops us.
            let mut out = io::stdout().lock();
            let _ = writeln!(out, "{}", s.json()).and_then(|()| out.flush());
        }
    }
}

fn say(s: Status) -> &'static str {
    match s {
        Status::Waiting => "waiting for a QuadStick in emulation mode 2 (16d0:092c)",
        Status::Bridging => "QuadStick found, games now see an Xbox 360 pad",
        Status::NoAccess => {
            "found the QuadStick but cannot open it. Install 70-quadstick.rules (see README) and replug it."
        }
        Status::NoVirtualPad => {
            "cannot make the virtual pad. Install 70-quadstick.rules (see README) and replug the stick."
        }
        Status::Unsupported => "only Linux can make the virtual pad so far. --print works here.",
    }
}

/// Runs the pad until the stick goes or the host stops us, and returns the
/// status to show after. Every button is released before it returns.
#[cfg(target_os = "linux")]
fn bridge(stick: &HidDevice, stop: &AtomicBool, status: &mut Reporter) -> Status {
    let mut pad = match qs_bridge::uinput::VirtualPad::create() {
        Ok(pad) => pad,
        Err(e) => {
            eprintln!("qs-bridge: {e}");
            return Status::NoVirtualPad;
        }
    };
    status.set(Status::Bridging);
    pump(stick, stop, |_, p| {
        if let Err(e) = pad.send(p) {
            eprintln!("qs-bridge: write to the virtual pad failed: {e}");
        }
    });
    // Dropping `pad` sends a neutral report and removes the device.
    Status::Waiting
}

#[cfg(not(target_os = "linux"))]
fn bridge(_: &HidDevice, _: &AtomicBool, _: &mut Reporter) -> Status {
    Status::Unsupported
}

/// Reads until the stick goes or `stop` is set. Every state change reaches
/// `send`, and a report that cannot be decoded is sent as neutral so nothing
/// stays held on a bad read.
fn pump(stick: &HidDevice, stop: &AtomicBool, mut send: impl FnMut(&[u8], &Pad)) {
    let mut buf = [0u8; 64];
    let mut last = None;
    let tick = i32::try_from(TICK.as_millis()).unwrap_or(250);
    while !stop.load(Ordering::SeqCst) {
        let n = match stick.read_timeout(&mut buf, tick) {
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
/// `Err` means the stick is plugged in but this user may not open it.
fn open_stick(api: &mut HidApi) -> Result<Option<HidDevice>, ()> {
    if api.refresh_devices().is_err() {
        return Ok(None);
    }
    let ours =
        |d: &&hidapi::DeviceInfo| d.vendor_id() == VENDOR_ID && d.product_id() == PRODUCT_ID_X360CE;
    let Some(info) = api
        .device_list()
        .filter(ours)
        .find(|d| d.usage_page() == 0x01 && d.usage() == 0x05)
    else {
        return Ok(None);
    };
    info.open_device(api)
        .map(Some)
        .map_err(|e| eprintln!("qs-bridge: {e}"))
}
