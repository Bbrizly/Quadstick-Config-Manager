//! The device storage port.
//!
//! There is no `read_file(path)` here and there never will be. A caller names a
//! device by an opaque ID it was given, and a file by a validated direct-child
//! name; the adapter owns the mount point and is the only thing that ever sees
//! one. That is the whole reason this is a trait and not a filesystem helper.
//!
//! The operations are cut to match the transaction in `Device.Install`:
//! revalidate the marker, back up off the device, stage a temp write, read it
//! back, replace, restore if the replace broke, clean up. Nothing wider.

use crate::error::{DeviceError, NameRejection, StorageError};
use qcm_config::{is_invalid_filename_char, is_reserved_windows_name, is_too_long_for_device};
use std::fmt;

/// The file that proves a volume is a QuadStick. Its presence is checked again
/// immediately before every destructive step, never once at discovery.
pub const MARKER_FILE_NAME: &str = "default.csv";

/// Device-wide settings. Writing it changes every profile at once.
pub const PREFERENCES_FILE_NAME: &str = "prefs.csv";

/// Opaque device handle. The mount point stays inside the adapter.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct StorageDeviceId(u64);

impl StorageDeviceId {
    #[must_use]
    pub const fn from_raw(raw: u64) -> Self {
        Self(raw)
    }

    #[must_use]
    pub const fn raw(self) -> u64 {
        self.0
    }
}

impl fmt::Display for StorageDeviceId {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "dev-{}", self.0)
    }
}

/// Bumped on every rediscovery. An operation captures the generation it planned
/// against and hands it back, so a drive letter reused by an unrelated volume
/// fails the check instead of getting written to.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct DeviceGeneration(u64);

impl DeviceGeneration {
    #[must_use]
    pub const fn from_raw(raw: u64) -> Self {
        Self(raw)
    }

    #[must_use]
    pub const fn raw(self) -> u64 {
        self.0
    }

    #[must_use]
    pub const fn next(self) -> Self {
        Self(self.0.wrapping_add(1))
    }
}

impl fmt::Display for DeviceGeneration {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", self.0)
    }
}

/// A volume label, cut down to something printable. Never a path: an adapter
/// that has no label falls back to a generic word rather than the mount point.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct DeviceDisplayName(String);

impl DeviceDisplayName {
    const MAX: usize = 64;

    #[must_use]
    pub fn new(label: &str) -> Self {
        let cleaned: String = label
            .chars()
            .filter(|c| !c.is_control() && !matches!(c, '/' | '\\'))
            .take(Self::MAX)
            .collect();
        let trimmed = cleaned.trim();
        if trimmed.is_empty() {
            Self("QuadStick drive".to_owned())
        } else {
            Self(trimmed.to_owned())
        }
    }

    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl fmt::Display for DeviceDisplayName {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

/// What a file on the device is for. `default.csv` and `prefs.csv` are the
/// device's own, and the rules around them are different from a game profile's.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum DeviceFileRole {
    Profile,
    DefaultConfig,
    DevicePreferences,
}

impl DeviceFileRole {
    /// Protected files are never removed by the normal delete path and never
    /// overwritten without their own confirmation.
    #[must_use]
    pub const fn is_protected(self) -> bool {
        matches!(self, Self::DefaultConfig | Self::DevicePreferences)
    }
}

/// A name of something sitting directly in the device root.
///
/// This is what the device already holds, so it is permissive on purpose: a
/// file copied on by hand with a space or 40 characters in its name still has
/// to be listable and deletable. What it guarantees is the part that matters
/// here, that the name cannot walk out of the root or name a place on the host.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct DeviceFileName(String);

impl DeviceFileName {
    const MAX: usize = 255;

    /// Reject anything that is not a plain direct child.
    pub fn new(name: &str) -> Result<Self, NameRejection> {
        if name.is_empty() {
            return Err(NameRejection::Empty);
        }
        if name.len() > Self::MAX
            || name == "."
            || name == ".."
            || name
                .chars()
                .any(|c| matches!(c, '/' | '\\' | ':') || c.is_control())
            || name.trim() != name
        {
            return Err(NameRejection::NotAPlainName);
        }
        Ok(Self(name.to_owned()))
    }

    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }

