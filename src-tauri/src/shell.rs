//! What the profile/settings commands actually do.

use crate::adapters::picker::ProfilePicker;
use crate::follow::FollowLinks;
use crate::follow_shell::LatestTable;
use crate::ipc::{
    AppSnapshotDto, ApplyEditorOpsRequest, CapabilitiesDto, CloseOutcomeDto, CloseProfileRequest,
    NewProfileRequest, SessionRequest, SessionRevisionRequest, UpdateSettingsRequest, parse,
    session_id,
};
use qcm_config::{EditorOp, ProfileFile};
use qcm_core::error::{ProfileError, QcmError};
use qcm_core::ports::local::LocalProfileStore;
use qcm_core::ports::storage::{DeviceFileName, DeviceGeneration, StorageDeviceId};
use qcm_core::profiles::SessionId;
use qcm_core::profiles::{EditorSnapshot, ProfileSessions, SaveReceiptDto};
use qcm_core::settings::{AppSettingsDto, Settings, SettingsStore};
use serde_json::Value;
use std::collections::BTreeMap;
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};

#[derive(Debug, Clone)]
pub struct DriveProfileSnapshot {
    pub persistent_key: String,
    pub display_name: String,
    pub revision: u64,
    pub file: ProfileFile,
}

pub struct Shell<L: LocalProfileStore, P: ProfilePicker, S: SettingsStore> {
    sessions: Mutex<ProfileSessions<Arc<L>>>,
    settings: Mutex<Settings<S>>,
    picker: P,
    follow: FollowLinks,
    /// `AppSettings.CustomNames`: names no row uses yet, by profile.
    custom_names: FollowLinks,
    pending_follow: Mutex<BTreeMap<SessionId, String>>,
    latest: Mutex<LatestTable>,
    /// Keep the unsaved-edits draft in the rescue folder. Off for a new shell,
    /// so tests never write, or clear, the person's real draft.
    autosave: bool,
}

impl<L: LocalProfileStore, P: ProfilePicker, S: SettingsStore> Shell<L, P, S> {
    pub fn new(library: Arc<L>, picker: P, settings: S) -> Self {
        Self {
            sessions: Mutex::new(ProfileSessions::new(library)),
            settings: Mutex::new(Settings::load(settings)),
            picker,
            follow: FollowLinks::in_memory(),
            custom_names: FollowLinks::in_memory(),
            pending_follow: Mutex::new(BTreeMap::new()),
            latest: Mutex::new(LatestTable::default()),
            autosave: false,
        }
    }

    /// Write the rescue draft as edits happen (the shipped app's autosave).
    #[must_use]
    pub const fn with_autosave(mut self) -> Self {
        self.autosave = true;
        self
    }

    fn clear_draft(&self) {
        if self.autosave {
            crate::diagnostics::clear_autosave_draft();
        }
    }

    /// Where followed links are kept. A new shell keeps them for the run only.
    #[must_use]
    pub fn with_follow_links(mut self, follow: FollowLinks) -> Self {
        self.follow = follow;
        self
    }

    /// Where unused custom output names are kept. A new shell keeps them for
    /// the run only.
    #[must_use]
    pub fn with_custom_names(mut self, names: FollowLinks) -> Self {
        self.custom_names = names;
        self
    }

    pub(crate) const fn custom_name_store(&self) -> &FollowLinks {
        &self.custom_names
    }

    /// True while any open profile has edits that are not on disk.
    pub fn has_unsaved_work(&self) -> bool {
        self.sessions().any_dirty()
    }

