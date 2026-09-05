//! A QuadStick drive that lives in memory and does what it is told.
//!
//! The install transaction is the most dangerous code in this app and the
//! hardest to exercise: the failures that matter are a stick pulled out
//! mid-write, a full FAT volume, a read-only mount, a mount point handed to
//! another drive. This fake makes each of those a line in a test.
//!
//! It models what the real device layer has to reason about and nothing more:
//! the `default.csv` marker that proves a volume is a QuadStick, the file set,
//! a generation that moves whenever the mount does, and one injected failure at
//! a named stage.

use qcm_core::error::{DeviceError, OsDetail, StorageError, StorageStage, TargetState};
use qcm_core::ports::storage::{
    BackupReceipt, BackupStore, CommitFailure, DeviceFileEntry, DeviceFileName, DeviceGeneration,
    DeviceListing, DeviceStorage, MARKER_FILE_NAME, SafeDeviceFileName, StagedWrite,
    StorageCapabilities, StorageDeviceId, StorageProbe, check_deletable, check_generation,
};
use qcm_core::{BackupLocationDisplay, DeviceDisplayName};
use std::collections::BTreeMap;
use std::sync::{Mutex, MutexGuard, PoisonError};

/// The suffix the legacy device layer used for its temp file. Kept so a test
/// can assert the drive is left clean the same way `InstallCleanupTests` does.
pub const TEMP_SUFFIX: &str = ".qscm-tmp";

/// What goes wrong when a fault fires.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Fault {
    /// The volume disappears mid-operation.
    Unplug,
    ReadOnly,
    Full,
    PermissionDenied,
    /// Anything else the OS can raise at that point.
    Io,
}

#[derive(Debug)]
struct Planned {
    stage: StorageStage,
    fault: Fault,
    /// `None` means it keeps firing.
    remaining: Option<usize>,
}

#[derive(Debug)]
struct Temp {
    target: String,
    bytes: Vec<u8>,
}

#[derive(Debug)]
struct FakeDevice {
    id: StorageDeviceId,
    label: String,
    present: bool,
    read_only: bool,
    /// `None` is a volume that will not say how much room is left, which is a
    /// real answer on some platforms and must not read as zero.
    capacity_bytes: Option<u64>,
    generation: DeviceGeneration,
    files: BTreeMap<String, Vec<u8>>,
    temps: BTreeMap<u64, Temp>,
}

impl FakeDevice {
    fn has_marker(&self) -> bool {
        self.files
            .keys()
            .any(|name| name.eq_ignore_ascii_case(MARKER_FILE_NAME))
    }

    fn used_bytes(&self) -> u64 {
        let files: u64 = self.files.values().map(|bytes| bytes.len() as u64).sum();
        let temps: u64 = self
            .temps
            .values()
            .map(|temp| temp.bytes.len() as u64)
            .sum();
        files + temps
    }

    fn free_bytes(&self) -> Option<u64> {
        self.capacity_bytes
            .map(|capacity| capacity.saturating_sub(self.used_bytes()))
    }

    fn probe(&self) -> StorageProbe {
        StorageProbe {
            id: self.id,
            generation: self.generation,
            display_name: DeviceDisplayName::new(&self.label),
            capabilities: StorageCapabilities {
                writable: !self.read_only,
                free_bytes: self.free_bytes(),
            },
        }
    }
}

#[derive(Debug, Default)]
struct State {
    devices: Vec<FakeDevice>,
    faults: Vec<Planned>,
    /// How many times the port was asked to enumerate. The only way to prove a
    /// scan cache above the port actually stopped a scan.
    discoveries: usize,
    next_id: u64,
    next_token: u64,
    next_generation: u64,
}

/// An in-memory QuadStick drive, or several.
#[derive(Debug, Default)]
pub struct FakeQuadStick {
    state: Mutex<State>,
}

impl FakeQuadStick {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// A plugged-in QuadStick with only its marker file on it.
    #[must_use]
    pub fn with_device() -> (Self, StorageDeviceId) {
        let fake = Self::new();
        let id = fake.plug("QUADSTICK");
        (fake, id)
    }

