//! Saved settings, in memory.
//!
//! The one thing a real settings file cannot be made to do on demand is fail to
//! write. That is the case that matters: a save that never reached disk must
//! not leave a window showing the setting anyway.

use qcm_core::error::{StorageError, StorageStage};
use qcm_core::settings::{AppSettings, SettingsStore};
use std::sync::{Mutex, MutexGuard, PoisonError};

#[derive(Debug, Default)]
struct State {
    saved: Option<AppSettings>,
    /// Set when the file on disk is there but unreadable, which the port
    /// reports the same way as nothing saved yet.
    unreadable: bool,
    fail_next_save: bool,
    saves: usize,
}

#[derive(Debug, Default)]
pub struct FakeSettingsFile {
    state: Mutex<State>,
}

impl FakeSettingsFile {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// A file that is already there with these settings in it.
    #[must_use]
    pub fn with(settings: AppSettings) -> Self {
        let file = Self::default();
        file.state().saved = Some(settings);
        file
    }

    /// A file that exists and cannot be parsed.
    #[must_use]
    pub fn unreadable() -> Self {
        let file = Self::default();
        file.state().unreadable = true;
        file
    }

    fn state(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }

    #[must_use]
    pub fn saved(&self) -> Option<AppSettings> {
        self.state().saved.clone()
    }

    #[must_use]
    pub fn saves(&self) -> usize {
        self.state().saves
    }

    pub fn fail_next_save(&self) {
        self.state().fail_next_save = true;
    }
}

impl SettingsStore for FakeSettingsFile {
    fn load(&self) -> Option<AppSettings> {
        let state = self.state();
        if state.unreadable {
            return None;
        }
        state.saved.clone()
    }

    fn save(&self, settings: &AppSettings) -> Result<(), StorageError> {
        let mut state = self.state();
        if state.fail_next_save {
            state.fail_next_save = false;
            return Err(StorageError::PermissionDenied {
                stage: StorageStage::TempWrite,
            });
        }
        state.saved = Some(settings.clone());
        state.saves += 1;
        Ok(())
    }
}
