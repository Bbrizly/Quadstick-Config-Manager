use crate::community::{CommunityCatalogDto, CommunityImportRequest, CommunityLoadRequest, CommunityService};
use crate::ipc::parse;
use crate::workbook_shell::{WorkbookImportReviewDto, WorkbookShellState};
use qcm_core::error::QcmErrorDto;
use serde_json::Value;
use tauri::State;

type Failure = Box<QcmErrorDto>;

fn redact<T>(result: Result<T, qcm_core::QcmError>) -> Result<T, Failure> {
    result.map_err(|error| Box::new(QcmErrorDto::from(&error)))
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
    let request: CommunityImportRequest = redact(parse(request, "import_community_profile request"))?;
    let bytes = redact(community.download_workbook(&request))?;
    let display_name = request
        .csv_name
        .strip_suffix(".csv")
        .or_else(|| request.csv_name.strip_suffix(".CSV"))
        .map_or_else(|| format!("{}.xlsx", request.csv_name), |stem| format!("{stem}.xlsx"));
    redact(workbook.import_bytes(&display_name, bytes))
}
