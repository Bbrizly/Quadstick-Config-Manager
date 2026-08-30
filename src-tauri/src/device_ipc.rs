//! Device command wire types.
//!
//! The WebView gets opaque device/operation/confirmation ids and validated file
//! names only. Mount points, paths, staged-write handles and install bytes stay
//! native.

use qcm_core::confirmation::{ConfirmationId, ConfirmationRequirement};
use qcm_core::devices::{DeletePlan, DeleteReceipt, DeviceSummary, GuideEntry, InstallPlan, InstallReceipt};
use qcm_core::error::{ProfileError, QcmError, RequestError};
use qcm_core::operation::OperationId;
use qcm_core::ports::storage::{DeviceGeneration, StorageDeviceId, StorageProbe};
use serde::{Deserialize, Serialize};
use std::str::FromStr;

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DeviceRequest {
    pub device_id: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PrepareInstallRequest {
    pub session_id: String,
    pub device_id: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CommitInstallRequest {
    pub plan_id: String,
    #[serde(default)]
    pub confirmation_id: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DeviceFileRequest {
    pub device_id: String,
    pub expected_generation: u64,
    pub name: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CommitDeleteRequest {
    pub plan_id: String,
    pub confirmation_id: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DeviceGenerationRequest {
    pub device_id: String,
    pub expected_generation: u64,
}

pub fn device_id(raw: &str) -> Result<StorageDeviceId, QcmError> {
    raw.strip_prefix("dev-")
        .and_then(|digits| digits.parse::<u64>().ok())
        .map(StorageDeviceId::from_raw)
        .ok_or_else(|| RequestError::OutOfRange { what: "device id" }.into())
}

pub fn operation_id(raw: &str) -> Result<OperationId, QcmError> {
    OperationId::from_str(raw)
        .map_err(|()| RequestError::OutOfRange { what: "operation id" }.into())
}

pub fn confirmation_id(raw: &str) -> Result<ConfirmationId, QcmError> {
    ConfirmationId::from_str(raw)
        .map_err(|()| RequestError::OutOfRange { what: "confirmation id" }.into())
}

pub fn optional_confirmation_id(raw: Option<&str>) -> Result<Option<ConfirmationId>, QcmError> {
    raw.map(confirmation_id).transpose()
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DeviceSummaryDto {
    pub device_id: String,
    pub generation: u64,
    pub display_name: String,
    pub writable: bool,
    pub free_bytes: Option<u64>,
}

impl From<&DeviceSummary> for DeviceSummaryDto {
    fn from(device: &DeviceSummary) -> Self {
        Self {
            device_id: device.id.to_string(),
            generation: device.generation.raw(),
            display_name: device.display_name.to_string(),
            writable: device.writable,
            free_bytes: device.free_bytes,
        }
    }
}

impl From<&StorageProbe> for DeviceSummaryDto {
    fn from(device: &StorageProbe) -> Self {
        Self {
            device_id: device.id.to_string(),
            generation: device.generation.raw(),
            display_name: device.display_name.to_string(),
            writable: device.capabilities.writable,
            free_bytes: device.capabilities.free_bytes,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DevicePresenceSnapshotDto {
    pub devices: Vec<DeviceSummaryDto>,
    pub changed: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DeviceProfileEntryDto {
    pub name: String,
    pub file_number: usize,
    pub lights: Vec<String>,
    pub protected: bool,
}

impl From<&GuideEntry> for DeviceProfileEntryDto {
    fn from(entry: &GuideEntry) -> Self {
        Self {
            name: entry.name.to_string(),
            file_number: entry.file_number,
            lights: entry
                .lights
                .iter()
                .map(|colour| colour.as_str().to_owned())
                .collect(),
            protected: entry.name.role().is_protected(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DeviceLibrarySnapshotDto {
    pub device_id: String,
    pub generation: u64,
    pub files: Vec<DeviceProfileEntryDto>,
    pub protected_files: Vec<String>,
    pub unnameable: usize,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ConfirmationDto {
    pub confirmation_id: String,
    pub kind: String,
    pub summary: String,
}

impl From<&ConfirmationRequirement> for ConfirmationDto {
    fn from(required: &ConfirmationRequirement) -> Self {
        Self {
            confirmation_id: required.id.to_string(),
            kind: required.kind.as_str().to_owned(),
            summary: required.summary.clone(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallPlanDto {
    pub plan_id: String,
    pub target: String,
    pub bytes: usize,
    pub confirmation: Option<ConfirmationDto>,
}

impl From<&InstallPlan> for InstallPlanDto {
    fn from(plan: &InstallPlan) -> Self {
        Self {
            plan_id: plan.operation().to_string(),
            target: plan.target().to_string(),
            bytes: plan.bytes().len(),
            confirmation: plan.confirmation().map(ConfirmationDto::from),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallReceiptDto {
    pub operation_id: String,
    pub device_id: String,
    pub target: String,
    pub bytes: usize,
    pub backup: Option<String>,
    pub confirmed_on_device: bool,
    pub stages: Vec<String>,
}

impl From<&InstallReceipt> for InstallReceiptDto {
    fn from(receipt: &InstallReceipt) -> Self {
        Self {
            operation_id: receipt.operation.to_string(),
            device_id: receipt.device.to_string(),
            target: receipt.target.to_string(),
            bytes: receipt.bytes,
            backup: receipt.backup.as_ref().map(ToString::to_string),
            confirmed_on_device: receipt.confirmed_on_device,
            stages: receipt
                .stages
                .iter()
                .map(|stage| stage.as_str().to_owned())
                .collect(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DeletePlanDto {
    pub plan_id: String,
    pub name: String,
    pub bytes: usize,
    pub confirmation: ConfirmationDto,
}

impl From<&DeletePlan> for DeletePlanDto {
    fn from(plan: &DeletePlan) -> Self {
        Self {
            plan_id: plan.operation().to_string(),
            name: plan.name().to_string(),
            bytes: plan.bytes(),
            confirmation: ConfirmationDto::from(plan.confirmation()),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DeleteReceiptDto {
    pub operation_id: String,
    pub device_id: String,
    pub name: String,
    pub backup: String,
}

impl From<&DeleteReceipt> for DeleteReceiptDto {
    fn from(receipt: &DeleteReceipt) -> Self {
        Self {
            operation_id: receipt.operation.to_string(),
            device_id: receipt.device.to_string(),
            name: receipt.name.to_string(),
            backup: receipt.backup.to_string(),
        }
    }
}

#[derive(Debug, Clone)]
pub struct OpenDeviceFile {
    pub device: StorageDeviceId,
    pub generation: DeviceGeneration,
    pub name: qcm_core::ports::storage::DeviceFileName,
    pub csv_text: String,
}

pub fn unknown_profile_session() -> QcmError {
    ProfileError::UnknownSession.into()
}
