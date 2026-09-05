//! Native workbook import/export boundary.
//!
//! Paths stop inside `NativeWorkbookPicker`. The WebView sees only a sanitized
//! display name, an opaque one-shot import id and a bounded review projection.
//! Full skipped-tab cells stay native until the user accepts or repairs them.

use qcm_config::{
    Grid, ProfileFile, SkippedTabKind, TabRename, WorkbookImport, WorkbookLimitation,
    XLSX_MAX_WORKBOOK_BYTES, export_xlsx, import_xlsx, parse_csv, repaired_as_mode, write_csv,
};
use qcm_core::error::{ConfigError, InternalError, OsDetail, QcmError, RequestError};
use qcm_core::ports::local::ProfileDisplayName;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::BTreeMap;
use std::fs;
use std::io::{Read, Take};
use std::sync::{Mutex, MutexGuard, PoisonError};

const MAX_PREVIEW_ROWS: usize = 8;
const MAX_PREVIEW_COLUMNS: usize = 12;
const MAX_PENDING_IMPORTS: usize = 8;
const MAX_IMPORT_ID: usize = 64;

#[derive(Debug)]
pub struct PickedWorkbook {
    display_name: ProfileDisplayName,
    bytes: Vec<u8>,
}

pub trait WorkbookPicker: Send + Sync {
    fn pick_import(&self) -> Result<Option<PickedWorkbook>, QcmError>;
    fn save_export(
        &self,
        suggested: &str,
        bytes: &[u8],
    ) -> Result<Option<ProfileDisplayName>, QcmError>;
}

#[derive(Debug, Default)]
pub struct NativeWorkbookPicker;

impl WorkbookPicker for NativeWorkbookPicker {
    fn pick_import(&self) -> Result<Option<PickedWorkbook>, QcmError> {
        let Some(path) = rfd::FileDialog::new()
            .set_title("Import spreadsheet")
            .add_filter("Excel workbook", &["xlsx"])
            .pick_file()
        else {
            return Ok(None);
        };

        let metadata =
            fs::metadata(&path).map_err(|error| workbook_io("read workbook metadata", error))?;
        if metadata.len() > XLSX_MAX_WORKBOOK_BYTES as u64 {
            return Err(QcmError::Config(ConfigError::TooLarge {
                limit_bytes: XLSX_MAX_WORKBOOK_BYTES as u64,
            }));
        }

        let file = fs::File::open(&path).map_err(|error| workbook_io("open workbook", error))?;
        let mut bounded: Take<fs::File> = file.take(XLSX_MAX_WORKBOOK_BYTES as u64 + 1);
        let mut bytes = Vec::with_capacity(metadata.len() as usize);
        bounded
            .read_to_end(&mut bytes)
            .map_err(|error| workbook_io("read workbook", error))?;
        if bytes.len() > XLSX_MAX_WORKBOOK_BYTES {
            return Err(QcmError::Config(ConfigError::TooLarge {
                limit_bytes: XLSX_MAX_WORKBOOK_BYTES as u64,
            }));
        }

        let display_name = path.file_name().and_then(|name| name.to_str()).map_or_else(
            || ProfileDisplayName::new("Workbook.xlsx"),
            ProfileDisplayName::new,
        );
        Ok(Some(PickedWorkbook {
            display_name,
            bytes,
        }))
    }

    fn save_export(
        &self,
        suggested: &str,
        bytes: &[u8],
    ) -> Result<Option<ProfileDisplayName>, QcmError> {
        let suggested = xlsx_name(suggested);
        let Some(path) = rfd::FileDialog::new()
            .set_title("Export spreadsheet")
            .set_file_name(&suggested)
            .add_filter("Excel workbook", &["xlsx"])
            .save_file()
        else {
            return Ok(None);
        };
        // Beside the target, then renamed, so a failed write cannot leave a
        // truncated workbook under the name the person chose.
        let temp = path.with_extension("xlsx.qscm-tmp");
        fs::write(&temp, bytes).map_err(|error| {
            let _ = fs::remove_file(&temp);
            workbook_io("write workbook", error)
        })?;
        fs::rename(&temp, &path).map_err(|error| {
            let _ = fs::remove_file(&temp);
            workbook_io("replace workbook", error)
        })?;
        Ok(Some(
            path.file_name().and_then(|name| name.to_str()).map_or_else(
                || ProfileDisplayName::new(&suggested),
                ProfileDisplayName::new,
            ),
        ))
    }
}

