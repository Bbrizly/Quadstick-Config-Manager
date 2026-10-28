use std::io::{self, BufRead, Write};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::{thread, time::Duration};

use hidapi::{HidApi, HidDevice};
use qs_bridge::{PRODUCT_ID_X360CE, Pad, Status, VENDOR_ID, decode};

const USAGE: &str = "usage: qs-bridge [--json | --print]

Copies a QuadStick in emulation mode 2 (x360ce) onto a virtual Xbox 360 pad.
--json   also print one status line per change on stdout, and stop when
         stdin closes (how the Config Manager runs it)
--print  show each report and what it decodes to, and make no pad
--replay read hex reports from stdin to emulate a controller stream; make no pad";

/// How long a read or a wait may run before the stop flag is checked again.
/// Reads return as soon as a report arrives, so this is not input latency.
const TICK: Duration = Duration::from_millis(250);

fn main() {
    let (json, print, replay) = match std::env::args().nth(1).as_deref() {
        None => (false, false, false),
        Some("--json") => (true, false, false),
        Some("--print") => (false, true, false),
        Some("--replay") => (false, false, true),
        Some(_) => {
            eprintln!("{USAGE}");
            std::process::exit(2);
        }
    };
    if replay {
        if let Err(error) = replay_reports(io::stdin().lock(), io::stdout().lock()) {
            eprintln!("qs-bridge: replay failed: {error}");
            std::process::exit(2);
        }
        return;
    }
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
                pump(&stick, &stop, |raw, pad| {
                    println!("{raw:02x?}\n  {pad:?}");
                    true
                });
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

/// Emulates a mode-2 HID stream from hex lines. EOF is a disconnect: release
/// any non-neutral controls just as dropping the real virtual pad does.
fn replay_reports(reader: impl BufRead, mut writer: impl Write) -> io::Result<()> {
    let mut last = None;
    for (index, line) in reader.lines().enumerate() {
        let line = line?;
        if line.trim().is_empty() {
            continue;
        }
        let raw = parse_report_hex(&line).map_err(|message| {
            io::Error::new(
                io::ErrorKind::InvalidData,
                format!("line {}: {message}", index + 1),
            )
        })?;
        let pad = decode(&raw).unwrap_or_default();
        if last != Some(pad) {
            writeln!(writer, "{pad:?}")?;
            last = Some(pad);
        }
    }
    if last.is_some_and(|pad: Pad| pad != Pad::default()) {
        writeln!(writer, "{:?} (released at EOF)", Pad::default())?;
    }
    Ok(())
}

fn parse_report_hex(line: &str) -> Result<Vec<u8>, &'static str> {
    let mut bytes = Vec::new();
    let mut high_nibble = None;
    for c in line.chars() {
        if c.is_ascii_whitespace() || c == ',' || c == ':' {
            continue;
        }
        if !c.is_ascii_hexdigit() {
            return Err("invalid hexadecimal byte");
        }
        let nibble = c.to_digit(16).ok_or("invalid hexadecimal byte")? as u8;
        if let Some(high) = high_nibble.take() {
            bytes.push((high << 4) | nibble);
        } else {
            high_nibble = Some(nibble);
        }
    }
    if high_nibble.is_some() {
        return Err("expected pairs of hexadecimal digits");
    }
    Ok(bytes)
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
        Status::PadWriteFailed => {
            "writing to the virtual pad failed; it was released and will retry"
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
    let mut write_failed = false;
    pump(stick, stop, |_, p| match pad.send(p) {
        Ok(()) => true,
        Err(e) => {
            eprintln!("qs-bridge: write to the virtual pad failed: {e}");
            write_failed = true;
            false
        }
    });
    // Dropping `pad` sends a neutral report and removes the device.
    if write_failed {
        Status::PadWriteFailed
    } else {
        Status::Waiting
    }
}

#[cfg(not(target_os = "linux"))]
fn bridge(_: &HidDevice, _: &AtomicBool, _: &mut Reporter) -> Status {
    Status::Unsupported
}

/// Reads until the stick goes or `stop` is set. Every state change reaches
/// `send`, and a report that cannot be decoded is sent as neutral so nothing
/// stays held on a bad read. A failed send ends the pump so its owner can drop
/// and recreate the virtual device instead of silently losing input updates.
fn pump(stick: &HidDevice, stop: &AtomicBool, mut send: impl FnMut(&[u8], &Pad) -> bool) {
    let mut buf = [0u8; 64];
    let mut last = None;
    let tick = i32::try_from(TICK.as_millis()).unwrap_or(250);
    while !stop.load(Ordering::SeqCst) {
        let n = match stick.read_timeout(&mut buf, tick) {
            Ok(0) => continue,
            Ok(n) => n,
            Err(_) => return,
        };
        if !send_changed(&mut last, &buf[..n], &mut send) {
            return;
        }
    }
}

fn send_changed(
    last: &mut Option<Pad>,
    raw: &[u8],
    send: &mut impl FnMut(&[u8], &Pad) -> bool,
) -> bool {
    let pad = decode(raw).unwrap_or_default();
    if *last == Some(pad) {
        return true;
    }
    if !send(raw, &pad) {
        return false;
    }
    *last = Some(pad);
    true
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

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;

    fn report(buttons: u8) -> String {
        let mut bytes = [0u8; 14];
        for i in [0, 2, 4, 6] {
            bytes[i..i + 2].copy_from_slice(&0x8000u16.to_le_bytes());
        }
        bytes[12] = buttons;
        bytes[13] = 15 << 3;
        bytes
            .iter()
            .map(|byte| format!("{byte:02x}"))
            .collect::<Vec<_>>()
            .join(" ")
    }

    #[test]
    fn replay_emulates_report_changes_and_releases_controls_on_disconnect() {
        let input = format!("{}\n{}\n{}\n", report(0), report(1 << 1), report(1 << 1));
        let mut output = Vec::new();
        replay_reports(Cursor::new(input), &mut output).unwrap();
        let output = String::from_utf8(output).unwrap();
        assert_eq!(
            output.lines().count(),
            3,
            "neutral, pressed, release at EOF"
        );
        assert!(output.lines().nth(1).unwrap().contains("a: true"));
        assert!(output.lines().last().unwrap().contains("released at EOF"));
    }

    #[test]
    fn replay_malformed_report_releases_controls_and_invalid_hex_fails() {
        let mut output = Vec::new();
        replay_reports(
            Cursor::new(format!("{}\n00\n", report(1 << 1))),
            &mut output,
        )
        .unwrap();
        assert!(String::from_utf8(output).unwrap().contains("a: false"));
        assert!(parse_report_hex("0xz1").is_err());
        assert!(parse_report_hex("abc").is_err());
        assert!(parse_report_hex("é").is_err());
    }

    #[test]
    fn failed_send_stops_and_does_not_cache_the_unsent_state() {
        let mut last = None;
        let mut raw = [0u8; 14];
        raw[12] = 1 << 1; // A pressed.
        let mut calls = 0;

        let keep_running = send_changed(&mut last, &raw, &mut |_, _| {
            calls += 1;
            false
        });

        assert!(!keep_running);
        assert_eq!(calls, 1);
        assert_eq!(last, None);
    }

    #[test]
    fn unchanged_state_does_not_write_again() {
        let mut raw = [0u8; 14];
        raw[12] = 1 << 1; // A pressed.
        let pad = decode(&raw).unwrap();
        let mut last = Some(pad);
        let mut calls = 0;

        let keep_running = send_changed(&mut last, &raw, &mut |_, _| {
            calls += 1;
            true
        });

        assert!(keep_running);
        assert_eq!(calls, 0);
        assert_eq!(last, Some(pad));
    }
}
