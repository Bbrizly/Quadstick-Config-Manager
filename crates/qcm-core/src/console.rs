//! The QuadStick's own text console, over Bluetooth (the RN-42 in SPP mode) or
//! the 3.3V serial cable on the digital inputs 1&2 jack.
//!
//! Ported from `src/QuadStick.Format/DeviceConsole.cs`. Both links arrive at
//! the same firmware parser, so this works over any `Read + Write` and never
//! knows which one it has. Every rule is read from `Joystick/Console.c` on
//! firmware 2373:
//!
//! - A line ends at `\r` and only `\r` (`poll_for_console_input`, :607). A `\n`
//!   inside a line is kept, which is how `write_file,1` carries line breaks.
//! - A backspace throws away everything before it on the line (:606), so each
//!   command is sent after one, and half a line left over from anything else
//!   cannot glue itself onto it.
//! - The line buffer is 530 bytes (:151). A longer line is dropped without a
//!   word (:618), so nothing sent here comes near it.
//! - Every answer ends `"\n>"` (:540, :424, :562, :579). That prompt is the
//!   only end-of-answer signal the device gives.

use crate::confirmation::ConfirmationKind;
use crate::error::{
    BackupLocationDisplay, ConfigError, ConfirmationError, NameRejection, OsDetail, QcmError,
    StorageError,
};
use crate::ports::storage::{BackupStore, SafeDeviceFileName};
use qcm_config::{ProfileFile, Severity, is_too_long_for_device};
use std::collections::BTreeMap;
use std::io::{ErrorKind, Read, Write};
use std::time::{Duration, Instant};

const PROMPT: &str = "\n>";
/// `Console.c:399`.
const END_OF_FILE: &str = "**END OF FILE**";

/// Room for the longest prefix, `write_file,1,`, inside the 530 byte buffer
/// with plenty to spare for a multi-byte character at the edge.
pub const CHUNK_BYTES: usize = 480;

/// How long one answer may take by default. A file read over Bluetooth is the
/// slow case: a few kilobytes at 115200 is well under a second.
pub const DEFAULT_TIMEOUT: Duration = Duration::from_secs(5);

/// The local wall-clock time stamped on a written file. The core has no time
/// zone database, so the shell supplies it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct FileStamp {
    pub year: i32,
    pub month: u32,
    pub day: u32,
    pub hour: u32,
    pub minute: u32,
    pub second: u32,
}

/// Every way the console can fail. The window turns each into the matching
/// `DeviceConsole_*` sentence; none of them carries a host path.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ConsoleError {
    /// Nothing ended with the prompt before the timeout.
    NoAnswer,
    /// A command or file text held a carriage return or a backspace.
    InvalidText,
    SettingRefused {
        name: String,
    },
    CouldNotOpen {
        name: String,
    },
    WriteRefused {
        name: String,
    },
    CouldNotLoad {
        name: String,
    },
    /// The file did not read back as sent, so it was not loaded.
    ReadBackDiffers {
        name: String,
        backup: Option<BackupLocationDisplay>,
    },
    /// The same gates a drive install has: name, blocking problems and the two
    /// protected files.
    Rejected(QcmError),
    /// The port itself failed (not a timeout).
    Port {
        detail: OsDetail,
    },
}

