//! The device library: what is on the stick, in what order, and taking one off.
//!
//! Deleting from someone's device cannot be undone from the device, so every
//! rule lives here and not in the window that calls it. No caller can pass its
//! way around a protected name, and nothing is removed until a copy already
//! exists off the drive.

use super::Devices;
use crate::clock::Clock;
use crate::confirmation::{ConfirmationId, ConfirmationKind, ConfirmationRequirement};
use crate::error::{
    BackupLocationDisplay, ConfigError, DeviceError, NameRejection, QcmError, StorageError,
};
use crate::operation::{OperationFingerprint, OperationId, OperationKind};
use crate::ports::storage::{
    BackupStore, DeviceFileName, DeviceStorage, MARKER_FILE_NAME, PREFERENCES_FILE_NAME,
    StorageDeviceId, check_deletable,
};

/// One of the five lights on the device, named rather than coloured.
///
/// The name is the value. Nothing in this app may signal by colour alone, and a
/// guide read aloud has to say "purple, grey, grey, grey, grey".
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum LedColour {
    Purple,
    Grey,
    Blue,
    Red,
}

impl LedColour {
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Purple => "purple",
            Self::Grey => "grey",
            Self::Blue => "blue",
            Self::Red => "red",
        }
    }
}

use LedColour::{Blue as B, Grey as G, Purple as P, Red as R};

/// The audited QuadStick Manager Program table, copied as data.
///
/// Five lights, left to right, for file numbers 1 to 32. There is no rule to
/// infer here and nothing past 32 is documented, so nothing is extrapolated.
const LED_PATTERNS: [[LedColour; 5]; 32] = [
    [P, G, G, G, G],
    [G, P, G, G, G],
    [G, G, P, G, G],
    [G, G, G, P, G],
    [G, G, G, G, P],
    [P, G, G, G, P],
    [G, P, G, G, P],
    [G, G, P, G, P],
    [G, G, G, P, P],
    [G, G, G, G, B],
    [P, G, G, G, B],
    [G, P, G, G, B],
    [G, G, P, G, B],
    [G, G, G, P, B],
    [G, G, G, G, R],
    [P, G, G, G, R],
    [G, P, G, G, R],
    [G, G, P, G, R],
    [G, G, G, P, R],
    [B, B, B, B, B],
    [P, B, B, B, B],
    [B, P, B, B, B],
    [B, B, P, B, B],
    [B, B, B, P, B],
    [B, B, B, B, P],
    [P, B, B, B, P],
    [B, P, B, B, P],
    [B, B, P, B, P],
    [B, B, B, P, P],
    [R, R, R, R, P],
    [P, R, R, R, R],
    [R, P, R, R, R],
];

/// The lights for a file number, or nothing outside the documented table.
#[must_use]
pub fn led_pattern(file_number: usize) -> &'static [LedColour] {
    match file_number.checked_sub(1) {
        Some(index) if index < LED_PATTERNS.len() => &LED_PATTERNS[index],
        _ => &[],
    }
}

/// The order the device steps through files when you cycle profiles.
///
/// `default.csv` is always first. `prefs.csv` is settings, not a profile, so it
/// is never selectable. A QuadStick drive is FAT, so macOS drops AppleDouble
/// sidecars like `._Racing.csv` next to anything copied onto it; they are
/// metadata and must never reach the list, the guide or delete.
#[must_use]
pub fn selection_order(names: &[DeviceFileName]) -> Vec<DeviceFileName> {
    let profiles = names.iter().filter(|name| {
        name.is_profile() && !name.as_str().eq_ignore_ascii_case(PREFERENCES_FILE_NAME)
    });

    // The legacy rule takes the first name matching default.csv and excludes
    // every name matching it from the tail, so a second one would be dropped.
    // A QuadStick drive is FAT and cannot hold two names differing only in
    // case, so this is unreachable on the device; it is written the shipped way
    // rather than improved on a path no device can reach.
    let mut ordered: Vec<DeviceFileName> = Vec::new();
    let mut rest: Vec<DeviceFileName> = Vec::new();
    for name in profiles {
        if name.as_str().eq_ignore_ascii_case(MARKER_FILE_NAME) {
            if ordered.is_empty() {
                ordered.push(name.clone());
            }
        } else {
            rest.push(name.clone());
        }
    }
    // Stable, so two names that differ only in case keep the order the device
    // listed them in, the way the legacy `OrderBy` did.
    rest.sort_by_key(|name| upper_key(name.as_str()));
    ordered.extend(rest);
    ordered
}

