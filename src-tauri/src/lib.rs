//! The desktop shell.
//!
//! Paths, drive letters, HID paths, OAuth tokens, Drive ids and OS handles stop
//! at native adapters. The WebView receives bounded DTOs and opaque refs only.

use std::sync::Arc;
use tauri::Manager;

pub mod adapters;
pub mod close_guard;
pub mod commands;
pub mod community;
pub mod community_commands;
pub mod console_shell;
pub mod controller;
pub mod device_ipc;
pub mod device_rename_ipc;
pub mod device_shell;
pub mod diagnostics;
pub mod drive;
pub mod drive_auto;
pub mod drive_commands;
#[cfg(feature = "e2e")]
pub mod e2e;
pub mod follow;
pub mod follow_commands;
pub mod follow_shell;
pub mod google_auth;
pub mod google_commands;
pub mod host;
pub mod ipc;
pub mod library_shell;
pub mod names_shell;
pub mod preference_ipc;
pub mod registry;
pub mod shell;
pub mod streaming;
pub mod telemetry;
pub mod updates;
pub mod workbook_shell;

#[must_use]
pub fn core_policy() -> &'static str {
    qcm_core::CORE_CRATE_POLICY
}

#[must_use]
pub fn registered_commands() -> &'static [&'static str] {
    &[
        "get_app_snapshot",
        "get_settings",
        "update_settings",
        "new_profile",
        "choose_and_open_profile",
        "get_profile_snapshot",
        "apply_editor_ops",
        "undo_editor",
        "save_profile",
        "save_profile_as",
        "close_profile",
        "choose_and_import_workbook",
        "repair_workbook_tab",
        "accept_workbook_import",
        "cancel_workbook_import",
        "export_profile_xlsx",
        "get_preference_catalog",
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
        "share_drive_profile",
        "subscribe_drive_backups",
        "unsubscribe_drive_backups",
        "list_devices",
        "refresh_devices",
        "choose_device_folder",
        "get_device_library",
        "prepare_install",
        "commit_install",
        "prepare_delete_device_profile",
        "commit_delete_device_profile",
        "rename_device_profile",
        "open_device_profile",
        "open_device_preferences",
        "start_live_input",
        "stop_live_input",
        "subscribe_devices_changed",
        "unsubscribe_devices_changed",
        "check_for_update",
        "open_external_url",
        "send_feedback",
        "track_telemetry_event",
        "get_pending_crash_report",
        "resolve_crash_report",
        "get_pending_rescue",
        "open_rescue_profile",
        "discard_pending_rescues",
        "list_console_ports",
        "connect_console",
        "close_console",
        "choose_console_profile",
        "console_send_profile",
        "console_strength_begin",
        "console_calibrate",
        "console_set_threshold",
        "console_strength_end",
        "console_record_strength",
        "take_launch_profile_link",
        "open_registry_profile",
        "record_install_cancelled",
        "open_privacy_policy",
        "open_contact_link",
        "subscribe_profile_links",
        "unsubscribe_profile_links",
        "get_controller_options",
        "set_controller_bridge",
        "set_mac_gamepad",
        "subscribe_controller_bridge",
        "unsubscribe_controller_bridge",
        "import_sheets_link",
        "get_follow_state",
        "forget_follow_link",
        "check_latest_from_link",
        "replace_with_latest",
        "restore_drive_backups",
        "list_library_profiles",
        "open_library_profile",
        "delete_library_profile",
        "list_templates",
        "open_template",
        "save_template",
        "rename_template",
        "delete_template",
        "device_location",
        "get_custom_name_drafts",
        "set_custom_name_drafts",
        "subscribe_close_requests",
        "unsubscribe_close_requests",
        "ack_close_request",
        "quit_app",
    ]
}

/// `dev_url` is the host's own `build.devUrl`, only in debug builds, so a
/// host app on another dev port is allowed and nothing else is.
fn navigation_allowed_for(
    url: &tauri::webview::Url,
    dev_url: Option<&tauri::webview::Url>,
) -> bool {
    let packaged = matches!(
        (url.scheme(), url.host_str(), url.port()),
        ("tauri", Some("localhost"), None) | ("http", Some("tauri.localhost"), None)
    );
    if packaged {
        return true;
    }
    dev_url.is_some_and(|dev| {
        url.scheme() == dev.scheme()
            && url.host_str() == dev.host_str()
            && url.port_or_known_default() == dev.port_or_known_default()
    })
}