fn workbook_io(what: &'static str, error: std::io::Error) -> QcmError {
    QcmError::Internal(InternalError {
        what,
        detail: OsDetail::new(error.to_string()),
    })
}

fn import_error(error: qcm_config::WorkbookError) -> QcmError {
    match error {
        qcm_config::WorkbookError::FileTooLarge { limit, .. } => {
            QcmError::Config(ConfigError::TooLarge {
                limit_bytes: limit as u64,
            })
        }
        qcm_config::WorkbookError::PartTooLarge { limit, .. } => {
            QcmError::Config(ConfigError::TooLarge { limit_bytes: limit })
        }
        qcm_config::WorkbookError::InvalidArchive
        | qcm_config::WorkbookError::InvalidXml
        | qcm_config::WorkbookError::MissingWorkbookParts => {
            QcmError::Config(ConfigError::Unreadable)
        }
    }
}

#[derive(Debug)]
struct PendingWorkbook {
    name: ProfileDisplayName,
    import: WorkbookImport,
}

#[derive(Debug, Default)]
struct PendingTable {
    next: u64,
    items: BTreeMap<u64, PendingWorkbook>,
}

#[derive(Debug)]
pub struct WorkbookShell<P: WorkbookPicker> {
    picker: P,
    pending: Mutex<PendingTable>,
}

impl<P: WorkbookPicker> WorkbookShell<P> {
    pub const fn new(picker: P) -> Self {
        Self {
            picker,
            pending: Mutex::new(PendingTable {
                next: 0,
                items: BTreeMap::new(),
            }),
        }
    }

