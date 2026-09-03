//! The user's own profile folder, reached through the filesystem.
//!
//! The adapter owns paths and turns them into opaque refs. `persistent_key` is
//! native-only and exists so Drive link state can survive restarts without ever
//! exposing the path to the WebView.

use crate::adapters::storage::{is_quadstick, map_io};
use qcm_core::error::{StorageError, StorageStage, TargetState};
use qcm_core::ports::local::{
    LocalProfileId, LocalProfileRef, LocalProfileStore, LocalWriteReceipt, ProfileDisplayName,
};
use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, MutexGuard, PoisonError};

const TEMP_SUFFIX: &str = ".qscm-tmp";

#[derive(Debug, Default)]
struct Table {
    files: BTreeMap<u64, PathBuf>,
    next: u64,
}

#[derive(Debug)]
pub struct FileSystemProfileLibrary<V> {
    volumes: V,
    table: Mutex<Table>,
}

impl<V> FileSystemProfileLibrary<V> {
    pub const fn new(volumes: V) -> Self {
        Self {
            volumes,
            table: Mutex::new(Table { files: BTreeMap::new(), next: 0 }),
        }
    }

    fn table(&self) -> MutexGuard<'_, Table> {
        self.table.lock().unwrap_or_else(PoisonError::into_inner)
    }

    pub fn adopt(&self, path: &Path) -> LocalProfileRef {
        let display = path.file_name().and_then(|name| name.to_str()).map_or_else(
            || ProfileDisplayName::new(""),
            ProfileDisplayName::new,
        );
        let mut table = self.table();
        table.next = table.next.saturating_add(1);
        let id = table.next;
        table.files.insert(id, path.to_path_buf());
        LocalProfileRef::new(LocalProfileId::from_raw(id), display)
    }

    fn path_of(&self, target: &LocalProfileRef, stage: StorageStage) -> Result<PathBuf, StorageError> {
        self.table().files.get(&target.id().raw()).cloned().ok_or(StorageError::Io {
            stage,
            target: TargetState::Unchanged,
            detail: qcm_core::error::OsDetail::new("unknown local profile id".to_owned()),
        })
    }
}

impl<V: crate::adapters::storage::volumes::VolumeSource> LocalProfileStore for FileSystemProfileLibrary<V> {
    fn read(&self, target: &LocalProfileRef) -> Result<String, StorageError> {
        let path = self.path_of(target, StorageStage::ReadFile)?;
        fs::read_to_string(&path).map_err(|error| map_io(&error, StorageStage::ReadFile, TargetState::Unchanged))
    }

    fn write(&self, target: &LocalProfileRef, text: &str) -> Result<LocalWriteReceipt, StorageError> {
        let path = self.path_of(target, StorageStage::TempCreate)?;
        let temp = temp_beside(&path);
        fs::write(&temp, text.as_bytes()).map_err(|error| {
            let _ = fs::remove_file(&temp);
            map_io(&error, StorageStage::TempWrite, TargetState::Unchanged)
        })?;
        fs::rename(&temp, &path).map_err(|error| {
            let _ = fs::remove_file(&temp);
            map_io(&error, StorageStage::ReplaceBeforeDisplace, TargetState::Unchanged)
        })?;
        Ok(LocalWriteReceipt { bytes: text.len() })
    }

    fn is_on_quadstick(&self, target: &LocalProfileRef) -> Result<bool, StorageError> {
        let path = self.path_of(target, StorageStage::Revalidate)?;
        Ok(self
            .volumes
            .candidate_roots()
            .iter()
            .any(|root| path.starts_with(root) && is_quadstick(root)))
    }

    fn persistent_key(&self, target: &LocalProfileRef) -> Result<String, StorageError> {
        let path = self.path_of(target, StorageStage::ReadFile)?;
        let stable = fs::canonicalize(&path).unwrap_or(path);
        Ok(stable.to_string_lossy().into_owned())
    }
}

fn temp_beside(path: &Path) -> PathBuf {
    let mut name = path.file_name().unwrap_or_default().to_os_string();
    name.push(TEMP_SUFFIX);
    path.with_file_name(name)
}