    #[must_use]
    pub fn role(&self) -> DeviceFileRole {
        if self.0.eq_ignore_ascii_case(MARKER_FILE_NAME) {
            DeviceFileRole::DefaultConfig
        } else if self.0.eq_ignore_ascii_case(PREFERENCES_FILE_NAME) {
            DeviceFileRole::DevicePreferences
        } else {
            DeviceFileRole::Profile
        }
    }

    /// A profile the file list may show.
    ///
    /// A QuadStick drive is FAT, so macOS drops AppleDouble sidecars like
    /// `._Racing.csv` beside anything copied onto it. They are metadata, not
    /// profiles, and a dot leader is enough to know: every system that writes
    /// them hides them the same way.
    #[must_use]
    pub fn is_profile(&self) -> bool {
        !self.0.starts_with('.') && self.0.to_ascii_lowercase().ends_with(".csv")
    }
}

impl fmt::Display for DeviceFileName {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

/// A name this app is allowed to create on a device.
///
/// Stricter than [`DeviceFileName`] on purpose. Writing is where the app can do
/// harm, so a target name has to survive both filesystems the user might sync
/// through and fit the firmware's 31 character slot.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct SafeDeviceFileName(DeviceFileName);

impl SafeDeviceFileName {
    pub fn new(name: &str) -> Result<Self, NameRejection> {
        let plain = DeviceFileName::new(name)?;
        if name.chars().any(is_invalid_filename_char) {
            return Err(NameRejection::NotAPlainName);
        }
        if name.starts_with('.') {
            return Err(NameRejection::HiddenName);
        }
        if !name.to_ascii_lowercase().ends_with(".csv") || name.encode_utf16().count() <= 4 {
            return Err(NameRejection::NotCsv);
        }
        if is_reserved_windows_name(name) {
            return Err(NameRejection::ReservedOnWindows);
        }
        if is_too_long_for_device(name) {
            return Err(NameRejection::TooLongForDevice);
        }
        Ok(Self(plain))
    }

    #[must_use]
    pub fn as_str(&self) -> &str {
        self.0.as_str()
    }

    #[must_use]
    pub const fn as_device_name(&self) -> &DeviceFileName {
        &self.0
    }

    #[must_use]
    pub fn role(&self) -> DeviceFileRole {
        self.0.role()
    }
}

impl fmt::Display for SafeDeviceFileName {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.0.as_str())
    }
}

impl From<SafeDeviceFileName> for DeviceFileName {
    fn from(name: SafeDeviceFileName) -> Self {
        name.0
    }
}

/// What the device can do right now, asked rather than inferred from OS type.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct StorageCapabilities {
    pub writable: bool,
    /// `None` when the platform will not say. Absence is not zero.
    pub free_bytes: Option<u64>,
}

/// One mounted candidate, as the core sees it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StorageProbe {
    pub id: StorageDeviceId,
    pub generation: DeviceGeneration,
    pub display_name: DeviceDisplayName,
    pub capabilities: StorageCapabilities,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DeviceFileEntry {
    pub name: DeviceFileName,
    pub size_bytes: u64,
}

/// What is on the device.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct DeviceListing {
    pub files: Vec<DeviceFileEntry>,
    /// Entries the adapter could not turn into a name it would show. Counted,
    /// not dropped: a list that quietly hides part of a drive is the bug this
    /// app keeps getting reported for.
    pub unnameable: usize,
}

/// A temp file written beside the target and not yet swapped in.
///
/// Opaque and not `Clone`: committing or discarding consumes it, so the same
/// staged write cannot be swapped into place twice.
#[derive(Debug, PartialEq, Eq)]
pub struct StagedWrite {
    device: StorageDeviceId,
    generation: DeviceGeneration,
    target: SafeDeviceFileName,
    token: u64,
}

impl StagedWrite {
    #[must_use]
    pub const fn new(
        device: StorageDeviceId,
        generation: DeviceGeneration,
        target: SafeDeviceFileName,
        token: u64,
    ) -> Self {
        Self {
            device,
            generation,
            target,
            token,
        }
    }

    #[must_use]
    pub const fn device(&self) -> StorageDeviceId {
        self.device
    }

    #[must_use]
    pub const fn generation(&self) -> DeviceGeneration {
        self.generation
    }

    #[must_use]
    pub const fn target(&self) -> &SafeDeviceFileName {
        &self.target
    }

    #[must_use]
    pub const fn token(&self) -> u64 {
        self.token
    }
}

