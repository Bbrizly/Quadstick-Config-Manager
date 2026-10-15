//! The Linux Xbox controller bridge and the Mac SDL gamepad switch.
//!
//! Ported from `ControllerBridge.cs`, `MacGamepad.cs` and the bridge/Mac rows
//! of `DeviceSettingsPage.cs`. The bridge is `qs-bridge --json` as a child
//! process beside the app, run for as long as the switch is on and the app is
//! open. Closing its stdin is the stop: it lets every button go first, and the
//! close also happens when this app dies.
//!
//! Idle cost: switch off, nothing runs. Switch on, one thread is parked on the
//! child's stdout and wakes only when the bridge prints a status change.

use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::{Duration, Instant};

use qcm_core::error::{InternalError, OsDetail, QcmError, QcmErrorDto};
use serde::Serialize;
use tauri::State;
use tauri::ipc::Channel;

use crate::shell::ShellState;

type Failure = Box<QcmErrorDto>;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum BridgeState {
    Waiting,
    Bridging,
    NoAccess,
    NoVirtualPad,
    Unsupported,
    Stopped,
}

/// One status line from `qs-bridge --json`. `None` for anything else, so a
/// newer bridge with a new status is ignored, not misread.
#[must_use]
pub fn parse_status(line: &str) -> Option<BridgeState> {
    let value: serde_json::Value = serde_json::from_str(line.trim()).ok()?;
    match value.get("status")?.as_str()? {
        "waiting" => Some(BridgeState::Waiting),
        "bridging" => Some(BridgeState::Bridging),
        "no_access" => Some(BridgeState::NoAccess),
        "no_virtual_pad" => Some(BridgeState::NoVirtualPad),
        "unsupported" => Some(BridgeState::Unsupported),
        _ => None,
    }
}

/// The bridge binary shipped beside the app, or `None`.
#[must_use]
pub fn find_binary() -> Option<PathBuf> {
    let exe = std::env::current_exe().ok()?;
    let name = if cfg!(windows) {
        "qs-bridge.exe"
    } else {
        "qs-bridge"
    };
    let path = exe.parent()?.join(name);
    path.is_file().then_some(path)
}

/// Only Linux can make the virtual pad so far; macOS waits on Apple's
/// virtual HID entitlement.
#[must_use]
pub fn bridge_offered() -> bool {
    cfg!(target_os = "linux") && find_binary().is_some()
}

struct Running {
    child: Child,
    stdin: Option<ChildStdin>,
    stopping: Arc<std::sync::atomic::AtomicBool>,
}

#[derive(Default)]
struct Inner {
    running: Option<Running>,
    state: Option<BridgeState>,
    listener: Option<(u64, Channel<Option<BridgeState>>)>,
    generation: u64,
}

/// The one bridge this app runs.
#[derive(Default, Clone)]
pub struct BridgeRuntime {
    inner: Arc<Mutex<Inner>>,
}

impl BridgeRuntime {
    fn lock(&self) -> MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn publish(&self, state: Option<BridgeState>) {
        let mut inner = self.lock();
        inner.state = state;
        if let Some((_, channel)) = &inner.listener {
            let _ = channel.send(state);
        }
    }

    #[must_use]
    pub fn state(&self) -> Option<BridgeState> {
        self.lock().state
    }

    #[must_use]
    pub fn is_running(&self) -> bool {
        self.lock().running.is_some()
    }

    /// Start the bridge at `path`. A second start while one runs does nothing.
    pub fn start(&self, path: &Path) {
        if self.is_running() {
            return;
        }
        let spawned = Command::new(path)
            .arg("--json")
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            // For a person running it by hand; dropped so a full pipe can
            // never block the bridge mid-game.
            .stderr(Stdio::null())
            .spawn();
        let mut child = match spawned {
            Ok(child) => child,
            Err(_) => {
                self.publish(Some(BridgeState::Stopped));
                return;
            }
        };
        let stdin = child.stdin.take();
        let stdout = child.stdout.take();
        let stopping = Arc::new(std::sync::atomic::AtomicBool::new(false));
        self.lock().running = Some(Running {
            child,
            stdin,
            stopping: Arc::clone(&stopping),
        });
        self.publish(Some(BridgeState::Waiting));
        let Some(stdout) = stdout else { return };
        let runtime = self.clone();
        let _ = std::thread::Builder::new()
            .name("qcm-bridge-status".into())
            .spawn(move || runtime.read_status(stdout, &stopping));
    }

