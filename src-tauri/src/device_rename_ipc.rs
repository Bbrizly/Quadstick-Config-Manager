//! Wire types for device-profile rename.
//!
//! The request carries only an opaque device id, its rendered generation and
//! direct-child file names. No path can cross the WebView boundary.

use qcm_core::devices::RenameReceipt;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RenameDeviceProfileRequest {
    pub device_id: String,
    pub expected_generation: u64,
    pub from: String,
    pub to: String,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RenameDeviceProfileReceiptDto {
    pub device_id: String,
    pub from: String,
    pub to: String,
    pub backup: String,
}

impl From<&RenameReceipt> for RenameDeviceProfileReceiptDto {
    fn from(receipt: &RenameReceipt) -> Self {
        Self {
            device_id: receipt.device.to_string(),
            from: receipt.from.to_string(),
            to: receipt.to.to_string(),
            backup: receipt.backup.to_string(),
        }
    }
}
