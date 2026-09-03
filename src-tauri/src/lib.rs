//! The desktop shell.
//!
//! This crate owns the window, native adapters and the narrow command surface.
//! Paths, drive letters, HID paths and OS handles stop at adapters; the WebView
//! sees typed snapshots, opaque ids and caller-scoped channels only.

pub mod adapters;
pub mod commands;
pub mod community;
pub mod community_commands;
pub mod device_ipc;
pub mod device_rename_ipc;
pub mod device_shell;
pub mod ipc;
pub mod preference_ipc;
pub mod shell;
pub mod streaming;
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
    ]
}

fn navigation_allowed_for(url: &tauri::webview::Url, development: bool) -> bool {
    let packaged = matches!(
        (url.scheme(), url.host_str(), url.port()),
        ("tauri", Some("localhost"), None) | ("http", Some("tauri.localhost"), None)
    );
    if packaged {
        return true;
    }
    development
        && url.scheme() == "http"
        && url.host_str() == Some("localhost")
        && url.port() == Some(1420)
}

fn navigation_guard<R: tauri::Runtime>() -> tauri::plugin::TauriPlugin<R> {
    tauri::plugin::Builder::new("qcm-navigation-guard")
        .on_navigation(|_webview, url| navigation_allowed_for(url, cfg!(debug_assertions)))
        .build()
}

pub fn run() {
    tauri::Builder::default()
        .plugin(navigation_guard())
        .manage(shell::native_shell())
        .manage(workbook_shell::native_workbook_shell())
        .manage(device_shell::native_device_shell())
        .manage(
            community::CommunityService::native()
                .expect("failed to build the native Community HTTP client"),
        )
        .manage(streaming::LiveRuntime::new())
        .manage(streaming::DeviceInvalidationHub::default())
        .invoke_handler(tauri::generate_handler![
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
        ])
        .run(tauri::generate_context!())
        .expect("failed to start the QuadStick Config Manager window");
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
    fn command_surface_is_auditable_and_contains_no_global_event_api() {
        let commands = super::registered_commands();
        assert_eq!(commands.len(), 34);
        for expected in [
            "choose_and_import_workbook",
            "repair_workbook_tab",
            "accept_workbook_import",
            "cancel_workbook_import",
            "export_profile_xlsx",
            "get_preference_catalog",
            "load_community_catalog",
            "import_community_profile",
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
        ] {
            assert!(commands.contains(&expected), "{expected}");
        }
        for absent in ["emit_live_frame", "listen_live_frame", "reorder_device_profiles"] {
            assert!(!commands.contains(&absent), "{absent}");
        }
        assert!(commands.iter().all(|command| !command.starts_with("plugin:")));
    }

    #[test]
    fn packaged_navigation_is_local_only() {
        for allowed in [
            "tauri://localhost/index.html",
            "tauri://localhost/editor?session=session-1",
            "http://tauri.localhost/index.html",
        ] {
            let url = Url::parse(allowed).expect("valid app URL");
            assert!(super::navigation_allowed_for(&url, false), "{allowed}");
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
            assert!(!super::navigation_allowed_for(&url, false), "{forbidden}");
        }
        let spelled_out = "http://tauri.localhost:80/index.html";
        let url = Url::parse(spelled_out).expect("valid URL");
        assert_eq!(url.port(), None);
        assert!(super::navigation_allowed_for(&url, false));
    }

    #[test]
    fn development_navigation_allows_only_the_pinned_vite_origin() {
        let vite = Url::parse("http://localhost:1420/").expect("valid Vite URL");
        assert!(super::navigation_allowed_for(&vite, true));
        for forbidden in [
            "http://localhost:1421/",
            "https://localhost:1420/",
            "http://127.0.0.1:1420/",
            "https://example.com/",
        ] {
            let url = Url::parse(forbidden).expect("valid forbidden URL");
            assert!(!super::navigation_allowed_for(&url, true), "{forbidden}");
        }
    }
}
