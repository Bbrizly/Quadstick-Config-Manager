//! The wire: what a command accepts, what it answers with, and what it refuses
//! before the core is asked anything.
//!
//! Every command takes one raw JSON value and reads it here rather than letting
//! Tauri deserialize a typed argument. That is not ceremony. A typed argument
//! that fails to deserialize is rejected by the framework as a plain string,
//! and a string is neither a stable code the window can switch on nor something
//! the redaction rule has ever looked at. Reading the payload here means every
//! refusal, including a malformed one, leaves as a [`QcmErrorDto`].
//!
//! The limits are ours, so they are printable. The values are the caller's, so
//! they are not: no rejection echoes back what it was sent.

use qcm_config::EditorOp;
use qcm_core::error::{ProfileError, QcmError, RequestError};
use qcm_core::profiles::{CloseOutcome, CloseRequest, SaveReceiptDto, SessionId};
use qcm_core::settings::{
    AppSettingsDto, InterfaceScale, LanguageChoice, SettingsPatch, grouping_from_wire,
    model_from_wire, out_of_range, theme_from_wire,
};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::str::FromStr;

/// The longest name a new profile may be given.
///
/// The device reads a name as CP437 and shows a mangled 8.3 name for anything
/// it cannot, and the display-name type already trims to 128. This is the same
/// number, applied before the work rather than after it, so an absurd payload
/// is refused rather than silently shortened.
pub const MAX_PROFILE_NAME: usize = 128;

/// The most edits one `apply_editor_ops` call may carry.
///
/// A batch is a convenience for a window submitting one gesture. Nothing the UI
/// does produces hundreds of operations at once, and a batch is applied to a
/// clone of the whole profile, so an unbounded list is an unbounded allocation.
pub const MAX_OPS_PER_BATCH: usize = 256;

/// The longest text one operation may carry.
///
/// Deliberately far above anything a real cell holds. It is a ceiling on a
/// hostile payload, not an opinion about what a person may type: the action
/// name limit that actually matters is the firmware's 40, and it is enforced by
/// the format crate.
pub const MAX_OP_TEXT: usize = 4096;

/// Read a request, or refuse it with a code instead of a sentence.
pub fn parse<T: serde::de::DeserializeOwned>(
    raw: Value,
    what: &'static str,
) -> Result<T, QcmError> {
    // The serde message is dropped on purpose. It quotes the payload, and a
    // malformed payload is exactly where a path or a pasted secret arrives.
    serde_json::from_value(raw).map_err(|_| RequestError::Malformed { what }.into())
}

/// Read a session id that came back from a window.
///
/// An id this app never minted is an unknown session, which is what a window
/// gets for acting on a profile that has been closed. It is not a separate
/// failure the UI needs a separate branch for.
pub fn session_id(raw: &str) -> Result<SessionId, QcmError> {
    SessionId::from_str(raw).map_err(|()| ProfileError::UnknownSession.into())
}

