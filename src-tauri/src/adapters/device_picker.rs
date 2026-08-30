//! Native folder selection for a QuadStick drive.
//!
//! A path is allowed to exist here because this is an adapter. It never leaves
//! this module: a selected folder is marker-checked, remembered as one extra
//! candidate root, and the storage adapter turns it into its normal opaque
//! device id on the next discovery.

use crate::adapters::storage::{
    is_quadstick,
    volumes::{PlatformVolumes, VolumeSource},
};
use qcm_core::error::{DeviceError, QcmError};
use std::path::PathBuf;
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};

/// The device service's volume source: normal OS enumeration plus folders the
/// user explicitly picked during this run.
#[derive(Debug, Clone, Default)]
pub struct DeviceVolumeSource {
    manual: Arc<Mutex<Vec<PathBuf>>>,
}

impl DeviceVolumeSource {
    fn manual(&self) -> MutexGuard<'_, Vec<PathBuf>> {
        self.manual.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn add(&self, root: PathBuf) {
        let mut manual = self.manual();
        if !manual.contains(&root) {
            manual.push(root);
        }
    }
}

impl VolumeSource for DeviceVolumeSource {
    fn candidate_roots(&self) -> Vec<PathBuf> {
        let mut roots = PlatformVolumes.candidate_roots();
        for root in self.manual().iter() {
            if !roots.contains(root) {
                roots.push(root.clone());
            }
        }
        roots
    }
}

/// A picker returns only whether a validated candidate was added. The path is
/// deliberately not part of the contract; the caller refreshes discovery and
/// receives the same opaque device snapshot as every other path into storage.
pub trait DeviceFolderPicker {
    fn pick_device_folder(&self) -> Result<bool, QcmError>;
}

#[derive(Debug, Clone)]
pub struct NativeDeviceFolderPicker {
    volumes: DeviceVolumeSource,
}

impl NativeDeviceFolderPicker {
    #[must_use]
    pub fn new(volumes: DeviceVolumeSource) -> Self {
        Self { volumes }
    }
}

impl DeviceFolderPicker for NativeDeviceFolderPicker {
    fn pick_device_folder(&self) -> Result<bool, QcmError> {
        let Some(root) = rfd::FileDialog::new()
            .set_title("Choose QuadStick drive")
            .pick_folder()
        else {
            return Ok(false);
        };

        if !is_quadstick(&root) {
            return Err(DeviceError::NotQuadStick.into());
        }
        self.volumes.add(root);
        Ok(true)
    }
}

#[cfg(test)]
mod tests {
    use super::DeviceVolumeSource;
    use crate::adapters::storage::volumes::VolumeSource;
    use std::path::PathBuf;

    #[test]
    fn a_manually_added_root_is_deduplicated() {
        let source = DeviceVolumeSource::default();
        let root = PathBuf::from("/definitely-not-a-real-quadstick-test-root");
        source.add(root.clone());
        source.add(root.clone());
        assert_eq!(
            source
                .candidate_roots()
                .into_iter()
                .filter(|candidate| candidate == &root)
                .count(),
            1
        );
    }
}
