//! Following a Sheets link at the profile-session boundary: remember on save,
//! Get the latest (check, then replace on a yes), forget.
//! Port of `MainWindow.Follow.cs`. The network call happens between
//! [`Shell::latest_begin`] and [`Shell::latest_compare`], with no lock held.

use crate::adapters::picker::ProfilePicker;
use crate::follow::{SheetDownload, SheetLink, SheetRefusal, usable};
use crate::ipc::{SessionRequest, SessionRevisionRequest, parse, session_id};
use crate::shell::Shell;
use qcm_config::{ProfileChange, ProfileFile, profile_changes};
use qcm_core::error::{QcmError, RequestError};
use qcm_core::ports::local::{LocalProfileRef, LocalProfileStore};
use qcm_core::profiles::{EditorSnapshot, SessionId};
use qcm_core::settings::SettingsStore;
use serde::{Deserialize, Serialize};
use serde_json::Value;

/// Plans kept at once. A press makes one; an old one is simply dropped.
const MAX_PLANS: usize = 4;

#[derive(Debug)]
pub(crate) struct LatestPlan {
    session: SessionId,
    revision: u64,
    target: LocalProfileRef,
    /// The file as compared. `Err` when it could not be read.
    on_disk: Result<Option<Vec<u8>>, ()>,
    text: String,
}

