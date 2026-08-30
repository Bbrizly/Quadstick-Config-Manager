//! The desktop shell.
//!
//! This crate owns the window, the adapters and the command surface. The
//! profile and settings commands are registered; the device, live-input and
//! event surfaces arrive with TASK-033 and TASK-034.
//!
//! [`adapters`] is the only place in the app that sees a path. `qcm-core` holds
//! the rules, this crate holds the operating system, and the ports between them
//! carry opaque ids and validated names in both directions.
//!
//! The window is granted no plugin permission at all, so there is no generic
//! filesystem, shell or network call for it to make. The file picker is a plain
//! Rust dialog opened from inside a command, not a plugin the window can reach.

pub mod adapters;
pub mod commands;
pub mod ipc;
pub mod shell;

/// The promise `qcm-core` makes about itself, read through the linked crate.
///
/// The point is the link, not the string. If the shell ever stops depending on
/// the native core this stops compiling.
#[must_use]
pub fn core_policy() -> &'static str {
    qcm_core::CORE_CRATE_POLICY
}

/// Every command this build registers.
///
/// One list, so the API ledger and the capability audit have a single thing to
/// read. TASK-033 and TASK-034 append to it; nothing else may.
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
    ]
}

/// Builds and runs the window. Panics if the WebView cannot be created, which
/// is unrecoverable: there is no UI left to report it in.
pub fn run() {
    tauri::Builder::default()
        .manage(shell::native_shell())
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
}
