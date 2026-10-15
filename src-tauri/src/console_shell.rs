//! The serial/Bluetooth console window's native side.
//!
//! Ported from `src/QuadStick.App/DeviceLinkWindow.cs`. The protocol is
//! `qcm_core::console`; this owns the port, the opaque port ids, the picked
//! profile and the backup folder. The port is opened only by Connect and is
//! dropped by Close, by a failed connect and by the restart that ends a
//! strength test, so nothing holds a serial port while the window is shut.
//!
//! The breath-by-breath orchestration stays in the window, exactly as it does
//! in Avalonia, because it waits on live USB input the window already reads.
//! Every step it asks for is one bounded console exchange here.

use crate::adapters::storage::FileSystemBackupStore;
use crate::ipc::parse;
use qcm_config::ProfileFile;
use qcm_core::console::{
    ConsoleError, DeviceConsole, FileStamp, STRENGTH_TEST_NAME, STRENGTH_TEST_PROFILE,
    SWEEP_HIGHEST, SWEEP_LOWEST, current_hard_threshold, maximum_preference, threshold_preference,
};
use qcm_core::error::{
    ConfigError, InternalError, OsDetail, QcmError, QcmErrorDto, RecoveryActionDto, RequestError,
    scrub,
};
use qcm_core::ports::storage::BackupStore;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::io::{Read, Write};
use std::path::Path;
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::Duration;

/// The QuadStick's console speed (`FORMAT.md`, serial console).
pub const BAUD: u32 = 115_200;
/// The link's own read timeout. `DeviceConsole` loops on it until its answer
/// deadline, so this only bounds one wait.
const READ_TIMEOUT: Duration = Duration::from_millis(200);
/// A profile is a few kilobytes. Anything this size is not one.
pub const MAX_PROFILE_BYTES: u64 = 1024 * 1024;

pub trait Link: Read + Write + Send {}
impl<T: Read + Write + Send> Link for T {}

/// One serial port the OS offers.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PortInfo {
    /// What the OS opens it by (`COM3`, `/dev/cu.QuadStick-SPP`).
    pub name: String,
    pub label: String,
}

/// Where ports come from and how one is opened. The real one is `serialport`;
/// tests hand in a fake console.
pub trait SerialPorts: Send + Sync {
    fn list(&self) -> Vec<PortInfo>;
    fn open(&self, name: &str) -> Result<Box<dyn Link>, String>;
}

pub struct NativeSerialPorts;

impl SerialPorts for NativeSerialPorts {
    fn list(&self) -> Vec<PortInfo> {
        let Ok(found) = serialport::available_ports() else {
            return Vec::new();
        };
        let infos = found
            .into_iter()
            .map(|port| {
                let friendly = match &port.port_type {
                    serialport::SerialPortType::UsbPort(usb) => usb.product.clone(),
                    _ => None,
                };
                let label = match friendly {
                    Some(name) if !name.trim().is_empty() && name != port.port_name => {
                        format!("{name} ({})", port.port_name)
                    }
                    _ => port.port_name.clone(),
                };
                PortInfo {
                    name: port.port_name,
                    label,
                }
            })
            .collect();
        order_ports(infos)
    }

    fn open(&self, name: &str) -> Result<Box<dyn Link>, String> {
        serialport::new(name, BAUD)
            .timeout(READ_TIMEOUT)
            .open()
            .map(|port| Box::new(port) as Box<dyn Link>)
            .map_err(|error| error.to_string())
    }
}

/// A Bluetooth QuadStick names itself, so it goes first when it is there.
/// On macOS each port appears twice, `/dev/tty.*` and `/dev/cu.*`; the `tty`
/// one blocks on open waiting for carrier, so only `cu` is offered.
#[must_use]
pub fn order_ports(mut ports: Vec<PortInfo>) -> Vec<PortInfo> {
    let has_cu: Vec<String> = ports
        .iter()
        .filter_map(|p| p.name.strip_prefix("/dev/cu.").map(str::to_owned))
        .collect();
    ports.retain(|p| {
        p.name
            .strip_prefix("/dev/tty.")
            .is_none_or(|rest| !has_cu.iter().any(|cu| cu == rest))
    });
    // Stable, like OrderByDescending.
    ports.sort_by_key(|p| !p.label.to_lowercase().contains("quad"));
    ports
}