/// The legacy comparison was `StringComparer.OrdinalIgnoreCase`, which upper
/// cases each character on its own. Rust's `to_uppercase` can turn one character
/// into several, so a multi-character expansion is left alone rather than
/// changing how many characters are being compared.
fn upper_key(name: &str) -> Vec<char> {
    name.chars()
        .map(|c| {
            let mut upper = c.to_uppercase();
            match (upper.next(), upper.next()) {
                (Some(single), None) => single,
                _ => c,
            }
        })
        .collect()
}

/// One row of the guide a user reads while cycling profiles on the device.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GuideEntry {
    /// 1-based, the number the device counts to.
    pub file_number: usize,
    pub name: DeviceFileName,
    pub lights: &'static [LedColour],
}

/// The selection guide: the order, numbered, with the lights for each number.
#[must_use]
pub fn selection_guide(names: &[DeviceFileName]) -> Vec<GuideEntry> {
    selection_order(names)
        .into_iter()
        .enumerate()
        .map(|(index, name)| GuideEntry {
            file_number: index + 1,
            name,
            lights: led_pattern(index + 1),
        })
        .collect()
}

/// A delete that has passed every rule and is waiting to be acknowledged.
#[derive(Debug, PartialEq, Eq)]
pub struct DeletePlan {
    operation: OperationId,
    device: StorageDeviceId,
    generation: crate::ports::storage::DeviceGeneration,
    name: DeviceFileName,
    bytes: Vec<u8>,
    fingerprint: OperationFingerprint,
    confirmation: ConfirmationRequirement,
}

impl DeletePlan {
    #[must_use]
    pub const fn operation(&self) -> OperationId {
        self.operation
    }

    #[must_use]
    pub const fn name(&self) -> &DeviceFileName {
        &self.name
    }

    #[must_use]
    pub const fn confirmation(&self) -> &ConfirmationRequirement {
        &self.confirmation
    }

    /// How big the file is, so the window can say what is about to go.
    #[must_use]
    pub const fn bytes(&self) -> usize {
        self.bytes.len()
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DeleteReceipt {
    pub operation: OperationId,
    pub device: StorageDeviceId,
    pub name: DeviceFileName,
    /// Always present. A delete with no backup does not happen.
    pub backup: BackupLocationDisplay,
}

/// The text of one file on the device.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DeviceFileText {
    pub device: StorageDeviceId,
    pub csv_text: String,
}

impl<S: DeviceStorage, B: BackupStore, C: Clock + Clone> Devices<S, B, C> {
    /// Everything on the drive, in the order the device cycles it.
    ///
    /// The count of entries the adapter could not name travels with it. A list
    /// that quietly hides part of a drive is the bug this app keeps being
    /// reported for, so the window is given the number and says so.
    pub fn list_profiles(
        &mut self,
        device: StorageDeviceId,
    ) -> Result<(Vec<GuideEntry>, usize), QcmError> {
        let handle = self.resolve_device(device)?;
        let listing = self.storage.list_files(handle.device, handle.generation)?;
        let names: Vec<DeviceFileName> =
            listing.files.into_iter().map(|entry| entry.name).collect();
        Ok((selection_guide(&names), listing.unnameable))
    }

    /// Check every delete rule and take the copy the delete will need.
    ///
    /// The order matters and is the shipped one: a protected name is refused
    /// before anything is copied or removed. The backup is taken here rather
    /// than at execution time so a failure to copy stops the whole thing while
    /// the file is still whole.
    pub fn plan_delete(
        &mut self,
        device: StorageDeviceId,
        name: &DeviceFileName,
    ) -> Result<DeletePlan, QcmError> {
        check_deletable(name)?;
        let handle = self.resolve_device(device)?;
        let bytes = self
            .storage
            .read_file(handle.device, handle.generation, name)?;

        let operation = self.operations.mint();
        let fingerprint = OperationFingerprint::builder(OperationKind::DeleteDeviceProfile)
            .number("device", handle.device.raw())
            .number("generation", handle.generation.raw())
            .field("target", name.as_str())
            .number("bytes", bytes.len() as u64)
            .finish();
        let confirmation = self.confirmations.require(
            ConfirmationKind::DeleteDeviceProfile,
            fingerprint.clone(),
            format!("Remove {name} from the QuadStick. The device cannot undo this."),
        );

        Ok(DeletePlan {
            operation,
            device: handle.device,
            generation: handle.generation,
            name: name.clone(),
            bytes,
            fingerprint,
            confirmation,
        })
    }

