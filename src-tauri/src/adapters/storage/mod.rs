//! The storage adapter: the only thing in the app that sees a mount point.
//!
//! Everything above this file names a device by an opaque id and a file by a
//! validated direct-child name. The table that turns an id back into a path
//! lives here and goes no further, so nothing above it, including a compromised
//! window, can name a place on the machine to read or write.
//!
//! The safety reasoning is in `qcm-core`. This is the part that has to be
//! careful about the filesystem: write beside the target and rename, never over
//! it; look before saying what happened after a rename went wrong; never leave a
//! `.qscm-tmp` behind.

pub mod volumes;

use qcm_core::error::{DeviceError, OsDetail, StorageError, StorageStage, TargetState};
use qcm_core::ports::storage::{
    BackupReceipt, BackupStore, CommitFailure, DeviceFileEntry, DeviceFileName, DeviceGeneration,
    DeviceListing, DeviceStorage, MARKER_FILE_NAME, SafeDeviceFileName, StagedWrite,
    StorageCapabilities, StorageDeviceId, StorageProbe, check_deletable, check_generation,
};
use qcm_core::{BackupLocationDisplay, DeviceDisplayName};
use std::collections::BTreeMap;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, MutexGuard, PoisonError};
use volumes::{PlatformVolumes, VolumeSource};

/// The file that proves a volume is a QuadStick.
pub(crate) const MARKER: &str = MARKER_FILE_NAME;

/// The suffix the shipped device layer used, kept so a drive that still has one
/// on it from an older version is recognizable.
const TEMP_SUFFIX: &str = ".qscm-tmp";

/// Where a rescue copy is written before it is moved into place.
const RESTORE_SUFFIX: &str = ".qscm-restore";

#[derive(Debug)]
struct Mounted {
    id: StorageDeviceId,
    root: PathBuf,
    label: String,
    generation: DeviceGeneration,
    /// False once the mount has gone. The entry is kept so the same drive coming
    /// back keeps its id, at a new generation.
    present: bool,
}

#[derive(Debug)]
struct Staged {
    device: StorageDeviceId,
    path: PathBuf,
}

#[derive(Debug, Default)]
struct Registry {
    devices: Vec<Mounted>,
    staged: BTreeMap<u64, Staged>,
    next_id: u64,
    next_generation: u64,
    next_token: u64,
}

/// A real QuadStick drive, reached through the filesystem.
#[derive(Debug)]
pub struct FileSystemDeviceStorage<V: VolumeSource> {
    volumes: V,
    registry: Mutex<Registry>,
}

impl Default for FileSystemDeviceStorage<PlatformVolumes> {
    fn default() -> Self {
        Self::new(PlatformVolumes)
    }
}

impl<V: VolumeSource> FileSystemDeviceStorage<V> {
    #[must_use]
    pub fn new(volumes: V) -> Self {
        Self {
            volumes,
            registry: Mutex::new(Registry::default()),
        }
    }

    /// A poisoned lock means a thread panicked while holding it. The table is
    /// still what it was, and a second panic on top only buries the first.
    fn registry(&self) -> MutexGuard<'_, Registry> {
        self.registry.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

impl Registry {
    /// The root behind an id, proven to still be a mounted QuadStick.
    fn root_of(&self, device: StorageDeviceId) -> Result<&Mounted, StorageError> {
        let found = self
            .devices
            .iter()
            .find(|mounted| mounted.id == device && mounted.present)
            .ok_or(StorageError::Device(DeviceError::NotFound))?;
        if !is_quadstick(&found.root) {
            return Err(StorageError::Device(DeviceError::NotQuadStick));
        }
        Ok(found)
    }