fn navigation_guard<R: tauri::Runtime>(
    dev_url: Option<tauri::webview::Url>,
) -> tauri::plugin::TauriPlugin<R> {
    tauri::plugin::Builder::new("qcm-navigation-guard")
        .on_navigation(move |_webview, url| navigation_allowed_for(url, dev_url.as_ref()))
        .build()
}

pub fn run() {
    run_with(host::HostConfig::free(), tauri::generate_context!());
}

/// The shell inside a host app, with the host's own context (identifier,
/// icon, assets, capabilities). `run()` is this with the free app's.
///
/// # Panics
///
/// If a host command shadows a free one, or this is called twice.
pub fn run_with(mut config: host::HostConfig, context: tauri::Context<tauri::Wry>) {
    let dev_url = cfg!(debug_assertions)
        .then(|| context.config().build.dev_url.clone())
        .flatten();
    #[cfg(feature = "e2e")]
    e2e::guard();
    if let Err(problem) = config.check() {
        panic!("{problem}");
    }
    host::install(&config);
    diagnostics::install_panic_hook();
    // A `qcm://profile/<id>` link this launch was given (Windows passes it in
    // argv). Read once; the window asks for it after it mounts.
    if config.deep_links {
        registry::remember_launch_args();
    }
    let profiles = shell::native_shell();
    // The saved answer, before any window can ask for anything. Nothing is
    // started unless it is a yes to the current notice and the build has a
    // token. With network off the telemetry kill switch is on.
    profiles.apply_telemetry_consent();
    let builder = tauri::Builder::default();
    // First, so a second launch exits before building anything. With the
    // deep-link feature it forwards the link to `on_open_url` below; this
    // callback only brings the running window forward. The smoke test build
    // leaves it out so it can never hand itself to a real running copy.
    #[cfg(not(feature = "e2e"))]
    let builder = builder.plugin(tauri_plugin_single_instance::init(|app, _argv, _cwd| {
        if let Some(window) = app.get_webview_window("main") {
            let _ = window.unminimize();
            let _ = window.set_focus();
        }
    }));
    let deep_links = config.deep_links;
    let builder = if deep_links {
        builder.plugin(tauri_plugin_deep_link::init())
    } else {
        builder
    };
    // Network off: no Google, Drive, Community or Sheets client exists, and
    // the invoke handler refuses every command that would want one.
    let builder = if !config.network && config.drive_share.is_some() {
        // The Drive share alone: sign-in and Drive, nothing else. The
        // invoke handler lets only the share commands reach them, and only
        // while the host's gate is open.
        let google = Arc::new(
            google_auth::GoogleAuthService::native()
                .expect("failed to build native Google auth client"),
        );
        let drive = Arc::new(
            drive::DriveService::native(Arc::clone(&google))
                .expect("failed to build native Drive client"),
        );
        builder.manage(google).manage(drive)
    } else if config.network {
        let google = Arc::new(
            google_auth::GoogleAuthService::native()
                .expect("failed to build native Google auth client"),
        );
        let drive = Arc::new(
            drive::DriveService::native(Arc::clone(&google))
                .expect("failed to build native Drive client"),
        );
        builder
            .manage(
                community::CommunityService::native()
                    .expect("failed to build native Community HTTP client"),
            )
            .manage(follow_commands::SheetsState::new(Arc::new(
                follow::NativeSheetFetcher::new().expect("failed to build native Sheets client"),
            )))
            .manage(google)
            .manage(drive)
            .manage(Arc::new(drive_auto::DriveBackupHub::default()))
    } else {
        builder
    };
    builder
        .setup(move |app| {
            if deep_links {
                use tauri_plugin_deep_link::DeepLinkExt;
                // `ProtocolRegistration.cs`: a per-user registration at runtime on
                // Windows and Linux. macOS takes it from the bundle. Best effort.
                #[cfg(any(windows, target_os = "linux"))]
                let _ = app.deep_link().register_all();
                app.deep_link().on_open_url(|event| {
                    registry::deliver_links(event.urls().iter().map(tauri::Url::as_str));
                });
            }
            // `StartBridgeIfOn`: left on last time, so on again for this run.
            controller::start_if_on(
                &app.state::<controller::BridgeRuntime>(),
                &app.state::<shell::ShellState>(),
            );
            Ok(())
        })
        .plugin(navigation_guard(dev_url))
        // `MainWindow.Closing`: unsaved work holds the close and the window
        // asks Save / Don't save / Cancel.
        .on_window_event(|window, event| {
            if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                let unsaved = window.state::<shell::ShellState>().has_unsaved_work();
                if window.state::<close_guard::CloseGuard>().hold(unsaved) {
                    api.prevent_close();
                }
            }
        })
        .manage(close_guard::CloseGuard::default())
        .manage(profiles)
        .manage(library_shell::LibraryCards::default())
        .manage(workbook_shell::native_workbook_shell())
        .manage(device_shell::native_device_shell())
        .manage(console_shell::native_console_shell())
        .manage(streaming::LiveRuntime::new())
        .manage(controller::BridgeRuntime::default())
        .manage(streaming::DeviceInvalidationHub::default())
        .invoke_handler(host::invoke_handler(
            &mut config,
            tauri::generate_handler![
                commands::get_app_snapshot,
                commands::get_settings,
                commands::update_settings,
                commands::new_profile,
                commands::choose_and_open_profile,
                commands::get_profile_snapshot,
                commands::apply_editor_ops,
                commands::undo_editor,
                commands::save_profile,
                commands::save_profile_as,
                commands::close_profile,
                commands::choose_and_import_workbook,
                commands::repair_workbook_tab,
                commands::accept_workbook_import,
                commands::cancel_workbook_import,
                commands::export_profile_xlsx,
                commands::get_preference_catalog,
                community_commands::load_community_catalog,
                community_commands::import_community_profile,
                community_commands::open_community_sheet,
                google_commands::get_google_auth_status,
                google_commands::connect_google,
                google_commands::disconnect_google,
                drive_commands::backup_profile_to_drive,
                drive_commands::resolve_drive_conflict,
                drive_commands::list_drive_backups,
                drive_commands::restore_drive_backup,
                drive_commands::restore_drive_backups,
                drive_commands::share_drive_profile,
                drive_auto::subscribe_drive_backups,
                drive_auto::unsubscribe_drive_backups,
                commands::list_devices,
                commands::refresh_devices,
                commands::choose_device_folder,
                commands::get_device_library,
                commands::prepare_install,
                commands::commit_install,
                commands::prepare_delete_device_profile,
                commands::commit_delete_device_profile,
                commands::rename_device_profile,
                commands::open_device_profile,
                commands::open_device_preferences,
                commands::start_live_input,
                commands::stop_live_input,
                commands::subscribe_devices_changed,
                commands::unsubscribe_devices_changed,
                updates::check_for_update,
                updates::open_external_url,
                diagnostics::send_feedback,
                diagnostics::track_telemetry_event,
                diagnostics::get_pending_crash_report,
                diagnostics::resolve_crash_report,
                diagnostics::get_pending_rescue,
                diagnostics::open_rescue_profile,
                diagnostics::discard_pending_rescues,
                console_shell::list_console_ports,
                console_shell::connect_console,
                console_shell::close_console,
                console_shell::choose_console_profile,
                console_shell::console_send_profile,
                console_shell::console_strength_begin,
                console_shell::console_calibrate,
                console_shell::console_set_threshold,
                console_shell::console_strength_end,
                console_shell::console_record_strength,
                registry::take_launch_profile_link,
                registry::open_registry_profile,
                commands::record_install_cancelled,
                updates::open_privacy_policy,
                updates::open_contact_link,
                registry::subscribe_profile_links,
                registry::unsubscribe_profile_links,
                controller::get_controller_options,
                controller::set_controller_bridge,
                follow_commands::import_sheets_link,
                follow_commands::get_follow_state,
                follow_commands::forget_follow_link,
                follow_commands::check_latest_from_link,
                follow_commands::replace_with_latest,
                controller::set_mac_gamepad,
                controller::subscribe_controller_bridge,
                controller::unsubscribe_controller_bridge,
                commands::list_library_profiles,
                commands::open_library_profile,
                commands::delete_library_profile,
                commands::list_templates,
                commands::open_template,
                commands::save_template,
                commands::rename_template,
                commands::delete_template,
                commands::device_location,
                commands::get_custom_name_drafts,
                commands::set_custom_name_drafts,
                close_guard::subscribe_close_requests,
                close_guard::unsubscribe_close_requests,
                close_guard::ack_close_request,
                close_guard::quit_app,
                #[cfg(feature = "e2e")]
                e2e::e2e_log,
                #[cfg(feature = "e2e")]
                e2e::e2e_bring_forward,
                #[cfg(feature = "e2e")]
                e2e::e2e_window_state,
                #[cfg(feature = "e2e")]
                e2e::e2e_request_close,
                #[cfg(feature = "e2e")]
                e2e::e2e_report,
                #[cfg(feature = "e2e")]
                e2e::e2e_exit,
            ],
        ))
        .build(context)
        .expect("failed to start the QuadStick Config Manager window")
        .run(|app, event| {
            // A quit the app did not ask for (the menu's Quit) is held the
            // same way while there is unsaved work.
            if let tauri::RunEvent::ExitRequested {
                code: None, api, ..
            } = &event
            {
                let unsaved = app.state::<shell::ShellState>().has_unsaved_work();
                if app.state::<close_guard::CloseGuard>().hold(unsaved) {
                    api.prevent_exit();
                }
            }
            if let tauri::RunEvent::Exit = event {
                // Every button on the virtual pad is let go before it goes.
                app.state::<controller::BridgeRuntime>().stop();
                // A strength test left running is undone before the port goes.
                app.state::<Arc<console_shell::ConsoleShell>>().close();
                // Push what is queued so a short session is not a silent one.
                telemetry::global().shutdown();
            }
        });
}

