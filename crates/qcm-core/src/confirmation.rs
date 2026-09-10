//! Confirmations that expire and cannot be spent twice.
//!
//! The window never passes a boolean saying the user agreed. It asks the core
//! to prepare the change, the core hands back a requirement, and the window
//! sends the requirement's ID back with the commit. The ID is matched against
//! the fingerprint of the operation being committed, so an acknowledgement of
//! "overwrite default.csv" cannot be replayed onto a different write.

use crate::clock::{Clock, Moment};
use crate::error::ConfirmationError;
use crate::operation::OperationFingerprint;
use std::collections::HashMap;
use std::fmt;
use std::str::FromStr;
use std::time::Duration;

/// How long an unanswered confirmation stays good. Long enough to read the
/// sentence, short enough that a dialog left open over lunch does not still
/// authorize a write to a drive that has been swapped since.
pub const DEFAULT_CONFIRMATION_TTL: Duration = Duration::from_secs(120);

/// What the user is being asked to agree to. One variant per irreversible act,
/// because the summary text and the risk are not the same for any two of them.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ConfirmationKind {
    /// default.csv is the device's fallback profile.
    OverwriteDefaultCsv,
    /// prefs.csv is device-wide settings, so it changes every profile at once.
    OverwriteDevicePreferences,
    OverwriteExistingProfile,
    DeleteDeviceProfile,
}

impl ConfirmationKind {
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::OverwriteDefaultCsv => "overwrite_default_csv",
            Self::OverwriteDevicePreferences => "overwrite_device_preferences",
            Self::OverwriteExistingProfile => "overwrite_existing_profile",
            Self::DeleteDeviceProfile => "delete_device_profile",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct ConfirmationId(u64);

impl ConfirmationId {
    #[must_use]
    pub const fn from_raw(raw: u64) -> Self {
        Self(raw)
    }

    #[must_use]
    pub const fn raw(self) -> u64 {
        self.0
    }
}

impl fmt::Display for ConfirmationId {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "cnf-{}", self.0)
    }
}

impl FromStr for ConfirmationId {
    type Err = ();

    fn from_str(value: &str) -> Result<Self, Self::Err> {
        value
            .strip_prefix("cnf-")
            .and_then(|digits| digits.parse().ok())
            .map(Self)
            .ok_or(())
    }
}

/// What the core hands back instead of doing the work.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ConfirmationRequirement {
    pub id: ConfirmationId,
    pub kind: ConfirmationKind,
    /// The exact sentence the user is agreeing to, written by the core so the
    /// window cannot soften it.
    pub summary: String,
    pub expires_at: Moment,
    pub fingerprint: OperationFingerprint,
}

#[derive(Debug)]
struct Outstanding {
    kind: ConfirmationKind,
    expires_at: Moment,
    fingerprint: OperationFingerprint,
}

/// The open confirmations. Redeeming one removes it, so the same answer cannot
/// authorize two writes.
#[derive(Debug)]
pub struct ConfirmationLedger<C: Clock> {
    clock: C,
    ttl: Duration,
    next: u64,
    outstanding: HashMap<ConfirmationId, Outstanding>,
}

impl<C: Clock> ConfirmationLedger<C> {
    #[must_use]
    pub fn new(clock: C) -> Self {
        Self::with_ttl(clock, DEFAULT_CONFIRMATION_TTL)
    }

    #[must_use]
    pub fn with_ttl(clock: C, ttl: Duration) -> Self {
        Self {
            clock,
            ttl,
            next: 1,
            outstanding: HashMap::new(),
        }
    }

    /// Record that this operation may not proceed until it is acknowledged.
    pub fn require(
        &mut self,
        kind: ConfirmationKind,
        fingerprint: OperationFingerprint,
        summary: impl Into<String>,
    ) -> ConfirmationRequirement {
        let id = ConfirmationId(self.next);
        self.next += 1;
        let expires_at = self.clock.now().plus(self.ttl);
        self.outstanding.insert(
            id,
            Outstanding {
                kind,
                expires_at,
                fingerprint: fingerprint.clone(),
            },
        );
        ConfirmationRequirement {
            id,
            kind,
            summary: summary.into(),
            expires_at,
            fingerprint,
        }
    }

    /// Spend a confirmation on exactly the operation it was issued for.
    ///
    /// Order matters. Expiry is checked before the fingerprint so a stale
    /// answer reads as timed out rather than as a mismatch, and a mismatch does
    /// not consume the confirmation: the operation it really belongs to is
    /// still waiting for it.
    pub fn redeem(
        &mut self,
        id: ConfirmationId,
        kind: ConfirmationKind,
        fingerprint: &OperationFingerprint,
    ) -> Result<(), ConfirmationError> {
        let Some(open) = self.outstanding.get(&id) else {
            return Err(ConfirmationError::Unknown);
        };
        if self.clock.now() > open.expires_at {
            self.outstanding.remove(&id);
            return Err(ConfirmationError::Expired);
        }
        if open.kind != kind || &open.fingerprint != fingerprint {
            return Err(ConfirmationError::Mismatch);
        }
        self.outstanding.remove(&id);
        Ok(())
    }

    /// Drop everything already timed out. Housekeeping only: [`Self::redeem`]
    /// checks expiry itself, so forgetting to call this cannot let one through.
    pub fn purge_expired(&mut self) {
        let now = self.clock.now();
        self.outstanding.retain(|_, open| now <= open.expires_at);
    }

    #[must_use]
    pub fn outstanding_count(&self) -> usize {
        self.outstanding.len()
    }
}