    /// A poisoned lock means a test thread panicked while holding it. The data
    /// is still what it was, and hiding a fake's state behind a second panic
    /// only buries the first one.
    fn state(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// Mount a device carrying the marker that proves what it is.
    pub fn plug(&self, label: &str) -> StorageDeviceId {
        let mut state = self.state();
        state.next_id += 1;
        state.next_generation += 1;
        let id = StorageDeviceId::from_raw(state.next_id);
        let generation = DeviceGeneration::from_raw(state.next_generation);
        let mut files = BTreeMap::new();
        files.insert(
            MARKER_FILE_NAME.to_owned(),
            b"QuadStick Configuration File,\n".to_vec(),
        );
        state.devices.push(FakeDevice {
            id,
            label: label.to_owned(),
            present: true,
            read_only: false,
            capacity_bytes: None,
            generation,
            files,
            temps: BTreeMap::new(),
        });
        id
    }

    /// Mount something that is not a QuadStick: no marker, so nothing may be
    /// written to it however it was chosen.
    pub fn plug_without_marker(&self, label: &str) -> StorageDeviceId {
        let id = self.plug(label);
        self.remove_marker(id);
        id
    }

    /// Pull the drive out between operations.
    pub fn unplug(&self, device: StorageDeviceId) {
        let mut state = self.state();
        state.next_generation += 1;
        if let Some(found) = state.devices.iter_mut().find(|found| found.id == device) {
            found.present = false;
        }
    }

    /// Put it back. A new generation, because the OS may have handed the same
    /// mount point to something else in between.
    pub fn replug(&self, device: StorageDeviceId) {
        let mut state = self.state();
        state.next_generation += 1;
        let generation = DeviceGeneration::from_raw(state.next_generation);
        if let Some(found) = state.devices.iter_mut().find(|found| found.id == device) {
            found.present = true;
            found.generation = generation;
        }
    }

    pub fn set_read_only(&self, device: StorageDeviceId, read_only: bool) {
        self.with_device_mut(device, |found| found.read_only = read_only);
    }

    /// `None` for a volume that will not report its size.
    pub fn set_capacity(&self, device: StorageDeviceId, capacity_bytes: Option<u64>) {
        self.with_device_mut(device, |found| found.capacity_bytes = capacity_bytes);
    }

    /// Put a file on the drive without going through the port.
    pub fn put_file(&self, device: StorageDeviceId, name: &str, bytes: &[u8]) {
        self.with_device_mut(device, |found| {
            found.files.insert(name.to_owned(), bytes.to_vec());
        });
    }

    /// Take the marker away, the way a user reformatting a stick would.
    pub fn remove_marker(&self, device: StorageDeviceId) {
        self.with_device_mut(device, |found| {
            found
                .files
                .retain(|name, _| !name.eq_ignore_ascii_case(MARKER_FILE_NAME));
        });
    }

    /// Fail once at this stage, so a retry can be tested too.
    pub fn fail_at(&self, stage: StorageStage, fault: Fault) {
        self.state().faults.push(Planned {
            stage,
            fault,
            remaining: Some(1),
        });
    }

    /// Fail at this stage every time.
    pub fn fail_always(&self, stage: StorageStage, fault: Fault) {
        self.state().faults.push(Planned {
            stage,
            fault,
            remaining: None,
        });
    }

    pub fn clear_faults(&self) {
        self.state().faults.clear();
    }

    /// Everything on the drive, for assertions.
    #[must_use]
    pub fn file_names(&self, device: StorageDeviceId) -> Vec<String> {
        self.read_device(device, |found| found.files.keys().cloned().collect())
            .unwrap_or_default()
    }

    #[must_use]
    pub fn file(&self, device: StorageDeviceId, name: &str) -> Option<Vec<u8>> {
        self.read_device(device, |found| found.files.get(name).cloned())
            .flatten()
    }

    /// Temp files still on the drive. The install transaction must leave none.
    #[must_use]
    pub fn stray_temp_names(&self, device: StorageDeviceId) -> Vec<String> {
        self.read_device(device, |found| {
            found
                .temps
                .values()
                .map(|temp| format!("{}{TEMP_SUFFIX}", temp.target))
                .collect()
        })
        .unwrap_or_default()
    }

    /// How many times [`DeviceStorage::discover`] has run. A cache that claims
    /// to collapse a burst of lookups has to show this number standing still.
    #[must_use]
    pub fn discover_count(&self) -> usize {
        self.state().discoveries
    }

    #[must_use]
    pub fn generation(&self, device: StorageDeviceId) -> Option<DeviceGeneration> {
        self.read_device(device, |found| found.generation)
    }

    fn with_device_mut(&self, device: StorageDeviceId, edit: impl FnOnce(&mut FakeDevice)) {
        let mut state = self.state();
        if let Some(found) = state.devices.iter_mut().find(|found| found.id == device) {
            edit(found);
        }
    }

    fn read_device<T>(
        &self,
        device: StorageDeviceId,
        read: impl FnOnce(&FakeDevice) -> T,
    ) -> Option<T> {
        let state = self.state();
        state
            .devices
            .iter()
            .find(|found| found.id == device)
            .map(read)
    }
}

impl State {
    fn take_fault(&mut self, stage: StorageStage) -> Option<Fault> {
        let index = self.faults.iter().position(|plan| plan.stage == stage)?;
        let fault = self.faults[index].fault;
        if let Some(remaining) = self.faults[index].remaining.as_mut() {
            *remaining -= 1;
            if *remaining == 0 {
                self.faults.remove(index);
            }
        }
        Some(fault)
    }