fn within(value: &str, limit: usize, what: &'static str) -> Result<(), QcmError> {
    let actual = value.chars().count();
    if actual > limit {
        return Err(RequestError::TooLarge {
            what,
            limit,
            actual,
        }
        .into());
    }
    Ok(())
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NewProfileRequest {
    /// The name stamped into the profile's own file-name cell. Passed through
    /// exactly as given: it is written into the file, so trimming or
    /// re-casing it would be the app editing something nobody typed.
    pub name: String,
}

impl NewProfileRequest {
    pub fn check(&self) -> Result<(), QcmError> {
        within(&self.name, MAX_PROFILE_NAME, "profile name")
    }
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ApplyEditorOpsRequest {
    pub session_id: String,
    pub expected_revision: u64,
    pub ops: Vec<EditorOp>,
}

impl ApplyEditorOpsRequest {
    pub fn check(&self) -> Result<(), QcmError> {
        if self.ops.len() > MAX_OPS_PER_BATCH {
            return Err(RequestError::TooLarge {
                what: "editor operations",
                limit: MAX_OPS_PER_BATCH,
                actual: self.ops.len(),
            }
            .into());
        }
        for op in &self.ops {
            for text in op_text(op) {
                within(text, MAX_OP_TEXT, "editor operation text")?;
            }
        }
        Ok(())
    }
}

/// Every caller-supplied string inside one operation.
///
/// Listed by hand rather than serialized and measured, so a new operation with
/// a new string field is a compile error here instead of an unchecked field.
fn op_text(op: &EditorOp) -> Vec<&str> {
    match op {
        EditorOp::SetCell { value, .. } => vec![value.as_str()],
        EditorOp::SetOutput { token, action, .. } => vec![token.as_str(), action.as_str()],
        EditorOp::AddMode { name } => vec![name.as_str()],
        EditorOp::RenameMode { name, .. } => vec![name.as_str()],
        EditorOp::SetModeChannel { channel, .. } => vec![channel.as_str()],
        EditorOp::AddRow { .. }
        | EditorOp::DeleteRow { .. }
        | EditorOp::MoveRow { .. }
        | EditorOp::Normalize => Vec::new(),
    }
}

/// What undo, save and save-as all need: which profile, and the revision the
/// window was looking at when the user asked.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionRevisionRequest {
    pub session_id: String,
    pub expected_revision: u64,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CloseProfileRequest {
    pub session_id: String,
    /// `if_clean`, `save` or `discard`. There is no fourth, implicit answer:
    /// closing something dirty without saying which of these applies is
    /// refused rather than guessed at.
    pub disposition: String,
}

impl CloseProfileRequest {
    pub fn close_request(&self) -> Result<CloseRequest, QcmError> {
        match self.disposition.as_str() {
            "if_clean" => Ok(CloseRequest::IfClean),
            "save" => Ok(CloseRequest::Save),
            "discard" => Ok(CloseRequest::Discard),
            _ => Err(out_of_range("close disposition")),
        }
    }
}

/// Settings as they arrive: strings and numbers, none of them trusted yet.
///
/// Every field is wider than the setting it becomes, so an illegal value is
/// read successfully and then refused by name. If these were the typed values,
/// a scale of 137 would be a deserialization failure, and the window would be
/// told "malformed request" when the truthful answer is "137 is not a size".
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SettingsPatchDto {
    #[serde(default)]
    pub model: Option<String>,
    #[serde(default)]
    pub theme: Option<String>,
    #[serde(default)]
    pub language: Option<String>,
    #[serde(default)]
    pub interface_scale_percent: Option<i64>,
    #[serde(default)]
    pub reduce_motion: Option<bool>,
    #[serde(default)]
    pub remember_window: Option<bool>,
    #[serde(default)]
    pub device_cards: Option<bool>,
    #[serde(default)]
    pub picker_grouping: Option<String>,
    #[serde(default)]
    pub tutorial_seen: Option<bool>,
}

impl SettingsPatchDto {
    /// Turn the wire into the typed patch, refusing anything outside the set.
    ///
    /// Nothing here rounds, truncates or falls back to a default. A value this
    /// app does not offer is a refusal with the field named.
    pub fn validate(&self) -> Result<SettingsPatch, QcmError> {
        let model = match &self.model {
            Some(value) => Some(model_from_wire(value).ok_or_else(|| out_of_range("model"))?),
            None => None,
        };
        let theme = match &self.theme {
            Some(value) => Some(theme_from_wire(value).ok_or_else(|| out_of_range("theme"))?),
            None => None,
        };
        let language = match &self.language {
            Some(value) => {
                Some(LanguageChoice::new(value).ok_or_else(|| out_of_range("language"))?)
            }
            None => None,
        };
        let interface_scale = match self.interface_scale_percent {
            Some(percent) => Some(
                u16::try_from(percent)
                    .ok()
                    .and_then(InterfaceScale::new)
                    .ok_or_else(|| out_of_range("interface scale"))?,
            ),
            None => None,
        };
        let picker_grouping = match &self.picker_grouping {
            Some(value) => {
                Some(grouping_from_wire(value).ok_or_else(|| out_of_range("picker grouping"))?)
            }
            None => None,
        };
        Ok(SettingsPatch {
            model,
            theme,
            language,
            interface_scale,
            reduce_motion: self.reduce_motion,
            remember_window: self.remember_window,
            device_cards: self.device_cards,
            picker_grouping,
            tutorial_seen: self.tutorial_seen,
        })
    }
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateSettingsRequest {
    pub expected_revision: u64,
    pub patch: SettingsPatchDto,
}

/// What the app is and what it can currently do.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppSnapshotDto {
    pub version: String,
    pub platform: String,
    pub capabilities: CapabilitiesDto,
    pub settings: AppSettingsDto,
}

/// Which parts of the app are wired, not which parts are planned.
///
/// A window uses these to decide what to show, so a flag that is true before
/// its commands exist is a button that does nothing. Each one is flipped by the
/// task that registers its commands.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CapabilitiesDto {
    /// Open, edit, undo and save a profile on this computer.
    pub profile_editing: bool,
    /// Device discovery, the library and the install transaction. TASK-033.
    pub device_install: bool,
    /// Live input from the QuadStick. TASK-034.
    pub live_input: bool,
    /// The community catalog. TASK-043.
    pub community_catalog: bool,
    /// Google backup and sharing. TASK-045.
    pub google_backup: bool,
    /// The agent. Switched off in the shipped app too.
    pub agent: bool,
}

/// What closing actually did.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum CloseOutcomeDto {
    Closed,
    #[serde(rename_all = "camelCase")]
    SavedAndClosed {
        receipt: SaveReceiptDto,
    },
    /// Still open, still dirty. The window has to ask the user.
    KeptOpenUnsavedChanges,
}

impl From<&CloseOutcome> for CloseOutcomeDto {
    fn from(outcome: &CloseOutcome) -> Self {
        match outcome {
            CloseOutcome::Closed => Self::Closed,
            CloseOutcome::SavedAndClosed(receipt) => Self::SavedAndClosed {
                receipt: SaveReceiptDto::from(receipt),
            },
            CloseOutcome::KeptOpenUnsavedChanges => Self::KeptOpenUnsavedChanges,
        }
    }
}
