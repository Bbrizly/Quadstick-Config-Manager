//! The read-only projection a window renders.
//!
//! Revision-tagged on purpose. The frontend never derives dirty by comparing
//! objects and never serializes CSV of its own: it draws this, and it sends the
//! revision back with every edit.
//!
//! The DTO is written by hand rather than derived off the format types. Those
//! types are the compatibility surface against the frozen C# core, and giving
//! them a wire shape would make an IPC convenience a reason not to change them.

use crate::ports::local::ProfileDisplayName;
use crate::profiles::session::{ProfileOrigin, ProfileSession, SaveReceipt};
use qcm_config::{Issue, IssueKind, ModeSheet, Severity, SheetType};
use serde::{Deserialize, Serialize};

/// Where the open profile came from, in the form a window may print.
///
/// The device is a printed handle and the file is a bare name, so nothing here
/// can spell out a place on this machine.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum ProfileSourceDto {
    New,
    #[serde(rename_all = "camelCase")]
    Local {
        name: String,
    },
    #[serde(rename_all = "camelCase")]
    Device {
        device: String,
        generation: u64,
        name: String,
    },
    #[serde(rename_all = "camelCase")]
    Community {
        catalog_id: String,
    },
}

impl From<&ProfileOrigin> for ProfileSourceDto {
    fn from(origin: &ProfileOrigin) -> Self {
        match origin {
            ProfileOrigin::New => Self::New,
            ProfileOrigin::Local(target) => Self::Local {
                name: target.display_name().as_str().to_owned(),
            },
            ProfileOrigin::Device {
                device,
                generation,
                name,
            } => Self::Device {
                device: device.to_string(),
                generation: generation.raw(),
                name: name.as_str().to_owned(),
            },
            ProfileOrigin::Community { catalog_id } => Self::Community {
                catalog_id: catalog_id.clone(),
            },
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct IssueDto {
    pub severity: String,
    pub cell: String,
    pub message: String,
    pub fix: String,
    pub kind: String,
}

impl From<&Issue> for IssueDto {
    fn from(issue: &Issue) -> Self {
        Self {
            severity: match issue.severity {
                Severity::Error => "error",
                Severity::Warning => "warning",
            }
            .to_owned(),
            cell: issue.cell.clone(),
            message: issue.message.clone(),
            fix: issue.fix.clone(),
            kind: match issue.kind {
                IssueKind::None => "none",
                IssueKind::UnknownInput => "unknown_input",
            }
            .to_owned(),
        }
    }
}

/// One section of the profile, listed the way the firmware reads it.
///
/// `number` is the position the device counts to, not a name. Two modes may
/// share a name and that is normal, so the number is what a window shows
/// beside one.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModeDto {
    /// Index in the file, from zero, including preferences and infrared.
    pub index: usize,
    /// Mode number the firmware would count to, or `None` for the sheets it
    /// reads on their own keyword and never numbers.
    pub number: Option<usize>,
    pub kind: String,
    pub name: String,
    pub channel: String,
    /// One-based row of the sheet keyword.
    pub start_row: usize,
    pub binding_count: usize,
}

/// Everything a window needs to draw one open profile.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EditorSnapshot {
    pub session_id: String,
    pub revision: u64,
    pub dirty: bool,
    pub can_undo: bool,
    pub source: ProfileSourceDto,
    /// The name Save writes to. `None` means Save has to become Save As.
    pub save_target: Option<String>,
    pub title: String,
    /// The raw grid, which is the canonical state. Odd columns, comments and
    /// formatting are in here because they are in the file.
    pub grid: Vec<Vec<String>>,
    pub issues: Vec<IssueDto>,
    pub error_count: usize,
    pub modes: Vec<ModeDto>,
}

impl EditorSnapshot {
    #[must_use]
    pub fn of(session: &ProfileSession) -> Self {
        let file = session.file();
        let issues: Vec<IssueDto> = file.issues.iter().map(IssueDto::from).collect();
        let error_count = file
            .issues
            .iter()
            .filter(|issue| issue.severity == Severity::Error)
            .count();
        Self {
            session_id: session.id().to_string(),
            revision: session.revision(),
            dirty: session.dirty(),
            can_undo: session.can_undo(),
            source: ProfileSourceDto::from(session.origin()),
            save_target: session
                .save_target_name()
                .map(|name| name.as_str().to_owned()),
            title: file.document.title().to_owned(),
            grid: file.grid.clone(),
            issues,
            error_count,
            modes: modes(&file.document.sheets),
        }
    }
}

fn modes(sheets: &[ModeSheet]) -> Vec<ModeDto> {
    let mut number = 0;
    sheets
        .iter()
        .enumerate()
        .map(|(index, sheet)| {
            // Only Profile Name segments are counted. Preferences and Infrared
            // are separate keywords the firmware reads without a mode number,
            // and counting them made an import of one mode plus a preferences
            // sheet announce itself as two.
            let counted = if sheet.sheet_type == SheetType::ProfileName {
                number += 1;
                Some(number)
            } else {
                None
            };
            ModeDto {
                index,
                number: counted,
                kind: match sheet.sheet_type {
                    SheetType::ProfileName => "mode",
                    SheetType::Preferences => "preferences",
                    SheetType::Infrared => "infrared",
                }
                .to_owned(),
                name: sheet.mode_name.clone(),
                channel: sheet.channel.clone(),
                start_row: sheet.start_row,
                binding_count: sheet.bindings.len(),
            }
        })
        .collect()
}

/// What a completed save tells a window.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SaveReceiptDto {
    pub session_id: String,
    pub revision: u64,
    pub name: String,
    pub bytes: usize,
}

impl From<&SaveReceipt> for SaveReceiptDto {
    fn from(receipt: &SaveReceipt) -> Self {
        Self {
            session_id: receipt.session.to_string(),
            revision: receipt.revision,
            name: ProfileDisplayName::as_str(&receipt.name).to_owned(),
            bytes: receipt.bytes,
        }
    }
}