fn local_stamp() -> FileStamp {
    use chrono::{Datelike, Timelike};
    let now = chrono::Local::now();
    FileStamp {
        year: now.year(),
        month: now.month(),
        day: now.day(),
        hour: now.hour(),
        minute: now.minute(),
        second: now.second(),
    }
}

struct Inner {
    listed: Vec<(String, String)>,
    generation: u64,
    console: Option<DeviceConsole<Box<dyn Link>>>,
    pick: Option<(String, ProfileFile)>,
    picks: u64,
}

pub struct ConsoleShell {
    ports: Box<dyn SerialPorts>,
    backups: Box<dyn BackupStore + Send + Sync>,
    now: fn() -> FileStamp,
    timeout: Duration,
    inner: Mutex<Inner>,
}

#[must_use]
pub fn native_console_shell() -> Arc<ConsoleShell> {
    Arc::new(ConsoleShell::new(
        Box::new(NativeSerialPorts),
        Box::new(FileSystemBackupStore::default_location()),
        local_stamp,
        qcm_core::console::DEFAULT_TIMEOUT,
    ))
}

// ---- wire shapes ----

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ConsolePortDto {
    pub id: String,
    pub label: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum ConnectOutcomeDto {
    Connected { build: i32 },
    NotAQuadStick,
    CouldNotOpen { port: String, detail: String },
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ConsoleProfilePickDto {
    pub pick_id: String,
    pub name: Option<String>,
    pub is_default: bool,
    pub is_preferences: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ConsoleSentDto {
    pub name: String,
    pub backup: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StrengthSetupDto {
    /// The hard threshold the person plays with, for the result sentence.
    pub threshold: i32,
}

/// A console failure, shaped as a superset of `QcmErrorDto` so the window's
/// one error path reads it. `subject` is the file or setting name the
/// `DeviceConsole_*` sentence is about; it is a device name, never a path.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ConsoleFailureDto {
    pub code: String,
    pub message: String,
    pub recoverable: bool,
    pub action: Option<RecoveryActionDto>,
    pub operation_id: Option<String>,
    pub target_state: Option<String>,
    pub backup: Option<String>,
    pub subject: Option<String>,
}

pub type Failure = Box<ConsoleFailureDto>;

impl From<&QcmError> for ConsoleFailureDto {
    fn from(error: &QcmError) -> Self {
        let dto = QcmErrorDto::from(error);
        Self {
            code: dto.code,
            message: dto.message,
            recoverable: dto.recoverable,
            action: dto.action,
            operation_id: dto.operation_id,
            target_state: dto.target_state,
            backup: dto.backup,
            subject: None,
        }
    }
}

impl From<&ConsoleError> for ConsoleFailureDto {
    fn from(error: &ConsoleError) -> Self {
        if let ConsoleError::Rejected(inner) = error {
            return Self::from(inner);
        }
        let code = match error {
            ConsoleError::NoAnswer => "QCM_CONSOLE_NO_ANSWER",
            ConsoleError::InvalidText => "QCM_CONSOLE_INVALID_TEXT",
            ConsoleError::SettingRefused { .. } => "QCM_CONSOLE_SETTING_REFUSED",
            ConsoleError::CouldNotOpen { .. } => "QCM_CONSOLE_COULD_NOT_OPEN",
            ConsoleError::WriteRefused { .. } => "QCM_CONSOLE_WRITE_REFUSED",
            ConsoleError::CouldNotLoad { .. } => "QCM_CONSOLE_COULD_NOT_LOAD",
            ConsoleError::ReadBackDiffers { .. } => "QCM_CONSOLE_READ_BACK_DIFFERS",
            ConsoleError::Port { .. } | ConsoleError::Rejected(_) => "QCM_CONSOLE_PORT",
        };
        let backup = match error {
            ConsoleError::ReadBackDiffers {
                backup: Some(location),
                ..
            } => Some(scrub(location.as_str())),
            _ => None,
        };
        Self {
            code: code.to_owned(),
            message: scrub(&error.message()),
            recoverable: true,
            action: Some(RecoveryActionDto {
                kind: "retry".to_owned(),
            }),
            operation_id: None,
            target_state: None,
            backup,
            subject: error.subject().map(scrub),
        }
    }
}

fn fail(error: &ConsoleError) -> Failure {
    Box::new(ConsoleFailureDto::from(error))
}

fn fail_qcm(error: &QcmError) -> Failure {
    Box::new(ConsoleFailureDto::from(error))
}

fn not_connected() -> Failure {
    fail(&ConsoleError::NoAnswer)
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ConnectRequest {
    port_id: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SendRequest {
    pick_id: String,
    confirm_default_csv: bool,
    confirm_preferences_csv: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ThresholdRequest {
    puff: bool,
    value: i32,
}

impl ConsoleShell {
    pub fn new(
        ports: Box<dyn SerialPorts>,
        backups: Box<dyn BackupStore + Send + Sync>,
        now: fn() -> FileStamp,
        timeout: Duration,
    ) -> Self {
        Self {
            ports,
            backups,
            now,
            timeout,
            inner: Mutex::new(Inner {
                listed: Vec::new(),
                generation: 0,
                console: None,
                pick: None,
                picks: 0,
            }),
        }
    }

    fn inner(&self) -> MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// The ports the OS offers now, under ids that only this list redeems.
    pub fn list_ports(&self) -> Vec<ConsolePortDto> {
        let found = self.ports.list();
        let mut inner = self.inner();
        inner.generation += 1;
        let generation = inner.generation;
        inner.listed = found
            .iter()
            .enumerate()
            .map(|(index, port)| (format!("port-{generation}-{index}"), port.name.clone()))
            .collect();
        found
            .into_iter()
            .zip(inner.listed.iter())
            .map(|(port, (id, _))| ConsolePortDto {
                id: id.clone(),
                label: port.label,
            })
            .collect()
    }

    pub fn connect(&self, request: Value) -> Result<ConnectOutcomeDto, Failure> {
        let request: ConnectRequest =
            parse(request, "connect_console request").map_err(|e| fail_qcm(&e))?;
        let mut inner = self.inner();
        let name = inner
            .listed
            .iter()
            .find(|(id, _)| *id == request.port_id)
            .map(|(_, name)| name.clone())
            .ok_or_else(|| {
                fail_qcm(&QcmError::Request(RequestError::OutOfRange {
                    what: "port",
                }))
            })?;
        // A new connect always lets go of the old link first.
        inner.console = None;
        let port_label = scrub(&name);
        let link = match self.ports.open(&name) {
            Ok(link) => link,
            Err(detail) => {
                return Ok(ConnectOutcomeDto::CouldNotOpen {
                    port: port_label,
                    detail: scrub(&detail),
                });
            }
        };
        let mut console = DeviceConsole::new(link, self.now).with_timeout(self.timeout);
        match console.build() {
            Ok(Some(build)) => {
                inner.console = Some(console);
                Ok(ConnectOutcomeDto::Connected { build })
            }
            Ok(None) => Ok(ConnectOutcomeDto::NotAQuadStick),
            Err(error) => Ok(ConnectOutcomeDto::CouldNotOpen {
                port: port_label,
                detail: scrub(&error.message()),
            }),
        }
    }

    /// Drop the port. Called when the window closes.
    pub fn close(&self) {
        let mut inner = self.inner();
        inner.console = None;
        inner.pick = None;
    }

    #[must_use]
    pub fn is_open(&self) -> bool {
        self.inner().console.is_some()
    }

    /// Keep a profile read from disk under an opaque id until it is sent.
    pub fn adopt_profile(&self, csv: &str) -> ConsoleProfilePickDto {
        let file = ProfileFile::load(csv);
        let mut inner = self.inner();
        inner.picks += 1;
        let pick_id = format!("pick-{}", inner.picks);
        let dto = ConsoleProfilePickDto {
            pick_id: pick_id.clone(),
            name: file.document.csv_file_name().map(str::to_owned),
            is_default: file.document.is_default_config(),
            is_preferences: file.document.is_device_preferences(),
        };
        inner.pick = Some((pick_id, file));
        dto
    }

    /// Read a picked file, bounded, and adopt it.
    pub fn adopt_path(&self, path: &Path) -> Result<ConsoleProfilePickDto, QcmError> {
        let too_large = ConfigError::TooLarge {
            limit_bytes: MAX_PROFILE_BYTES,
        };
        let meta = std::fs::metadata(path).map_err(|_| ConfigError::Unreadable)?;
        if meta.len() > MAX_PROFILE_BYTES {
            return Err(too_large.into());
        }
        let bytes = std::fs::read(path).map_err(|_| ConfigError::Unreadable)?;
        if bytes.len() as u64 > MAX_PROFILE_BYTES {
            return Err(too_large.into());
        }
        let text = String::from_utf8(bytes).map_err(|_| ConfigError::Unreadable)?;
        Ok(self.adopt_profile(text.trim_start_matches('\u{feff}')))
    }

    pub fn send_profile(&self, request: Value) -> Result<ConsoleSentDto, Failure> {
        let request: SendRequest =
            parse(request, "console_send_profile request").map_err(|e| fail_qcm(&e))?;
        let mut inner = self.inner();
        let file = match &inner.pick {
            Some((id, file)) if *id == request.pick_id => file.clone(),
            _ => {
                return Err(fail_qcm(&QcmError::Request(RequestError::OutOfRange {
                    what: "profile pick",
                })));
            }
        };
        let console = inner.console.as_mut().ok_or_else(not_connected)?;
        let sent = console
            .send_profile(
                &file,
                &*self.backups,
                request.confirm_default_csv,
                request.confirm_preferences_csv,
            )
            .map_err(|e| fail(&e))?;
        inner.pick = None;
        Ok(ConsoleSentDto {
            name: sent.name,
            backup: sent.backup.map(|b| scrub(b.as_str())),
        })
    }

    /// Read the settings, load the test profile, and hold both ceilings at
    /// 100, in the Avalonia order.
    pub fn strength_begin(&self) -> Result<StrengthSetupDto, Failure> {
        let mut inner = self.inner();
        let console = inner.console.as_mut().ok_or_else(not_connected)?;
        let before = console.preferences().map_err(|e| fail(&e))?;
        console
            .send_profile(
                &ProfileFile::load(STRENGTH_TEST_PROFILE),
                &*self.backups,
                false,
                false,
            )
            .map_err(|e| fail(&e))?;
        for puff in [true, false] {
            console
                .set_preference(maximum_preference(puff), 100)
                .map_err(|e| fail(&e))?;
        }
        Ok(StrengthSetupDto {
            threshold: current_hard_threshold(&before),
        })
    }

    pub fn calibrate(&self) -> Result<(), Failure> {
        let mut inner = self.inner();
        let console = inner.console.as_mut().ok_or_else(not_connected)?;
        console.calibrate().map_err(|e| fail(&e))
    }

    pub fn set_threshold(&self, request: Value) -> Result<(), Failure> {
        let request: ThresholdRequest =
            parse(request, "console_set_threshold request").map_err(|e| fail_qcm(&e))?;
        if !(0..=100).contains(&request.value) {
            return Err(fail_qcm(&QcmError::Request(RequestError::OutOfRange {
                what: "threshold",
            })));
        }
        let mut inner = self.inner();
        let console = inner.console.as_mut().ok_or_else(not_connected)?;
        console
            .set_preference(threshold_preference(request.puff), request.value)
            .map_err(|e| fail(&e))
    }

    /// Always: delete the test file while the console is still there to take
    /// it, then restart, which puts the person's own profile and settings
    /// back. The restart drops the port, so the link is let go too. Failures
    /// are swallowed: unplugging does the same, and the confirm said so.
    pub fn strength_end(&self) {
        let mut inner = self.inner();
        if let Some(console) = inner.console.as_mut() {
            let _ = console.delete(STRENGTH_TEST_NAME);
            let _ = console.boot();
        }
        inner.console = None;
    }
}

pub fn internal(what: &'static str, detail: impl std::fmt::Display) -> Failure {
    fail_qcm(&QcmError::Internal(InternalError {
        what,
        detail: OsDetail::new(detail.to_string()),
    }))
}

// ---- commands ----

use tauri::State;

type Shell<'a> = State<'a, Arc<ConsoleShell>>;

async fn blocking<T: Send + 'static>(
    shell: &Shell<'_>,
    run: impl FnOnce(&ConsoleShell) -> Result<T, Failure> + Send + 'static,
) -> Result<T, Failure> {
    let shell = Arc::clone(shell);
    tauri::async_runtime::spawn_blocking(move || run(&shell))
        .await
        .map_err(|error| internal("console worker", error))?
}

#[tauri::command]
pub async fn list_console_ports(shell: Shell<'_>) -> Result<Vec<ConsolePortDto>, Failure> {
    blocking(&shell, |s| Ok(s.list_ports())).await
}

#[tauri::command]
pub async fn connect_console(
    shell: Shell<'_>,
    request: Value,
) -> Result<ConnectOutcomeDto, Failure> {
    blocking(&shell, move |s| s.connect(request)).await
}

#[tauri::command]
pub fn close_console(shell: Shell<'_>) {
    shell.close();
}

/// Synchronous on purpose: the native file dialog belongs on the main thread.
#[tauri::command]
pub fn choose_console_profile(shell: Shell<'_>) -> Result<Option<ConsoleProfilePickDto>, Failure> {
    let chosen = rfd::FileDialog::new()
        .set_title("Send a profile")
        .add_filter("CSV", &["csv"])
        .pick_file();
    match chosen {
        None => Ok(None),
        Some(path) => shell.adopt_path(&path).map(Some).map_err(|e| fail_qcm(&e)),
    }
}

#[tauri::command]
pub async fn console_send_profile(
    shell: Shell<'_>,
    request: Value,
) -> Result<ConsoleSentDto, Failure> {
    blocking(&shell, move |s| s.send_profile(request)).await
}

#[tauri::command]
pub async fn console_strength_begin(shell: Shell<'_>) -> Result<StrengthSetupDto, Failure> {
    blocking(&shell, ConsoleShell::strength_begin).await
}

#[tauri::command]
pub async fn console_calibrate(shell: Shell<'_>) -> Result<(), Failure> {
    blocking(&shell, ConsoleShell::calibrate).await
}

#[tauri::command]
pub async fn console_set_threshold(shell: Shell<'_>, request: Value) -> Result<(), Failure> {
    blocking(&shell, move |s| s.set_threshold(request)).await
}

#[tauri::command]
pub async fn console_strength_end(shell: Shell<'_>) -> Result<(), Failure> {
    blocking(&shell, |s| {
        s.strength_end();
        Ok(())
    })
    .await
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct StrengthRequest {
    puff: i32,
    sip: i32,
}

/// The strength test finished: `strength_measured`, if usage data is on.
#[tauri::command]
pub fn console_record_strength(request: Value) -> Result<bool, Failure> {
    let request: StrengthRequest =
        parse(request, "console_record_strength request").map_err(|e| fail_qcm(&e))?;
    let range = (SWEEP_LOWEST - 1)..=SWEEP_HIGHEST;
    if !range.contains(&request.puff) || !range.contains(&request.sip) {
        return Err(fail_qcm(&QcmError::Request(RequestError::OutOfRange {
            what: "strength",
        })));
    }
    Ok(crate::telemetry::global().strength_measured(request.puff, request.sip))
}

#[cfg(test)]
mod tests {
    use super::*;
    use qcm_testkit::{FakeBackupStore, FakeConsole};
    use serde_json::json;
    use std::sync::Arc as StdArc;

    /// A port list with one fake QuadStick behind it, shared so the test can
    /// read what the device was told after the shell let go of it.
    struct FakePorts {
        device: StdArc<Mutex<Option<FakeConsole>>>,
    }

    struct SharedLink(StdArc<Mutex<Option<FakeConsole>>>);

    impl Read for SharedLink {
        fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
            self.0
                .lock()
                .expect("fake")
                .as_mut()
                .map_or(Ok(0), |d| d.read(buf))
        }
    }

    impl Write for SharedLink {
        fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
            self.0
                .lock()
                .expect("fake")
                .as_mut()
                .map_or(Ok(buf.len()), |d| d.write(buf))
        }
        fn flush(&mut self) -> std::io::Result<()> {
            Ok(())
        }
    }

    impl SerialPorts for FakePorts {
        fn list(&self) -> Vec<PortInfo> {
            order_ports(vec![
                PortInfo {
                    name: "/dev/cu.Bluetooth-Incoming-Port".into(),
                    label: "/dev/cu.Bluetooth-Incoming-Port".into(),
                },
                PortInfo {
                    name: "/dev/tty.QuadStick-SPP".into(),
                    label: "/dev/tty.QuadStick-SPP".into(),
                },
                PortInfo {
                    name: "/dev/cu.QuadStick-SPP".into(),
                    label: "/dev/cu.QuadStick-SPP".into(),
                },
            ])
        }
        fn open(&self, name: &str) -> Result<Box<dyn Link>, String> {
            if name.contains("QuadStick") {
                Ok(Box::new(SharedLink(StdArc::clone(&self.device))))
            } else {
                Err("Resource busy".into())
            }
        }
    }

    const STAMP: fn() -> FileStamp = || FileStamp {
        year: 2026,
        month: 1,
        day: 2,
        hour: 3,
        minute: 4,
        second: 5,
    };

    fn rig() -> (ConsoleShell, StdArc<Mutex<Option<FakeConsole>>>) {
        let device = StdArc::new(Mutex::new(Some(FakeConsole::new())));
        let shell = ConsoleShell::new(
            Box::new(FakePorts {
                device: StdArc::clone(&device),
            }),
            Box::new(FakeBackupStore::new()),
            STAMP,
            Duration::from_millis(200),
        );
        (shell, device)
    }

    fn connect(shell: &ConsoleShell) -> ConnectOutcomeDto {
        let ports = shell.list_ports();
        let id = ports[0].id.clone();
        shell.connect(json!({ "portId": id })).expect("connect")
    }

    #[test]
    fn the_quadstick_is_offered_first_and_only_once_on_a_mac() {
        let (shell, _) = rig();
        let labels: Vec<_> = shell.list_ports().into_iter().map(|p| p.label).collect();
        assert_eq!(
            labels,
            ["/dev/cu.QuadStick-SPP", "/dev/cu.Bluetooth-Incoming-Port"]
        );
    }

    #[test]
    fn connect_reads_the_build_and_close_lets_go() {
        let (shell, _) = rig();
        assert_eq!(
            connect(&shell),
            ConnectOutcomeDto::Connected { build: 2373 }
        );
        assert!(shell.is_open());
        shell.close();
        assert!(!shell.is_open());
    }

    #[test]
    fn a_port_that_will_not_open_says_which() {
        let (shell, _) = rig();
        let ports = shell.list_ports();
        let outcome = shell
            .connect(json!({ "portId": ports[1].id }))
            .expect("outcome");
        assert!(matches!(outcome, ConnectOutcomeDto::CouldNotOpen { .. }));
        assert!(!shell.is_open());
    }

    #[test]
    fn a_stale_port_id_is_refused() {
        let (shell, _) = rig();
        let old = shell.list_ports()[0].id.clone();
        let _ = shell.list_ports();
        assert!(shell.connect(json!({ "portId": old })).is_err());
    }

    #[test]
    fn sending_needs_the_pick_and_reports_the_backup() {
        let (shell, device) = rig();
        device
            .lock()
            .expect("fake")
            .as_mut()
            .expect("device")
            .files
            .insert("game.csv".into(), "old\n".into());
        connect(&shell);
        let pick = shell.adopt_profile(
            "Profile Name,,Game,,,,,,,,\ngame.csv,,,,,,,,,,\nPlayStation Outputs,Function,usb,,,,,,,,\nx,normal,mp_center_puff,,,,,,,,\n",
        );
        assert_eq!(pick.name.as_deref(), Some("game.csv"));
        let sent = shell
            .send_profile(json!({
                "pickId": pick.pick_id,
                "confirmDefaultCsv": false,
                "confirmPreferencesCsv": false,
            }))
            .expect("sent");
        assert_eq!(sent.name, "game.csv");
        assert!(sent.backup.is_some());
        let guard = device.lock().expect("fake");
        let fake = guard.as_ref().expect("device");
        assert_eq!(
            fake.commands.last().map(String::as_str),
            Some("load,game.csv")
        );
    }

    #[test]
    fn a_default_csv_without_the_yes_is_a_confirmation_failure() {
        let (shell, _) = rig();
        connect(&shell);
        let pick = shell.adopt_profile(
            "Profile Name,,Game,,,,,,,,\ndefault.csv,,,,,,,,,,\nPlayStation Outputs,Function,usb,,,,,,,,\nx,normal,mp_center_puff,,,,,,,,\n",
        );
        assert!(pick.is_default);
        let error = shell
            .send_profile(json!({
                "pickId": pick.pick_id,
                "confirmDefaultCsv": false,
                "confirmPreferencesCsv": false,
            }))
            .expect_err("gated");
        assert_eq!(error.code, "QCM_CONFIRMATION_REQUIRED");
    }

    #[test]
    fn the_strength_steps_restore_everything_and_drop_the_port() {
        let (shell, device) = rig();
        connect(&shell);
        assert_eq!(shell.strength_begin().expect("begin").threshold, 60);
        shell.calibrate().expect("calibrate");
        shell
            .set_threshold(json!({ "puff": true, "value": 50 }))
            .expect("set");
        assert!(
            shell
                .set_threshold(json!({ "puff": true, "value": 101 }))
                .is_err()
        );
        shell.strength_end();
        assert!(!shell.is_open());
        let guard = device.lock().expect("fake");
        let fake = guard.as_ref().expect("device");
        assert!(fake.commands.iter().any(|c| c == "puff_maximum,100"));
        assert!(fake.commands.iter().any(|c| c == "sip_maximum,100"));
        assert!(!fake.files.contains_key(STRENGTH_TEST_NAME));
        assert_eq!(fake.commands.last().map(String::as_str), Some("boot"));
    }

    #[test]
    fn a_failure_carries_its_subject_but_never_a_path() {
        let error = ConsoleError::ReadBackDiffers {
            name: "game.csv".into(),
            backup: None,
        };
        let dto = ConsoleFailureDto::from(&error);
        assert_eq!(dto.code, "QCM_CONSOLE_READ_BACK_DIFFERS");
        assert_eq!(dto.subject.as_deref(), Some("game.csv"));
        let port = ConsoleFailureDto::from(&ConsoleError::Port {
            detail: OsDetail::new("/Users/someone/secret"),
        });
        assert!(!port.message.contains("/Users"));
    }
}
