//! The serial/Bluetooth console against an in-memory QuadStick.
//!
//! Ported from `tests/QuadStick.Format.Tests/DeviceConsoleTests.cs` with its
//! `FakeQuadStick` (here `qcm_testkit::FakeConsole`). One substitution: the C#
//! strength-profile test reads the written file with `FirmwareOracle`; this
//! one reads it with the `qcm-config` parser, which the parity suite already
//! holds to that oracle.

use qcm_config::{ProfileFile, SheetType, load_preferences};
use qcm_core::console::{
    CHUNK_BYTES, ConsoleError, DeviceConsole, FileStamp, STRENGTH_TEST_NAME, STRENGTH_TEST_PROFILE,
    SWEEP_HIGHEST, SWEEP_LOWEST, chunks, maximum_preference, strength_sweep, threshold_preference,
};
use qcm_core::error::{ConfirmationError, QcmError};
use qcm_testkit::{FakeBackupStore, FakeConsole};
use std::convert::Infallible;
use std::time::Duration;

const STAMP: FileStamp = FileStamp {
    year: 2026,
    month: 10,
    day: 15,
    hour: 9,
    minute: 8,
    second: 7,
};

fn console(fake: FakeConsole) -> DeviceConsole<FakeConsole> {
    DeviceConsole::new(fake, || STAMP)
}

fn profile_text(name: &str, comment: &str) -> String {
    format!(
        "Profile Name,,Game,,,,,,,,Comments\n{name},,,,,,,,,,\n\
         PlayStation Outputs,Function,usb,,,,,,,,\n\
         x,normal,mp_center_puff,,,,,,,,{comment}\n\
         circle,normal,mp_center_sip,,,,,,,,\n"
    )
}

fn profile(name: &str) -> ProfileFile {
    ProfileFile::load(&profile_text(name, ""))
}

fn device_text(file: &ProfileFile) -> String {
    let mut expected = ProfileFile::load(&file.to_csv_text());
    expected.normalize_for_device_csv();
    expected.to_csv_text().replace("\r\n", "\n")
}

#[test]
fn a_profile_lands_byte_for_byte_and_is_loaded_last() {
    let mut fake = FakeConsole::new();
    fake.files
        .insert("game.csv".to_owned(), "the old one\n".to_owned());
    let backups = FakeBackupStore::new();
    let mut console = console(fake);

    let result = console
        .send_profile(&profile("game.csv"), &backups, false, false)
        .expect("sent");

    let fake = console.port_mut();
    assert_eq!(fake.files["game.csv"], device_text(&profile("game.csv")));
    // Kept as it came off the device, carriage returns and all.
    let entries = backups.entries();
    assert_eq!(entries.len(), 1);
    assert_eq!(
        String::from_utf8_lossy(&entries[0].1).replace('\r', ""),
        "the old one\n"
    );
    assert!(result.backup.is_some());
    assert_eq!(
        fake.commands.last().map(String::as_str),
        Some("load,game.csv")
    );
    // The close carries a two digit year.
    assert!(
        fake.commands
            .iter()
            .any(|c| c == "write_file,2,26,10,15,9,8,7")
    );
}

#[test]
fn a_new_file_has_no_backup() {
    let backups = FakeBackupStore::new();
    let mut console = console(FakeConsole::new());
    let result = console
        .send_profile(&profile("game.csv"), &backups, false, false)
        .expect("sent");
    assert_eq!(result.backup, None);
    assert!(backups.is_empty());
}

#[test]
fn a_file_that_reads_back_wrong_is_never_loaded() {
    let mut fake = FakeConsole::new();
    fake.corrupt_writes = true;
    let mut console = console(fake);
    let error = console
        .send_profile(&profile("game.csv"), &FakeBackupStore::new(), false, false)
        .expect_err("refused");
    assert!(matches!(&error, ConsoleError::ReadBackDiffers { name, .. } if name == "game.csv"));
    assert!(
        !console
            .port_mut()
            .commands
            .iter()
            .any(|c| c.starts_with("load,"))
    );
}

#[test]
fn default_csv_needs_the_same_yes_over_the_console_as_on_a_drive() {
    let mut console = console(FakeConsole::new());
    let error = console
        .send_profile(
            &profile("default.csv"),
            &FakeBackupStore::new(),
            false,
            false,
        )
        .expect_err("gated");
    assert!(matches!(
        error,
        ConsoleError::Rejected(QcmError::Confirmation(ConfirmationError::Missing { .. }))
    ));
    assert!(console.port_mut().commands.is_empty());

    console
        .send_profile(
            &profile("default.csv"),
            &FakeBackupStore::new(),
            true,
            false,
        )
        .expect("sent after yes");
}

#[test]
fn a_long_profile_never_overruns_the_530_byte_line_and_keeps_its_accents() {
    // Enough rows to need many chunks, and a comment with multi-byte
    // characters so a chunk edge lands inside one somewhere.
    let mut csv = String::from(
        "Profile Name,,Game,,,,,,,,Comments\nlong.csv,,,,,,,,,,\nPlayStation Outputs,Function,usb,,,,,,,,\n",
    );
    for i in 0..60 {
        csv.push_str(&format!("x,normal,mp_center_puff,,,,,,,,é è ü 日本 {i}\n"));
    }
    let mut console = console(FakeConsole::new());
    console
        .send_profile(
            &ProfileFile::load(&csv),
            &FakeBackupStore::new(),
            false,
            false,
        )
        .expect("sent");
    let fake = console.port_mut();
    assert!(
        (1..=529).contains(&fake.longest_line),
        "{}",
        fake.longest_line
    );
    assert!(fake.files["long.csv"].contains("日本 59"));
    assert_eq!(
        fake.commands.last().map(String::as_str),
        Some("load,long.csv")
    );
}

