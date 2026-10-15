//! Tauri commands for the pasted Sheets link and Get the latest. Network work
//! runs on a blocking worker; no lock is held across it.

use crate::follow::{SheetFetcher, SheetLink, SheetRefusal, fetch_sheet, usable};
use crate::follow_shell::{FollowStateDto, LatestBegin, LatestCheckDto, LatestReplaceDto};
use crate::ipc::parse;
use crate::shell::ShellState;
use crate::workbook_shell::{WorkbookImportReviewDto, WorkbookShellState};
use qcm_config::WorkbookImport;
use qcm_core::error::{InternalError, OsDetail, QcmError, QcmErrorDto};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use tauri::State;

type Failure = Box<QcmErrorDto>;

fn redact<T>(result: Result<T, QcmError>) -> Result<T, Failure> {
    result.map_err(|error| Box::new(QcmErrorDto::from(&error)))
}

fn join_error(error: impl std::fmt::Display) -> Failure {
    Box::new(QcmErrorDto::from(&QcmError::Internal(InternalError {
        what: "Sheets worker",
        detail: OsDetail::new(error.to_string()),
    })))
}

/// The Sheets road and the one-download-at-a-time flag (a mouth stick can
/// double-fire one press).
pub struct SheetsState {
    fetcher: Arc<dyn SheetFetcher>,
    busy: AtomicBool,
}

impl SheetsState {
    #[must_use]
    pub fn new(fetcher: Arc<dyn SheetFetcher>) -> Self {
        Self {
            fetcher,
            busy: AtomicBool::new(false),
        }
    }
}

struct Busy<'a>(&'a AtomicBool);
impl Drop for Busy<'_> {
    fn drop(&mut self) {
        self.0.store(false, Ordering::SeqCst);
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct LinkRequest {
    url: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum SheetsImportDto {
    Review {
        review: Box<WorkbookImportReviewDto>,
    },
    Refused {
        refusal: SheetRefusal,
    },
}

/// The name the review shows, from the profile's own file-name cell.
fn display_name(file: &qcm_config::ProfileFile) -> String {
    let name = file.document.csv_file_name().unwrap_or("").trim();
    let stem = name
        .strip_suffix(".csv")
        .or_else(|| name.strip_suffix(".CSV"))
        .unwrap_or(name);
    format!(
        "{}.xlsx",
        if stem.is_empty() {
            "Imported profile"
        } else {
            stem
        }
    )
}

#[tauri::command]
pub async fn import_sheets_link(
    workbook: State<'_, WorkbookShellState>,
    sheets: State<'_, SheetsState>,
    request: Value,
) -> Result<SheetsImportDto, Failure> {
    let request: LinkRequest = redact(parse(request, "import_sheets_link request"))?;
    let Some(link) = SheetLink::parse(&request.url) else {
        return Ok(SheetsImportDto::Refused {
            refusal: SheetRefusal::NotALink,
        });
    };
    let fetcher = Arc::clone(&sheets.fetcher);
    let worker_link = link.clone();
    let got = tauri::async_runtime::spawn_blocking(move || {
        fetch_sheet(&worker_link, fetcher.as_ref()).and_then(usable)
    })
    .await
    .map_err(join_error)?;
    let got = match got {
        Ok(got) => got,
        Err(refusal) => return Ok(SheetsImportDto::Refused { refusal }),
    };
    let name = got
        .file
        .as_ref()
        .map_or_else(|| "Imported profile.xlsx".to_owned(), display_name);
    let import = WorkbookImport {
        csv: got.text,
        skipped: got.skipped,
        limitation: got.limitation,
        renamed: got.renamed,
    };
    let review =
        workbook.import_sheet_download(&name, import, link.as_str().to_owned(), got.single_tab);
    Ok(SheetsImportDto::Review {
        review: Box::new(review),
    })
}

#[tauri::command]
pub fn get_follow_state(
    profiles: State<'_, ShellState>,
    request: Value,
) -> Result<FollowStateDto, Failure> {
    redact(profiles.follow_state(request))
}

#[tauri::command]
pub fn forget_follow_link(
    profiles: State<'_, ShellState>,
    request: Value,
) -> Result<FollowStateDto, Failure> {
    redact(profiles.forget_follow_link(request))
}

#[tauri::command]
pub async fn check_latest_from_link(
    profiles: State<'_, ShellState>,
    sheets: State<'_, SheetsState>,
    request: Value,
) -> Result<LatestCheckDto, Failure> {
    if sheets.busy.swap(true, Ordering::SeqCst) {
        return Ok(LatestCheckDto::Busy);
    }
    let _busy = Busy(&sheets.busy);
    let (session, revision, link) = match redact(profiles.latest_begin(request))? {
        LatestBegin::Done(answer) => return Ok(answer),
        LatestBegin::Fetch {
            session,
            revision,
            link,
        } => (session, revision, link),
    };
    let fetcher = Arc::clone(&sheets.fetcher);
    let download =
        tauri::async_runtime::spawn_blocking(move || fetch_sheet(&link, fetcher.as_ref()))
            .await
            .map_err(join_error)?;
    redact(profiles.latest_compare(session, revision, download))
}

#[tauri::command]
pub fn replace_with_latest(
    profiles: State<'_, ShellState>,
    request: Value,
) -> Result<LatestReplaceDto, Failure> {
    redact(profiles.replace_with_latest(request))
}