    fn pending(&self) -> MutexGuard<'_, PendingTable> {
        self.pending.lock().unwrap_or_else(PoisonError::into_inner)
    }

    pub fn choose_import(&self) -> Result<Option<WorkbookImportReviewDto>, QcmError> {
        let Some(picked) = self.picker.pick_import()? else {
            return Ok(None);
        };
        self.import_bytes(picked.display_name.as_str(), picked.bytes)
            .map(Some)
    }

    /// Register workbook bytes obtained by another native service such as
    /// Community or Drive. The WebView still receives the exact same bounded
    /// review DTO and opaque one-shot import id as a locally picked workbook.
    pub fn import_bytes(
        &self,
        display_name: &str,
        bytes: Vec<u8>,
    ) -> Result<WorkbookImportReviewDto, QcmError> {
        if bytes.len() > XLSX_MAX_WORKBOOK_BYTES {
            return Err(QcmError::Config(ConfigError::TooLarge {
                limit_bytes: XLSX_MAX_WORKBOOK_BYTES as u64,
            }));
        }
        let import = import_xlsx(&bytes).map_err(import_error)?;
        let mut table = self.pending();
        if table.items.len() >= MAX_PENDING_IMPORTS
            && let Some(oldest) = table.items.keys().next().copied()
        {
            table.items.remove(&oldest);
        }
        table.next = table.next.saturating_add(1);
        let id = table.next;
        table.items.insert(
            id,
            PendingWorkbook {
                name: ProfileDisplayName::new(display_name),
                import,
            },
        );
        table
            .items
            .get(&id)
            .map(|pending| review(id, pending))
            .ok_or_else(unknown_import)
    }

    pub fn repair_tab(&self, raw: Value) -> Result<WorkbookImportReviewDto, QcmError> {
        let request: ImportTabRequest = crate::ipc::parse(raw, "repair_workbook_tab request")?;
        let id = import_id(&request.import_id)?;
        let mut table = self.pending();
        let pending = table.items.get_mut(&id).ok_or_else(unknown_import)?;
        let tab = pending
            .import
            .skipped
            .get(request.tab_index)
            .ok_or(QcmError::Request(RequestError::OutOfRange {
                what: "workbook tab index",
            }))?
            .clone();
        if tab.kind != SkippedTabKind::UnreadableA1 {
            return Err(QcmError::Request(RequestError::OutOfRange {
                what: "workbook tab is not repairable",
            }));
        }

        let repaired = repaired_as_mode(&tab);
        let mut grid = parse_csv(&pending.import.csv);
        append_sheet(&mut grid, repaired);
        pending.import.csv = write_csv(&grid);
        pending.import.skipped.remove(request.tab_index);
        Ok(review(id, pending))
    }

    pub fn accept_import(&self, raw: Value) -> Result<AcceptedWorkbook, QcmError> {
        let request: ImportRequest = crate::ipc::parse(raw, "accept_workbook_import request")?;
        let id = import_id(&request.import_id)?;
        let pending = self
            .pending()
            .items
            .remove(&id)
            .ok_or_else(unknown_import)?;
        Ok(AcceptedWorkbook {
            name: csv_name(pending.name.as_str()),
            csv: pending.import.csv,
        })
    }

    pub fn cancel_import(&self, raw: Value) -> Result<(), QcmError> {
        let request: ImportRequest = crate::ipc::parse(raw, "cancel_workbook_import request")?;
        let id = import_id(&request.import_id)?;
        self.pending().items.remove(&id);
        Ok(())
    }

    pub fn export_profile(
        &self,
        profile: &ProfileFile,
        suggested: &str,
    ) -> Result<Option<WorkbookExportReceiptDto>, QcmError> {
        let bytes = export_xlsx(profile).map_err(|error| {
            QcmError::Internal(InternalError {
                what: "export workbook",
                detail: OsDetail::new(error.to_string()),
            })
        })?;
        let Some(name) = self.picker.save_export(suggested, &bytes)? else {
            return Ok(None);
        };
        Ok(Some(WorkbookExportReceiptDto {
            name: name.as_str().to_owned(),
            bytes: bytes.len(),
        }))
    }
}

fn append_sheet(grid: &mut Grid, repaired: Grid) {
    if !grid.is_empty() && !grid.last().is_some_and(Vec::is_empty) {
        grid.push(Vec::new());
    }
    grid.extend(repaired);
}

fn unknown_import() -> QcmError {
    QcmError::Request(RequestError::OutOfRange {
        what: "workbook import id",
    })
}

fn import_id(raw: &str) -> Result<u64, QcmError> {
    if raw.len() > MAX_IMPORT_ID {
        return Err(QcmError::Request(RequestError::TooLarge {
            what: "workbook import id",
            limit: MAX_IMPORT_ID,
            actual: raw.len(),
        }));
    }
    raw.strip_prefix("workbook-")
        .and_then(|number| number.parse::<u64>().ok())
        .filter(|number| *number > 0)
        .ok_or(QcmError::Request(RequestError::Malformed {
            what: "workbook import id",
        }))
}

/// A workbook can put 32 MB in one cell; the review shows a person a name.
const MAX_REVIEW_TEXT: usize = 200;

fn bounded(text: &str) -> String {
    text.chars()
        .filter(|c| !c.is_control())
        .take(MAX_REVIEW_TEXT)
        .collect()
}