#[test]
fn chunks_join_back_to_the_text_and_each_fits() {
    let text = "aé日😀\n".repeat(400);
    let parts = chunks(&text);
    assert_eq!(parts.concat(), text);
    assert!(parts.iter().all(|c| (1..=CHUNK_BYTES).contains(&c.len())));
}

#[test]
fn a_setting_the_firmware_does_not_know_is_reported_not_assumed() {
    let mut console = console(FakeConsole::new()).with_timeout(Duration::from_millis(200));
    console.set_preference("puff_threshold", 45).expect("taken");
    assert_eq!(console.port_mut().pref("puff_threshold"), Some(45));
    assert_eq!(console.preferences().expect("prefs")["puff_threshold"], 45);
    // The firmware answers an unknown name with nothing at all (:570).
    let error = console
        .set_preference("puff_threshhold", 45)
        .expect_err("refused");
    assert_eq!(
        error,
        ConsoleError::SettingRefused {
            name: "puff_threshhold".to_owned()
        }
    );
}

#[test]
fn nothing_on_the_port_times_out_with_a_reason() {
    let mut fake = FakeConsole::new();
    fake.silent = true;
    let mut console = console(fake).with_timeout(Duration::from_millis(150));
    assert_eq!(console.build(), Err(ConsoleError::NoAnswer));
}

#[test]
fn half_a_line_left_on_the_port_does_not_eat_the_next_command() {
    // Somebody's terminal, or a send cut off by a dropped Bluetooth link,
    // leaves bytes with no carriage return. The backspace in front of each
    // command is what throws them away (Console.c:606).
    use std::io::Write;
    let mut fake = FakeConsole::new();
    fake.write_all(b"read_fi").expect("junk");
    let mut console = console(fake).with_timeout(Duration::from_millis(300));
    assert_eq!(console.build(), Ok(Some(2373)));
}

#[test]
fn build_identifies_a_quadstick() {
    assert_eq!(console(FakeConsole::new()).build(), Ok(Some(2373)));
}

#[test]
fn a_boot_is_sent_and_never_waited_for() {
    let mut console = console(FakeConsole::new()).with_timeout(Duration::from_millis(50));
    console.boot().expect("sent");
    assert_eq!(console.port_mut().commands, ["boot"]);
}

#[test]
fn the_strength_test_profile_is_what_the_firmware_reads_it_as() {
    // Sent the way every profile is sent, so read what that path writes.
    let mut console = console(FakeConsole::new());
    console
        .send_profile(
            &ProfileFile::load(STRENGTH_TEST_PROFILE),
            &FakeBackupStore::new(),
            false,
            false,
        )
        .expect("sent");
    let written = ProfileFile::load(&console.port_mut().files[STRENGTH_TEST_NAME]);
    let modes: Vec<_> = written
        .document
        .sheets
        .iter()
        .filter(|s| s.sheet_type == SheetType::ProfileName)
        .collect();
    assert_eq!(modes.len(), 1);
    let mode = modes[0];
    assert!(mode.bindings.iter().all(|b| b.output == "x"));
    let inputs: Vec<_> = mode
        .bindings
        .iter()
        .map(|b| {
            assert_eq!(b.inputs.len(), 1);
            b.inputs[0].as_str()
        })
        .collect();
    assert_eq!(inputs, ["mp_center_puff", "mp_center_sip"]);
    // Nothing soft: a bound soft input is what lets the latch block a hard one.
    assert!(
        !mode
            .bindings
            .iter()
            .any(|b| b.inputs.iter().any(|i| i.ends_with("_soft")))
    );
    let file = ProfileFile::load(STRENGTH_TEST_PROFILE);
    assert!(
        !file
            .issues
            .iter()
            .any(|i| i.severity == qcm_config::Severity::Error),
        "{:?}",
        file.issues
    );
    assert_eq!(file.document.csv_file_name(), Some(STRENGTH_TEST_NAME));
}

#[test]
fn the_threshold_preferences_are_real_firmware_words() {
    let names: Vec<String> = load_preferences()
        .expect("catalog")
        .into_iter()
        .map(|p| p.name)
        .collect();
    for puff in [true, false] {
        assert!(names.iter().any(|n| n == threshold_preference(puff)));
        assert!(names.iter().any(|n| n == maximum_preference(puff)));
    }
}

#[test]
fn the_sweep_finds_every_strength_exactly_in_seven_breaths() {
    for strength in (SWEEP_LOWEST - 1)..=SWEEP_HIGHEST {
        let mut tries = 0;
        let found = strength_sweep(SWEEP_LOWEST, SWEEP_HIGHEST, |t| {
            tries += 1;
            Ok::<_, Infallible>(t <= strength)
        })
        .expect("infallible");
        assert_eq!(found, strength);
        assert!((1..=7).contains(&tries), "{tries}");
    }
}

#[test]
fn a_stopped_sweep_stops_at_once() {
    let mut tries = 0;
    let result: Result<i32, &str> = strength_sweep(SWEEP_LOWEST, SWEEP_HIGHEST, |_| {
        tries += 1;
        Err("stopped")
    });
    assert_eq!(result, Err("stopped"));
    assert_eq!(tries, 1);
}
