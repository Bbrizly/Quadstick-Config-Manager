//! The local profile store port.
//!
//! Open and save on the host side, under the same rule as the device port: no
//! path crosses it. The adapter owns the file picker and the table that maps an
//! id to a place on disk. The core holds an opaque id and a name it is only
//! ever allowed to print.
//!
//! Separate from [`DeviceStorage`](super::storage::DeviceStorage) because the
//! two are not the same transaction. Local save is the legacy
//! `ProfileFile.WriteAtomic`: temp file, rename, best-effort cleanup of the
//! temp. The device install is backup, stage, read back, replace, restore.
//! OQ-004 asks whether local save should adopt the stronger contract; it stays
//! at parity for now, and [`LocalProfileStore::write`] is the one seam where a
//! different answer lands.

use crate::error::StorageError;
use std::fmt;

/// Opaque handle for one place on the host a profile can be read or written.
///
/// Minted by the adapter once the user has picked the file. The path it stands
/// for never leaves the adapter, so nothing above this port, including a
/// compromised window, can name a place on the machine.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct LocalProfileId(u64);

impl LocalProfileId {
    #[must_use]
    pub const fn from_raw(raw: u64) -> Self {
        Self(raw)
    }

    #[must_use]
    pub const fn raw(self) -> u64 {
        self.0
    }
}

impl fmt::Display for LocalProfileId {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "local-{}", self.0)
    }
}

/// The name a window may print for a local profile.
///
/// Display text and nothing else: it locates nothing, so a name that cannot be
/// cleaned falls back to a word rather than failing. Only the last component
/// survives, which is what keeps a status line from spelling out the user's
/// home directory.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct ProfileDisplayName(String);

impl ProfileDisplayName {
    const MAX: usize = 128;
    const FALLBACK: &'static str = "Untitled profile";

    #[must_use]
    pub fn new(name: &str) -> Self {
        let last = name
            .rsplit(['/', '\\'])
            .find(|part| !part.trim().is_empty())
            .unwrap_or_default();
        let cleaned: String = last
            .chars()
            .filter(|c| !c.is_control() && *c != ':')
            .take(Self::MAX)
            .collect();
        let trimmed = cleaned.trim();
        if trimmed.is_empty() {
            Self(Self::FALLBACK.to_owned())
        } else {
            Self(trimmed.to_owned())
        }
    }

    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl fmt::Display for ProfileDisplayName {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

/// One file on the host, as the core sees it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LocalProfileRef {
    id: LocalProfileId,
    display_name: ProfileDisplayName,
}

impl LocalProfileRef {
    #[must_use]
    pub const fn new(id: LocalProfileId, display_name: ProfileDisplayName) -> Self {
        Self { id, display_name }
    }

    #[must_use]
    pub const fn id(&self) -> LocalProfileId {
        self.id
    }

    #[must_use]
    pub const fn display_name(&self) -> &ProfileDisplayName {
        &self.display_name
    }
}

/// What a completed local write can say about itself. No path, by construction.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LocalWriteReceipt {
    pub bytes: usize,
}

/// Reading and writing a profile in the user's own library.
///
/// Blocking, like the device port, and for the same reason: the adapter runs it
/// on a worker rather than dragging a runtime into a crate whose whole claim is
/// that it has no OS dependency.
pub trait LocalProfileStore {
    fn read(&self, target: &LocalProfileRef) -> Result<String, StorageError>;

    /// Write the exact text handed over.
    ///
    /// Exact matters: the caller already normalized and serialized, and a store
    /// that re-encoded line endings on the way past would change a file the
    /// user did not change.
    fn write(
        &self,
        target: &LocalProfileRef,
        text: &str,
    ) -> Result<LocalWriteReceipt, StorageError>;

    /// True when this target sits on a mounted QuadStick.
    ///
    /// Asked immediately before every save, not once when the file was picked:
    /// a folder becomes a device folder the moment a stick is plugged in.
    /// Saving never writes to the device, because only the install transaction
    /// carries the backup, the read-back and the `default.csv` confirmation.
    fn is_on_quadstick(&self, target: &LocalProfileRef) -> Result<bool, StorageError>;
}

#[cfg(test)]
mod tests {
    use super::{LocalProfileId, ProfileDisplayName};
    use crate::error::looks_like_absolute_path;

    #[test]
    fn a_display_name_keeps_the_file_and_drops_the_directories() {
        for (given, shown) in [
            ("/Users/b/Documents/Racing.csv", "Racing.csv"),
            ("C:\\Users\\b\\Racing.csv", "Racing.csv"),
            ("Racing.csv", "Racing.csv"),
            ("  ", "Untitled profile"),
            ("", "Untitled profile"),
            ("/", "Untitled profile"),
        ] {
            let name = ProfileDisplayName::new(given);
            assert_eq!(name.as_str(), shown, "{given}");
            assert!(!looks_like_absolute_path(name.as_str()), "{given}");
        }
    }

    #[test]
    fn a_local_id_prints_as_a_handle_and_not_as_a_place() {
        assert_eq!(LocalProfileId::from_raw(4).to_string(), "local-4");
    }
}
