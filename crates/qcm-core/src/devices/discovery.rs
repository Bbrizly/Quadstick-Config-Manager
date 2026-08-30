//! Finding mounted QuadSticks, and the short cache in front of it.
//!
//! The enumeration itself belongs to the adapter. What lives here is everything
//! above it: turning a raw probe into a candidate a window may hold, deciding
//! whether the set actually changed, and proving an id still points at the drive
//! it pointed at before anything is done to it.

use super::Devices;
use crate::clock::{Clock, Moment};
use crate::error::StorageError;
use crate::ports::storage::{
    BackupStore, DeviceDisplayName, DeviceGeneration, DeviceStorage, StorageDeviceId, StorageProbe,
};
use std::time::Duration;

/// How long a display-only device list stays good.
///
/// Ported from the legacy `Device.FindCandidatesCached`, including the reason.
/// Refreshing the editor, saving and undoing all ask for the device list, and a
/// live scan each time enumerates every volume and stats `default.csv` on the
/// thread the user is waiting on, which a spun-down USB stick can stall for
/// seconds. A short window collapses the burst. Nothing destructive reads it:
/// every write revalidates the device first.
pub const DEFAULT_SCAN_TTL: Duration = Duration::from_secs(3);

/// A cached list and the moment it was taken.
///
/// The two travel together in one value for the same reason the legacy code
/// kept them in one record: a list and its timestamp updated separately can be
/// read half done, giving a fresh list a stale time or the other way about.
#[derive(Debug, Clone)]
pub struct Scan {
    pub(super) devices: Vec<DeviceSummary>,
    pub(super) at: Moment,
}

/// One mounted QuadStick, as a window is allowed to see it.
///
/// Everything here is either opaque or display text. There is no mount point,
/// no drive letter and no volume path, so a device list rendered into a bug
/// report cannot spell out where the user's drives are.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DeviceSummary {
    pub id: StorageDeviceId,
    pub generation: DeviceGeneration,
    pub display_name: DeviceDisplayName,
    pub writable: bool,
    /// `None` when the platform will not say. Absence is not zero: a drive that
    /// will not report its size must not be shown as full.
    pub free_bytes: Option<u64>,
}

impl DeviceSummary {
    /// Rebuild the display name from the probe rather than trusting it.
    ///
    /// [`DeviceDisplayName::new`] is idempotent, so this costs an allocation and
    /// buys the guarantee that an adapter which one day builds a probe some
    /// other way still cannot put a mount point on screen.
    #[must_use]
    pub fn from_probe(probe: &StorageProbe) -> Self {
        Self {
            id: probe.id,
            generation: probe.generation,
            display_name: DeviceDisplayName::new(probe.display_name.as_str()),
            writable: probe.capabilities.writable,
            free_bytes: probe.capabilities.free_bytes,
        }
    }

    /// What has to change before the app tells anyone the drives moved.
    ///
    /// Free space is left out on purpose. It moves on its own as the firmware
    /// writes, and an event on every tick is exactly what the discovery spec
    /// forbids.
    fn same_membership(&self, other: &Self) -> bool {
        self.id == other.id
            && self.generation == other.generation
            && self.writable == other.writable
            && self.display_name == other.display_name
    }
}

/// The result of a live scan.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DeviceScan {
    pub devices: Vec<DeviceSummary>,
    /// True when the set differs from the last one this service saw. The one
    /// thing worth waking the window for.
    pub changed: bool,
}

/// A device id plus the generation it was proven at.
///
/// Every scoped call takes the generation back, so a mount point the OS handed
/// to an unrelated volume fails the check instead of being written to. Copy on
/// purpose: it is two numbers and holding one prevents nothing.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DeviceHandle {
    pub device: StorageDeviceId,
    pub generation: DeviceGeneration,
}

impl<S: DeviceStorage, B: BackupStore, C: Clock + Clone> Devices<S, B, C> {
    /// Enumerate now, whatever the cache says.
    pub fn refresh_devices(&mut self) -> Result<DeviceScan, StorageError> {
        let probes = self.storage.discover()?;
        let devices: Vec<DeviceSummary> = probes.iter().map(DeviceSummary::from_probe).collect();
        let changed = match self.scan.as_ref() {
            None => true,
            Some(seen) => {
                seen.devices.len() != devices.len()
                    || !seen
                        .devices
                        .iter()
                        .zip(&devices)
                        .all(|(before, now)| before.same_membership(now))
            }
        };
        self.scan = Some(Scan {
            devices: devices.clone(),
            at: self.clock.now(),
        });
        Ok(DeviceScan { devices, changed })
    }

    /// The device list for display, from the cache when it is fresh enough.
    ///
    /// Never used to decide a write. Install and delete revalidate the device
    /// they were given, which is what makes a stale entry here harmless.
    pub fn list_devices(&mut self) -> Result<Vec<DeviceSummary>, StorageError> {
        if let Some(seen) = self.scan.as_ref()
            && self.age_of(seen) < self.scan_ttl
        {
            return Ok(seen.devices.clone());
        }
        Ok(self.refresh_devices()?.devices)
    }

    /// Drop the cache so the next display lookup enumerates again.
    ///
    /// An explicit Refresh must not have to wait out the window, and neither
    /// must a device the app has just written to.
    pub fn invalidate_device_cache(&mut self) {
        self.scan = None;
    }

    /// True while the cached list would be served without enumerating.
    #[must_use]
    pub fn device_cache_is_fresh(&self) -> bool {
        self.scan
            .as_ref()
            .is_some_and(|seen| self.age_of(seen) < self.scan_ttl)
    }

    /// Saturating, because the clock is monotonic and a negative age would mean
    /// a bug somewhere else. Reading it as zero keeps the cache fresh rather
    /// than making the app enumerate in a loop.
    fn age_of(&self, seen: &Scan) -> Duration {
        self.clock
            .now()
            .since_start()
            .saturating_sub(seen.at.since_start())
    }

    /// Prove the id still points at a mounted QuadStick and say which
    /// generation it is now.
    ///
    /// The marker is checked again here, not once at discovery: a user can
    /// reformat a stick with the app open, and the window would still be showing
    /// the row it found a minute ago.
    pub fn resolve_device(
        &mut self,
        device: StorageDeviceId,
    ) -> Result<DeviceHandle, StorageError> {
        let probe = self.storage.revalidate(device)?;
        let summary = DeviceSummary::from_probe(&probe);
        if let Some(seen) = self.scan.as_mut()
            && let Some(entry) = seen.devices.iter_mut().find(|found| found.id == device)
        {
            *entry = summary;
        }
        Ok(DeviceHandle {
            device,
            generation: probe.generation,
        })
    }
}
