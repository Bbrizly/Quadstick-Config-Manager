//! Running this shell inside another app.
//!
//! `run()` is the free app: its own folders, network, `qcm://` links and Drive
//! backup on save. A host app calls `run_with` instead, with its own
//! `tauri::Context` (identifier, icon, assets, capabilities) and a
//! [`HostConfig`] that moves the folders, turns network, links and backup off,
//! and adds the host's own commands. Nothing here changes the free app: with
//! no host installed every accessor answers what the free app always did.

use qcm_core::error::{QcmError, QcmErrorDto, RequestError};
use std::path::PathBuf;
use std::sync::OnceLock;
use tauri::Wry;
use tauri::ipc::Invoke;

/// Commands a host adds beside the free app's: the names it answers and the
/// handler `tauri::generate_handler!` built for them.
pub struct HostCommands {
    names: &'static [&'static str],
    handler: Box<dyn Fn(Invoke<Wry>) -> bool + Send + Sync>,
}

impl HostCommands {
    pub fn new(
        names: &'static [&'static str],
        handler: impl Fn(Invoke<Wry>) -> bool + Send + Sync + 'static,
    ) -> Self {
        Self {
            names,
            handler: Box::new(handler),
        }
    }
}

/// What a host changes. [`HostConfig::free`] is the free app, field for field.
pub struct HostConfig {
    /// Replaces the app's own data folder: settings, rescue, pending crash
    /// reports, followed links, custom names, Drive links, community cache,
    /// device backups.
    pub data_dir: Option<PathBuf>,
    /// Replaces `Documents/QuadStick Profiles` (and its `Templates`).
    pub library_dir: Option<PathBuf>,
    /// Off: no Google, Drive, Community or Sheets client is built, telemetry
    /// never starts, and every command that reaches the internet is refused.
    pub network: bool,
    /// Off: the `qcm://` plugin is never registered and launch links are
    /// never read.
    pub deep_links: bool,
    /// Off: a save, open or save-and-close never pushes to Drive.
    pub drive_auto_backup: bool,
    pub commands: Option<HostCommands>,
}

impl HostConfig {
    #[must_use]
    pub const fn free() -> Self {
        Self {
            data_dir: None,
            library_dir: None,
            network: true,
            deep_links: true,
            drive_auto_backup: true,
            commands: None,
        }
    }

    /// A host command may not shadow one of the free app's.
    pub fn check(&self) -> Result<(), String> {
        let Some(commands) = &self.commands else {
            return Ok(());
        };
        let free = crate::registered_commands();
        match commands.names.iter().find(|name| free.contains(name)) {
            Some(name) => Err(format!("host command `{name}` is already a free command")),
            None => Ok(()),
        }
    }
}

/// The part of the config the rest of the shell reads, fixed for the run.
struct Installed {
    data_dir: Option<PathBuf>,
    library_dir: Option<PathBuf>,
    network: bool,
    drive_auto_backup: bool,
}

static INSTALLED: OnceLock<Installed> = OnceLock::new();

/// Fix the config for this run, before anything reads a path. Once only.
pub(crate) fn install(config: &HostConfig) {
    let installed = INSTALLED.set(Installed {
        data_dir: config.data_dir.clone(),
        library_dir: config.library_dir.clone(),
        network: config.network,
        drive_auto_backup: config.drive_auto_backup,
    });
    assert!(installed.is_ok(), "the host config is set once per run");
}

/// The host's data folder, if it gave one.
pub(crate) fn data_dir() -> Option<PathBuf> {
    INSTALLED.get().and_then(|host| host.data_dir.clone())
}

/// The host's profile library, if it gave one.
pub(crate) fn library_dir() -> Option<PathBuf> {
    INSTALLED.get().and_then(|host| host.library_dir.clone())
}

pub(crate) fn network() -> bool {
    INSTALLED.get().is_none_or(|host| host.network)
}

pub(crate) fn drive_auto_backup() -> bool {
    INSTALLED
        .get()
        .is_none_or(|host| host.network && host.drive_auto_backup)
}

/// Every free command that reaches the internet, from this process or by
/// handing a web page to the browser.
pub const NETWORK_COMMANDS: &[&str] = &[
    "load_community_catalog",
    "import_community_profile",
    "open_community_sheet",
    "get_google_auth_status",
    "connect_google",
    "disconnect_google",
    "backup_profile_to_drive",
    "resolve_drive_conflict",
    "list_drive_backups",
    "restore_drive_backup",
    "restore_drive_backups",
    "share_drive_profile",
    "subscribe_drive_backups",
    "unsubscribe_drive_backups",
    "check_for_update",
    "open_external_url",
    "open_privacy_policy",
    "send_feedback",
    "track_telemetry_event",
    "open_registry_profile",
    "import_sheets_link",
    "check_latest_from_link",
    "replace_with_latest",
];

