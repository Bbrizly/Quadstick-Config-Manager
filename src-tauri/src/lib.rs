//! The desktop shell.
//!
//! This crate owns the window and nothing else yet. No commands, no adapters,
//! no device code: TASK-031 adds the client contracts and TASK-032 the commands
//! that sit behind them. `qcm-core` is already linked so the dependency
//! direction is fixed from the first build, not retrofitted.

/// The promise `qcm-core` makes about itself, read through the linked crate.
///
/// The point is the link, not the string. If the shell ever stops depending on
/// the native core this stops compiling.
#[must_use]
pub fn core_policy() -> &'static str {
    qcm_core::CORE_CRATE_POLICY
}

/// Builds and runs the window. Panics if the WebView cannot be created, which
/// is unrecoverable: there is no UI left to report it in.
pub fn run() {
    tauri::Builder::default()
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