    fn scoped(
        &self,
        device: StorageDeviceId,
        expected: DeviceGeneration,
    ) -> Result<&Path, StorageError> {
        let found = self.root_of(device)?;
        check_generation(expected, found.generation)?;
        Ok(&found.root)
    }
}

impl Mounted {
    const fn new(
        id: StorageDeviceId,
        root: PathBuf,
        label: String,
        generation: DeviceGeneration,
    ) -> Self {
        Self {
            id,
            root,
            label,
            generation,
            present: true,
        }
    }
}

fn is_quadstick(root: &Path) -> bool {
    root.join(MARKER).is_file()
}

/// The label a window may print: the volume's own folder name, never its path.
fn label_of(root: &Path) -> String {
    root.file_name()
        .and_then(|name| name.to_str())
        .map_or_else(|| "QuadStick drive".to_owned(), ToOwned::to_owned)
}

fn free_bytes(_root: &Path) -> Option<u64> {
    // std has no way to ask, and every crate that can needs an unsafe call this
    // workspace forbids. `None` is the honest answer and the core already treats
    // it as "will not say" rather than as zero.
    None
}

fn writable(root: &Path) -> bool {
    !fs::metadata(root)
        .map(|meta| meta.permissions().readonly())
        .unwrap_or(true)
}

/// Map an I/O failure onto the family that decides what the user can do next.
fn map_io(error: &io::Error, stage: StorageStage, target: TargetState) -> StorageError {
    match error.kind() {
        io::ErrorKind::PermissionDenied => StorageError::PermissionDenied { stage },
        io::ErrorKind::StorageFull | io::ErrorKind::QuotaExceeded => {
            StorageError::Full { stage, target }
        }
        io::ErrorKind::NotFound => StorageError::Io {
            stage,
            target,
            detail: OsDetail::new(error.to_string()),
        },
        _ => StorageError::Io {
            stage,
            target,
            detail: OsDetail::new(error.to_string()),
        },
    }
}

impl<V: VolumeSource> DeviceStorage for FileSystemDeviceStorage<V> {
    fn discover(&self) -> Result<Vec<StorageProbe>, StorageError> {
        let roots: Vec<PathBuf> = self
            .volumes
            .candidate_roots()
            .into_iter()
            .filter(|root| is_quadstick(root))
            .collect();

        let mut registry = self.registry();
        // Anything that was there and is not now goes absent rather than being
        // forgotten, so the same drive coming back keeps its id. Only the
        // absence is recorded here: marking a root present again would erase the
        // very fact the generation bump below depends on.
        for index in 0..registry.devices.len() {
            if !roots.contains(&registry.devices[index].root) {
                registry.devices[index].present = false;
            }
        }
        for root in roots {
            let label = label_of(&root);
            match registry
                .devices
                .iter()
                .position(|mounted| mounted.root == root)
            {
                Some(index) => {
                    // A root the OS handed back after it went away may be an
                    // unrelated volume, so it answers at a new generation and any
                    // plan made against the old one is refused.
                    if !registry.devices[index].present {
                        registry.next_generation += 1;
                        let generation = DeviceGeneration::from_raw(registry.next_generation);
                        registry.devices[index].generation = generation;
                    }
                    registry.devices[index].present = true;
                    registry.devices[index].label = label;
                }
                None => {
                    registry.next_id += 1;
                    registry.next_generation += 1;
                    let id = StorageDeviceId::from_raw(registry.next_id);
                    let generation = DeviceGeneration::from_raw(registry.next_generation);
                    registry
                        .devices
                        .push(Mounted::new(id, root, label, generation));
                }
            }
        }

        Ok(registry
            .devices
            .iter()
            .filter(|mounted| mounted.present)
            .map(probe_of)
            .collect())
    }

    fn revalidate(&self, device: StorageDeviceId) -> Result<StorageProbe, StorageError> {
        let registry = self.registry();
        let found = registry.root_of(device)?;
        Ok(probe_of(found))
    }