impl ConsoleError {
    /// Stable code for the window.
    #[must_use]
    pub const fn code(&self) -> &'static str {
        match self {
            Self::NoAnswer => "noAnswer",
            Self::InvalidText => "invalidText",
            Self::SettingRefused { .. } => "settingRefused",
            Self::CouldNotOpen { .. } => "couldNotOpen",
            Self::WriteRefused { .. } => "writeRefused",
            Self::CouldNotLoad { .. } => "couldNotLoad",
            Self::ReadBackDiffers { .. } => "readBackDiffers",
            Self::Rejected(_) => "rejected",
            Self::Port { .. } => "port",
        }
    }

    /// The file or setting name the sentence is about, where there is one.
    #[must_use]
    pub fn subject(&self) -> Option<&str> {
        match self {
            Self::SettingRefused { name }
            | Self::CouldNotOpen { name }
            | Self::WriteRefused { name }
            | Self::CouldNotLoad { name }
            | Self::ReadBackDiffers { name, .. } => Some(name),
            _ => None,
        }
    }

    /// Fallback English for logs. Built from typed fields only.
    #[must_use]
    pub fn message(&self) -> String {
        match self {
            Self::NoAnswer => "The QuadStick did not answer.".to_owned(),
            Self::InvalidText => {
                "A console line cannot hold a carriage return or a backspace.".to_owned()
            }
            Self::SettingRefused { name } => {
                format!("The QuadStick did not take the setting {name}.")
            }
            Self::CouldNotOpen { name } => {
                format!("The QuadStick could not open {name} for writing.")
            }
            Self::WriteRefused { name } => {
                format!("The QuadStick stopped taking {name} partway through.")
            }
            Self::CouldNotLoad { name } => {
                format!("{name} is on the QuadStick, but it could not load it.")
            }
            Self::ReadBackDiffers { name, .. } => {
                format!("{name} reads back different from what was sent, so it was not loaded.")
            }
            Self::Rejected(error) => error.message(),
            Self::Port { .. } => "The port stopped working.".to_owned(),
        }
    }
}

impl From<QcmError> for ConsoleError {
    fn from(error: QcmError) -> Self {
        Self::Rejected(error)
    }
}

/// What a successful send left behind.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SendResult {
    pub name: String,
    pub backup: Option<BackupLocationDisplay>,
}

/// The console over one open link.
pub struct DeviceConsole<P> {
    port: P,
    timeout: Duration,
    now: Box<dyn Fn() -> FileStamp + Send>,
}

impl<P: Read + Write> DeviceConsole<P> {
    pub fn new(port: P, now: impl Fn() -> FileStamp + Send + 'static) -> Self {
        Self {
            port,
            timeout: DEFAULT_TIMEOUT,
            now: Box::new(now),
        }
    }

    #[must_use]
    pub fn with_timeout(mut self, timeout: Duration) -> Self {
        self.timeout = timeout;
        self
    }

    pub fn port_mut(&mut self) -> &mut P {
        &mut self.port
    }

    /// Send one command and return everything the device said back, prompt
    /// included.
    pub fn send(&mut self, command: &str) -> Result<String, ConsoleError> {
        if command.contains(['\r', '\u{8}']) {
            return Err(ConsoleError::InvalidText);
        }
        self.write_line(command)?;
        self.read_until(|text| text.ends_with(PROMPT))
    }

    fn write_line(&mut self, command: &str) -> Result<(), ConsoleError> {
        let line = format!("\u{8}{command}\r");
        self.port
            .write_all(line.as_bytes())
            .and_then(|()| self.port.flush())
            .map_err(port_error)
    }

    fn read_until(&mut self, done: impl Fn(&str) -> bool) -> Result<String, ConsoleError> {
        let mut got = Vec::new();
        let mut buffer = [0_u8; 1024];
        let deadline = Instant::now() + self.timeout;
        while Instant::now() < deadline {
            // A serial port with a read timeout reports it as an error rather
            // than returning nothing. Either way it means no bytes yet.
            let n = match self.port.read(&mut buffer) {
                Ok(n) => n,
                Err(error)
                    if matches!(
                        error.kind(),
                        ErrorKind::TimedOut | ErrorKind::WouldBlock | ErrorKind::Interrupted
                    ) =>
                {
                    continue;
                }
                Err(error) => return Err(port_error(error)),
            };
            if n == 0 {
                std::thread::sleep(Duration::from_millis(10));
                continue;
            }
            got.extend_from_slice(&buffer[..n]);
            let text = String::from_utf8_lossy(&got);
            if done(&text) {
                return Ok(text.into_owned());
            }
        }
        Err(ConsoleError::NoAnswer)
    }