    pub(crate) fn sessions(&self) -> MutexGuard<'_, ProfileSessions<Arc<L>>> {
        self.sessions.lock().unwrap_or_else(PoisonError::into_inner)
    }
    pub(crate) const fn follow_links(&self) -> &FollowLinks {
        &self.follow
    }
    pub(crate) fn pending_follow(&self) -> MutexGuard<'_, BTreeMap<SessionId, String>> {
        self.pending_follow
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
    }
    pub(crate) fn latest_plans(&self) -> MutexGuard<'_, LatestTable> {
        self.latest.lock().unwrap_or_else(PoisonError::into_inner)
    }
    fn settings(&self) -> MutexGuard<'_, Settings<S>> {
        self.settings.lock().unwrap_or_else(PoisonError::into_inner)
    }

    pub fn app_snapshot(&self) -> AppSnapshotDto {
        AppSnapshotDto {
            version: env!("CARGO_PKG_VERSION").to_owned(),
            platform: std::env::consts::OS.to_owned(),
            capabilities: CapabilitiesDto {
                profile_editing: true,
                device_install: true,
                live_input: true,
                community_catalog: true,
                google_backup: cfg!(any(
                    target_os = "macos",
                    target_os = "windows",
                    target_os = "linux"
                )),
                agent: qcm_core::agent_feature::ENABLED,
                usage_data: !crate::telemetry::global().disabled_by_environment(),
            },
            settings: self.settings().snapshot(),
        }
    }

    pub fn get_settings(&self) -> AppSettingsDto {
        self.settings().snapshot()
    }
    pub fn update_settings(&self, raw: Value) -> Result<AppSettingsDto, QcmError> {
        let request: UpdateSettingsRequest = parse(raw, "update_settings request")?;
        let mut patch = request.patch.validate()?;
        // `ApplyTelemetryAnswer`: the Settings toggle is an answer to the
        // current notice, so it records that notice as seen.
        if patch.usage_analytics.is_some() {
            patch.telemetry_notice_version = Some(crate::telemetry::NOTICE_VERSION);
        }
        let updated = self.settings().update(request.expected_revision, &patch)?;
        if patch.usage_analytics.is_some() {
            self.apply_telemetry_consent();
            // A first yes mints the install id, which Settings shows.
            return Ok(self.settings().snapshot());
        }
        Ok(updated)
    }

    /// Push the saved answer into telemetry, minting the install id only for
    /// someone who said yes. Called at launch and after the toggle.
    pub fn apply_telemetry_consent(&self) {
        let telemetry = crate::telemetry::global();
        let (notice, usage, id) = {
            let mut settings = self.settings();
            let current = settings.current();
            let (notice, usage) = (current.telemetry_notice_version, current.usage_analytics);
            let wants = usage
                && notice >= crate::telemetry::NOTICE_VERSION
                && !telemetry.disabled_by_environment();
            let id = if wants {
                settings.ensure_install_id(crate::telemetry::new_install_id)
            } else {
                String::new()
            };
            (notice, usage && !id.is_empty(), id)
        };
        telemetry.apply_consent(notice, usage, &id);
    }

    /// The bridge switch as saved.
    pub fn controller_bridge_on(&self) -> bool {
        self.settings().current().controller_bridge
    }

    /// Save the bridge switch. Not a window patch, so no revision is needed.
    pub fn set_controller_bridge(&self, on: bool) -> Result<(), QcmError> {
        self.settings()
            .update_native(&qcm_core::settings::SettingsPatch {
                controller_bridge: Some(on),
                ..Default::default()
            })
    }

    /// The durable install id, minted if needed. A crash report the person
    /// sends needs one even without usage consent.
    pub fn install_id_for_crash_report(&self) -> String {
        self.settings()
            .ensure_install_id(crate::telemetry::new_install_id)
    }
    pub fn new_profile(&self, raw: Value) -> Result<EditorSnapshot, QcmError> {
        let request: NewProfileRequest = parse(raw, "new_profile request")?;
        request.check()?;
        Ok(self.sessions().open_new(&request.name))
    }
    pub fn choose_and_open_profile(&self) -> Result<Option<EditorSnapshot>, QcmError> {
        let Some(target) = self.picker.pick_open()? else {
            return Ok(None);
        };
        self.sessions().open_local(target).map(Some)
    }
    pub fn get_profile_snapshot(&self, raw: Value) -> Result<EditorSnapshot, QcmError> {
        let request: SessionRequest = parse(raw, "get_profile_snapshot request")?;
        let session = session_id(&request.session_id)?;
        let sessions = self.sessions();
        Ok(EditorSnapshot::of(sessions.session(session)?))
    }
    pub fn apply_editor_ops(&self, raw: Value) -> Result<EditorSnapshot, QcmError> {
        let request: ApplyEditorOpsRequest = parse(raw, "apply_editor_ops request")?;
        request.check()?;
        let session = session_id(&request.session_id)?;
        let snapshot =
            self.sessions()
                .apply_ops(session, request.expected_revision, &request.ops)?;
        sync_autosave_draft(self, &snapshot);
        Ok(snapshot)
    }
    pub fn undo_editor(&self, raw: Value) -> Result<EditorSnapshot, QcmError> {
        let request: SessionRevisionRequest = parse(raw, "undo_editor request")?;
        let session = session_id(&request.session_id)?;
        let snapshot = self.sessions().undo(session, request.expected_revision)?;
        sync_autosave_draft(self, &snapshot);
        Ok(snapshot)
    }
    pub fn save_profile(&self, raw: Value) -> Result<SaveReceiptDto, QcmError> {
        let request: SessionRevisionRequest = parse(raw, "save_profile request")?;
        let session = session_id(&request.session_id)?;
        let receipt = self
            .sessions()
            .save(session, request.expected_revision)
            .map(|receipt| SaveReceiptDto::from(&receipt))?;
        self.clear_draft();
        self.remember_follow(session);
        Ok(receipt)
    }
    pub fn save_profile_as(&self, raw: Value) -> Result<Option<SaveReceiptDto>, QcmError> {
        let request: SessionRevisionRequest = parse(raw, "save_profile_as request")?;
        let session = session_id(&request.session_id)?;
        let suggested = {
            let sessions = self.sessions();
            let open = sessions.session(session)?;
            if open.revision() != request.expected_revision {
                return Err(ProfileError::RevisionConflict {
                    expected: request.expected_revision,
                    actual: open.revision(),
                }
                .into());
            }
            suggested_save_name(
                open.save_target_name().map(ToString::to_string),
                open.file().document.csv_file_name(),
            )
        };
        let Some(target) = self.picker.pick_save_as(&suggested)? else {
            return Ok(None);
        };
        let receipt = self
            .sessions()
            .save_as(session, request.expected_revision, target)
            .map(|receipt| Some(SaveReceiptDto::from(&receipt)))?;
        if receipt.is_some() {
            self.clear_draft();
            self.remember_follow(session);
        }
        Ok(receipt)
    }
    pub fn close_profile(&self, raw: Value) -> Result<CloseOutcomeDto, QcmError> {
        let request: CloseProfileRequest = parse(raw, "close_profile request")?;
        let session = session_id(&request.session_id)?;
        let close = request.close_request()?;
        // A save on the way out still keeps the link, against the file the
        // session points at before it closes.
        let follow_key = self
            .pending_follow()
            .get(&session)
            .cloned()
            .and_then(|link| {
                let sessions = self.sessions();
                let open = sessions.session(session).ok()?;
                let key = sessions
                    .store()
                    .persistent_key(open.save_target_ref()?)
                    .ok()?;
                Some((key, link))
            });
        let outcome = self
            .sessions()
            .close(session, close)
            .map(|outcome| CloseOutcomeDto::from(&outcome))?;
        if matches!(
            outcome,
            CloseOutcomeDto::Closed | CloseOutcomeDto::SavedAndClosed { .. }
        ) {
            self.clear_draft();
            self.forget_pending_follow(session);
        }
        if let (CloseOutcomeDto::SavedAndClosed { .. }, Some((key, link))) = (&outcome, follow_key)
        {
            self.follow_links().set(&key, &link);
        }
        Ok(outcome)
    }
    pub fn profile_for_install(&self, session_raw: &str) -> Result<ProfileFile, QcmError> {
        let session = session_id(session_raw)?;
        Ok(self.sessions().session(session)?.file().clone())
    }

    pub fn profile_for_export(&self, raw: Value) -> Result<(ProfileFile, String), QcmError> {
        let request: SessionRevisionRequest = parse(raw, "export_profile_xlsx request")?;
        let session = session_id(&request.session_id)?;
        let sessions = self.sessions();
        let open = sessions.session(session)?;
        if open.revision() != request.expected_revision {
            return Err(ProfileError::RevisionConflict {
                expected: request.expected_revision,
                actual: open.revision(),
            }
            .into());
        }
        let suggested = open
            .save_target_name()
            .map(ToString::to_string)
            .or_else(|| open.file().document.csv_file_name().map(ToOwned::to_owned))
            .filter(|name| !name.trim().is_empty())
            .unwrap_or_else(|| "Profile.csv".to_owned());
        Ok((open.file().clone(), suggested))
    }

    /// Exact canonical profile + persistent native-only local identity for Drive.
    /// A profile without a save target must Save As before it can have a stable
    /// backup relationship.
    pub fn profile_for_drive(&self, raw: Value) -> Result<DriveProfileSnapshot, QcmError> {
        let request: SessionRevisionRequest = parse(raw, "Drive profile request")?;
        let session = session_id(&request.session_id)?;
        let sessions = self.sessions();
        let open = sessions.session(session)?;
        if open.revision() != request.expected_revision {
            return Err(ProfileError::RevisionConflict {
                expected: request.expected_revision,
                actual: open.revision(),
            }
            .into());
        }
        drive_snapshot_of(&sessions, open)
    }

    /// The same snapshot for a save the native side just made, with no
    /// revision to check. Only a clean session: what is open is then exactly
    /// what is on disk, and an edit made since the save waits for its own.
    /// `None` too for a profile Drive never backs up: no save target, a file
    /// on the QuadStick, or a session already gone.
    pub fn drive_snapshot(&self, raw_session: &str) -> Option<DriveProfileSnapshot> {
        let session = session_id(raw_session).ok()?;
        let sessions = self.sessions();
        let open = sessions.session(session).ok()?;
        if open.dirty() {
            return None;
        }
        drive_snapshot_of(&sessions, open).ok()
    }

    /// What a save on the way out is about to write, taken before the close,
    /// because the session is gone after it.
    pub fn drive_snapshot_before_close(&self, raw_session: &str) -> Option<DriveProfileSnapshot> {
        let session = session_id(raw_session).ok()?;
        let sessions = self.sessions();
        let open = sessions.session(session).ok()?;
        drive_snapshot_of(&sessions, open).ok()
    }

    pub fn open_workbook_copy(
        &self,
        name: &str,
        csv_text: &str,
    ) -> Result<EditorSnapshot, QcmError> {
        let source_id = format!("workbook:{name}");
        let mut sessions = self.sessions();
        let opened = sessions.open_community(&source_id, csv_text);
        let session = session_id(&opened.session_id)?;
        let (row, existing) = {
            let open = sessions.session(session)?;
            let row = open.file().document.file_name_cell_row();
            (row, open.file().get_cell(row, 0).to_owned())
        };
        let desired = if name.trim().is_empty() {
            existing
        } else {
            name.to_owned()
        };
        sessions.apply_ops(
            session,
            opened.revision,
            &[EditorOp::SetCell {
                row,
                col: 0,
                value: desired,
            }],
        )
    }

    pub fn open_device_copy(
        &self,
        device: StorageDeviceId,
        generation: DeviceGeneration,
        name: DeviceFileName,
        csv_text: &str,
    ) -> EditorSnapshot {
        self.sessions()
            .open_device_copy(device, generation, name, csv_text)
    }

    /// Crash/autosave recovery: dirty working copy with no save target.
    /// A registry profile is an imported starting point: no save target, so
    /// Save asks where instead of overwriting a cache file.
    pub fn open_registry_copy(
        &self,
        profile_id: &str,
        csv_text: &str,
    ) -> Result<EditorSnapshot, QcmError> {
        Ok(self
            .sessions()
            .open_community(&format!("registry:{profile_id}"), csv_text))
    }

    pub fn open_unsaved_profile(&self, csv_text: &str) -> EditorSnapshot {
        self.sessions().open_unsaved(csv_text)
    }
}

