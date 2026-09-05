//! The profile session manager.
//!
//! Canonical editor state lives here and nowhere else. A window holds a session
//! id, a revision and a picture; every edit it sends carries the revision it was
//! made against, and an edit made against a stale one is refused rather than
//! quietly winning.
//!
//! Nothing in this file touches the filesystem. Reading and writing go through
//! [`LocalProfileStore`], which takes an opaque id and never a path.

use crate::error::{ProfileError, QcmError};
use crate::ports::local::{LocalProfileRef, LocalProfileStore, ProfileDisplayName};
use crate::ports::storage::{DeviceFileName, DeviceGeneration, StorageDeviceId};
use crate::profiles::session::{
    CloseOutcome, CloseRequest, ProfileOrigin, ProfileSession, SaveReceipt, SessionId,
};
use crate::profiles::snapshot::EditorSnapshot;
use qcm_config::vocab::default_template;
use qcm_config::{EditorOp, ProfileFile};
use std::collections::BTreeMap;

/// Serialized bytes and the place they go, produced before anything is written.
///
/// The split is the point. Everything that can fail on the profile's own terms
/// (unknown session, stale revision, no target yet, a target that turned out to
/// be a QuadStick) has already failed by the time a plan exists, so
/// [`ProfileSessions::commit_save`] is the only step that touches the store.
///
/// OQ-004 asks whether local save should grow the device install's
/// backup-and-read-back contract. It stays at parity with the legacy
/// `WriteAtomic` for now; when the answer changes, `commit_save` is the body
/// that changes and this type is what the stronger version is handed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SavePlan {
    session: SessionId,
    revision: u64,
    target: LocalProfileRef,
    text: String,
}

impl SavePlan {
    #[must_use]
    pub const fn session(&self) -> SessionId {
        self.session
    }

    /// The revision the bytes were taken at. Normalization on the way out is an
    /// edit, so this can be past the revision the caller asked to save.
    #[must_use]
    pub const fn revision(&self) -> u64 {
        self.revision
    }

    #[must_use]
    pub const fn target_name(&self) -> &ProfileDisplayName {
        self.target.display_name()
    }

    /// Exactly what will be written. Nothing between here and the store is
    /// allowed to reformat it.
    #[must_use]
    pub fn text(&self) -> &str {
        &self.text
    }
}

/// Every profile the app currently has open.
#[derive(Debug)]
pub struct ProfileSessions<S: LocalProfileStore> {
    store: S,
    open: BTreeMap<SessionId, ProfileSession>,
    next: u64,
}

impl<S: LocalProfileStore> ProfileSessions<S> {
    #[must_use]
    pub fn new(store: S) -> Self {
        Self {
            store,
            open: BTreeMap::new(),
            next: 1,
        }
    }

    #[must_use]
    pub const fn store(&self) -> &S {
        &self.store
    }

    #[must_use]
    pub fn open_count(&self) -> usize {
        self.open.len()
    }

    #[must_use]
    pub fn is_open(&self, session: SessionId) -> bool {
        self.open.contains_key(&session)
    }