    /// The firmware build number, or `None` if what answered is not a
    /// QuadStick. `build` prints `FW_VERSION` alone (`Console.c:342`).
    pub fn build(&mut self) -> Result<Option<i32>, ConsoleError> {
        let reply = self.send("build")?;
        Ok(lines(&reply).find_map(parse_digits))
    }

    /// Every preference and its live value (`pref`, `Console.c:335`).
    pub fn preferences(&mut self) -> Result<BTreeMap<String, i32>, ConsoleError> {
        let reply = self.send("pref")?;
        let mut prefs = BTreeMap::new();
        for line in lines(&reply) {
            let Some(colon) = line.find(':') else {
                continue;
            };
            if colon == 0 {
                continue;
            }
            if let Some(value) = parse_signed(line[colon + 1..].trim()) {
                prefs.insert(line[..colon].trim().to_owned(), value);
            }
        }
        Ok(prefs)
    }

    /// Change one preference in the device's memory only. `prefs.csv` is not
    /// touched, so unplugging or [`Self::boot`] puts the file's value back.
    pub fn set_preference(&mut self, name: &str, value: i32) -> Result<(), ConsoleError> {
        // A name the firmware does not know gets no answer at all (:570), so
        // silence means refused, and the echo is the only proof it took.
        let reply = match self.send(&format!("{name},{value}")) {
            Ok(reply) => reply,
            Err(ConsoleError::NoAnswer) => String::new(),
            Err(error) => return Err(error),
        };
        if reply.contains(&format!("{name}: {value}")) {
            Ok(())
        } else {
            Err(ConsoleError::SettingRefused {
                name: name.to_owned(),
            })
        }
    }

    /// The file's text, or `None` if the device has no file by that name.
    pub fn read_file(&mut self, name: &str) -> Result<Option<String>, ConsoleError> {
        self.write_line(&format!("read_file,{name}"))?;
        let reply = self.read_until(|text| {
            text.ends_with(PROMPT) && (text.contains(END_OF_FILE) || text.contains("ErrorCode"))
        })?;
        Ok(reply.rfind(END_OF_FILE).map(|end| reply[..end].to_owned()))
    }

    /// Create or replace a file: open, lines in chunks, close.
    pub fn write_file(&mut self, name: &str, text: &str) -> Result<(), ConsoleError> {
        if text.contains(['\r', '\u{8}']) {
            return Err(ConsoleError::InvalidText);
        }
        let open = self.send(&format!("write_file,0,{name}"))?;
        if open.contains("ErrorCode") {
            return Err(ConsoleError::CouldNotOpen {
                name: name.to_owned(),
            });
        }
        for chunk in chunks(text) {
            // write_file,1 answers with f_puts' count (:424), -1 on a failure.
            let reply = self.send(&format!("write_file,1,{chunk}"))?;
            if !lines(&reply).any(|line| parse_digits(line).is_some()) {
                return Err(ConsoleError::WriteRefused {
                    name: name.to_owned(),
                });
            }
        }
        // Two digit year: diskio.c:77 stores it as years since 2000.
        let now = (self.now)();
        self.send(&format!(
            "write_file,2,{},{},{},{},{},{}",
            now.year.rem_euclid(100),
            now.month,
            now.day,
            now.hour,
            now.minute,
            now.second
        ))?;
        Ok(())
    }

    /// Make the device read a file now (`Console.c:256`).
    pub fn load(&mut self, name: &str) -> Result<(), ConsoleError> {
        let reply = self.send(&format!("load,{name}"))?;
        if reply.contains(" OK") {
            Ok(())
        } else {
            Err(ConsoleError::CouldNotLoad {
                name: name.to_owned(),
            })
        }
    }

    pub fn delete(&mut self, name: &str) -> Result<(), ConsoleError> {
        self.send(&format!("delete_file,{name}")).map(drop)
    }

    /// Zero the pressure sensors. Nobody may be touching the mouthpiece.
    pub fn calibrate(&mut self) -> Result<(), ConsoleError> {
        self.send("calibrate").map(drop)
    }

