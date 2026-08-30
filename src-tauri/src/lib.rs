//! The desktop shell.
//!
//! This crate owns the window, native adapters and the narrow command surface.
//! Paths, drive letters, HID paths and OS handles stop at adapters; the WebView
//! sees typed snapshots, opaque ids and caller-scoped channels only.

pub mod adapters;
pub mod commands;
pub mod device_ipc;
pub mod device_shell;
pub mod ipc;
pub mod shell;
pub mod streaming;

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
        "apply_editor_ops",
        "undo_editor",
        "save_profile",
        "save_profile_as",
        "close_profile",
        "list_devices",
        "refresh_devices",
        "choose_device_folder",
        "get_device_library",
        "prepare_install",
        "commit_install",
        "prepare_delete_device_profile",
        "commit_delete_device_profile",
        "open_device_profile",
        "open_device_preferences",
        "start_live_input",
        "stop_live_input",
        "subscribe_devices_changed",
        "unsubscribe_devices_changed",
    ]
}

pub fn run() {
    tauri::Builder::default()
        .manage(shell::native_shell())
        .manage(device_shell::native_device_shell())
        .manage(streaming::LiveRuntime::new())
        .manage(streaming::DeviceInvalidationHub::default())
        .invoke_handler(tauri::generate_handler![
            commands::get_app_snapshot,
            commands::get_settings,
            commands::update_settings,
            commands::new_profile,
            commands::choose_and_open_profile,
            commands::apply_editor_ops,
            commands::undo_editor,
            commands::save_profile,
            commands::save_profile_as,
            commands::close_profile,
            commands::list_devices,
            commands::refresh_devices,
            commands::choose_device_folder,
            commands::get_device_library,
            commands::prepare_install,
            commands::commit_install,
            commands::prepare_delete_device_profile,
            commands::commit_delete_device_profile,
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
        assert_eq!(commands.len(), 24);
        for expected in [
            "list_devices",
            "refresh_devices",
            "choose_device_folder",
            "get_device_library",
            "prepare_install",
            "commit_install",
            "prepare_delete_device_profile",
            "commit_delete_device_profile",
            "open_device_profile",
            "open_device_preferences",
            "start_live_input",
            "stop_live_input",
            "subscribe_devices_changed",
            "unsubscribe_devices_changed",
        ] {
            assert!(commands.contains(&expected), "{expected}");
        }
        for absent in [
            "emit_live_frame",
            "listen_live_frame",
            "rename_device_profile",
            "reorder_device_profiles",
        ] {
            assert!(!commands.contains(&absent), "{absent}");
        }
    }
}