    fn device(&self, id: StorageDeviceId) -> Result<&FakeDevice, StorageError> {
        match self.devices.iter().find(|found| found.id == id) {
            Some(found) if found.present => Ok(found),
            Some(_) => Err(StorageError::Device(DeviceError::NotFound)),
            None => Err(StorageError::Device(DeviceError::NotFound)),
        }
    }

    fn device_mut(&mut self, id: StorageDeviceId) -> Result<&mut FakeDevice, StorageError> {
        match self.devices.iter_mut().find(|found| found.id == id) {
            Some(found) if found.present => Ok(found),
            Some(_) | None => Err(StorageError::Device(DeviceError::NotFound)),
        }
    }

    /// Resolve, prove the marker, and prove the generation, in that order, the
    /// way every destructive call on a real device has to.
    fn resolve(
        &mut self,
        id: StorageDeviceId,
        expected: DeviceGeneration,
    ) -> Result<(), StorageError> {
        let device = self.device(id)?;
        if !device.has_marker() {
            return Err(StorageError::Device(DeviceError::NotQuadStick));
        }
        check_generation(expected, device.generation)
    }

    /// Apply an injected fault, if one is planned for this stage.
    ///
    /// `after_swap` is what the caller can still prove about the target. An
    /// unplug is deliberately harsher than the fake's own knowledge: once the
    /// mount is gone a real adapter cannot look, so it may not claim the file
    /// survived.
    fn fire(
        &mut self,
        id: StorageDeviceId,
        stage: StorageStage,
        target: TargetState,
    ) -> Result<(), StorageError> {
        let Some(fault) = self.take_fault(stage) else {
            return Ok(());
        };
        match fault {
            Fault::Unplug => {
                self.next_generation += 1;
                if let Ok(device) = self.device_mut(id) {
                    device.present = false;
                }
                Err(StorageError::RemovedDuringOperation {
                    stage,
                    target: if stage.is_before_swap() {
                        TargetState::Unchanged
                    } else {
                        TargetState::Uncertain
                    },
                })
            }
            Fault::ReadOnly => Err(StorageError::ReadOnly { stage }),
            Fault::Full => Err(StorageError::Full { stage, target }),
            Fault::PermissionDenied => Err(StorageError::PermissionDenied { stage }),
            Fault::Io => Err(StorageError::Io {
                stage,
                target,
                detail: OsDetail::new("injected fault"),
            }),
        }
    }
}

impl DeviceStorage for FakeQuadStick {
    fn discover(&self) -> Result<Vec<StorageProbe>, StorageError> {
        let mut state = self.state();
        state.discoveries += 1;
        state.fire(
            StorageDeviceId::from_raw(0),
            StorageStage::Discover,
            TargetState::Unchanged,
        )?;
        Ok(state
            .devices
            .iter()
            .filter(|device| device.present && device.has_marker())
            .map(FakeDevice::probe)
            .collect())
    }

    fn revalidate(&self, device: StorageDeviceId) -> Result<StorageProbe, StorageError> {
        let mut state = self.state();
        state.fire(device, StorageStage::Revalidate, TargetState::Unchanged)?;
        let found = state.device(device)?;
        if !found.has_marker() {
            return Err(StorageError::Device(DeviceError::NotQuadStick));
        }
        Ok(found.probe())
    }