    /// Restart the QuadStick. Everything set in memory goes; the files are read
    /// again. It does not answer: it is gone mid-reply.
    pub fn boot(&mut self) -> Result<(), ConsoleError> {
        self.write_line("boot")
    }

    /// Install a profile over the console. Same gates as a drive install,
    /// then: back up what the device has, write, read it back, and only load
    /// it if it matches.
    pub fn send_profile<B: BackupStore + ?Sized>(
        &mut self,
        file: &ProfileFile,
        backups: &B,
        confirm_default_csv: bool,
        confirm_preferences_csv: bool,
    ) -> Result<SendResult, ConsoleError> {
        let name = install_name(file, confirm_default_csv, confirm_preferences_csv)?;

        let mut outgoing = ProfileFile::load(&file.to_csv_text());
        outgoing.normalize_for_device_csv();
        let text = outgoing.to_csv_text().replace("\r\n", "\n");

        let mut backup = None;
        if let Some(old) = self.read_file(name.as_str())? {
            let receipt = backups
                .store(name.as_device_name(), old.as_bytes())
                .map_err(|error| ConsoleError::Rejected(error.into()))?;
            backup = Some(receipt.location);
        }

        self.write_file(name.as_str(), &text)?;

        // f_gets and printf may each turn \n into \r\n on the way back, so the
        // comparison ignores carriage returns and nothing else.
        let back = self.read_file(name.as_str())?;
        if back.is_none_or(|back| back.replace('\r', "") != text) {
            return Err(ConsoleError::ReadBackDiffers {
                name: name.as_str().to_owned(),
                backup,
            });
        }

        self.load(name.as_str())?;
        Ok(SendResult {
            name: name.as_str().to_owned(),
            backup,
        })
    }
}

/// The C# `Device.InstallName` gates, in its order.
pub fn install_name(
    file: &ProfileFile,
    confirm_default_csv: bool,
    confirm_preferences_csv: bool,
) -> Result<SafeDeviceFileName, QcmError> {
    let declared = file.document.csv_file_name();
    if declared.is_some_and(is_too_long_for_device) {
        return Err(StorageError::NameRejected {
            reason: NameRejection::TooLongForDevice,
        }
        .into());
    }
    let errors = file
        .issues
        .iter()
        .filter(|issue| issue.severity == Severity::Error)
        .count();
    if errors > 0 {
        return Err(ConfigError::HasBlockingProblems { errors }.into());
    }
    let Some(declared) = declared else {
        return Err(StorageError::NameRejected {
            reason: NameRejection::Empty,
        }
        .into());
    };
    if file.document.is_default_config() && !confirm_default_csv {
        return Err(ConfirmationError::Missing {
            kind: ConfirmationKind::OverwriteDefaultCsv,
        }
        .into());
    }
    if file.document.is_device_preferences() && !confirm_preferences_csv {
        return Err(ConfirmationError::Missing {
            kind: ConfirmationKind::OverwriteDevicePreferences,
        }
        .into());
    }
    SafeDeviceFileName::new(declared).map_err(|reason| StorageError::NameRejected { reason }.into())
}

fn port_error(error: std::io::Error) -> ConsoleError {
    ConsoleError::Port {
        detail: OsDetail::new(error.to_string()),
    }
}

/// Split on whole characters, never inside one, so a UTF-8 comment cell
/// reaches the file intact.
#[must_use]
pub fn chunks(text: &str) -> Vec<&str> {
    let mut out = Vec::new();
    let mut start = 0;
    let mut bytes = 0;
    for (index, ch) in text.char_indices() {
        let n = ch.len_utf8();
        if bytes + n > CHUNK_BYTES {
            out.push(&text[start..index]);
            start = index;
            bytes = 0;
        }
        bytes += n;
    }
    if start < text.len() {
        out.push(&text[start..]);
    }
    out
}

fn lines(reply: &str) -> impl Iterator<Item = &str> {
    reply
        .split('\n')
        .map(str::trim)
        .filter(|line| !line.is_empty() && *line != ">")
}

