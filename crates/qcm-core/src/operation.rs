//! Operation identity: what a long or destructive job is, and how a
//! confirmation is tied to one job and not another.

use std::fmt;
use std::str::FromStr;
use std::sync::atomic::{AtomicU64, Ordering};

/// Opaque handle for one operation.
///
/// Not a secret and not proof of anything. Anything that can call a command can
/// guess a number, so authority lives in [`OperationFingerprint`] plus the
/// single-use confirmation, never in the ID being hard to find.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct OperationId(u64);

impl OperationId {
    #[must_use]
    pub const fn from_raw(raw: u64) -> Self {
        Self(raw)
    }

    #[must_use]
    pub const fn raw(self) -> u64 {
        self.0
    }
}

impl fmt::Display for OperationId {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "op-{}", self.0)
    }
}

/// Parsing exists so an ID that came back from a window can be checked rather
/// than trusted. An unparsable ID is a rejected request, not a panic.
impl FromStr for OperationId {
    type Err = ();

    fn from_str(value: &str) -> Result<Self, Self::Err> {
        value
            .strip_prefix("op-")
            .and_then(|digits| digits.parse().ok())
            .map(Self)
            .ok_or(())
    }
}

/// Mints operation IDs. One per process; IDs are never reused inside a run, so
/// a late reply from a finished job cannot be mistaken for the current one.
#[derive(Debug)]
pub struct OperationIds {
    next: AtomicU64,
}

impl Default for OperationIds {
    fn default() -> Self {
        Self::new()
    }
}

impl OperationIds {
    #[must_use]
    pub const fn new() -> Self {
        Self {
            next: AtomicU64::new(1),
        }
    }

    pub fn mint(&self) -> OperationId {
        OperationId(self.next.fetch_add(1, Ordering::SeqCst))
    }
}

/// The kinds of job that carry an ID. Discovery is here because a stale
/// discovery result must be droppable by ID like any other late reply.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum OperationKind {
    DiscoverDevices,
    ReadDeviceProfile,
    InstallProfile,
    DeleteDeviceProfile,
    RenameDeviceProfile,
    ReorderDeviceProfiles,
    WriteDevicePreferences,
}

impl OperationKind {
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::DiscoverDevices => "discover_devices",
            Self::ReadDeviceProfile => "read_device_profile",
            Self::InstallProfile => "install_profile",
            Self::DeleteDeviceProfile => "delete_device_profile",
            Self::RenameDeviceProfile => "rename_device_profile",
            Self::ReorderDeviceProfiles => "reorder_device_profiles",
            Self::WriteDevicePreferences => "write_device_preferences",
        }
    }

    /// True for the jobs that can change or remove something on a device.
    #[must_use]
    pub const fn is_destructive(self) -> bool {
        matches!(
            self,
            Self::InstallProfile
                | Self::DeleteDeviceProfile
                | Self::RenameDeviceProfile
                | Self::ReorderDeviceProfiles
                | Self::WriteDevicePreferences
        )
    }
}

/// What an operation is about, in a form two operations cannot share by
/// accident. A confirmation carries one of these, so an acknowledgement of
/// "overwrite default.csv on this drive" cannot be spent on anything else.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct OperationFingerprint(String);

