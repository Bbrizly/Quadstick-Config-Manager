//! The user's own profile folder, in memory.
//!
//! The session manager must never be tested against a real directory: a test
//! that writes to disk cannot make a write fail on demand, and a test that
//! makes one fail by breaking a real path is testing the machine. This fake
//! models the three things the local store port promises and nothing else: a
//! file has text, a write either lands whole or does not land, and a target can
//! turn out to be sitting on a QuadStick.

use qcm_core::error::{OsDetail, StorageError, StorageStage, TargetState};
use qcm_core::ports::local::{
    LocalProfileId, LocalProfileRef, LocalProfileStore, LocalWriteReceipt, ProfileDisplayName,
};
use std::collections::BTreeMap;
use std::sync::{Mutex, MutexGuard, PoisonError};

#[derive(Debug)]
struct Entry {
    /// `None` for a place the user has named but nothing has been written to
    /// yet, which is what a Save As target is until the save lands.
    text: Option<String>,
    on_quadstick: bool,
    writes: usize,
}

#[derive(Debug, Default)]
struct LibraryState {
    files: BTreeMap<u64, Entry>,
    next: u64,
    fail_next_read: bool,
    fail_next_write: bool,
}

/// A profile library that lives in memory and does what it is told.
#[derive(Debug, Default)]
pub struct FakeProfileLibrary {
    state: Mutex<LibraryState>,
}

impl FakeProfileLibrary {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    fn state(&self) -> MutexGuard<'_, LibraryState> {
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn put(&self, name: &str, text: Option<String>) -> LocalProfileRef {
        let mut state = self.state();
        state.next += 1;
        let id = state.next;
        state.files.insert(
            id,
            Entry {
                text,
                on_quadstick: false,
                writes: 0,
            },
        );
        LocalProfileRef::new(LocalProfileId::from_raw(id), ProfileDisplayName::new(name))
    }

    /// A file that is already there.
    pub fn add(&self, name: &str, text: &str) -> LocalProfileRef {
        self.put(name, Some(text.to_owned()))
    }

    /// A place the user has just named in a Save As. Nothing is there yet.
    pub fn slot(&self, name: &str) -> LocalProfileRef {
        self.put(name, None)
    }

    /// A reference to something this library never handed out, for proving that
    /// a stale target fails instead of writing somewhere.
    #[must_use]
    pub fn unknown(&self, name: &str) -> LocalProfileRef {
        LocalProfileRef::new(
            LocalProfileId::from_raw(u64::MAX),
            ProfileDisplayName::new(name),
        )
    }

    #[must_use]
    pub fn text(&self, target: &LocalProfileRef) -> Option<String> {
        self.state()
            .files
            .get(&target.id().raw())
            .and_then(|entry| entry.text.clone())
    }

    #[must_use]
    pub fn writes(&self, target: &LocalProfileRef) -> usize {
        self.state()
            .files
            .get(&target.id().raw())
            .map_or(0, |entry| entry.writes)
    }

    /// Put this target on a mounted QuadStick, the way plugging a stick in
    /// turns a folder the user picked yesterday into the device.
    pub fn set_on_quadstick(&self, target: &LocalProfileRef, on_quadstick: bool) {
        if let Some(entry) = self.state().files.get_mut(&target.id().raw()) {
            entry.on_quadstick = on_quadstick;
        }
    }

    pub fn fail_next_read(&self) {
        self.state().fail_next_read = true;
    }

    /// The next write fails with nothing written, so a caller can prove it
    /// keeps the user's work rather than reporting a save that never happened.
    pub fn fail_next_write(&self) {
        self.state().fail_next_write = true;
    }
}

fn gone(name: &str, stage: StorageStage) -> StorageError {
    StorageError::Io {
        stage,
        target: TargetState::Unchanged,
        detail: OsDetail::new(format!("no such profile in the fake library: {name}")),
    }
}

impl LocalProfileStore for FakeProfileLibrary {
    fn read(&self, target: &LocalProfileRef) -> Result<String, StorageError> {
        let mut state = self.state();
        if state.fail_next_read {
            state.fail_next_read = false;
            return Err(StorageError::PermissionDenied {
                stage: StorageStage::ReadFile,
            });
        }
        state
            .files
            .get(&target.id().raw())
            .and_then(|entry| entry.text.clone())
            .ok_or_else(|| gone(target.display_name().as_str(), StorageStage::ReadFile))
    }

    fn write(
        &self,
        target: &LocalProfileRef,
        text: &str,
    ) -> Result<LocalWriteReceipt, StorageError> {
        let mut state = self.state();
        if state.fail_next_write {
            state.fail_next_write = false;
            return Err(StorageError::Full {
                stage: StorageStage::TempWrite,
                target: TargetState::Unchanged,
            });
        }
        let Some(entry) = state.files.get_mut(&target.id().raw()) else {
            return Err(gone(
                target.display_name().as_str(),
                StorageStage::TempCreate,
            ));
        };
        entry.text = Some(text.to_owned());
        entry.writes += 1;
        Ok(LocalWriteReceipt { bytes: text.len() })
    }

    fn is_on_quadstick(&self, target: &LocalProfileRef) -> Result<bool, StorageError> {
        Ok(self
            .state()
            .files
            .get(&target.id().raw())
            .is_some_and(|entry| entry.on_quadstick))
    }
}
