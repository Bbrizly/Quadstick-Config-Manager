//! One open profile: what it is, where it came from, and where Save writes.

use crate::ports::local::{LocalProfileRef, ProfileDisplayName};
use crate::ports::storage::{DeviceFileName, DeviceGeneration, StorageDeviceId};
use qcm_config::ProfileFile;
use std::fmt;
use std::str::FromStr;

/// Opaque handle for one open profile.
///
/// Not a secret. Anything that can call a command can guess a number, so this
/// is identity, not authority: every mutation still carries the revision it was
/// made against, and every device write still carries its own confirmation.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct SessionId(u64);

impl SessionId {
    #[must_use]
    pub const fn from_raw(raw: u64) -> Self {
        Self(raw)
    }

    #[must_use]
    pub const fn raw(self) -> u64 {
        self.0
    }
}

impl fmt::Display for SessionId {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "session-{}", self.0)
    }
}

/// Parsing exists so an id that came back from a window is checked rather than
/// trusted. An unparsable id is a rejected request, not a panic.
impl FromStr for SessionId {
    type Err = ();

    fn from_str(value: &str) -> Result<Self, Self::Err> {
        value
            .strip_prefix("session-")
            .and_then(|digits| digits.parse().ok())
            .map(Self)
            .ok_or(())
    }
}

/// Where the open profile came from.
///
/// Deliberately not the same thing as where Save writes. The legacy window kept
/// `ProfileSource` and `_savePath` apart for a reason: a profile read off a
/// QuadStick opens as a working copy, and its origin stays the device however
/// many times it is saved into the user's own library.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ProfileOrigin {
    /// Started from the built-in template. Never saved anywhere yet.
    New,
    Local(LocalProfileRef),
    /// A working copy of a file on a device. Save does not write back: only the
    /// install transaction does, and it is the one path with a backup, a
    /// read-back and the `default.csv` confirmation.
    Device {
        device: StorageDeviceId,
        generation: DeviceGeneration,
        name: DeviceFileName,
    },
    Community {
        catalog_id: String,
    },
}

/// What the caller wants done about unsaved work when a profile is closed.
///
/// Three answers, matching the legacy leave prompt exactly. There is no fourth,
/// implicit one: closing something dirty without saying which of these applies
/// is refused rather than guessed at.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CloseRequest {
    /// Close only if there is nothing to lose. A dirty profile stays open and
    /// the caller is told, so a second press of the same button is the same
    /// question and not a second answer to it.
    IfClean,
    /// Save first. A failed or blocked save keeps the profile open, because the
    /// legacy rule is that Save only earns the close if it reached disk.
    Save,
    /// Drop the changes. Only ever from an explicit "Don't save".
    Discard,
}

/// What closing actually did.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum CloseOutcome {
    Closed,
    /// Saved, then closed.
    SavedAndClosed(SaveReceipt),
    /// Still open, still dirty. The caller has to ask the user.
    KeptOpenUnsavedChanges,
}

/// Proof that bytes reached the user's library.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SaveReceipt {
    pub session: SessionId,
    /// The revision the saved bytes were taken at. Normalization on the way out
    /// is an edit, so this can be past the revision the caller asked to save.
    pub revision: u64,
    pub name: ProfileDisplayName,
    pub bytes: usize,
}

/// One open profile.
#[derive(Debug)]
pub struct ProfileSession {
    id: SessionId,
    origin: ProfileOrigin,
    target: Option<LocalProfileRef>,
    file: ProfileFile,
}

impl ProfileSession {
    pub(crate) const fn new(
        id: SessionId,
        origin: ProfileOrigin,
        target: Option<LocalProfileRef>,
        file: ProfileFile,
    ) -> Self {
        Self {
            id,
            origin,
            target,
            file,
        }
    }

    #[must_use]
    pub const fn id(&self) -> SessionId {
        self.id
    }

    #[must_use]
    pub const fn origin(&self) -> &ProfileOrigin {
        &self.origin
    }

    #[must_use]
    pub const fn file(&self) -> &ProfileFile {
        &self.file
    }

    #[must_use]
    pub const fn revision(&self) -> u64 {
        self.file.revision()
    }

    #[must_use]
    pub const fn dirty(&self) -> bool {
        self.file.dirty()
    }

    #[must_use]
    pub fn can_undo(&self) -> bool {
        self.file.can_undo()
    }

    /// The name Save would write to, if it has one to write to.
    #[must_use]
    pub fn save_target_name(&self) -> Option<&ProfileDisplayName> {
        self.target.as_ref().map(LocalProfileRef::display_name)
    }

    pub(crate) const fn target(&self) -> Option<&LocalProfileRef> {
        self.target.as_ref()
    }

    pub(crate) fn set_target(&mut self, target: Option<LocalProfileRef>) {
        self.target = target;
    }

    pub(crate) const fn file_mut(&mut self) -> &mut ProfileFile {
        &mut self.file
    }

    pub(crate) fn replace_file(&mut self, file: ProfileFile) {
        self.file = file;
    }
}

#[cfg(test)]
mod tests {
    use super::SessionId;

    #[test]
    fn a_session_id_survives_a_round_trip_through_text() {
        let id = SessionId::from_raw(12);
        assert_eq!(id.to_string(), "session-12");
        assert_eq!("session-12".parse(), Ok(id));
    }

    #[test]
    fn a_forged_session_id_is_rejected_rather_than_guessed_at() {
        for forged in ["12", "session-", "session-x", "session--1", "op-1", ""] {
            assert!(forged.parse::<SessionId>().is_err(), "{forged}");
        }
    }
}