fn sync_autosave_draft<L, P, S>(shell: &Shell<L, P, S>, snapshot: &EditorSnapshot)
where
    L: LocalProfileStore,
    P: ProfilePicker,
    S: SettingsStore,
{
    if !shell.autosave {
        return;
    }
    if !snapshot.dirty {
        crate::diagnostics::clear_autosave_draft();
        return;
    }
    let Ok(session) = session_id(&snapshot.session_id) else {
        return;
    };
    let sessions = shell.sessions();
    let Ok(open) = sessions.session(session) else {
        return;
    };
    crate::diagnostics::write_autosave_draft(open.file().to_csv_text().as_bytes());
}

fn drive_snapshot_of<L: LocalProfileStore>(
    sessions: &ProfileSessions<Arc<L>>,
    open: &qcm_core::profiles::ProfileSession,
) -> Result<DriveProfileSnapshot, QcmError> {
    let target = open
        .save_target_ref()
        .ok_or(QcmError::Profile(ProfileError::NeedsSaveTarget))?;
    if sessions.store().is_on_quadstick(target)? {
        return Err(ProfileError::SaveTargetOnDevice.into());
    }
    let persistent_key = sessions.store().persistent_key(target)?;
    Ok(DriveProfileSnapshot {
        persistent_key,
        display_name: target.display_name().to_string(),
        revision: open.revision(),
        file: open.file().clone(),
    })
}