/// A commit that did not happen.
///
/// The staged file comes back whenever it may still be sitting on the device,
/// because somebody has to remove it and the caller is the only one that knows
/// the operation is over. The legacy device layer had a `finally` block for
/// exactly this: a stray `.qscm-tmp` next to a profile is litter a user cannot
/// tell apart from the real thing.
#[derive(Debug, PartialEq, Eq)]
pub struct CommitFailure {
    pub error: StorageError,
    pub staged: Option<StagedWrite>,
}

/// Where a rescue copy went. The location is display text, not a path the
/// caller can hand back to anything.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BackupReceipt {
    pub location: crate::error::BackupLocationDisplay,
    pub bytes: usize,
}

/// Scoped device storage.
///
/// Blocking on purpose. A real adapter runs it on a worker; making the port
/// async would pull a runtime into a crate that is meant to have no OS
/// dependency at all, and the transaction above it is a sequence, not a race.
pub trait DeviceStorage {
    /// Every mounted volume that proves it is a QuadStick.
    fn discover(&self) -> Result<Vec<StorageProbe>, StorageError>;

    /// Prove the ID still points at a mounted QuadStick, and say which
    /// generation it is now. Called immediately before anything destructive.
    fn revalidate(&self, device: StorageDeviceId) -> Result<StorageProbe, StorageError>;

    fn list_files(
        &self,
        device: StorageDeviceId,
        expected: DeviceGeneration,
    ) -> Result<DeviceListing, StorageError>;

    fn read_file(
        &self,
        device: StorageDeviceId,
        expected: DeviceGeneration,
        name: &DeviceFileName,
    ) -> Result<Vec<u8>, StorageError>;

    /// Create the temp file next to the target and write every byte, flushing
    /// as far as the platform allows. The target is untouched until
    /// [`DeviceStorage::commit_staged`].
    fn stage_write(
        &self,
        device: StorageDeviceId,
        expected: DeviceGeneration,
        target: &SafeDeviceFileName,
        bytes: &[u8],
    ) -> Result<StagedWrite, StorageError>;

    /// Reopen the staged file and compare it byte for byte. A mismatch is
    /// [`StorageError::VerifyFailed`] and the target is still untouched.
    fn verify_staged(&self, staged: &StagedWrite, expected: &[u8]) -> Result<(), StorageError>;

    /// Put the staged file in the target's place.
    ///
    /// Same directory, same volume, which is the most a FAT drive will give:
    /// this narrows the window where neither file is in place, it does not
    /// close it. The read-back and the backup are what make it safe.
    fn commit_staged(&self, staged: StagedWrite) -> Result<(), CommitFailure>;

    /// Remove a staged file that will not be committed. Best effort by
    /// contract: the caller may drop the result, and a failure here must never
    /// replace the error that led to the discard.
    fn discard_staged(&self, staged: StagedWrite) -> Result<(), StorageError>;

    /// Put known-good bytes back under an existing name after a broken swap.
    ///
    /// One call, because the safe way to do it is to write beside the target
    /// and move it into place. A plain copy over the target can be cut short by
    /// the same full volume that broke the swap, and the device reads a profile
    /// until the first blank line, so it would load the truncated half without
    /// complaining and silently drop every binding after the cut.
    fn restore_file(
        &self,
        device: StorageDeviceId,
        expected: DeviceGeneration,
        name: &DeviceFileName,
        bytes: &[u8],
    ) -> Result<(), StorageError>;

    fn delete_file(
        &self,
        device: StorageDeviceId,
        expected: DeviceGeneration,
        name: &DeviceFileName,
    ) -> Result<(), StorageError>;
}

/// The off-device backup area.
///
/// Separate from [`DeviceStorage`] because backups must not live on the
/// QuadStick: the firmware deletes files it does not recognize at startup, and
/// a backup on the drive that just failed is no backup at all. No path crosses
/// this trait in either direction.
pub trait BackupStore {
    /// Copy bytes into the backup area under a name derived from `name`.
    ///
    /// Two backups of the same file in the same instant must both survive, so
    /// an implementation makes the name unique rather than overwriting.
    fn store(&self, name: &DeviceFileName, bytes: &[u8]) -> Result<BackupReceipt, StorageError>;
}

/// Both ports forward through a reference, so one adapter can back the device
/// service, a diagnostics view and a test at the same time without any of them
/// owning it. Every method takes `&self` already, which is what makes this
/// nothing more than forwarding.
impl<T: DeviceStorage + ?Sized> DeviceStorage for &T {
    fn discover(&self) -> Result<Vec<StorageProbe>, StorageError> {
        (**self).discover()
    }