    fn list_files(
        &self,
        device: StorageDeviceId,
        expected: DeviceGeneration,
    ) -> Result<DeviceListing, StorageError> {
        let registry = self.registry();
        let root = registry.scoped(device, expected)?;
        let entries = fs::read_dir(root)
            .map_err(|error| map_io(&error, StorageStage::ListFiles, TargetState::Unchanged))?;

        let mut listing = DeviceListing::default();
        for entry in entries {
            let Ok(entry) = entry else {
                // A directory entry the OS will not describe is counted, never
                // dropped. A list that quietly hides part of a drive is the bug
                // this app keeps being reported for.
                listing.unnameable += 1;
                continue;
            };
            let size = entry.metadata().map(|meta| meta.len()).unwrap_or_default();
            match entry
                .file_name()
                .to_str()
                .map(DeviceFileName::new)
                .transpose()
            {
                Ok(Some(name)) => listing.files.push(DeviceFileEntry {
                    name,
                    size_bytes: size,
                }),
                Ok(None) | Err(_) => listing.unnameable += 1,
            }
        }
        listing.files.sort_by(|a, b| a.name.cmp(&b.name));
        Ok(listing)
    }

    fn read_file(
        &self,
        device: StorageDeviceId,
        expected: DeviceGeneration,
        name: &DeviceFileName,
    ) -> Result<Vec<u8>, StorageError> {
        let registry = self.registry();
        let root = registry.scoped(device, expected)?;
        let path = root.join(name.as_str());
        fs::read(&path).map_err(|error| {
            if error.kind() == io::ErrorKind::NotFound {
                StorageError::FileNotFound { name: name.clone() }
            } else {
                map_io(&error, StorageStage::ReadFile, TargetState::Unchanged)
            }
        })
    }

    fn stage_write(
        &self,
        device: StorageDeviceId,
        expected: DeviceGeneration,
        target: &SafeDeviceFileName,
        bytes: &[u8],
    ) -> Result<StagedWrite, StorageError> {
        let mut registry = self.registry();
        let root = registry.scoped(device, expected)?.to_path_buf();
        let path = root.join(format!("{target}{TEMP_SUFFIX}"));

        // Same directory as the target on purpose: the replace has to stay on
        // one filesystem, which is the most a FAT drive gives.
        write_and_sync(&path, bytes).map_err(|error| {
            let _ = fs::remove_file(&path);
            map_io(&error, StorageStage::TempWrite, TargetState::Unchanged)
        })?;

        registry.next_token += 1;
        let token = registry.next_token;
        registry.staged.insert(token, Staged { device, path });
        Ok(StagedWrite::new(device, expected, target.clone(), token))
    }

    fn verify_staged(&self, staged: &StagedWrite, expected: &[u8]) -> Result<(), StorageError> {
        let registry = self.registry();
        let Some(entry) = registry.staged.get(&staged.token()) else {
            return Err(StorageError::VerifyFailed);
        };
        // Reopened and compared byte for byte. The target is untouched until
        // this passes, and it is what everything after it rests on.
        match fs::read(&entry.path) {
            Ok(found) if found == expected => Ok(()),
            Ok(_) => Err(StorageError::VerifyFailed),
            Err(error) => Err(map_io(
                &error,
                StorageStage::TempReadBack,
                TargetState::Unchanged,
            )),
        }
    }

