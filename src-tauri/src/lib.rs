//! The desktop shell.
//!
//! This crate owns the window, native adapters and the narrow command surface.
//! Device operations are now registered through TASK-033; live-input channels
//! arrive with TASK-034.
//!
//! [`adapters`] is the only place in the app that sees a path. `qcm-core` holds
//! the rules, this crate holds the operating system, and the ports between them
//! carry opaque ids and validated names in both directions.

pub mod adapters;
pub mod commands;
pub mod device_ipc;
pub mod device_shell;
pub mod ipc;
pub mod shell;

#[must_use]
pub fn core_policy() -> &'static str {
    qcm_core::CORE_CRATE_POLICY
}

/// Every command this build registers.
///
/// Kept as one audit list so the capability surface cannot silently grow.
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
    ]
}

pub fn run() {
    tauri::Builder::default()
        .manage(shell::native_shell())
        .manage(device_shell::native_device_shell())
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
    fn device_commands_are_auditable_as_one_surface() {
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
        ] {
            assert!(super::registered_commands().contains(&expected));
        }
        assert!(!super::registered_commands().contains(&"rename_device_profile"));
        assert!(!super::registered_commands().contains(&"reorder_device_profiles"));
    }
}
