//! Device/storage command state.
//!
//! Prepared install/delete objects stay in this process. The window receives an
//! operation id and, when necessary, a one-shot confirmation id; it never gets
//! the bytes, generation authority, staged-write handle or a filesystem path.

use crate::adapters::device_picker::{
    DeviceFolderPicker, DeviceVolumeSource, NativeDeviceFolderPicker,
};
use crate::adapters::storage::{FileSystemBackupStore, FileSystemDeviceStorage};
use crate::device_ipc::{
    CommitDeleteRequest, CommitInstallRequest, DeletePlanDto, DeleteReceiptDto, DeviceFileRequest,
    DeviceGenerationRequest, DeviceLibrarySnapshotDto, DevicePresenceSnapshotDto,
    DeviceProfileEntryDto, DeviceRequest, DeviceSummaryDto, InstallPlanDto, InstallReceiptDto,
    OpenDeviceFile, confirmation_id, device_id, operation_id, optional_confirmation_id,
};
use crate::device_rename_ipc::{RenameDeviceProfileReceiptDto, RenameDeviceProfileRequest};
use crate::ipc::parse;
use qcm_config::ProfileFile;
use qcm_core::clock::{Clock, Moment, SystemClock};
use qcm_core::devices::{DeletePlan, Devices, InstallPlan};
use qcm_core::error::{DeviceError, QcmError, RequestError, StorageError, StorageStage};
use qcm_core::operation::OperationId;
use qcm_core::ports::storage::{
    BackupStore, DeviceFileName, DeviceGeneration, DeviceStorage, PREFERENCES_FILE_NAME,
    SafeDeviceFileName, StorageDeviceId,
};
use serde_json::Value;
use std::collections::BTreeMap;
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};

#[derive(Debug)]
pub struct DeviceOperationError {
    pub operation: Option<OperationId>,
    pub error: QcmError,
}

impl DeviceOperationError {
    fn plain(error: QcmError) -> Self {
        Self {
            operation: None,
            error,
        }
    }

    fn for_operation(operation: OperationId, error: QcmError) -> Self {
        Self {
            operation: Some(operation),
            error,
        }
    }
}

#[derive(Debug, Clone)]
pub struct SharedSystemClock(Arc<SystemClock>);

impl SharedSystemClock {
    #[must_use]
    pub fn new() -> Self {
        Self(Arc::new(SystemClock::new()))
    }
}

impl Default for SharedSystemClock {
    fn default() -> Self {
        Self::new()
    }
}

impl Clock for SharedSystemClock {
    fn now(&self) -> Moment {
        self.0.now()
    }
}

pub struct DeviceShell<S: DeviceStorage, B: BackupStore, C: Clock + Clone, P: DeviceFolderPicker> {
    devices: Mutex<Devices<S, B, C>>,
    picker: P,
    install_plans: Mutex<BTreeMap<OperationId, InstallPlan>>,
    delete_plans: Mutex<BTreeMap<OperationId, DeletePlan>>,
}