/// The name Save As offers (Avalonia `SaveAsync`): the file it already has,
/// else the profile name from the editor's name box without `.csv`, else
/// "profile". Never the first mode's name, which is not a file name.
fn suggested_save_name(target: Option<String>, csv_file_name: Option<&str>) -> String {
    if let Some(target) = target {
        return target;
    }
    let name = csv_file_name.unwrap_or("").trim();
    let bare = name
        .len()
        .checked_sub(4)
        .filter(|&cut| name.is_char_boundary(cut) && name[cut..].eq_ignore_ascii_case(".csv"))
        .map_or(name, |cut| &name[..cut]);
    if bare.is_empty() {
        "profile".to_owned()
    } else {
        bare.to_owned()
    }
}

pub type ShellState = Shell<
    crate::adapters::library::FileSystemProfileLibrary<
        crate::adapters::storage::volumes::PlatformVolumes,
    >,
    crate::adapters::picker::NativeProfilePicker<
        crate::adapters::storage::volumes::PlatformVolumes,
    >,
    crate::adapters::settings::SettingsFile,
>;

#[must_use]
pub fn native_shell() -> ShellState {
    use crate::adapters::library::FileSystemProfileLibrary;
    use crate::adapters::picker::NativeProfilePicker;
    use crate::adapters::settings::SettingsFile;
    use crate::adapters::storage::volumes::PlatformVolumes;
    let library = Arc::new(FileSystemProfileLibrary::new(PlatformVolumes));
    let picker = NativeProfilePicker::new(Arc::clone(&library));
    let follow = crate::diagnostics::app_data_dir().join("follow-links.json");
    let names = crate::diagnostics::app_data_dir().join("custom-names.json");
    Shell::new(library, picker, SettingsFile::default_location())
        .with_follow_links(FollowLinks::file(follow))
        .with_custom_names(FollowLinks::file(names))
        .with_autosave()
}

#[cfg(test)]
mod tests {
    use super::suggested_save_name;

    #[test]
    fn save_as_offers_the_profile_name_not_the_first_mode() {
        assert_eq!(suggested_save_name(None, Some("untitled.csv")), "untitled");
        assert_eq!(suggested_save_name(None, Some("Forza.CSV")), "Forza");
        assert_eq!(suggested_save_name(None, Some("racing")), "racing");
        assert_eq!(suggested_save_name(None, Some("")), "profile");
        assert_eq!(suggested_save_name(None, Some(".csv")), "profile");
        assert_eq!(suggested_save_name(None, None), "profile");
        assert_eq!(suggested_save_name(None, Some("é.csv")), "é");
        assert_eq!(
            suggested_save_name(Some("Kept.csv".to_owned()), Some("untitled.csv")),
            "Kept.csv"
        );
    }
}