    fn read_status(
        &self,
        stdout: std::process::ChildStdout,
        stopping: &std::sync::atomic::AtomicBool,
    ) {
        let mut last = None;
        for line in BufReader::new(stdout).lines() {
            let Ok(line) = line else { break };
            if let Some(state) = parse_status(&line) {
                last = Some(state);
                if !stopping.load(std::sync::atomic::Ordering::SeqCst) {
                    self.publish(Some(state));
                }
            }
        }
        // EOF: the bridge exited, or closed its stdout. Wait at most a second
        // (as `ControllerBridge.cs` does), never holding the lock across a
        // sleep, so a child that lingers stays in `running` and Stop still
        // reaches it. "unsupported" is its own last word.
        let deadline = Instant::now() + Duration::from_secs(1);
        loop {
            if stopping.load(std::sync::atomic::Ordering::SeqCst) {
                return;
            }
            let exited = {
                let mut inner = self.lock();
                let done = inner
                    .running
                    .as_mut()
                    .is_none_or(|running| !matches!(running.child.try_wait(), Ok(None)));
                if done {
                    inner.running = None;
                }
                done
            };
            if exited || Instant::now() >= deadline {
                break;
            }
            std::thread::sleep(Duration::from_millis(50));
        }
        if last != Some(BridgeState::Unsupported) {
            self.publish(Some(BridgeState::Stopped));
        }
    }

    /// Close stdin, wait up to two seconds (it checks four times a second),
    /// then kill. Only runs when the switch goes off or the app exits.
    pub fn stop(&self) {
        let running = {
            let mut inner = self.lock();
            if let Some(running) = &inner.running {
                running
                    .stopping
                    .store(true, std::sync::atomic::Ordering::SeqCst);
            }
            inner.running.take()
        };
        if let Some(mut running) = running {
            drop(running.stdin.take());
            let deadline = Instant::now() + Duration::from_secs(2);
            loop {
                match running.child.try_wait() {
                    Ok(Some(_)) | Err(_) => break,
                    Ok(None) if Instant::now() >= deadline => {
                        let _ = running.child.kill();
                        let _ = running.child.wait();
                        break;
                    }
                    Ok(None) => std::thread::sleep(Duration::from_millis(50)),
                }
            }
        }
        self.publish(None);
    }

    pub fn subscribe(&self, channel: Channel<Option<BridgeState>>) -> u64 {
        let mut inner = self.lock();
        inner.generation += 1;
        let generation = inner.generation;
        let _ = channel.send(inner.state);
        inner.listener = Some((generation, channel));
        generation
    }

    pub fn unsubscribe(&self, generation: u64) {
        let mut inner = self.lock();
        if inner
            .listener
            .as_ref()
            .is_some_and(|(id, _)| *id == generation)
        {
            inner.listener = None;
        }
    }
}

/// Launch: start the bridge if it was left on and can run here.
pub fn start_if_on(runtime: &BridgeRuntime, shell: &ShellState) {
    if shell.controller_bridge_on()
        && cfg!(target_os = "linux")
        && let Some(path) = find_binary()
    {
        runtime.start(&path);
    }
}

// ---- the Mac SDL gamepad switch (`MacGamepad.cs`) ----

pub const MAC_LABEL: &str = "com.bbrizly.quadstickconfigmanager.sdl-gamepad";

// Mode 0, 16D0:092B, FW 2373 Descriptors.h:85. The second GUID carries the
// device's bcdDevice (0x0003) for SDL builds that only match exactly.
const MAC_LAYOUT: &str = "a:b1,b:b2,back:b8,dpdown:h0.4,dpleft:h0.8,dpright:h0.2,dpup:h0.1,guide:b12,\
leftshoulder:b4,leftstick:b10,lefttrigger:b6,leftx:a0,lefty:a1,rightshoulder:b5,rightstick:b11,\
righttrigger:b7,rightx:a2,righty:a3,start:b9,x:b0,y:b3,platform:Mac OS X,";