impl OperationFingerprint {
    #[must_use]
    pub fn builder(kind: OperationKind) -> FingerprintBuilder {
        FingerprintBuilder::new(kind)
    }

    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl fmt::Display for OperationFingerprint {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

/// Builds a fingerprint by listing the fields that make the operation what it
/// is: kind, device, generation, target name, and the ID of the plan holding
/// the bytes.
///
/// Length-prefixed, not hashed. The fields are short identifiers, so a hash
/// would only add a way for two different operations to land on one value, and
/// this value is the thing standing between a confirmation and the wrong file.
#[derive(Debug)]
pub struct FingerprintBuilder {
    encoded: String,
}

impl FingerprintBuilder {
    #[must_use]
    pub fn new(kind: OperationKind) -> Self {
        Self {
            encoded: String::new(),
        }
        .field("kind", kind.as_str())
    }

    #[must_use]
    pub fn field(mut self, name: &str, value: &str) -> Self {
        self.encoded.push_str(name);
        self.encoded.push('=');
        self.encoded.push_str(&value.len().to_string());
        self.encoded.push(':');
        self.encoded.push_str(value);
        self.encoded.push(';');
        self
    }

    #[must_use]
    pub fn number(self, name: &str, value: u64) -> Self {
        self.field(name, &value.to_string())
    }

    #[must_use]
    pub fn finish(self) -> OperationFingerprint {
        OperationFingerprint(self.encoded)
    }
}

/// One job in flight.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Operation {
    pub id: OperationId,
    pub kind: OperationKind,
    pub fingerprint: OperationFingerprint,
}

impl Operation {
    #[must_use]
    pub const fn new(
        id: OperationId,
        kind: OperationKind,
        fingerprint: OperationFingerprint,
    ) -> Self {
        Self {
            id,
            kind,
            fingerprint,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::{OperationFingerprint, OperationId, OperationIds, OperationKind};

    #[test]
    fn ids_are_never_reused_and_survive_a_round_trip_through_text() {
        let ids = OperationIds::new();
        let first = ids.mint();
        let second = ids.mint();
        assert_ne!(first, second);
        assert_eq!(first.to_string(), "op-1");
        assert_eq!("op-1".parse(), Ok(first));
        assert_eq!("op-2".parse(), Ok(second));
    }

    #[test]
    fn a_forged_id_is_rejected_rather_than_guessed_at() {
        assert!("1".parse::<OperationId>().is_err());
        assert!("op-".parse::<OperationId>().is_err());
        assert!("op-x".parse::<OperationId>().is_err());
        assert!("op--1".parse::<OperationId>().is_err());
        assert!("session-1".parse::<OperationId>().is_err());
    }

    #[test]
    fn the_same_operation_fingerprints_the_same_way() {
        let build = || {
            OperationFingerprint::builder(OperationKind::InstallProfile)
                .number("device", 7)
                .number("generation", 3)
                .field("target", "racing.csv")
                .finish()
        };
        assert_eq!(build(), build());
    }

    #[test]
    fn changing_any_field_changes_the_fingerprint() {
        let base = OperationFingerprint::builder(OperationKind::InstallProfile)
            .number("device", 7)
            .number("generation", 3)
            .field("target", "racing.csv")
            .finish();
        let other_kind = OperationFingerprint::builder(OperationKind::DeleteDeviceProfile)
            .number("device", 7)
            .number("generation", 3)
            .field("target", "racing.csv")
            .finish();
        let other_device = OperationFingerprint::builder(OperationKind::InstallProfile)
            .number("device", 8)
            .number("generation", 3)
            .field("target", "racing.csv")
            .finish();
        let other_generation = OperationFingerprint::builder(OperationKind::InstallProfile)
            .number("device", 7)
            .number("generation", 4)
            .field("target", "racing.csv")
            .finish();
        let other_target = OperationFingerprint::builder(OperationKind::InstallProfile)
            .number("device", 7)
            .number("generation", 3)
            .field("target", "default.csv")
            .finish();
        for other in [other_kind, other_device, other_generation, other_target] {
            assert_ne!(base, other);
        }
    }

    // Without the length prefix "ab" + "c" and "a" + "bc" encode identically,
    // and a confirmation for one install answers for another.
    #[test]
    fn field_boundaries_cannot_be_shifted_between_values() {
        let first = OperationFingerprint::builder(OperationKind::InstallProfile)
            .field("target", "ab")
            .field("backup", "c")
            .finish();
        let second = OperationFingerprint::builder(OperationKind::InstallProfile)
            .field("target", "a")
            .field("backup", "bc")
            .finish();
        assert_ne!(first, second);
    }

    #[test]
    fn separators_inside_a_value_cannot_forge_a_field() {
        let honest = OperationFingerprint::builder(OperationKind::InstallProfile)
            .field("target", "a")
            .number("generation", 9)
            .finish();
        let forged = OperationFingerprint::builder(OperationKind::InstallProfile)
            .field("target", "a;generation=1:9;")
            .finish();
        assert_ne!(honest, forged);
    }

    #[test]
    fn every_destructive_kind_is_marked_destructive() {
        assert!(OperationKind::InstallProfile.is_destructive());
        assert!(OperationKind::DeleteDeviceProfile.is_destructive());
        assert!(OperationKind::WriteDevicePreferences.is_destructive());
        assert!(!OperationKind::DiscoverDevices.is_destructive());
        assert!(!OperationKind::ReadDeviceProfile.is_destructive());
    }
}