    /// Every open session, in the order they were opened.
    pub fn ids(&self) -> impl Iterator<Item = SessionId> + '_ {
        self.open.keys().copied()
    }

    /// True while any open profile has work that is not on disk. What a window
    /// asks before it lets the app quit.
    #[must_use]
    pub fn any_dirty(&self) -> bool {
        self.open.values().any(ProfileSession::dirty)
    }

    pub fn session(&self, session: SessionId) -> Result<&ProfileSession, QcmError> {
        self.open
            .get(&session)
            .ok_or_else(|| ProfileError::UnknownSession.into())
    }

    pub fn snapshot(&self, session: SessionId) -> Result<EditorSnapshot, QcmError> {
        self.session(session).map(EditorSnapshot::of)
    }

    /// A new profile from the built-in template, matching the legacy
    /// `NewFromTemplate`: stamp the file name, then forget that it happened, so
    /// an untitled profile opens with nothing to undo and nothing to lose.
    pub fn open_new(&mut self, csv_file_name: &str) -> EditorSnapshot {
        let mut file = ProfileFile::load(default_template());
        let row = file.document.file_name_cell_row();
        file.set_cell(row, 0, csv_file_name.to_owned());
        file.clear_undo();
        file.mark_clean();
        self.insert(ProfileOrigin::New, None, file)
    }

    /// Open a file from the user's own library. Save writes back to it.
    pub fn open_local(&mut self, target: LocalProfileRef) -> Result<EditorSnapshot, QcmError> {
        let text = self.store.read(&target)?;
        let file = ProfileFile::load(&text);
        Ok(self.insert(ProfileOrigin::Local(target.clone()), Some(target), file))
    }

    /// Open a working copy of a file read off a device.
    ///
    /// The bytes come in already read, because reading them is the device
    /// port's job. There is no save target: Save writes to the user's computer,
    /// and putting the profile back on the stick is the install transaction.
    pub fn open_device_copy(
        &mut self,
        device: StorageDeviceId,
        generation: DeviceGeneration,
        name: DeviceFileName,
        csv_text: &str,
    ) -> EditorSnapshot {
        let origin = ProfileOrigin::Device {
            device,
            generation,
            name,
        };
        self.insert(origin, None, ProfileFile::load(csv_text))
    }

    /// Open a downloaded community profile. Also a working copy: the catalog is
    /// read-only, so Save has to name a place in the user's library first.
    pub fn open_community(&mut self, catalog_id: &str, csv_text: &str) -> EditorSnapshot {
        let origin = ProfileOrigin::Community {
            catalog_id: catalog_id.to_owned(),
        };
        self.insert(origin, None, ProfileFile::load(csv_text))
    }

    /// Apply a batch of typed edits, all or nothing.
    ///
    /// A batch that cannot be applied in full applies none of it. That costs a
    /// clone of the profile: rolling back by undoing the applied part would
    /// leave the revision and the dirty flag past where they started, which is
    /// exactly the lie this whole contract exists to prevent.
    ///
    /// Each applied operation is its own undo step and its own revision, the
    /// same as the legacy editor. A batch is a convenience for the window, not
    /// a bigger thing for the user to undo.
    pub fn apply_ops(
        &mut self,
        session: SessionId,
        expected_revision: u64,
        ops: &[EditorOp],
    ) -> Result<EditorSnapshot, QcmError> {
        let open = self.session_mut(session)?;
        check_revision(open.revision(), expected_revision)?;
        if ops.is_empty() {
            return Ok(EditorSnapshot::of(open));
        }

        let mut candidate = open.file().clone();
        for (index, op) in ops.iter().enumerate() {
            if !candidate.apply_editor_op(op) {
                return Err(ProfileError::OperationRejected {
                    index,
                    op: op.name(),
                }
                .into());
            }
        }
        open.replace_file(candidate);
        Ok(EditorSnapshot::of(open))
    }

    /// Undo one edit. Undo is itself a change, so it dirties the profile and
    /// moves the revision on again, matching the legacy editor: undoing after a
    /// save puts memory and disk back out of step.
    pub fn undo(
        &mut self,
        session: SessionId,
        expected_revision: u64,
    ) -> Result<EditorSnapshot, QcmError> {
        let open = self.session_mut(session)?;
        check_revision(open.revision(), expected_revision)?;
        if !open.file_mut().undo() {
            return Err(ProfileError::NothingToUndo.into());
        }
        Ok(EditorSnapshot::of(open))
    }

    /// Serialize the profile for its current save target, writing nothing.
    ///
    /// Normalization happens here, before the bytes are taken, so a saved file
    /// and an installed file are the same bytes. It is a real edit: it can add
    /// the version header and the blank separators the firmware needs, and it
    /// moves the revision on and can be undone like any other.
    pub fn prepare_save(
        &mut self,
        session: SessionId,
        expected_revision: u64,
    ) -> Result<SavePlan, QcmError> {
        let target = {
            let open = self.session(session)?;
            check_revision(open.revision(), expected_revision)?;
            open.target()
                .cloned()
                .ok_or(QcmError::Profile(ProfileError::NeedsSaveTarget))?
        };

        // Asked now rather than when the file was picked: a plain folder
        // becomes a QuadStick the moment one is plugged in. Forgetting the
        // target is what the legacy window did too, so the next save falls
        // through to Save As instead of pointing at the device again.
        if self.store.is_on_quadstick(&target)? {
            self.session_mut(session)?.set_target(None);
            return Err(ProfileError::SaveTargetOnDevice.into());
        }

        let open = self.session_mut(session)?;
        open.file_mut().normalize_for_device_csv();
        Ok(SavePlan {
            session,
            revision: open.revision(),
            target,
            text: open.file().to_csv_text(),
        })
    }

    /// Write a prepared plan.
    ///
    /// The revision is checked again: an edit that landed between preparing and
    /// committing would make these bytes a silent rollback of it.
    ///
    /// Saving clears the dirty flag and nothing else. Undo history survives,
    /// because the user's ability to take back what they just did does not
    /// depend on where the file went.
    pub fn commit_save(&mut self, plan: SavePlan) -> Result<SaveReceipt, QcmError> {
        let open = self.session_mut(plan.session)?;
        check_revision(open.revision(), plan.revision)?;
        let receipt = self.store.write(&plan.target, &plan.text)?;
        let open = self.session_mut(plan.session)?;
        open.file_mut().mark_clean();
        Ok(SaveReceipt {
            session: plan.session,
            revision: plan.revision,
            name: plan.target.display_name().clone(),
            bytes: receipt.bytes,
        })
    }

    pub fn save(
        &mut self,
        session: SessionId,
        expected_revision: u64,
    ) -> Result<SaveReceipt, QcmError> {
        let plan = self.prepare_save(session, expected_revision)?;
        self.commit_save(plan)
    }

    /// Save somewhere the user just named.
    ///
    /// A place on a QuadStick is refused before the target is adopted, so a
    /// rejected Save As leaves the profile pointing where it pointed before.
    pub fn save_as(
        &mut self,
        session: SessionId,
        expected_revision: u64,
        target: LocalProfileRef,
    ) -> Result<SaveReceipt, QcmError> {
        {
            let open = self.session(session)?;
            check_revision(open.revision(), expected_revision)?;
        }
        if self.store.is_on_quadstick(&target)? {
            return Err(ProfileError::SaveTargetOnDevice.into());
        }
        self.session_mut(session)?.set_target(Some(target));
        self.save(session, expected_revision)
    }

    /// Close a profile, under an explicit answer about unsaved work.
    ///
    /// A failed save keeps the session open with the work intact. That is the
    /// rule the legacy window was careful about: a cancelled picker or a write
    /// that never reached disk must not be read as permission to discard.
    pub fn close(
        &mut self,
        session: SessionId,
        request: CloseRequest,
    ) -> Result<CloseOutcome, QcmError> {
        let dirty = self.session(session)?.dirty();
        match request {
            CloseRequest::IfClean if dirty => Ok(CloseOutcome::KeptOpenUnsavedChanges),
            CloseRequest::IfClean | CloseRequest::Discard => {
                self.open.remove(&session);
                Ok(CloseOutcome::Closed)
            }
            CloseRequest::Save => {
                let revision = self.session(session)?.revision();
                let receipt = self.save(session, revision)?;
                self.open.remove(&session);
                Ok(CloseOutcome::SavedAndClosed(receipt))
            }
        }
    }

    fn session_mut(&mut self, session: SessionId) -> Result<&mut ProfileSession, QcmError> {
        self.open
            .get_mut(&session)
            .ok_or_else(|| ProfileError::UnknownSession.into())
    }

    fn insert(
        &mut self,
        origin: ProfileOrigin,
        target: Option<LocalProfileRef>,
        file: ProfileFile,
    ) -> EditorSnapshot {
        // Ids are never reused inside a run, so a command that arrives late
        // for a closed profile fails instead of landing on a new one.
        let id = SessionId::from_raw(self.next);
        self.next = self.next.saturating_add(1);
        let open = ProfileSession::new(id, origin, target, file);
        let snapshot = EditorSnapshot::of(&open);
        self.open.insert(id, open);
        snapshot
    }
}

fn check_revision(actual: u64, expected: u64) -> Result<(), QcmError> {
    if actual == expected {
        return Ok(());
    }
    Err(ProfileError::RevisionConflict { expected, actual }.into())
}