#[must_use]
pub fn mac_mappings() -> [String; 2] {
    [
        format!("03000000d01600002b09000000000000,QuadStick,{MAC_LAYOUT}"),
        format!("03000000d01600002b09000003000000,QuadStick,{MAC_LAYOUT}"),
    ]
}

/// The command `MacGamepad.Command` runs, byte for byte.
#[must_use]
pub fn mac_command() -> String {
    let joined = mac_mappings().join("\\n");
    format!(
        "mkdir -p ~/Library/LaunchAgents && /usr/bin/plutil -convert xml1 -o ~/Library/LaunchAgents/{MAC_LABEL}\
.plist - <<< '{{\"Label\":\"{MAC_LABEL}\",\"ProgramArguments\":[\"/bin/launchctl\",\"setenv\",\
\"SDL_GAMECONTROLLERCONFIG\",\"{joined}\"],\"RunAtLoad\":true}}' && launchctl setenv SDL_GAMECONTROLLERCONFIG $'{joined}'"
    )
}

#[must_use]
pub fn mac_undo_command() -> String {
    format!(
        "launchctl unsetenv SDL_GAMECONTROLLERCONFIG; rm -f ~/Library/LaunchAgents/{MAC_LABEL}.plist"
    )
}

/// The App Store build is sandboxed and cannot run launchctl.
#[must_use]
pub fn mac_sandboxed() -> bool {
    std::env::var_os("APP_SANDBOX_CONTAINER_ID").is_some()
}

#[must_use]
pub fn mac_agent_path(home: &Path) -> PathBuf {
    home.join("Library")
        .join("LaunchAgents")
        .join(format!("{MAC_LABEL}.plist"))
}

fn home() -> Option<PathBuf> {
    std::env::var_os("HOME").map(PathBuf::from)
}

#[must_use]
pub fn mac_is_on() -> bool {
    home().is_some_and(|home| mac_agent_path(&home).is_file())
}