    fn revalidate(&self, device: StorageDeviceId) -> Result<StorageProbe, StorageError> {
        (**self).revalidate(device)
    }

    fn list_files(
        &self,
        device: StorageDeviceId,
        expected: DeviceGeneration,
    ) -> Result<DeviceListing, StorageError> {
        (**self).list_files(device, expected)
    }

    fn read_file(
        &self,
        device: StorageDeviceId,
        expected: DeviceGeneration,
        name: &DeviceFileName,
    ) -> Result<Vec<u8>, StorageError> {
        (**self).read_file(device, expected, name)
    }

    fn stage_write(
        &self,
        device: StorageDeviceId,
        expected: DeviceGeneration,
        target: &SafeDeviceFileName,
        bytes: &[u8],
    ) -> Result<StagedWrite, StorageError> {
        (**self).stage_write(device, expected, target, bytes)
    }

    fn verify_staged(&self, staged: &StagedWrite, expected: &[u8]) -> Result<(), StorageError> {
        (**self).verify_staged(staged, expected)
    }

    fn commit_staged(&self, staged: StagedWrite) -> Result<(), CommitFailure> {
        (**self).commit_staged(staged)
    }

    fn discard_staged(&self, staged: StagedWrite) -> Result<(), StorageError> {
        (**self).discard_staged(staged)
    }

    fn restore_file(
        &self,
        device: StorageDeviceId,
        expected: DeviceGeneration,
        name: &DeviceFileName,
        bytes: &[u8],
    ) -> Result<(), StorageError> {
        (**self).restore_file(device, expected, name, bytes)
    }

    fn delete_file(
        &self,
        device: StorageDeviceId,
        expected: DeviceGeneration,
        name: &DeviceFileName,
    ) -> Result<(), StorageError> {
        (**self).delete_file(device, expected, name)
    }
}

impl<T: BackupStore + ?Sized> BackupStore for &T {
    fn store(&self, name: &DeviceFileName, bytes: &[u8]) -> Result<BackupReceipt, StorageError> {
        (**self).store(name, bytes)
    }
}

/// The delete rules, in one place, because both the fake and the real adapter
/// have to enforce them and the window is not allowed to be the thing that
/// remembers. Order matters: a protected name is refused before anything is
/// copied or removed.
pub fn check_deletable(name: &DeviceFileName) -> Result<(), StorageError> {
    if name.role().is_protected() {
        return Err(StorageError::ProtectedFile { name: name.clone() });
    }
    if !name.is_profile() {
        return Err(StorageError::NameRejected {
            reason: NameRejection::NotCsv,
        });
    }
    Ok(())
}

/// The generation check every scoped call makes before touching anything. A
/// mount point the OS handed to another volume is the one way this app could
/// write to a stranger's drive, so the check is a shared function rather than
/// something each adapter remembers to do.
pub fn check_generation(
    expected: DeviceGeneration,
    actual: DeviceGeneration,
) -> Result<(), StorageError> {
    if expected == actual {
        return Ok(());
    }
    Err(StorageError::Device(DeviceError::Stale {
        expected,
        actual,
    }))
}

#[cfg(test)]
mod tests {
    use super::{
        DeviceDisplayName, DeviceFileName, DeviceFileRole, SafeDeviceFileName, check_deletable,
    };
    use crate::error::{NameRejection, StorageError, looks_like_absolute_path};

    #[test]
    fn a_device_file_name_can_never_walk_out_of_the_root() {
        for attempt in [
            "../outside.csv",
            "sub/game.csv",
            "sub\\game.csv",
            "/Users/b/game.csv",
            "C:\\Users\\b\\game.csv",
            "..",
            ".",
            "",
        ] {
            assert!(
                DeviceFileName::new(attempt).is_err(),
                "{attempt} must be refused"
            );
        }
    }

    // The type is what makes an error message safe to print: an accepted name
    // cannot be an absolute path, so no error carrying one can leak a location.
    #[test]
    fn an_accepted_name_is_never_a_path() {
        let name = DeviceFileName::new("Racing.csv").expect("plain name");
        assert!(!looks_like_absolute_path(name.as_str()));
        assert!(!name.as_str().contains('/'));
        assert!(!name.as_str().contains('\\'));
    }