/// `int.TryParse(..., NumberStyles.None)`: ASCII digits only.
fn parse_digits(line: &str) -> Option<i32> {
    if line.is_empty() || !line.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    line.parse().ok()
}

/// `NumberStyles.AllowLeadingSign`.
fn parse_signed(text: &str) -> Option<i32> {
    let digits = text.strip_prefix(['-', '+']).unwrap_or(text);
    parse_digits(digits)?;
    text.parse().ok()
}

// ---- measuring strength ----

/// The file the strength test loads.
pub const STRENGTH_TEST_NAME: &str = "qcm_strength.csv";

/// Hard sip and hard puff on the centre tube both press X, and nothing soft is
/// bound. With no soft input in use the soft latch timer never runs
/// (`DataFlow.c:319`), so a breath that reaches the hard threshold always
/// fires it.
pub const STRENGTH_TEST_PROFILE: &str = "Profile Name,,Strength test,,,,,,,,\n\
qcm_strength.csv,,,,,,,,,,\n\
PlayStation Outputs,Function,usb,,,,,,,,\n\
x,normal,mp_center_puff,,,,,,,,\n\
x,normal,mp_center_sip,,,,,,,,\n";

/// The per-direction hard threshold. 0 in prefs.csv means "use
/// `sip_puff_threshold`" (`DataFlow.c:310-316`), so setting it overrides the
/// shared one for that direction alone.
#[must_use]
pub const fn threshold_preference(puff: bool) -> &'static str {
    if puff {
        "puff_threshold"
    } else {
        "sip_threshold"
    }
}

/// Its ceiling. Held at 100 for the test: the analog scale divides by maximum
/// minus threshold (`DataFlow.c:353`).
#[must_use]
pub const fn maximum_preference(puff: bool) -> &'static str {
    if puff { "puff_maximum" } else { "sip_maximum" }
}

/// Percent of half-scale from the calibrated zero (`DataFlow.c:294-297`). 95,
/// not 100: at 100 the threshold meets the maximum held for the test.
pub const SWEEP_LOWEST: i32 = 5;
pub const SWEEP_HIGHEST: i32 = 95;

/// The highest threshold the person reaches, by binary search: 7 tries cover
/// 5 to 95. `lo - 1` means they reached none of it.
pub fn strength_sweep<E>(
    mut lo: i32,
    mut hi: i32,
    mut reaches: impl FnMut(i32) -> Result<bool, E>,
) -> Result<i32, E> {
    let mut best = lo - 1;
    while lo <= hi {
        let mid = (lo + hi + 1) / 2;
        if reaches(mid)? {
            best = mid;
            lo = mid + 1;
        } else {
            hi = mid - 1;
        }
    }
    Ok(best)
}

/// The threshold the person plays with: `puff_threshold` if set, otherwise the
/// shared `sip_puff_threshold`.
#[must_use]
pub fn current_hard_threshold(prefs: &BTreeMap<String, i32>) -> i32 {
    match prefs.get(threshold_preference(true)) {
        Some(&own) if own > 0 => own,
        _ => prefs.get("sip_puff_threshold").copied().unwrap_or(0),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parsing_matches_dotnet_number_styles() {
        assert_eq!(parse_digits("2373"), Some(2373));
        assert_eq!(parse_digits("-1"), None);
        assert_eq!(parse_digits(""), None);
        assert_eq!(parse_signed("-5"), Some(-5));
        assert_eq!(parse_signed("+5"), Some(5));
        assert_eq!(parse_signed("5x"), None);
    }

    #[test]
    fn threshold_falls_back_to_the_shared_one() {
        let mut prefs = BTreeMap::new();
        prefs.insert("sip_puff_threshold".to_owned(), 60);
        prefs.insert("puff_threshold".to_owned(), 0);
        assert_eq!(current_hard_threshold(&prefs), 60);
        prefs.insert("puff_threshold".to_owned(), 45);
        assert_eq!(current_hard_threshold(&prefs), 45);
    }
}