#[derive(Debug, Default)]
pub(crate) struct LatestTable {
    next: u64,
    plans: std::collections::BTreeMap<u64, LatestPlan>,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct FollowStateDto {
    pub follows: bool,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum ChangeDto {
    LocalUnreadable,
    ModeAdded {
        mode: String,
    },
    ModeRemoved {
        mode: String,
    },
    ModeChanged {
        mode: String,
        added: usize,
        removed: usize,
    },
    OtherChanged,
}

impl From<ProfileChange> for ChangeDto {
    fn from(change: ProfileChange) -> Self {
        let short = |s: String| s.chars().filter(|c| !c.is_control()).take(200).collect();
        match change {
            ProfileChange::LocalUnreadable => Self::LocalUnreadable,
            ProfileChange::ModeAdded(mode) => Self::ModeAdded { mode: short(mode) },
            ProfileChange::ModeRemoved(mode) => Self::ModeRemoved { mode: short(mode) },
            ProfileChange::ModeChanged {
                mode,
                added,
                removed,
            } => Self::ModeChanged {
                mode: short(mode),
                added,
                removed,
            },
            ProfileChange::OtherChanged => Self::OtherChanged,
        }
    }
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum LatestCheckDto {
    NotFollowed,
    /// Unsaved work: the window saves first, after asking.
    Unsaved,
    Busy,
    Refused {
        refusal: SheetRefusal,
    },
    UpToDate,
    Changes {
        #[serde(rename = "planId")]
        plan_id: String,
        changes: Vec<ChangeDto>,
        #[serde(rename = "singleTab")]
        single_tab: bool,
    },
}

#[derive(Debug, Clone, Serialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum LatestReplaceDto {
    Replaced {
        snapshot: Box<EditorSnapshot>,
    },
    /// An edit, or a change on disk, since the list was shown.
    ChangedMeanwhile,
}

/// What the download step needs, taken under the lock and then released.
#[derive(Debug)]
pub enum LatestBegin {
    Fetch {
        session: SessionId,
        revision: u64,
        link: SheetLink,
    },
    Done(LatestCheckDto),
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct PlanRequest {
    plan_id: String,
}

fn plan_id(raw: &str) -> Result<u64, QcmError> {
    raw.strip_prefix("latest-")
        .and_then(|n| n.parse::<u64>().ok())
        .filter(|n| *n > 0)
        .ok_or(QcmError::Request(RequestError::Malformed {
            what: "latest plan id",
        }))
}

impl<L: LocalProfileStore, P: ProfilePicker, S: SettingsStore> Shell<L, P, S> {
    /// The import that opened this session came from a link: keep it until
    /// the profile is saved somewhere it can be kept against.
    pub fn set_pending_follow(&self, session_raw: &str, link: String) {
        if let Ok(session) = session_id(session_raw) {
            self.pending_follow().insert(session, link);
        }
    }

    /// After a successful save: the pending link, if any, now has a file.
    pub(crate) fn remember_follow(&self, session: SessionId) {
        let Some(link) = self.pending_follow().remove(&session) else {
            return;
        };
        let sessions = self.sessions();
        let Ok(open) = sessions.session(session) else {
            return;
        };
        if let Some(target) = open.save_target_ref()
            && let Ok(key) = sessions.store().persistent_key(target)
        {
            self.follow_links().set(&key, &link);
        }
    }

    pub(crate) fn forget_pending_follow(&self, session: SessionId) {
        self.pending_follow().remove(&session);
    }

    fn followed_link(&self, session: SessionId) -> Result<Option<String>, QcmError> {
        let sessions = self.sessions();
        let open = sessions.session(session)?;
        let Some(target) = open.save_target_ref() else {
            return Ok(None);
        };
        let key = sessions.store().persistent_key(target)?;
        Ok(self.follow_links().get(&key))
    }

    pub fn follow_state(&self, raw: Value) -> Result<FollowStateDto, QcmError> {
        let request: SessionRequest = parse(raw, "get_follow_state request")?;
        let session = session_id(&request.session_id)?;
        Ok(FollowStateDto {
            follows: self.followed_link(session)?.is_some(),
        })
    }

    /// Stop following. The sheet itself is not touched: only the link on this
    /// computer is forgotten.
    pub fn forget_follow_link(&self, raw: Value) -> Result<FollowStateDto, QcmError> {
        let request: SessionRequest = parse(raw, "forget_follow_link request")?;
        let session = session_id(&request.session_id)?;
        self.forget_pending_follow(session);
        let sessions = self.sessions();
        let open = sessions.session(session)?;
        if let Some(target) = open.save_target_ref() {
            let key = sessions.store().persistent_key(target)?;
            if !self.follow_links().remove(&key) {
                return Err(QcmError::Internal(qcm_core::error::InternalError {
                    what: "forget followed link",
                    detail: qcm_core::error::OsDetail::new(
                        "the followed-links file could not be written".to_owned(),
                    ),
                }));
            }
        }
        Ok(FollowStateDto { follows: false })
    }

    /// Step one of Get the latest. No request has gone out yet.
    pub fn latest_begin(&self, raw: Value) -> Result<LatestBegin, QcmError> {
        let request: SessionRevisionRequest = parse(raw, "check_latest_from_link request")?;
        let session = session_id(&request.session_id)?;
        let Some(link) = self.followed_link(session)? else {
            return Ok(LatestBegin::Done(LatestCheckDto::NotFollowed));
        };
        {
            let sessions = self.sessions();
            let open = sessions.session(session)?;
            // Unsaved work is never thrown away to make room for the sheet:
            // the window saves it first, so it is what gets compared.
            if open.dirty() || open.revision() != request.expected_revision {
                return Ok(LatestBegin::Done(LatestCheckDto::Unsaved));
            }
        }
        let Some(link) = SheetLink::parse(&link) else {
            return Ok(LatestBegin::Done(LatestCheckDto::Refused {
                refusal: SheetRefusal::NotALink,
            }));
        };
        Ok(LatestBegin::Fetch {
            session,
            revision: request.expected_revision,
            link,
        })
    }

    /// Step two: compare what came back with the file as it is now.
    pub fn latest_compare(
        &self,
        session: SessionId,
        revision: u64,
        download: Result<SheetDownload, SheetRefusal>,
    ) -> Result<LatestCheckDto, QcmError> {
        let got = match download.and_then(usable) {
            Ok(got) => got,
            Err(refusal) => return Ok(LatestCheckDto::Refused { refusal }),
        };
        let Some(mut incoming) = got.file else {
            return Ok(LatestCheckDto::Refused {
                refusal: SheetRefusal::WebPage,
            });
        };
        // What lands on disk matches a save byte for byte.
        incoming.normalize_for_device_csv();
        let (target, on_disk) = {
            let sessions = self.sessions();
            let open = sessions.session(session)?;
            let Some(target) = open.save_target_ref().cloned() else {
                return Ok(LatestCheckDto::NotFollowed);
            };
            let on_disk = sessions.store().read_bytes(&target).map_err(|_| ());
            (target, on_disk)
        };
        // A copy that cannot be read is still a copy: it is compared as
        // unreadable and still goes to the rescue folder before a replace.
        let local = match &on_disk {
            Ok(Some(bytes)) => Some(ProfileFile::load(&String::from_utf8_lossy(bytes))),
            _ => None,
        };
        let changes = profile_changes(local.as_ref(), &incoming);
        if changes.is_empty() {
            return Ok(LatestCheckDto::UpToDate);
        }
        let mut table = self.latest_plans();
        while table.plans.len() >= MAX_PLANS {
            let Some(oldest) = table.plans.keys().next().copied() else {
                break;
            };
            table.plans.remove(&oldest);
        }
        table.next = table.next.saturating_add(1);
        let id = table.next;
        table.plans.insert(
            id,
            LatestPlan {
                session,
                revision,
                target,
                on_disk,
                text: incoming.to_csv_text(),
            },
        );
        Ok(LatestCheckDto::Changes {
            plan_id: format!("latest-{id}"),
            changes: changes.into_iter().map(ChangeDto::from).collect(),
            single_tab: got.single_tab,
        })
    }

    /// The yes. What the person agreed to is the list, compared against the
    /// copy as it was; an edit since, or a file changed on disk since, was
    /// never in it and is not replaced.
    pub fn replace_with_latest(&self, raw: Value) -> Result<LatestReplaceDto, QcmError> {
        let request: PlanRequest = parse(raw, "replace_with_latest request")?;
        let id = plan_id(&request.plan_id)?;
        let plan = self
            .latest_plans()
            .plans
            .remove(&id)
            .ok_or(QcmError::Request(RequestError::OutOfRange {
                what: "latest plan id",
            }))?;
        let mut sessions = self.sessions();
        let open = sessions.session(plan.session)?;
        if open.dirty() || open.revision() != plan.revision {
            return Ok(LatestReplaceDto::ChangedMeanwhile);
        }
        let now = sessions.store().read_bytes(&plan.target).map_err(|_| ());
        let same = matches!((&plan.on_disk, &now), (Ok(before), Ok(after)) if before == after);
        if !same {
            return Ok(LatestReplaceDto::ChangedMeanwhile);
        }
        if let Ok(Some(bytes)) = &plan.on_disk {
            let name = plan.target.display_name().as_str();
            let stem = name
                .strip_suffix(".csv")
                .or_else(|| name.strip_suffix(".CSV"))
                .unwrap_or(name);
            crate::diagnostics::write_rescue_before_latest(stem, bytes)?;
        }
        sessions.store().write(&plan.target, &plan.text)?;
        let snapshot = sessions.reopen_local(plan.session, plan.revision)?;
        Ok(LatestReplaceDto::Replaced {
            snapshot: Box::new(snapshot),
        })
    }
}
