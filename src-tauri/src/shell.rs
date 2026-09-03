//! What the profile/settings commands actually do.

use crate::adapters::picker::ProfilePicker;
use crate::ipc::{
    AppSnapshotDto, ApplyEditorOpsRequest, CapabilitiesDto, CloseOutcomeDto, CloseProfileRequest,
    NewProfileRequest, SessionRequest, SessionRevisionRequest, UpdateSettingsRequest, parse,
    session_id,
};
use qcm_config::{EditorOp, ProfileFile};
use qcm_core::error::{ProfileError, QcmError};
use qcm_core::ports::local::LocalProfileStore;
use qcm_core::ports::storage::{DeviceFileName, DeviceGeneration, StorageDeviceId};
use qcm_core::profiles::{EditorSnapshot, ProfileSessions, SaveReceiptDto};
use qcm_core::settings::{AppSettingsDto, Settings, SettingsStore};
use serde_json::Value;
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};

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
}

impl<L: LocalProfileStore, P: ProfilePicker, S: SettingsStore> Shell<L, P, S> {
    pub fn new(library: Arc<L>, picker: P, settings: S) -> Self {
        Self {
            sessions: Mutex::new(ProfileSessions::new(library)),
            settings: Mutex::new(Settings::load(settings)),
            picker,
        }
    }

    fn sessions(&self) -> MutexGuard<'_, ProfileSessions<Arc<L>>> {
        self.sessions.lock().unwrap_or_else(PoisonError::into_inner)
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
                google_backup: cfg!(any(target_os = "macos", target_os = "windows")),
                agent: false,
            },
            settings: self.settings().snapshot(),
        }
    }

    pub fn get_settings(&self) -> AppSettingsDto { self.settings().snapshot() }
    pub fn update_settings(&self, raw: Value) -> Result<AppSettingsDto, QcmError> {
        let request: UpdateSettingsRequest = parse(raw, "update_settings request")?;
        let patch = request.patch.validate()?;
        self.settings().update(request.expected_revision, &patch)
    }
    pub fn new_profile(&self, raw: Value) -> Result<EditorSnapshot, QcmError> {
        let request: NewProfileRequest = parse(raw, "new_profile request")?;
        request.check()?;
        Ok(self.sessions().open_new(&request.name))
    }
    pub fn choose_and_open_profile(&self) -> Result<Option<EditorSnapshot>, QcmError> {
        let Some(target) = self.picker.pick_open()? else { return Ok(None); };
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
        self.sessions().apply_ops(session, request.expected_revision, &request.ops)
    }
    pub fn undo_editor(&self, raw: Value) -> Result<EditorSnapshot, QcmError> {
        let request: SessionRevisionRequest = parse(raw, "undo_editor request")?;
        let session = session_id(&request.session_id)?;
        self.sessions().undo(session, request.expected_revision)
    }
    pub fn save_profile(&self, raw: Value) -> Result<SaveReceiptDto, QcmError> {
        let request: SessionRevisionRequest = parse(raw, "save_profile request")?;
        let session = session_id(&request.session_id)?;
        self.sessions().save(session, request.expected_revision).map(|receipt| SaveReceiptDto::from(&receipt))
    }
    pub fn save_profile_as(&self, raw: Value) -> Result<Option<SaveReceiptDto>, QcmError> {
        let request: SessionRevisionRequest = parse(raw, "save_profile_as request")?;
        let session = session_id(&request.session_id)?;
        let suggested = {
            let sessions = self.sessions();
            let open = sessions.session(session)?;
            if open.revision() != request.expected_revision {
                return Err(ProfileError::RevisionConflict { expected: request.expected_revision, actual: open.revision() }.into());
            }
            open.save_target_name().map_or_else(|| open.file().document.title().to_owned(), ToString::to_string)
        };
        let Some(target) = self.picker.pick_save_as(&suggested)? else { return Ok(None); };
        self.sessions().save_as(session, request.expected_revision, target).map(|receipt| Some(SaveReceiptDto::from(&receipt)))
    }
    pub fn close_profile(&self, raw: Value) -> Result<CloseOutcomeDto, QcmError> {
        let request: CloseProfileRequest = parse(raw, "close_profile request")?;
        let session = session_id(&request.session_id)?;
        let close = request.close_request()?;
        self.sessions().close(session, close).map(|outcome| CloseOutcomeDto::from(&outcome))
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
            return Err(ProfileError::RevisionConflict { expected: request.expected_revision, actual: open.revision() }.into());
        }
        let suggested = open.save_target_name().map(ToString::to_string)
            .or_else(|| open.file().document.csv_file_name().map(ToOwned::to_owned))
            .filter(|name| !name.trim().is_empty()).unwrap_or_else(|| "Profile.csv".to_owned());
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
            return Err(ProfileError::RevisionConflict { expected: request.expected_revision, actual: open.revision() }.into());
        }
        let target = open.save_target_ref().ok_or(QcmError::Profile(ProfileError::NeedsSaveTarget))?;
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

    pub fn open_workbook_copy(&self, name: &str, csv_text: &str) -> Result<EditorSnapshot, QcmError> {
        let source_id = format!("workbook:{name}");
        let mut sessions = self.sessions();
        let opened = sessions.open_community(&source_id, csv_text);
        let session = session_id(&opened.session_id)?;
        let (row, existing) = {
            let open = sessions.session(session)?;
            let row = open.file().document.file_name_cell_row();
            (row, open.file().get_cell(row, 0).to_owned())
        };
        let desired = if name.trim().is_empty() { existing } else { name.to_owned() };
        sessions.apply_ops(session, opened.revision, &[EditorOp::SetCell { row, col: 0, value: desired }])
    }

    pub fn open_device_copy(&self, device: StorageDeviceId, generation: DeviceGeneration, name: DeviceFileName, csv_text: &str) -> EditorSnapshot {
        self.sessions().open_device_copy(device, generation, name, csv_text)
    }
}

pub type ShellState = Shell<
    crate::adapters::library::FileSystemProfileLibrary<crate::adapters::storage::volumes::PlatformVolumes>,
    crate::adapters::picker::NativeProfilePicker<crate::adapters::storage::volumes::PlatformVolumes>,
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
    Shell::new(library, picker, SettingsFile::default_location())
}