    fn list_files(
        &self,
        device: StorageDeviceId,
        expected: DeviceGeneration,
    ) -> Result<DeviceListing, StorageError> {
        let mut state = self.state();
        state.fire(device, StorageStage::ListFiles, TargetState::Unchanged)?;
        state.resolve(device, expected)?;
        let found = state.device(device)?;
        let mut listing = DeviceListing::default();
        for (name, bytes) in &found.files {
            match DeviceFileName::new(name) {
                Ok(name) => listing.files.push(DeviceFileEntry {
                    name,
                    size_bytes: bytes.len() as u64,
                }),
                Err(_) => listing.unnameable += 1,
            }
        }
        Ok(listing)
    }

    fn read_file(
        &self,
        device: StorageDeviceId,
        expected: DeviceGeneration,
        name: &DeviceFileName,
    ) -> Result<Vec<u8>, StorageError> {
        let mut state = self.state();
        state.fire(device, StorageStage::ReadFile, TargetState::Unchanged)?;
        state.resolve(device, expected)?;
        let found = state.device(device)?;
        found
            .files
            .get(name.as_str())
            .cloned()
            .ok_or_else(|| StorageError::FileNotFound { name: name.clone() })
    }

    fn stage_write(
        &self,
        device: StorageDeviceId,
        expected: DeviceGeneration,
        target: &SafeDeviceFileName,
        bytes: &[u8],
    ) -> Result<StagedWrite, StorageError> {
        let mut state = self.state();
        state.fire(device, StorageStage::TempCreate, TargetState::Unchanged)?;
        state.resolve(device, expected)?;
        if state.device(device)?.read_only {
            return Err(StorageError::ReadOnly {
                stage: StorageStage::TempCreate,
            });
        }
        state.fire(device, StorageStage::TempWrite, TargetState::Unchanged)?;
        let room = state.device(device)?.free_bytes();
        if room.is_some_and(|free| free < bytes.len() as u64) {
            return Err(StorageError::Full {
                stage: StorageStage::TempWrite,
                target: TargetState::Unchanged,
            });
        }
        state.fire(device, StorageStage::TempFlush, TargetState::Unchanged)?;

        state.next_token += 1;
        let token = state.next_token;
        let generation = state.device(device)?.generation;
        let found = state.device_mut(device)?;
        found.temps.insert(
            token,
            Temp {
                target: target.as_str().to_owned(),
                bytes: bytes.to_vec(),
            },
        );
        Ok(StagedWrite::new(device, generation, target.clone(), token))
    }

    fn verify_staged(&self, staged: &StagedWrite, expected: &[u8]) -> Result<(), StorageError> {
        let mut state = self.state();
        state.fire(
            staged.device(),
            StorageStage::TempReadBack,
            TargetState::Unchanged,
        )?;
        state.resolve(staged.device(), staged.generation())?;
        let found = state.device(staged.device())?;
        let Some(temp) = found.temps.get(&staged.token()) else {
            return Err(StorageError::VerifyFailed);
        };
        if temp.bytes == expected {
            Ok(())
        } else {
            Err(StorageError::VerifyFailed)
        }
    }

    fn commit_staged(&self, staged: StagedWrite) -> Result<(), CommitFailure> {
        let mut state = self.state();
        // Everything up to here leaves the temp where it is, so the handle goes
        // back to the caller to clean up.
        let before = state
            .fire(
                staged.device(),
                StorageStage::ReplaceBeforeDisplace,
                TargetState::Unchanged,
            )
            .and_then(|()| state.resolve(staged.device(), staged.generation()))
            .and_then(|()| {
                if state.device(staged.device())?.read_only {
                    Err(StorageError::ReadOnly {
                        stage: StorageStage::ReplaceBeforeDisplace,
                    })
                } else {
                    Ok(())
                }
            });
        if let Err(error) = before {
            return Err(CommitFailure {
                error,
                staged: Some(staged),
            });
        }

        // The old directory entry goes first. That is the moment the target is
        // provably gone, and every failure after it has to say so.
        let temp = match state.device_mut(staged.device()) {
            Ok(found) => found.temps.remove(&staged.token()),
            Err(error) => {
                return Err(CommitFailure {
                    error,
                    staged: None,
                });
            }
        };
        let Some(temp) = temp else {
            return Err(CommitFailure {
                error: StorageError::Io {
                    stage: StorageStage::ReplaceBeforeDisplace,
                    target: TargetState::Unchanged,
                    detail: OsDetail::new("staged file is gone"),
                },
                staged: None,
            });
        };
        if let Ok(found) = state.device_mut(staged.device()) {
            found.files.remove(&temp.target);
        }

        // Past this point the temp is spent whichever way it goes, so nothing
        // comes back to clean up and the target is Missing until it lands.
        if let Err(error) = state.fire(
            staged.device(),
            StorageStage::ReplaceAfterDisplace,
            TargetState::Missing,
        ) {
            return Err(CommitFailure {
                error,
                staged: None,
            });
        }

        match state.device_mut(staged.device()) {
            Ok(found) => {
                found.files.insert(temp.target, temp.bytes);
                Ok(())
            }
            Err(error) => Err(CommitFailure {
                error,
                staged: None,
            }),
        }
    }

