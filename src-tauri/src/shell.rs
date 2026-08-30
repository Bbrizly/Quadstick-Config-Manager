//! What the commands actually do.
//!
//! Every command is a thin wrapper around a method here, so the whole command
//! surface can be driven by a test with a fake library, a fake picker and a fake
//! settings file. There is no OS dialog to automate and no window to build: the
//! things a test cannot drive are the adapters, and they are behind ports.
//!
//! Two rules run through all of it. Every mutation carries the revision it was
//! made against, and a stale one is refused rather than applied. And a cancelled
//! picker is a result, not a failure: the profile is left exactly as it was.

use crate::adapters::picker::ProfilePicker;
use crate::ipc::{
    AppSnapshotDto, ApplyEditorOpsRequest, CapabilitiesDto, CloseOutcomeDto, CloseProfileRequest,
    NewProfileRequest, SessionRevisionRequest, UpdateSettingsRequest, parse, session_id,
};
use qcm_core::error::QcmError;
use qcm_core::ports::local::LocalProfileStore;
use qcm_core::profiles::{EditorSnapshot, ProfileSessions, SaveReceiptDto};
use qcm_core::settings::{AppSettingsDto, Settings, SettingsStore};
use serde_json::Value;
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};

/// Everything one running app owns.
///
/// The library is shared with the picker: a file the user just chose has to
/// become an opaque id inside the adapter layer, and the only alternative is
/// passing a path up to the command that asked, which is the thing this whole
/// boundary exists to prevent.
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

    /// A poisoned lock means a thread panicked while holding it. The state is
    /// still what it was, and a second panic on top only buries the first.
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
                device_install: false,
                live_input: false,
                community_catalog: false,
                google_backup: false,
                agent: false,
            },
            settings: self.settings().snapshot(),
        }
    }

    pub fn get_settings(&self) -> AppSettingsDto {
        self.settings().snapshot()
    }

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

    /// Ask for a file and open it. `None` means the user cancelled, which is not
    /// an error and leaves nothing open.
    pub fn choose_and_open_profile(&self) -> Result<Option<EditorSnapshot>, QcmError> {
        // The dialog is opened before the lock is taken. It is modal and can
        // sit there for as long as the user likes; nothing else should be
        // waiting on the session table while it does.
        let Some(target) = self.picker.pick_open()? else {
            return Ok(None);
        };
        self.sessions().open_local(target).map(Some)
    }

    pub fn apply_editor_ops(&self, raw: Value) -> Result<EditorSnapshot, QcmError> {
        let request: ApplyEditorOpsRequest = parse(raw, "apply_editor_ops request")?;
        request.check()?;
        let session = session_id(&request.session_id)?;
        self.sessions()
            .apply_ops(session, request.expected_revision, &request.ops)
    }

    pub fn undo_editor(&self, raw: Value) -> Result<EditorSnapshot, QcmError> {
        let request: SessionRevisionRequest = parse(raw, "undo_editor request")?;
        let session = session_id(&request.session_id)?;
        self.sessions().undo(session, request.expected_revision)
    }

    pub fn save_profile(&self, raw: Value) -> Result<SaveReceiptDto, QcmError> {
        let request: SessionRevisionRequest = parse(raw, "save_profile request")?;
        let session = session_id(&request.session_id)?;
        self.sessions()
            .save(session, request.expected_revision)
            .map(|receipt| SaveReceiptDto::from(&receipt))
    }

    /// Save somewhere the user names now. `None` means they cancelled the
    /// dialog, and the profile keeps whatever target it already had.
    pub fn save_profile_as(&self, raw: Value) -> Result<Option<SaveReceiptDto>, QcmError> {
        let request: SessionRevisionRequest = parse(raw, "save_profile_as request")?;
        let session = session_id(&request.session_id)?;

        // Checked before the dialog, not only after it. Making somebody pick a
        // file and then telling them the profile moved on is a worse answer
        // than telling them first, and `save_as` checks it again anyway.
        let suggested = {
            let sessions = self.sessions();
            let open = sessions.session(session)?;
            if open.revision() != request.expected_revision {
                return Err(qcm_core::error::ProfileError::RevisionConflict {
                    expected: request.expected_revision,
                    actual: open.revision(),
                }
                .into());
            }
            open.save_target_name().map_or_else(
                || open.file().document.title().to_owned(),
                ToString::to_string,
            )
        };

        let Some(target) = self.picker.pick_save_as(&suggested)? else {
            return Ok(None);
        };
        self.sessions()
            .save_as(session, request.expected_revision, target)
            .map(|receipt| Some(SaveReceiptDto::from(&receipt)))
    }

    pub fn close_profile(&self, raw: Value) -> Result<CloseOutcomeDto, QcmError> {
        let request: CloseProfileRequest = parse(raw, "close_profile request")?;
        let session = session_id(&request.session_id)?;
        let close = request.close_request()?;
        self.sessions()
            .close(session, close)
            .map(|outcome| CloseOutcomeDto::from(&outcome))
    }
}

/// The concrete shell a running app holds.
///
/// Named once here so the commands can take `State<'_, ShellState>` and the
/// tests can build the same type over fakes.
pub type ShellState = Shell<
    crate::adapters::library::FileSystemProfileLibrary<
        crate::adapters::storage::volumes::PlatformVolumes,
    >,
    crate::adapters::picker::NativeProfilePicker<
        crate::adapters::storage::volumes::PlatformVolumes,
    >,
    crate::adapters::settings::SettingsFile,
>;

/// Build the state the window runs on.
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