impl<S: DeviceStorage, B: BackupStore, C: Clock + Clone, P: DeviceFolderPicker>
    DeviceShell<S, B, C, P>
{
    #[must_use]
    pub fn new(devices: Devices<S, B, C>, picker: P) -> Self {
        Self {
            devices: Mutex::new(devices),
            picker,
            install_plans: Mutex::new(BTreeMap::new()),
            delete_plans: Mutex::new(BTreeMap::new()),
        }
    }

    fn devices(&self) -> MutexGuard<'_, Devices<S, B, C>> {
        self.devices.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn install_plans(&self) -> MutexGuard<'_, BTreeMap<OperationId, InstallPlan>> {
        self.install_plans
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
    }

    fn delete_plans(&self) -> MutexGuard<'_, BTreeMap<OperationId, DeletePlan>> {
        self.delete_plans
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
    }

    pub fn list_devices(&self) -> Result<DevicePresenceSnapshotDto, QcmError> {
        let devices = self.devices().list_devices()?;
        Ok(DevicePresenceSnapshotDto {
            devices: devices.iter().map(DeviceSummaryDto::from).collect(),
            changed: false,
        })
    }

    pub fn refresh_devices(&self) -> Result<DevicePresenceSnapshotDto, QcmError> {
        let scan = self.devices().refresh_devices()?;
        Ok(DevicePresenceSnapshotDto {
            devices: scan.devices.iter().map(DeviceSummaryDto::from).collect(),
            changed: scan.changed,
        })
    }

    pub fn choose_device_folder(&self) -> Result<Option<DevicePresenceSnapshotDto>, QcmError> {
        if !self.picker.pick_device_folder()? {
            return Ok(None);
        }
        let mut devices = self.devices();
        devices.invalidate_device_cache();
        let scan = devices.refresh_devices()?;
        Ok(Some(DevicePresenceSnapshotDto {
            devices: scan.devices.iter().map(DeviceSummaryDto::from).collect(),
            changed: scan.changed,
        }))
    }

    pub fn get_device_library(&self, raw: Value) -> Result<DeviceLibrarySnapshotDto, QcmError> {
        let request: DeviceRequest = parse(raw, "get_device_library request")?;
        let device = device_id(&request.device_id)?;
        let mut devices = self.devices();
        let handle = devices.resolve_device(device)?;
        let (guide, unnameable) = devices.list_profiles(device)?;
        Ok(DeviceLibrarySnapshotDto {
            device_id: device.to_string(),
            generation: handle.generation.raw(),
            files: guide.iter().map(DeviceProfileEntryDto::from).collect(),
            protected_files: vec!["default.csv".to_owned(), "prefs.csv".to_owned()],
            unnameable,
        })
    }

    pub fn prepare_install(
        &self,
        device_raw: &str,
        file: &ProfileFile,
    ) -> Result<InstallPlanDto, QcmError> {
        let device = device_id(device_raw)?;
        let plan = self.devices().plan_install(device, file)?;
        let dto = InstallPlanDto::from(&plan);
        self.install_plans().insert(plan.operation(), plan);
        Ok(dto)
    }

    pub fn commit_install(&self, raw: Value) -> Result<InstallReceiptDto, DeviceOperationError> {
        self.commit_install_with_progress(raw, |_| {})
    }

    /// Commit one prepared plan while observing only completed storage stages.
    /// The callback receives no paths or bytes and has no cancellation authority.
    pub fn commit_install_with_progress(
        &self,
        raw: Value,
        progress: impl FnMut(StorageStage),
    ) -> Result<InstallReceiptDto, DeviceOperationError> {
        let request: CommitInstallRequest =
            parse(raw, "commit_install request").map_err(DeviceOperationError::plain)?;
        let operation = operation_id(&request.plan_id).map_err(DeviceOperationError::plain)?;
        let confirmation = optional_confirmation_id(request.confirmation_id.as_deref())
            .map_err(|error| DeviceOperationError::for_operation(operation, error))?;
        let plan = self.install_plans().remove(&operation).ok_or_else(|| {
            DeviceOperationError::for_operation(
                operation,
                RequestError::OutOfRange {
                    what: "install plan",
                }
                .into(),
            )
        })?;

        self.devices()
            .install_with_progress(plan, confirmation, progress)
            .map(|receipt| InstallReceiptDto::from(&receipt))
            .map_err(|failure| {
                DeviceOperationError::for_operation(failure.operation, failure.error)
            })
    }

    pub fn prepare_delete(&self, raw: Value) -> Result<DeletePlanDto, QcmError> {
        let request: DeviceFileRequest = parse(raw, "prepare_delete_device_profile request")?;
        let device = device_id(&request.device_id)?;
        let expected = DeviceGeneration::from_raw(request.expected_generation);
        let name = DeviceFileName::new(&request.name)
            .map_err(|reason| StorageError::NameRejected { reason })?;
        let mut devices = self.devices();
        require_generation(&mut devices, device, expected)?;
        let plan = devices.plan_delete(device, &name)?;
        let dto = DeletePlanDto::from(&plan);
        self.delete_plans().insert(plan.operation(), plan);
        Ok(dto)
    }

    pub fn commit_delete(&self, raw: Value) -> Result<DeleteReceiptDto, DeviceOperationError> {
        let request: CommitDeleteRequest = parse(raw, "commit_delete_device_profile request")
            .map_err(DeviceOperationError::plain)?;
        let operation = operation_id(&request.plan_id).map_err(DeviceOperationError::plain)?;
        let confirmation = confirmation_id(&request.confirmation_id)
            .map_err(|error| DeviceOperationError::for_operation(operation, error))?;
        let plan = self.delete_plans().remove(&operation).ok_or_else(|| {
            DeviceOperationError::for_operation(
                operation,
                RequestError::OutOfRange {
                    what: "delete plan",
                }
                .into(),
            )
        })?;

        self.devices()
            .delete_profile(plan, confirmation)
            .map(|receipt| DeleteReceiptDto::from(&receipt))
            .map_err(|error| DeviceOperationError::for_operation(operation, error))
    }

    pub fn rename_profile(&self, raw: Value) -> Result<RenameDeviceProfileReceiptDto, QcmError> {
        let request: RenameDeviceProfileRequest = parse(raw, "rename_device_profile request")?;
        let device = device_id(&request.device_id)?;
        let expected = DeviceGeneration::from_raw(request.expected_generation);
        let from = DeviceFileName::new(&request.from)
            .map_err(|reason| StorageError::NameRejected { reason })?;
        let to = SafeDeviceFileName::new(&request.to)
            .map_err(|reason| StorageError::NameRejected { reason })?;
        let receipt = self
            .devices()
            .rename_profile(device, expected, &from, &to)?;
        Ok(RenameDeviceProfileReceiptDto::from(&receipt))
    }

    pub fn open_device_profile(&self, raw: Value) -> Result<OpenDeviceFile, QcmError> {
        let request: DeviceFileRequest = parse(raw, "open_device_profile request")?;
        let device = device_id(&request.device_id)?;
        let expected = DeviceGeneration::from_raw(request.expected_generation);
        let name = DeviceFileName::new(&request.name)
            .map_err(|reason| StorageError::NameRejected { reason })?;
        let mut devices = self.devices();
        require_generation(&mut devices, device, expected)?;
        let file = devices.read_profile(device, &name)?;
        Ok(OpenDeviceFile {
            device,
            generation: expected,
            name,
            csv_text: file.csv_text,
        })
    }

    pub fn open_device_preferences(&self, raw: Value) -> Result<OpenDeviceFile, QcmError> {
        let request: DeviceGenerationRequest = parse(raw, "open_device_preferences request")?;
        let device = device_id(&request.device_id)?;
        let expected = DeviceGeneration::from_raw(request.expected_generation);
        let name = DeviceFileName::new(PREFERENCES_FILE_NAME)
            .map_err(|reason| StorageError::NameRejected { reason })?;
        let mut devices = self.devices();
        require_generation(&mut devices, device, expected)?;
        let file = devices.read_preferences(device)?;
        Ok(OpenDeviceFile {
            device,
            generation: expected,
            name,
            csv_text: file.csv_text,
        })
    }
}

fn require_generation<S: DeviceStorage, B: BackupStore, C: Clock + Clone>(
    devices: &mut Devices<S, B, C>,
    device: StorageDeviceId,
    expected: DeviceGeneration,
) -> Result<(), QcmError> {
    let actual = devices.resolve_device(device)?.generation;
    if actual != expected {
        return Err(StorageError::Device(DeviceError::Stale { expected, actual }).into());
    }
    Ok(())
}

pub type DeviceShellState = DeviceShell<
    FileSystemDeviceStorage<DeviceVolumeSource>,
    FileSystemBackupStore,
    SharedSystemClock,
    NativeDeviceFolderPicker,
>;

#[must_use]
pub fn native_device_shell() -> DeviceShellState {
    let volumes = DeviceVolumeSource::default();
    let picker = NativeDeviceFolderPicker::new(volumes.clone());
    let devices = Devices::new(
        FileSystemDeviceStorage::new(volumes),
        FileSystemBackupStore::default_location(),
        SharedSystemClock::new(),
    );
    DeviceShell::new(devices, picker)
}