    fn commit_staged(&self, staged: StagedWrite) -> Result<(), CommitFailure> {
        let mut registry = self.registry();
        let root = match registry.scoped(staged.device(), staged.generation()) {
            Ok(root) => root.to_path_buf(),
            Err(error) => {
                return Err(CommitFailure {
                    error,
                    staged: Some(staged),
                });
            }
        };
        let token = staged.token();
        // The device is checked as well as the token. Two drives can be open at
        // once, and a handle that says one thing while the table says another is
        // a bug that must not become a write to the wrong stick.
        let filed = registry
            .staged
            .get(&token)
            .is_some_and(|entry| entry.device == staged.device());
        let entry = if filed {
            registry.staged.remove(&token)
        } else {
            None
        };
        let Some(entry) = entry else {
            return Err(CommitFailure {
                error: StorageError::Io {
                    stage: StorageStage::ReplaceBeforeDisplace,
                    target: TargetState::Unchanged,
                    detail: OsDetail::new("the staged file is no longer on record"),
                },
                staged: None,
            });
        };
        let target = root.join(staged.target().as_str());

        match fs::rename(&entry.path, &target) {
            Ok(()) => Ok(()),
            Err(error) => {
                // A rename that failed does not say by itself whether the old
                // directory entry survived. Do not guess: look, and report only
                // what the drive can still be asked.
                let temp_left = entry.path.try_exists();
                let target_left = target.try_exists();
                let (target_state, staged_back) = match (temp_left, target_left) {
                    (Ok(true), Ok(true)) => (TargetState::Unchanged, Some(staged)),
                    (Ok(_), Ok(true)) => (TargetState::Uncertain, None),
                    (Ok(true), Ok(false)) => (TargetState::Missing, Some(staged)),
                    (Ok(false), Ok(false)) => (TargetState::Missing, None),
                    // The drive stopped answering, so nothing can be proven.
                    _ => (TargetState::Uncertain, None),
                };
                if staged_back.is_some() {
                    registry.staged.insert(token, entry);
                }
                Err(CommitFailure {
                    error: map_io(&error, StorageStage::ReplaceAfterDisplace, target_state),
                    staged: staged_back,
                })
            }
        }
    }

    fn discard_staged(&self, staged: StagedWrite) -> Result<(), StorageError> {
        let mut registry = self.registry();
        let filed = registry
            .staged
            .get(&staged.token())
            .is_some_and(|entry| entry.device == staged.device());
        let Some(entry) = filed
            .then(|| registry.staged.remove(&staged.token()))
            .flatten()
        else {
            return Ok(());
        };
        match fs::remove_file(&entry.path) {
            Ok(()) => Ok(()),
            Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(()),
            Err(error) => Err(map_io(
                &error,
                StorageStage::Cleanup,
                TargetState::Unchanged,
            )),
        }
    }

    fn restore_file(
        &self,
        device: StorageDeviceId,
        expected: DeviceGeneration,
        name: &DeviceFileName,
        bytes: &[u8],
    ) -> Result<(), StorageError> {
        let registry = self.registry();
        let root = registry.scoped(device, expected)?;
        let target = root.join(name.as_str());
        let beside = root.join(format!("{name}{RESTORE_SUFFIX}"));

        // Beside then into place, never a plain copy over the target. The same
        // full volume that broke the swap can cut a copy short, and the device
        // reads a profile until the first blank line, so it would load the
        // truncated half without complaining and silently drop every binding
        // after the cut.
        write_and_sync(&beside, bytes).map_err(|error| {
            let _ = fs::remove_file(&beside);
            map_io(&error, StorageStage::RestoreWrite, TargetState::Missing)
        })?;
        fs::rename(&beside, &target).map_err(|error| {
            // A leftover partial copy is litter, but it must not be mistaken for
            // the profile. The target is left alone: the rename either put the
            // old file there or never ran, and it is never a partial write.
            let _ = fs::remove_file(&beside);
            map_io(&error, StorageStage::RestoreReplace, TargetState::Missing)
        })
    }

    fn delete_file(
        &self,
        device: StorageDeviceId,
        expected: DeviceGeneration,
        name: &DeviceFileName,
    ) -> Result<(), StorageError> {
        check_deletable(name)?;
        let registry = self.registry();
        let root = registry.scoped(device, expected)?;
        let target = root.join(name.as_str());
        // Belt and braces after the name check: prove the file still sits
        // directly in the device root and is a file, not a directory or a link
        // out of it.
        if !target.is_file() {
            return Err(StorageError::FileNotFound { name: name.clone() });
        }
        fs::remove_file(&target).map_err(|error| {
            if error.kind() == io::ErrorKind::NotFound {
                StorageError::FileNotFound { name: name.clone() }
            } else {
                map_io(&error, StorageStage::Delete, TargetState::Unchanged)
            }
        })
    }
}

fn probe_of(mounted: &Mounted) -> StorageProbe {
    StorageProbe {
        id: mounted.id,
        generation: mounted.generation,
        display_name: DeviceDisplayName::new(&mounted.label),
        capabilities: StorageCapabilities {
            writable: writable(&mounted.root),
            free_bytes: free_bytes(&mounted.root),
        },
    }
}

/// Write every byte and push them at the platform before saying it is done.
///
/// `sync_all` is what turns "the OS took it" into "the drive has it", which
/// matters on a stick a user may pull the moment the progress bar disappears.
fn write_and_sync(path: &Path, bytes: &[u8]) -> io::Result<()> {
    use std::io::Write;
    let mut file = fs::File::create(path)?;
    file.write_all(bytes)?;
    file.flush()?;
    file.sync_all()
}

/// The off-device backup area.
///
/// Never on the QuadStick: the firmware deletes files it does not recognize at
/// startup, so a backup on the drive that just failed is no backup at all.
#[derive(Debug)]
pub struct FileSystemBackupStore {
    folder: PathBuf,
}

impl FileSystemBackupStore {
    #[must_use]
    pub const fn new(folder: PathBuf) -> Self {
        Self { folder }
    }