    #[test]
    fn a_name_the_device_holds_stays_listable_even_when_it_is_not_writable() {
        // A name copied on by hand: too long to write, still has to be
        // listed and deleted.
        let long = format!("{}.csv", "a".repeat(40));
        assert!(DeviceFileName::new(&long).is_ok());
        assert_eq!(
            SafeDeviceFileName::new(&long),
            Err(NameRejection::TooLongForDevice)
        );
        assert!(DeviceFileName::new("My Game.csv").is_ok());
    }

    #[test]
    fn the_writable_name_rules_match_the_firmware_slot_and_windows() {
        assert!(SafeDeviceFileName::new(&format!("{}.csv", "a".repeat(27))).is_ok());
        assert_eq!(
            SafeDeviceFileName::new(&format!("{}.csv", "a".repeat(28))),
            Err(NameRejection::TooLongForDevice)
        );
        for reserved in ["NUL.csv", "con.csv", "LPT1.csv", "CON.old.csv"] {
            assert_eq!(
                SafeDeviceFileName::new(reserved),
                Err(NameRejection::ReservedOnWindows),
                "{reserved}"
            );
        }
        assert_eq!(
            SafeDeviceFileName::new("notes.txt"),
            Err(NameRejection::NotCsv)
        );
        assert_eq!(
            SafeDeviceFileName::new(".csv"),
            Err(NameRejection::HiddenName)
        );
        assert_eq!(
            SafeDeviceFileName::new("._Racing.csv"),
            Err(NameRejection::HiddenName)
        );
        assert_eq!(
            SafeDeviceFileName::new("my\u{0}game.csv"),
            Err(NameRejection::NotAPlainName)
        );
        assert!(SafeDeviceFileName::new("racing.csv").is_ok());
    }

    #[test]
    fn device_files_know_what_they_are_whatever_the_case() {
        for name in ["default.csv", "Default.CSV", "DEFAULT.csv"] {
            assert_eq!(
                DeviceFileName::new(name).expect("plain").role(),
                DeviceFileRole::DefaultConfig
            );
        }
        for name in ["prefs.csv", "Prefs.Csv", "PREFS.CSV"] {
            assert_eq!(
                DeviceFileName::new(name).expect("plain").role(),
                DeviceFileRole::DevicePreferences
            );
        }
        assert_eq!(
            DeviceFileName::new("racing.csv").expect("plain").role(),
            DeviceFileRole::Profile
        );
        assert!(DeviceFileRole::DefaultConfig.is_protected());
        assert!(DeviceFileRole::DevicePreferences.is_protected());
        assert!(!DeviceFileRole::Profile.is_protected());
    }

    #[test]
    fn the_sidecars_macos_writes_to_fat_drives_are_not_profiles() {
        for hidden in ["._Racing.csv", "._prefs.csv", ".hidden.csv"] {
            assert!(!DeviceFileName::new(hidden).expect("plain").is_profile());
        }
        assert!(
            DeviceFileName::new("Racing.csv")
                .expect("plain")
                .is_profile()
        );
        assert!(
            !DeviceFileName::new("notes.txt")
                .expect("plain")
                .is_profile()
        );
    }

    #[test]
    fn delete_refuses_the_device_own_files_and_anything_that_is_not_a_profile() {
        for protected in ["default.csv", "Prefs.Csv"] {
            let name = DeviceFileName::new(protected).expect("plain");
            assert!(matches!(
                check_deletable(&name),
                Err(StorageError::ProtectedFile { .. })
            ));
        }
        let sidecar = DeviceFileName::new("._Racing.csv").expect("plain");
        assert!(matches!(
            check_deletable(&sidecar),
            Err(StorageError::NameRejected { .. })
        ));
        let notes = DeviceFileName::new("notes.txt").expect("plain");
        assert!(matches!(
            check_deletable(&notes),
            Err(StorageError::NameRejected { .. })
        ));
        assert!(check_deletable(&DeviceFileName::new("Racing.csv").expect("plain")).is_ok());
    }

    #[test]
    fn a_display_name_never_carries_a_mount_point() {
        assert_eq!(
            DeviceDisplayName::new("/Volumes/QUADSTICK").as_str(),
            "VolumesQUADSTICK"
        );
        assert_eq!(DeviceDisplayName::new("   ").as_str(), "QuadStick drive");
        assert_eq!(DeviceDisplayName::new("QUADSTICK").as_str(), "QUADSTICK");
    }
}