#[cfg(test)]
mod tests {
    use tauri::webview::Url;

    #[test]
    fn shell_is_linked_against_the_native_core() {
        assert_eq!(
            super::core_policy(),
            "pure-rust-no-tauri-no-os-no-network-no-filesystem-write"
        );
    }

    #[test]
    fn command_surface_is_auditable_and_keeps_secrets_unaddressable() {
        let commands = super::registered_commands();
        assert_eq!(commands.len(), 97);
        for expected in [
            "import_sheets_link",
            "check_latest_from_link",
            "replace_with_latest",
            "import_community_profile",
            "open_community_sheet",
            "get_google_auth_status",
            "connect_google",
            "disconnect_google",
            "backup_profile_to_drive",
            "resolve_drive_conflict",
            "list_drive_backups",
            "restore_drive_backup",
            "share_drive_profile",
            "prepare_install",
            "commit_install",
            "start_live_input",
            "check_for_update",
            "open_external_url",
            "send_feedback",
            "track_telemetry_event",
            "get_pending_crash_report",
            "resolve_crash_report",
            "get_pending_rescue",
            "open_rescue_profile",
            "discard_pending_rescues",
        ] {
            assert!(commands.contains(&expected), "{expected}");
        }
        for absent in [
            "emit_live_frame",
            "listen_live_frame",
            "reorder_device_profiles",
            "get_google_access_token",
            "get_google_refresh_token",
            "get_drive_file_id",
            "drive_raw_request",
            "open_arbitrary_url",
            "install_update",
        ] {
            assert!(!commands.contains(&absent), "{absent}");
        }
        assert!(
            commands
                .iter()
                .all(|command| !command.starts_with("plugin:"))
        );
    }