    /// The shipped location: `QuadStickBackups` in the user's home directory.
    #[must_use]
    pub fn default_location() -> Self {
        let home = std::env::var_os("HOME")
            .or_else(|| std::env::var_os("USERPROFILE"))
            .map_or_else(std::env::temp_dir, PathBuf::from);
        Self::new(home.join("QuadStickBackups"))
    }
}

impl BackupStore for FileSystemBackupStore {
    fn store(&self, name: &DeviceFileName, bytes: &[u8]) -> Result<BackupReceipt, StorageError> {
        fs::create_dir_all(&self.folder).map_err(|error| StorageError::BackupFailed {
            detail: OsDetail::new(error.to_string()),
        })?;

        let copy_name = unique_backup_name(&self.folder, &utc_stamp(), name.as_str());
        let path = self.folder.join(&copy_name);

        write_and_sync(&path, bytes).map_err(|error| StorageError::BackupFailed {
            detail: OsDetail::new(error.to_string()),
        })?;

        let folder_name = self
            .folder
            .file_name()
            .and_then(|part| part.to_str())
            .unwrap_or("QuadStickBackups");
        Ok(BackupReceipt {
            location: BackupLocationDisplay::new(folder_name, &copy_name),
            bytes: bytes.len(),
        })
    }
}

/// A backup name nothing in the folder already answers to.
///
/// The shipped naming rule: a millisecond stamp, then a counter. Two backups of
/// the same file in the same instant must both survive, never throw and never
/// overwrite, because the second one is often the copy taken when the first
/// attempt failed.
fn unique_backup_name(folder: &Path, stamp: &str, name: &str) -> String {
    let mut copy_name = format!("{stamp}-{name}");
    let mut n = 2u32;
    while folder.join(&copy_name).exists() {
        copy_name = format!("{stamp}-{n}-{name}");
        n += 1;
    }
    copy_name
}

/// `yyyymmdd-hhmmss-mmm`, in UTC.
///
/// The shipped app stamped local time. UTC is used here because working out the
/// user's offset needs a timezone database, and a backup name that is off by an
/// hour twice a year is worse than one that is plainly universal.
fn utc_stamp() -> String {
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default();
    let millis = now.subsec_millis();
    let secs = now.as_secs();
    let days = secs / 86_400;
    let rest = secs % 86_400;
    let (year, month, day) = civil_from_days(days);
    format!(
        "{year:04}{month:02}{day:02}-{:02}{:02}{:02}-{millis:03}",
        rest / 3600,
        (rest % 3600) / 60,
        rest % 60
    )
}

/// Days since 1970-01-01 to a calendar date, by Howard Hinnant's algorithm. No
/// dependency, and no leap-year rule written out by hand to get wrong.
fn civil_from_days(days: u64) -> (u64, u64, u64) {
    let z = days + 719_468;
    let era = z / 146_097;
    let doe = z % 146_097;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    (if m <= 2 { y + 1 } else { y }, m, d)
}

#[cfg(test)]
mod tests;
