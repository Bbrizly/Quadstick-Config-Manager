//! Safe device-profile rename.
//!
//! QuadStick profile order is filename-derived: `default.csv` first, then the
//! remaining profiles case-insensitively alphabetically. Renaming is therefore
//! also the honest reorder primitive. There is no hidden order file to mutate.
//!
//! A rename is implemented as a verified create-new-target transaction using
//! the same staged write path as install, followed by source deletion. The old
//! file is backed up before anything changes. If source deletion fails, the new
//! target is removed best-effort so the source remains authoritative.

use super::Devices;
use crate::clock::Clock;
use crate::error::{BackupLocationDisplay, DeviceError, QcmError, RequestError, StorageError};
use crate::ports::storage::{
    BackupStore, DeviceFileName, DeviceGeneration, DeviceStorage, SafeDeviceFileName,
    StorageDeviceId, check_deletable,
};

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RenameReceipt {
    pub device: StorageDeviceId,
    pub from: DeviceFileName,
    pub to: DeviceFileName,
    pub backup: BackupLocationDisplay,
}

impl<S: DeviceStorage, B: BackupStore, C: Clock + Clone> Devices<S, B, C> {
    /// Rename one ordinary profile without ever overwriting another profile.
    ///
    /// `expected_generation` is the generation the library UI rendered. A
    /// remove/replug while the rename dialog is open therefore fails stale.
    pub fn rename_profile(
        &mut self,
        device: StorageDeviceId,
        expected_generation: DeviceGeneration,
        from: &DeviceFileName,
        to: &SafeDeviceFileName,
    ) -> Result<RenameReceipt, QcmError> {
        check_deletable(from)?;
        if to.role().is_protected() {
            return Err(StorageError::ProtectedFile {
                name: to.as_device_name().clone(),
            }
            .into());
        }
        if from.as_str().eq_ignore_ascii_case(to.as_str()) {
            return Err(QcmError::Request(RequestError::OutOfRange {
                what: "rename target is unchanged",
            }));
        }

        let handle = self.resolve_device(device)?;
        if handle.generation != expected_generation {
            return Err(StorageError::Device(DeviceError::Stale {
                expected: expected_generation,
                actual: handle.generation,
            })
            .into());
        }

        // Refuse a collision before the staged write. `commit_staged` is built
        // for install and may replace a target, while rename must never do so.
        // Recheck immediately before commit as well to narrow the only race a
        // removable FAT volume can expose here.
        let listing = self.storage.list_files(handle.device, handle.generation)?;
        if listing
            .files
            .iter()
            .any(|entry| entry.name.as_str().eq_ignore_ascii_case(to.as_str()))
        {
            return Err(QcmError::Request(RequestError::OutOfRange {
                what: "rename target already exists",
            }));
        }

        let bytes = self
            .storage
            .read_file(handle.device, handle.generation, from)?;
        let backup = self.backups.store(from, &bytes)?;

        let staged = self
            .storage
            .stage_write(handle.device, handle.generation, to, &bytes)?;
        if let Err(error) = self.storage.verify_staged(&staged, &bytes) {
            let _ = self.storage.discard_staged(staged);
            return Err(error.into());
        }

        let second_listing = self.storage.list_files(handle.device, handle.generation)?;
        if second_listing
            .files
            .iter()
            .any(|entry| entry.name.as_str().eq_ignore_ascii_case(to.as_str()))
        {
            let _ = self.storage.discard_staged(staged);
            return Err(QcmError::Request(RequestError::OutOfRange {
                what: "rename target already exists",
            }));
        }

        if let Err(failure) = self.storage.commit_staged(staged) {
            if let Some(leftover) = failure.staged {
                let _ = self.storage.discard_staged(leftover);
            }
            return Err(failure.error.into());
        }

        // The committed target must still read as the exact source bytes before
        // the source is removed. If not, remove the new target and keep source.
        match self
            .storage
            .read_file(handle.device, handle.generation, to.as_device_name())
        {
            Ok(read_back) if read_back == bytes => {}
            Ok(_) | Err(_) => {
                let _ =
                    self.storage
                        .delete_file(handle.device, handle.generation, to.as_device_name());
                return Err(StorageError::VerifyFailed.into());
            }
        }

        if let Err(error) = self
            .storage
            .delete_file(handle.device, handle.generation, from)
        {
            let _ = self
                .storage
                .delete_file(handle.device, handle.generation, to.as_device_name());
            return Err(error.into());
        }

        self.invalidate_device_cache();
        Ok(RenameReceipt {
            device: handle.device,
            from: from.clone(),
            to: to.as_device_name().clone(),
            backup: backup.location,
        })
    }
}
