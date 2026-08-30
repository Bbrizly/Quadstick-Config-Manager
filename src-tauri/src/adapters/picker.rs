//! The file picker.
//!
//! Command-internal on purpose. The window has no dialog permission and no way
//! to ask for one: `capabilities/main.json` grants nothing, and this is a plain
//! Rust dialog rather than a Tauri plugin, so the only thing that can open it is
//! a command in this crate. What comes back is a path, and it never leaves the
//! adapter: the library mints an opaque id for it and that is what travels.
//!
//! Cancelling is a result, not a failure. `None` means the user pressed Escape,
//! and every caller has to leave the profile exactly as it was.

use crate::adapters::library::FileSystemProfileLibrary;
use crate::adapters::storage::volumes::VolumeSource;
use qcm_core::QcmError;
use qcm_core::ports::local::LocalProfileRef;
use std::sync::Arc;

/// Asking the user which file.
pub trait ProfilePicker: Send + Sync {
    /// Which profile to open. `None` is a cancel.
    fn pick_open(&self) -> Result<Option<LocalProfileRef>, QcmError>;

    /// Where to save. `None` is a cancel.
    ///
    /// `suggested` is a display name the caller already has, never a path, so
    /// this cannot be talked into pointing at a directory of someone's choosing.
    fn pick_save_as(&self, suggested: &str) -> Result<Option<LocalProfileRef>, QcmError>;
}

/// One picker, many owners. The shell holds it and a test holds the same one
/// to say what the user did next.
impl<T: ProfilePicker + ?Sized> ProfilePicker for Arc<T> {
    fn pick_open(&self) -> Result<Option<LocalProfileRef>, QcmError> {
        (**self).pick_open()
    }

    fn pick_save_as(&self, suggested: &str) -> Result<Option<LocalProfileRef>, QcmError> {
        (**self).pick_save_as(suggested)
    }
}

/// The real dialog.
///
/// It holds the library because a picked file has to become an opaque id
/// immediately. Handing the path up first, even for one function call, would
/// put a path in a layer whose whole claim is that it cannot hold one.
pub struct NativeProfilePicker<V> {
    library: Arc<FileSystemProfileLibrary<V>>,
}

impl<V> NativeProfilePicker<V> {
    pub const fn new(library: Arc<FileSystemProfileLibrary<V>>) -> Self {
        Self { library }
    }
}

impl<V: VolumeSource + Send + Sync> ProfilePicker for NativeProfilePicker<V> {
    fn pick_open(&self) -> Result<Option<LocalProfileRef>, QcmError> {
        // Blocking, and the commands are synchronous, so this runs on the main
        // thread where a native modal belongs.
        let chosen = rfd::FileDialog::new()
            .set_title("Open profile")
            .add_filter("QuadStick profile", &["csv"])
            .pick_file();
        Ok(chosen.map(|path| self.library.adopt(&path)))
    }

    fn pick_save_as(&self, suggested: &str) -> Result<Option<LocalProfileRef>, QcmError> {
        let chosen = rfd::FileDialog::new()
            .set_title("Save profile as")
            .set_file_name(suggested)
            .add_filter("QuadStick profile", &["csv"])
            .save_file();
        Ok(chosen.map(|path| self.library.adopt(&path)))
    }
}
