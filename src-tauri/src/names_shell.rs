//! Custom output names no mapping uses yet (`CustomNames.cs` `_drafts`).
//!
//! A name in use lives in its row's column L, in the file. One with no row
//! has nowhere in the file to live, so Avalonia keeps it in settings under the
//! profile's path (`AppSettings.CustomNames`). Here it is kept the same way,
//! under the profile's persistent key, in `custom-names.json`. An untitled
//! profile has no key yet: its names stay in the window until the first save.

use crate::adapters::picker::ProfilePicker;
use crate::ipc::{parse, session_id};
use crate::shell::Shell;
use qcm_core::error::{QcmError, RequestError};
use qcm_core::ports::local::LocalProfileStore;
use qcm_core::settings::SettingsStore;
use serde::{Deserialize, Serialize};
use serde_json::Value;

/// `ProfileFile.MaxActionName` and a sane ceiling on how many wait.
const MAX_NAME: usize = 40;
const MAX_DRAFTS: usize = 500;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct DraftsRequest {
    session_id: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct SetDraftsRequest {
    session_id: String,
    /// `[name, output token]` in table order.
    drafts: Vec<(String, String)>,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct CustomNameDraftsDto {
    pub drafts: Vec<(String, String)>,
    /// False while the profile has no file to file them under.
    pub kept: bool,
}

impl<L: LocalProfileStore, P: ProfilePicker, S: SettingsStore> Shell<L, P, S> {
    fn draft_key(&self, raw_session: &str) -> Result<Option<String>, QcmError> {
        let session = session_id(raw_session)?;
        let sessions = self.sessions();
        let open = sessions.session(session)?;
        let Some(target) = open.save_target_ref() else {
            return Ok(None);
        };
        Ok(Some(sessions.store().persistent_key(target)?))
    }

    pub fn custom_name_drafts(&self, raw: Value) -> Result<CustomNameDraftsDto, QcmError> {
        let request: DraftsRequest = parse(raw, "get_custom_name_drafts request")?;
        let Some(key) = self.draft_key(&request.session_id)? else {
            return Ok(CustomNameDraftsDto {
                drafts: Vec::new(),
                kept: false,
            });
        };
        let drafts = self
            .custom_name_store()
            .get(&key)
            .and_then(|json| serde_json::from_str::<Vec<(String, String)>>(&json).ok())
            .unwrap_or_default();
        Ok(CustomNameDraftsDto { drafts, kept: true })
    }

    /// `PersistDrafts`: the whole list, replaced; an empty list removes it.
    pub fn set_custom_name_drafts(&self, raw: Value) -> Result<CustomNameDraftsDto, QcmError> {
        let request: SetDraftsRequest = parse(raw, "set_custom_name_drafts request")?;
        if request.drafts.len() > MAX_DRAFTS {
            return Err(RequestError::TooLarge {
                what: "custom names",
                limit: MAX_DRAFTS,
                actual: request.drafts.len(),
            }
            .into());
        }
        if request.drafts.iter().any(|(name, token)| {
            name.trim().is_empty() || name.chars().count() > MAX_NAME || token.len() > 64
        }) {
            return Err(RequestError::OutOfRange {
                what: "custom name",
            }
            .into());
        }
        let Some(key) = self.draft_key(&request.session_id)? else {
            return Ok(CustomNameDraftsDto {
                drafts: request.drafts,
                kept: false,
            });
        };
        let store = self.custom_name_store();
        let kept = if request.drafts.is_empty() {
            store.remove(&key)
        } else {
            serde_json::to_string(&request.drafts).is_ok_and(|json| store.set(&key, &json))
        };
        Ok(CustomNameDraftsDto {
            drafts: request.drafts,
            kept,
        })
    }
}