fn review(id: u64, pending: &PendingWorkbook) -> WorkbookImportReviewDto {
    let profile = ProfileFile::load(&pending.import.csv);
    WorkbookImportReviewDto {
        import_id: format!("workbook-{id}"),
        name: bounded(pending.name.as_str()),
        modes: profile
            .document
            .sheets
            .iter()
            .enumerate()
            .map(|(index, mode)| WorkbookModeDto {
                number: index + 1,
                name: bounded(&mode.mode_name),
                kind: match mode.sheet_type {
                    qcm_config::SheetType::ProfileName => "mode",
                    qcm_config::SheetType::Preferences => "preferences",
                    qcm_config::SheetType::Infrared => "infrared",
                }
                .to_owned(),
                binding_count: mode.bindings.len(),
            })
            .collect(),
        skipped: pending
            .import
            .skipped
            .iter()
            .enumerate()
            .map(|(index, tab)| WorkbookSkippedTabDto {
                index,
                name: bounded(&tab.name),
                kind: match tab.kind {
                    SkippedTabKind::UnreadableA1 => "unreadable_a1",
                    SkippedTabKind::Helper => "helper",
                }
                .to_owned(),
                row_count: tab.rows.len(),
                repairable: tab.kind == SkippedTabKind::UnreadableA1,
                preview: tab
                    .rows
                    .iter()
                    .take(MAX_PREVIEW_ROWS)
                    .map(|row| {
                        row.iter()
                            .take(MAX_PREVIEW_COLUMNS)
                            .map(|cell| bounded(cell))
                            .collect()
                    })
                    .collect(),
            })
            .collect(),
        limitation: pending
            .import
            .limitation
            .clone()
            .map(|limitation| match limitation {
                WorkbookLimitation::SheetRows { tab, max } => WorkbookLimitation::SheetRows {
                    tab: bounded(&tab),
                    max,
                },
                other => other,
            }),
        renamed: pending
            .import
            .renamed
            .iter()
            .map(|rename| TabRename {
                mode_number: rename.mode_number,
                tab_name: bounded(&rename.tab_name),
                cell_c1: bounded(&rename.cell_c1),
            })
            .collect(),
        error_count: profile
            .issues
            .iter()
            .filter(|issue| issue.severity == qcm_config::Severity::Error)
            .count(),
        warning_count: profile
            .issues
            .iter()
            .filter(|issue| issue.severity == qcm_config::Severity::Warning)
            .count(),
    }
}

fn csv_name(workbook: &str) -> String {
    let stem = workbook
        .strip_suffix(".xlsx")
        .or_else(|| workbook.strip_suffix(".XLSX"))
        .unwrap_or(workbook)
        .trim();
    format!(
        "{}.csv",
        if stem.is_empty() {
            "Imported profile"
        } else {
            stem
        }
    )
}

fn xlsx_name(profile: &str) -> String {
    let stem = profile
        .strip_suffix(".csv")
        .or_else(|| profile.strip_suffix(".CSV"))
        .unwrap_or(profile)
        .trim();
    format!("{}.xlsx", if stem.is_empty() { "Profile" } else { stem })
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ImportRequest {
    import_id: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ImportTabRequest {
    import_id: String,
    tab_index: usize,
}

#[derive(Debug)]
pub struct AcceptedWorkbook {
    pub name: String,
    pub csv: String,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct WorkbookModeDto {
    pub number: usize,
    pub name: String,
    pub kind: String,
    pub binding_count: usize,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct WorkbookSkippedTabDto {
    pub index: usize,
    pub name: String,
    pub kind: String,
    pub row_count: usize,
    pub repairable: bool,
    pub preview: Vec<Vec<String>>,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct WorkbookImportReviewDto {
    pub import_id: String,
    pub name: String,
    pub modes: Vec<WorkbookModeDto>,
    pub skipped: Vec<WorkbookSkippedTabDto>,
    pub limitation: Option<WorkbookLimitation>,
    pub renamed: Vec<TabRename>,
    pub error_count: usize,
    pub warning_count: usize,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct WorkbookExportReceiptDto {
    pub name: String,
    pub bytes: usize,
}

pub type WorkbookShellState = WorkbookShell<NativeWorkbookPicker>;

#[must_use]
pub const fn native_workbook_shell() -> WorkbookShellState {
    WorkbookShell::new(NativeWorkbookPicker)
}
