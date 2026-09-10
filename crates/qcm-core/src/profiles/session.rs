//! One open profile: what it is, where it came from, and where Save writes.

use crate::ports::local::{LocalProfileRef, ProfileDisplayName};
use crate::ports::storage::{DeviceFileName, DeviceGeneration, StorageDeviceId};
use qcm_config::ProfileFile;
use std::fmt;
use std::str::FromStr;

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

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ProfileOrigin {
    New,
    Local(LocalProfileRef),
    Device {
        device: StorageDeviceId,
        generation: DeviceGeneration,
        name: DeviceFileName,
    },
    Community {
        catalog_id: String,
    },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CloseRequest {
    IfClean,
    Save,
    Discard,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum CloseOutcome {
    Closed,
    SavedAndClosed(SaveReceipt),
    KeptOpenUnsavedChanges,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SaveReceipt {
    pub session: SessionId,
    pub revision: u64,
    pub name: ProfileDisplayName,
    pub bytes: usize,
}

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

    #[must_use]
    pub fn save_target_name(&self) -> Option<&ProfileDisplayName> {
        self.target.as_ref().map(LocalProfileRef::display_name)
    }

    /// Native services may need the opaque target to ask the local-store port
    /// for native-only metadata (for example Drive's persistent link key). The
    /// ref still contains no host path and is never serialized to the WebView.
    #[must_use]
    pub const fn save_target_ref(&self) -> Option<&LocalProfileRef> {
        self.target.as_ref()
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