#[cfg(test)]
mod tests {
    use super::{ConfirmationId, ConfirmationKind, ConfirmationLedger, DEFAULT_CONFIRMATION_TTL};
    use crate::clock::ManualClock;
    use crate::error::ConfirmationError;
    use crate::operation::{OperationFingerprint, OperationKind};
    use std::time::Duration;

    fn install_of(target: &str) -> OperationFingerprint {
        OperationFingerprint::builder(OperationKind::InstallProfile)
            .number("device", 1)
            .number("generation", 1)
            .field("target", target)
            .finish()
    }

    #[test]
    fn a_confirmation_answers_the_operation_it_was_issued_for() {
        let clock = ManualClock::new();
        let mut ledger = ConfirmationLedger::new(&clock);
        let print = install_of("default.csv");
        let required = ledger.require(
            ConfirmationKind::OverwriteDefaultCsv,
            print.clone(),
            "sure?",
        );

        assert_eq!(
            ledger.redeem(required.id, ConfirmationKind::OverwriteDefaultCsv, &print),
            Ok(())
        );
    }

    // The one that matters: a dialog answered about default.csv must not become
    // permission to overwrite a different file.
    #[test]
    fn a_confirmation_cannot_be_replayed_against_another_operation() {
        let clock = ManualClock::new();
        let mut ledger = ConfirmationLedger::new(&clock);
        let required = ledger.require(
            ConfirmationKind::OverwriteDefaultCsv,
            install_of("default.csv"),
            "sure?",
        );

        assert_eq!(
            ledger.redeem(
                required.id,
                ConfirmationKind::OverwriteDefaultCsv,
                &install_of("prefs.csv"),
            ),
            Err(ConfirmationError::Mismatch)
        );
        // Still good for its own operation.
        assert_eq!(
            ledger.redeem(
                required.id,
                ConfirmationKind::OverwriteDefaultCsv,
                &install_of("default.csv"),
            ),
            Ok(())
        );
    }

    #[test]
    fn confirming_one_risk_does_not_unlock_another() {
        let clock = ManualClock::new();
        let mut ledger = ConfirmationLedger::new(&clock);
        let print = install_of("prefs.csv");
        let required = ledger.require(
            ConfirmationKind::OverwriteDefaultCsv,
            print.clone(),
            "sure?",
        );

        assert_eq!(
            ledger.redeem(
                required.id,
                ConfirmationKind::OverwriteDevicePreferences,
                &print,
            ),
            Err(ConfirmationError::Mismatch)
        );
    }

    #[test]
    fn a_confirmation_is_spent_once() {
        let clock = ManualClock::new();
        let mut ledger = ConfirmationLedger::new(&clock);
        let print = install_of("default.csv");
        let required = ledger.require(
            ConfirmationKind::OverwriteDefaultCsv,
            print.clone(),
            "sure?",
        );

        assert_eq!(
            ledger.redeem(required.id, ConfirmationKind::OverwriteDefaultCsv, &print),
            Ok(())
        );
        assert_eq!(
            ledger.redeem(required.id, ConfirmationKind::OverwriteDefaultCsv, &print),
            Err(ConfirmationError::Unknown)
        );
    }

    #[test]
    fn a_confirmation_expires() {
        let clock = ManualClock::new();
        let mut ledger = ConfirmationLedger::new(&clock);
        let print = install_of("default.csv");
        let required = ledger.require(
            ConfirmationKind::OverwriteDefaultCsv,
            print.clone(),
            "sure?",
        );

        clock.advance(DEFAULT_CONFIRMATION_TTL + Duration::from_secs(1));

        assert_eq!(
            ledger.redeem(required.id, ConfirmationKind::OverwriteDefaultCsv, &print),
            Err(ConfirmationError::Expired)
        );
    }

    #[test]
    fn a_confirmation_is_still_good_up_to_its_deadline() {
        let clock = ManualClock::new();
        let mut ledger = ConfirmationLedger::with_ttl(&clock, Duration::from_secs(60));
        let print = install_of("default.csv");
        let required = ledger.require(
            ConfirmationKind::OverwriteDefaultCsv,
            print.clone(),
            "sure?",
        );

        clock.advance(Duration::from_secs(60));

        assert_eq!(
            ledger.redeem(required.id, ConfirmationKind::OverwriteDefaultCsv, &print),
            Ok(())
        );
    }

    #[test]
    fn an_invented_confirmation_id_is_refused() {
        let clock = ManualClock::new();
        let mut ledger = ConfirmationLedger::new(&clock);
        assert_eq!(
            ledger.redeem(
                ConfirmationId::from_raw(4242),
                ConfirmationKind::DeleteDeviceProfile,
                &install_of("racing.csv"),
            ),
            Err(ConfirmationError::Unknown)
        );
    }

    #[test]
    fn expired_confirmations_do_not_pile_up() {
        let clock = ManualClock::new();
        let mut ledger = ConfirmationLedger::with_ttl(&clock, Duration::from_secs(10));
        ledger.require(
            ConfirmationKind::DeleteDeviceProfile,
            install_of("racing.csv"),
            "sure?",
        );
        ledger.require(
            ConfirmationKind::DeleteDeviceProfile,
            install_of("apex.csv"),
            "sure?",
        );
        assert_eq!(ledger.outstanding_count(), 2);

        clock.advance(Duration::from_secs(11));
        ledger.purge_expired();

        assert_eq!(ledger.outstanding_count(), 0);
    }

    #[test]
    fn ids_round_trip_through_text_and_reject_junk() {
        let id = ConfirmationId::from_raw(7);
        assert_eq!(id.to_string(), "cnf-7");
        assert_eq!("cnf-7".parse(), Ok(id));
        assert!("op-7".parse::<ConfirmationId>().is_err());
        assert!("cnf-".parse::<ConfirmationId>().is_err());
    }
}