/// Run one of the two commands in zsh with a 10 s cap. Output is not piped:
/// nothing reads it, and an unread pipe can stall the shell.
pub fn run_shell(command: &str, shell: &str, env: &[(&str, &str)]) -> bool {
    let mut cmd = Command::new(shell);
    cmd.arg("-c")
        .arg(command)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    for (key, value) in env {
        cmd.env(key, value);
    }
    let Ok(mut child) = cmd.spawn() else {
        return false;
    };
    let deadline = Instant::now() + Duration::from_secs(10);
    loop {
        match child.try_wait() {
            Ok(Some(status)) => return status.success(),
            Err(_) => return false,
            Ok(None) if Instant::now() >= deadline => {
                let _ = child.kill();
                let _ = child.wait();
                return false;
            }
            Ok(None) => std::thread::sleep(Duration::from_millis(50)),
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ControllerOptionsDto {
    /// `bridge` (Linux with the binary), `mac` (macOS), or `none`.
    pub kind: &'static str,
    pub bridge_on: bool,
    pub bridge_state: Option<BridgeState>,
    pub mac_on: bool,
    pub mac_sandboxed: bool,
    /// The commands to paste, only for the sandboxed Mac build.
    pub mac_command: Option<String>,
    pub mac_undo_command: Option<String>,
}

fn internal(what: &'static str, detail: impl Into<String>) -> Failure {
    Box::new(QcmErrorDto::from(&QcmError::Internal(InternalError {
        what,
        detail: OsDetail::new(detail.into()),
    })))
}

#[tauri::command]
pub fn get_controller_options(
    runtime: State<'_, BridgeRuntime>,
    shell: State<'_, ShellState>,
) -> ControllerOptionsDto {
    let kind = if bridge_offered() {
        "bridge"
    } else if cfg!(target_os = "macos") {
        "mac"
    } else {
        "none"
    };
    let sandboxed = kind == "mac" && mac_sandboxed();
    ControllerOptionsDto {
        kind,
        // The switch shows what was saved, as the Avalonia toggle keeps its
        // place after a failed start; the status line says it stopped.
        bridge_on: kind == "bridge" && shell.controller_bridge_on(),
        bridge_state: runtime.state(),
        mac_on: kind == "mac" && mac_is_on(),
        mac_sandboxed: sandboxed,
        mac_command: sandboxed.then(mac_command),
        mac_undo_command: sandboxed.then(mac_undo_command),
    }
}

/// The bridge switch: saved first, then started or stopped. Returns the saved
/// switch; a start that fails says so through the `stopped` state.
#[tauri::command]
pub fn set_controller_bridge(
    runtime: State<'_, BridgeRuntime>,
    shell: State<'_, ShellState>,
    on: bool,
) -> Result<bool, Failure> {
    let Some(path) = find_binary().filter(|_| cfg!(target_os = "linux")) else {
        return Err(internal(
            "controller bridge",
            "not offered on this computer",
        ));
    };
    shell
        .set_controller_bridge(on)
        .map_err(|error| Box::new(QcmErrorDto::from(&error)))?;
    if on {
        runtime.start(&path);
    } else {
        runtime.stop();
    }
    Ok(on)
}

/// The Mac switch. Half a change is worse than none: a failed turn-on takes
/// back whatever got made. Returns whether it is on now, and whether it took.
#[tauri::command]
pub fn set_mac_gamepad(on: bool) -> Result<MacGamepadResultDto, Failure> {
    if !cfg!(target_os = "macos") || mac_sandboxed() {
        return Err(internal("mac gamepad", "not available in this build"));
    }
    let command = if on {
        mac_command()
    } else {
        mac_undo_command()
    };
    let took = run_shell(&command, "/bin/zsh", &[]);
    if !took && on {
        run_shell(&mac_undo_command(), "/bin/zsh", &[]);
    }
    Ok(MacGamepadResultDto {
        on: mac_is_on(),
        took,
    })
}

#[derive(Debug, Clone, Copy, Serialize)]
pub struct MacGamepadResultDto {
    pub on: bool,
    pub took: bool,
}

#[tauri::command]
pub fn subscribe_controller_bridge(
    runtime: State<'_, BridgeRuntime>,
    on_state: Channel<Option<BridgeState>>,
) -> crate::streaming::SubscriptionDto {
    let generation = runtime.subscribe(on_state);
    crate::streaming::SubscriptionDto {
        subscription_id: format!("bridge-{generation}"),
    }
}

#[tauri::command]
pub fn unsubscribe_controller_bridge(runtime: State<'_, BridgeRuntime>, subscription_id: String) {
    if let Some(generation) = subscription_id
        .strip_prefix("bridge-")
        .and_then(|n| n.parse().ok())
    {
        runtime.unsubscribe(generation);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn status_lines_parse_like_controller_bridge_cs() {
        for (line, state) in [
            (r#"{"status":"waiting"}"#, BridgeState::Waiting),
            (r#"{"status":"bridging"}"#, BridgeState::Bridging),
            (r#"{"status":"no_access"}"#, BridgeState::NoAccess),
            (r#"{"status":"no_virtual_pad"}"#, BridgeState::NoVirtualPad),
            (r#"{"status":"unsupported"}"#, BridgeState::Unsupported),
        ] {
            assert_eq!(parse_status(line), Some(state), "{line}");
        }
        for ignored in [
            "",
            "  ",
            "waiting",
            r#"{"status":"new_thing"}"#,
            r#"{"status":3}"#,
            "{",
        ] {
            assert_eq!(parse_status(ignored), None, "{ignored}");
        }
    }

    #[cfg(unix)]
    fn script(dir: &Path, name: &str, body: &str) -> PathBuf {
        use std::os::unix::fs::PermissionsExt;
        let path = dir.join(name);
        std::fs::write(&path, format!("#!/bin/sh\n{body}\n")).expect("write script");
        std::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o755)).expect("chmod");
        path
    }

    #[cfg(unix)]
    fn scratch(tag: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("qcm-bridge-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).expect("scratch");
        dir
    }

    #[cfg(unix)]
    fn wait_for(runtime: &BridgeRuntime, want: Option<BridgeState>) {
        let deadline = Instant::now() + Duration::from_secs(5);
        while runtime.state() != want && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(20));
        }
        assert_eq!(runtime.state(), want);
    }

    #[cfg(unix)]
    #[test]
    fn the_bridge_reports_its_state_and_stops_when_stdin_closes() {
        let dir = scratch("run");
        // Stands in for qs-bridge: one status line, then waits for stdin EOF.
        let fake = script(
            &dir,
            "qs-bridge",
            "echo '{\"status\":\"bridging\"}'\ncat >/dev/null",
        );
        let runtime = BridgeRuntime::default();
        runtime.start(&fake);
        wait_for(&runtime, Some(BridgeState::Bridging));
        assert!(runtime.is_running());
        runtime.stop();
        assert!(!runtime.is_running());
        assert_eq!(runtime.state(), None);
        let _ = std::fs::remove_dir_all(dir);
    }

    #[cfg(unix)]
    #[test]
    fn a_bridge_that_dies_says_stopped_but_unsupported_is_its_own_word() {
        let dir = scratch("die");
        let dies = script(&dir, "dies", "echo '{\"status\":\"waiting\"}'");
        let runtime = BridgeRuntime::default();
        runtime.start(&dies);
        wait_for(&runtime, Some(BridgeState::Stopped));
        assert!(!runtime.is_running());

        let unsupported = script(&dir, "unsupported", "echo '{\"status\":\"unsupported\"}'");
        let runtime = BridgeRuntime::default();
        runtime.start(&unsupported);
        wait_for(&runtime, Some(BridgeState::Unsupported));
        std::thread::sleep(Duration::from_millis(100));
        assert_eq!(runtime.state(), Some(BridgeState::Unsupported));
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn the_mac_command_matches_mac_gamepad_cs() {
        let command = mac_command();
        assert!(command.starts_with("mkdir -p ~/Library/LaunchAgents && /usr/bin/plutil -convert xml1 -o ~/Library/LaunchAgents/com.bbrizly.quadstickconfigmanager.sdl-gamepad.plist - <<< '{\"Label\":"));
        assert!(command.contains("03000000d01600002b09000000000000,QuadStick,a:b1,b:b2,back:b8,"));
        assert!(
            command.contains("platform:Mac OS X,\\n03000000d01600002b09000003000000,QuadStick,")
        );
        assert!(command.ends_with("&& launchctl setenv SDL_GAMECONTROLLERCONFIG $'03000000d01600002b09000000000000,QuadStick,a:b1,b:b2,back:b8,dpdown:h0.4,dpleft:h0.8,dpright:h0.2,dpup:h0.1,guide:b12,leftshoulder:b4,leftstick:b10,lefttrigger:b6,leftx:a0,lefty:a1,rightshoulder:b5,rightstick:b11,righttrigger:b7,rightx:a2,righty:a3,start:b9,x:b0,y:b3,platform:Mac OS X,\\n03000000d01600002b09000003000000,QuadStick,a:b1,b:b2,back:b8,dpdown:h0.4,dpleft:h0.8,dpright:h0.2,dpup:h0.1,guide:b12,leftshoulder:b4,leftstick:b10,lefttrigger:b6,leftx:a0,lefty:a1,rightshoulder:b5,rightstick:b11,righttrigger:b7,rightx:a2,righty:a3,start:b9,x:b0,y:b3,platform:Mac OS X,'"));
        assert_eq!(
            mac_undo_command(),
            "launchctl unsetenv SDL_GAMECONTROLLERCONFIG; rm -f ~/Library/LaunchAgents/com.bbrizly.quadstickconfigmanager.sdl-gamepad.plist"
        );
    }

    // The real command, with HOME and a stand-in launchctl in a scratch folder.
    #[cfg(target_os = "macos")]
    #[test]
    fn the_mac_command_writes_the_agent_and_undo_removes_it() {
        let dir = scratch("mac");
        let bin = dir.join("bin");
        std::fs::create_dir_all(&bin).expect("bin");
        script(&bin, "launchctl", "exit 0");
        let home = dir.to_string_lossy().into_owned();
        let path = format!("{}:/usr/bin:/bin", bin.to_string_lossy());
        let env = [("HOME", home.as_str()), ("PATH", path.as_str())];
        assert!(run_shell(&mac_command(), "/bin/zsh", &env));
        let agent = std::fs::read_to_string(mac_agent_path(&dir)).expect("agent written");
        assert!(agent.contains("SDL_GAMECONTROLLERCONFIG"));
        assert!(agent.contains("<key>RunAtLoad</key>"));
        assert!(run_shell(&mac_undo_command(), "/bin/zsh", &env));
        assert!(!mac_agent_path(&dir).exists());
        let _ = std::fs::remove_dir_all(dir);
    }
}