#[derive(Debug, PartialEq, Eq)]
enum Route {
    Host,
    Refused,
    Free,
}

fn route(command: &str, network: bool, host_names: &[&str]) -> Route {
    if host_names.contains(&command) {
        Route::Host
    } else if !network && NETWORK_COMMANDS.contains(&command) {
        Route::Refused
    } else {
        Route::Free
    }
}

/// A command this run does not offer: a request the app refuses, not a bug.
fn network_off() -> QcmErrorDto {
    QcmErrorDto::from(&QcmError::Request(RequestError::OutOfRange {
        what: "network command",
    }))
}

/// One invoke handler from the free app's, the network switch and the host's.
pub(crate) fn invoke_handler(
    config: &mut HostConfig,
    free: impl Fn(Invoke<Wry>) -> bool + Send + Sync + 'static,
) -> impl Fn(Invoke<Wry>) -> bool + Send + Sync + 'static {
    let network = config.network;
    let host = config.commands.take();
    move |invoke| {
        let names = host.as_ref().map_or(&[][..], |host| host.names);
        match route(invoke.message.command(), network, names) {
            Route::Host => host.as_ref().is_some_and(|host| (host.handler)(invoke)),
            Route::Refused => {
                invoke.resolver.reject(network_off());
                true
            }
            Route::Free => free(invoke),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn free_config_is_the_free_app() {
        let free = HostConfig::free();
        assert!(free.data_dir.is_none() && free.library_dir.is_none());
        assert!(free.network && free.deep_links && free.drive_auto_backup);
        assert!(free.commands.is_none());
        assert_eq!(free.check(), Ok(()));
    }

    #[test]
    fn nothing_installed_reads_as_the_free_app() {
        // Unit tests never install a host, so this is what `run()` sees.
        assert_eq!(data_dir(), None);
        assert_eq!(library_dir(), None);
        assert!(network());
        assert!(drive_auto_backup());
    }

    #[test]
    fn network_commands_are_real_commands() {
        let commands = crate::registered_commands();
        for name in NETWORK_COMMANDS {
            assert!(commands.contains(name), "{name}");
        }
    }

    #[test]
    fn every_google_drive_and_community_command_is_a_network_command() {
        for source in [
            include_str!("google_commands.rs"),
            include_str!("drive_commands.rs"),
            include_str!("community_commands.rs"),
            include_str!("updates.rs"),
        ] {
            let mut lines = source.lines();
            while let Some(line) = lines.next() {
                if line.trim() != "#[tauri::command]" {
                    continue;
                }
                let signature = lines.next().unwrap_or_default();
                let name = signature
                    .split("fn ")
                    .nth(1)
                    .and_then(|rest| rest.split(['(', '<']).next())
                    .unwrap_or_default();
                assert!(NETWORK_COMMANDS.contains(&name), "{name}");
            }
        }
    }

    #[test]
    fn network_off_refuses_only_network_commands() {
        assert_eq!(route("connect_google", false, &[]), Route::Refused);
        assert_eq!(route("check_for_update", false, &[]), Route::Refused);
        assert_eq!(route("save_profile", false, &[]), Route::Free);
        assert_eq!(route("connect_google", true, &[]), Route::Free);
    }

    #[test]
    fn host_commands_go_to_the_host() {
        let names = ["open_roster", "list_clients"];
        assert_eq!(route("open_roster", false, &names), Route::Host);
        assert_eq!(route("list_clients", true, &names), Route::Host);
        assert_eq!(route("get_settings", false, &names), Route::Free);
    }

    #[test]
    fn a_host_command_cannot_shadow_a_free_one() {
        let mut config = HostConfig::free();
        config.commands = Some(HostCommands::new(&["open_roster"], |_| false));
        assert_eq!(config.check(), Ok(()));
        config.commands = Some(HostCommands::new(&["open_roster", "save_profile"], |_| {
            false
        }));
        assert!(config.check().unwrap_err().contains("save_profile"));
    }

    #[test]
    fn the_refusal_is_a_refused_request_not_a_bug() {
        let text = serde_json::to_string(&network_off()).expect("dto serialises");
        assert!(text.contains("QCM_REQUEST_OUT_OF_RANGE"), "{text}");
        assert!(!text.contains("report_bug"), "{text}");
    }
}
