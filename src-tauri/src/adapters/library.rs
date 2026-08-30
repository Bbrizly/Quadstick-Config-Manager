//! The user's own profile folder, reached through the filesystem.
//!
//! The second place in the app that sees a path, and the table that turns an
//! opaque id back into one lives here. A file gets an id when the user picks it
//! in a dialog; nothing above this file can mint one, so nothing above it can
//! name a place on the machine to read or write.
//!
//! Writing is the legacy `ProfileFile.WriteAtomic`: write beside the target,
//! rename over it, clean up the scratch file. The device install is a stronger
//! transaction with a backup and a read-back, and OQ-004 keeps the question of
//! whether local save should grow one open. Until it is answered, this is
//! parity.

use crate::adapters::storage::{is_quadstick, map_io};
use qcm_core::error::{StorageError, StorageStage, TargetState};
use qcm_core::ports::local::{
    LocalProfileId, LocalProfileRef, LocalProfileStore, LocalWriteReceipt, ProfileDisplayName,
};
use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, MutexGuard, PoisonError};

/// The scratch name a half-written save lives under.
const TEMP_SUFFIX: &str = ".qscm-tmp";

#[derive(Debug, Default)]
struct Table {
    files: BTreeMap<u64, PathBuf>,
    next: u64,
}

/// Files on this computer, addressed by opaque id.
///
/// `V` is where the list of mounted volumes comes from, so a test can say which
/// roots exist instead of depending on what is plugged into the machine running
/// it.
#[derive(Debug)]
pub struct FileSystemProfileLibrary<V> {
    volumes: V,
    table: Mutex<Table>,
}

impl<V> FileSystemProfileLibrary<V> {
    pub const fn new(volumes: V) -> Self {
        Self {
            volumes,
            table: Mutex::new(Table {
                files: BTreeMap::new(),
                next: 0,
            }),
        }
    }

    /// A poisoned lock means a thread panicked holding it. The table is still
    /// what it was, and a second panic on top only buries the first.
    fn table(&self) -> MutexGuard<'_, Table> {
        self.table.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// Take on a file the user just chose, and hand back the only name for it
    /// anything above this crate is allowed to hold.
    ///
    /// The same path picked twice gets a second id. That is deliberate: ids are
    /// identity for one open profile, not a cache key, and reusing one would
    /// make a command that arrived late for a closed profile land on a new one.
    pub fn adopt(&self, path: &Path) -> LocalProfileRef {
        let display = path
            .file_name()
            .and_then(|name| name.to_str())
            .map_or_else(|| ProfileDisplayName::new(""), ProfileDisplayName::new);
        let mut table = self.table();
        table.next = table.next.saturating_add(1);
        let id = table.next;
        table.files.insert(id, path.to_path_buf());
        LocalProfileRef::new(LocalProfileId::from_raw(id), display)
    }

    fn path_of(
        &self,
        target: &LocalProfileRef,
        stage: StorageStage,
    ) -> Result<PathBuf, StorageError> {
        self.table()
            .files
            .get(&target.id().raw())
            .cloned()
            .ok_or(StorageError::Io {
                stage,
                target: TargetState::Unchanged,
                // No name, no path: an id this library never handed out came
                // from something forging one, and echoing it back is how a
                // probe learns what it guessed.
                detail: qcm_core::error::OsDetail::new("unknown local profile id".to_owned()),
            })
    }
}

impl<V: crate::adapters::storage::volumes::VolumeSource> LocalProfileStore
    for FileSystemProfileLibrary<V>
{
    fn read(&self, target: &LocalProfileRef) -> Result<String, StorageError> {
        let path = self.path_of(target, StorageStage::ReadFile)?;
        fs::read_to_string(&path)
            .map_err(|error| map_io(&error, StorageStage::ReadFile, TargetState::Unchanged))
    }

    fn write(
        &self,
        target: &LocalProfileRef,
        text: &str,
    ) -> Result<LocalWriteReceipt, StorageError> {
        let path = self.path_of(target, StorageStage::TempCreate)?;
        let temp = temp_beside(&path);

        fs::write(&temp, text.as_bytes()).map_err(|error| {
            // The scratch file may or may not exist; either way it is not the
            // user's file, so a failed cleanup is not worth a second error.
            let _ = fs::remove_file(&temp);
            map_io(&error, StorageStage::TempWrite, TargetState::Unchanged)
        })?;

        // Rename over the target rather than writing into it. A crash mid-write
        // would otherwise leave half a profile where a whole one was.
        fs::rename(&temp, &path).map_err(|error| {
            let _ = fs::remove_file(&temp);
            map_io(
                &error,
                StorageStage::ReplaceBeforeDisplace,
                TargetState::Unchanged,
            )
        })?;

        Ok(LocalWriteReceipt { bytes: text.len() })
    }

    fn is_on_quadstick(&self, target: &LocalProfileRef) -> Result<bool, StorageError> {
        let path = self.path_of(target, StorageStage::Revalidate)?;
        // Asked against the mounted volumes rather than by walking up looking
        // for a marker: somebody with a `default.csv` in their home directory
        // would otherwise be told their whole library is a QuadStick.
        Ok(self
            .volumes
            .candidate_roots()
            .iter()
            .any(|root| path.starts_with(root) && is_quadstick(root)))
    }
}

fn temp_beside(path: &Path) -> PathBuf {
    let mut name = path.file_name().unwrap_or_default().to_os_string();
    name.push(TEMP_SUFFIX);
    path.with_file_name(name)
}