    /// Copy the file off the device, then remove it.
    ///
    /// If the copy throws, the source is still there and nothing has been
    /// deleted. That order is the whole point.
    pub fn delete_profile(
        &mut self,
        plan: DeletePlan,
        confirmation: ConfirmationId,
    ) -> Result<DeleteReceipt, QcmError> {
        self.confirmations.redeem(
            confirmation,
            ConfirmationKind::DeleteDeviceProfile,
            &plan.fingerprint,
        )?;

        // Proven again. A stick pulled and pushed back while the dialog was open
        // comes back with a new generation and is refused rather than written to.
        let handle = self.resolve_device(plan.device)?;
        if handle.generation != plan.generation {
            return Err(StorageError::Device(DeviceError::Stale {
                expected: plan.generation,
                actual: handle.generation,
            })
            .into());
        }

        let receipt = self.backups.store(&plan.name, &plan.bytes)?;
        self.storage
            .delete_file(handle.device, handle.generation, &plan.name)?;
        self.invalidate_device_cache();
        Ok(DeleteReceipt {
            operation: plan.operation,
            device: handle.device,
            name: plan.name,
            backup: receipt.location,
        })
    }

    /// Read `prefs.csv` off the device.
    ///
    /// The device's own settings, not a profile. It comes back as text so the
    /// preference editor can work on it; putting it back is an ordinary install
    /// of a profile named `prefs.csv`, which is what makes the write go through
    /// the same backup, read-back and restore the profiles get.
    pub fn read_preferences(
        &mut self,
        device: StorageDeviceId,
    ) -> Result<DeviceFileText, QcmError> {
        let name = DeviceFileName::new(PREFERENCES_FILE_NAME)
            .map_err(|reason| StorageError::NameRejected { reason })?;
        let handle = self.resolve_device(device)?;
        let bytes = self
            .storage
            .read_file(handle.device, handle.generation, &name)?;
        let csv_text = String::from_utf8(bytes).map_err(|_| {
            // Not clamped, not repaired, not guessed at. A settings file this
            // app cannot read is one it must not rewrite.
            QcmError::Config(ConfigError::Unreadable)
        })?;
        Ok(DeviceFileText {
            device: handle.device,
            csv_text,
        })
    }

    /// Read the profile the device falls back to.
    pub fn read_default_config(
        &mut self,
        device: StorageDeviceId,
    ) -> Result<DeviceFileText, QcmError> {
        let name = DeviceFileName::new(MARKER_FILE_NAME)
            .map_err(|reason| StorageError::NameRejected { reason })?;
        let handle = self.resolve_device(device)?;
        let bytes = self
            .storage
            .read_file(handle.device, handle.generation, &name)?;
        let csv_text =
            String::from_utf8(bytes).map_err(|_| QcmError::Config(ConfigError::Unreadable))?;
        Ok(DeviceFileText {
            device: handle.device,
            csv_text,
        })
    }

    /// Read one profile off the device, so it can be opened as a working copy.
    pub fn read_profile(
        &mut self,
        device: StorageDeviceId,
        name: &DeviceFileName,
    ) -> Result<DeviceFileText, QcmError> {
        if !name.is_profile() {
            return Err(StorageError::NameRejected {
                reason: NameRejection::NotCsv,
            }
            .into());
        }
        let handle = self.resolve_device(device)?;
        let bytes = self
            .storage
            .read_file(handle.device, handle.generation, name)?;
        let csv_text =
            String::from_utf8(bytes).map_err(|_| QcmError::Config(ConfigError::Unreadable))?;
        Ok(DeviceFileText {
            device: handle.device,
            csv_text,
        })
    }
}