    fn discard_staged(&self, staged: StagedWrite) -> Result<(), StorageError> {
        let mut state = self.state();
        state.fire(
            staged.device(),
            StorageStage::Cleanup,
            TargetState::Unchanged,
        )?;
        let found = state.device_mut(staged.device())?;
        found.temps.remove(&staged.token());
        Ok(())
    }

    fn restore_file(
        &self,
        device: StorageDeviceId,
        expected: DeviceGeneration,
        name: &DeviceFileName,
        bytes: &[u8],
    ) -> Result<(), StorageError> {
        let mut state = self.state();
        state.fire(device, StorageStage::RestoreWrite, TargetState::Missing)?;
        state.resolve(device, expected)?;
        state.fire(device, StorageStage::RestoreReplace, TargetState::Missing)?;
        let found = state.device_mut(device)?;
        // Beside then into place: the target is the old file or nothing, never
        // a half written profile the device would load without complaining.
        found.files.insert(name.as_str().to_owned(), bytes.to_vec());
        Ok(())
    }

    fn delete_file(
        &self,
        device: StorageDeviceId,
        expected: DeviceGeneration,
        name: &DeviceFileName,
    ) -> Result<(), StorageError> {
        let mut state = self.state();
        state.fire(device, StorageStage::Delete, TargetState::Unchanged)?;
        check_deletable(name)?;
        state.resolve(device, expected)?;
        if state.device(device)?.read_only {
            return Err(StorageError::ReadOnly {
                stage: StorageStage::Delete,
            });
        }
        let found = state.device_mut(device)?;
        if found.files.remove(name.as_str()).is_none() {
            return Err(StorageError::FileNotFound { name: name.clone() });
        }
        Ok(())
    }
}

/// The off-device backup area, in memory.
#[derive(Debug)]
pub struct FakeBackupStore {
    state: Mutex<BackupState>,
}

#[derive(Debug)]
struct BackupState {
    folder: String,
    entries: Vec<(String, Vec<u8>)>,
    fail_next: bool,
    /// Stands in for the legacy timestamp. Two backups of one name in the same
    /// instant must both survive, so the counter is part of the name.
    stamp: u64,
}

impl Default for FakeBackupStore {
    fn default() -> Self {
        Self::new()
    }
}

impl FakeBackupStore {
    #[must_use]
    pub fn new() -> Self {
        Self {
            state: Mutex::new(BackupState {
                folder: "QuadStickBackups".to_owned(),
                entries: Vec::new(),
                fail_next: false,
                stamp: 0,
            }),
        }
    }

    fn state(&self) -> MutexGuard<'_, BackupState> {
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// The next backup fails, so the caller can prove it stops before touching
    /// the device.
    pub fn fail_next(&self) {
        self.state().fail_next = true;
    }

    #[must_use]
    pub fn entries(&self) -> Vec<(String, Vec<u8>)> {
        self.state().entries.clone()
    }

    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.state().entries.is_empty()
    }
}

impl BackupStore for FakeBackupStore {
    fn store(&self, name: &DeviceFileName, bytes: &[u8]) -> Result<BackupReceipt, StorageError> {
        let mut state = self.state();
        if state.fail_next {
            state.fail_next = false;
            return Err(StorageError::BackupFailed {
                detail: OsDetail::new("injected backup failure"),
            });
        }
        state.stamp += 1;
        let copy_name = format!("{:04}-{name}", state.stamp);
        let location = BackupLocationDisplay::new(&state.folder, &copy_name);
        state.entries.push((copy_name, bytes.to_vec()));
        Ok(BackupReceipt {
            location,
            bytes: bytes.len(),
        })
    }
}