    #[test]
    fn packaged_navigation_is_local_only() {
        for allowed in [
            "tauri://localhost/index.html",
            "tauri://localhost/editor?session=session-1",
            "http://tauri.localhost/index.html",
        ] {
            let url = Url::parse(allowed).expect("valid app URL");
            assert!(super::navigation_allowed_for(&url, None), "{allowed}");
        }
        for forbidden in [
            "https://example.com/",
            "http://example.com/",
            "file:///tmp/profile.html",
            "data:text/html,hello",
            "http://localhost:1420/",
            "http://tauri.localhost:8080/index.html",
            "https://tauri.localhost/index.html",
        ] {
            let url = Url::parse(forbidden).expect("valid forbidden URL");
            assert!(!super::navigation_allowed_for(&url, None), "{forbidden}");
        }
    }

    #[test]
    fn development_navigation_allows_only_the_host_dev_origin() {
        let dev = Url::parse("http://localhost:1420").expect("valid dev URL");
        let vite = Url::parse("http://localhost:1420/editor").expect("valid Vite URL");
        assert!(super::navigation_allowed_for(&vite, Some(&dev)));
        let clinic = Url::parse("http://localhost:1430").expect("valid dev URL");
        let page = Url::parse("http://localhost:1430/").expect("valid URL");
        assert!(super::navigation_allowed_for(&page, Some(&clinic)));
        assert!(!super::navigation_allowed_for(&vite, Some(&clinic)));
        for forbidden in [
            "http://localhost:1421/",
            "https://localhost:1420/",
            "http://127.0.0.1:1420/",
            "https://example.com/",
        ] {
            let url = Url::parse(forbidden).expect("valid forbidden URL");
            assert!(
                !super::navigation_allowed_for(&url, Some(&dev)),
                "{forbidden}"
            );
        }
    }
}
