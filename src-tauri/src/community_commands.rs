use crate::community::{
    CommunityCatalogDto, CommunityImportRequest, CommunityLoadRequest, CommunityService,
};
use crate::ipc::parse;
use crate::workbook_shell::{WorkbookImportReviewDto, WorkbookShellState};
use qcm_core::error::{InternalError, OsDetail, QcmError, QcmErrorDto, RequestError};
use serde::Deserialize;
use serde_json::Value;
use tauri::State;

type Failure = Box<QcmErrorDto>;

fn redact<T>(result: Result<T, QcmError>) -> Result<T, Failure> {
    result.map_err(|error| Box::new(QcmErrorDto::from(&error)))
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct OpenCommunitySheetRequest {
    sheet_id: String,
}

fn checked_sheet_id(raw: &str) -> Result<&str, QcmError> {
    if (20..=200).contains(&raw.len())
        && raw
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_'))
    {
        Ok(raw)
    } else {
        Err(RequestError::OutOfRange {
            what: "community sheet id",
        }
        .into())
    }
}

#[tauri::command]
pub fn load_community_catalog(
    state: State<'_, CommunityService>,
    request: Value,
) -> Result<CommunityCatalogDto, Failure> {
    let request: CommunityLoadRequest = redact(parse(request, "load_community_catalog request"))?;
    redact(state.load(request.refresh))
}

#[tauri::command]
pub fn import_community_profile(
    community: State<'_, CommunityService>,
    workbook: State<'_, WorkbookShellState>,
    request: Value,
) -> Result<WorkbookImportReviewDto, Failure> {
    let request: CommunityImportRequest =
        redact(parse(request, "import_community_profile request"))?;
    let bytes = redact(community.download_workbook(&request))?;
    let display_name = request
        .csv_name
        .strip_suffix(".csv")
        .or_else(|| request.csv_name.strip_suffix(".CSV"))
        .map_or_else(
            || format!("{}.xlsx", request.csv_name),
            |stem| format!("{stem}.xlsx"),
        );
    redact(workbook.import_bytes(&display_name, bytes))
}

#[tauri::command]
pub fn open_community_sheet(request: Value) -> Result<(), Failure> {
    let request: OpenCommunitySheetRequest =
        redact(parse(request, "open_community_sheet request"))?;
    let sheet_id = redact(checked_sheet_id(&request.sheet_id))?;
    let url = format!("https://docs.google.com/spreadsheets/d/{sheet_id}/edit");
    open::that(url).map_err(|error| {
        Box::new(QcmErrorDto::from(&QcmError::Internal(InternalError {
            what: "open community sheet",
            detail: OsDetail::new(error.to_string()),
        })))
    })
}
