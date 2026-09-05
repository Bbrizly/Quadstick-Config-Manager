//! Native Google Drive backup/restore/share service.
//!
//! The WebView never sees access tokens, persistent local keys, workbook bytes,
//! or Drive file ids. Conflict and restore identities are short-lived opaque
//! refs minted here. Every push checks modifiedTime before writing so an online
//! edit can never be silently overwritten.

use crate::google_auth::GoogleAuthService;
use crate::shell::DriveProfileSnapshot;
use qcm_config::{ProfileFile, SheetType, XLSX_MAX_WORKBOOK_BYTES};
use qcm_core::error::{InternalError, OsDetail, QcmError, RequestError};
use reqwest::StatusCode;
use reqwest::blocking::{Client, RequestBuilder, Response};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::io::Read;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::Duration;

const SHEETS_BASE: &str = "https://sheets.googleapis.com/v4/spreadsheets";
const DRIVE_BASE: &str = "https://www.googleapis.com/drive/v3/files";
const XLSX_MIME: &str = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
const JSON_LIMIT: usize = 4 * 1024 * 1024;
const LAST_COLUMN: usize = 702;
const LAST_ROW: usize = 10_000;
const MAX_PENDING: usize = 8;

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
struct DriveLink {
    spreadsheet_id: String,
    last_seen_modified_time: String,
    backup_dirty: bool,
    shared: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
struct DriveState {
    links: BTreeMap<String, DriveLink>,
}

#[derive(Debug, Clone)]
struct PendingConflict {
    profile: DriveProfileSnapshot,
    remote_modified: Option<String>,
    missing: bool,
}

#[derive(Debug, Default)]
struct PendingTable {
    next: u64,
    conflicts: BTreeMap<u64, PendingConflict>,
    cloud_refs: BTreeMap<u64, String>,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", tag = "kind")]
pub enum DriveBackupOutcomeDto {
    Pushed { backup_dirty: bool },
    Conflict { resolution_id: String },
    Missing { resolution_id: String },
    Disabled,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct DriveFileDto {
    pub cloud_ref: String,
    pub name: String,
    pub modified_time: String,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct DriveShareDto {
    pub url: String,
}

#[derive(Debug, Clone)]
pub enum DriveResolution {
    Finished(DriveBackupOutcomeDto),
    KeepRemote { name: String, bytes: Vec<u8> },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ConflictChoice {
    ReplaceWithMine,
    KeepOnline,
    Recreate,
    Disable,
}

pub struct DriveService {
    auth: Arc<GoogleAuthService>,
    http: Client,
    state_path: Option<PathBuf>,
    state: Mutex<DriveState>,
    pending: Mutex<PendingTable>,
}

impl DriveService {
    pub fn native(auth: Arc<GoogleAuthService>) -> Result<Self, QcmError> {
        let http = Client::builder()
            .timeout(Duration::from_secs(30))
            .user_agent("QuadStickConfigManager")
            .build()
            .map_err(|error| drive_internal("build Drive HTTP client", error))?;
        let state_path = config_dir().map(|dir| dir.join("drive-links.json"));
        let state = state_path
            .as_deref()
            .and_then(read_state)
            .unwrap_or_default();
        Ok(Self {
            auth,
            http,
            state_path,
            state: Mutex::new(state),
            pending: Mutex::new(PendingTable::default()),
        })
    }

    fn state(&self) -> MutexGuard<'_, DriveState> {
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }
    fn pending(&self) -> MutexGuard<'_, PendingTable> {
        self.pending.lock().unwrap_or_else(PoisonError::into_inner)
    }

    pub fn backup(&self, profile: DriveProfileSnapshot) -> Result<DriveBackupOutcomeDto, QcmError> {
        let existing = self.state().links.get(&profile.persistent_key).cloned();
        let Some(mut link) = existing else {
            return self.create_and_record(&profile);
        };

        link.backup_dirty = true;
        self.set_link(&profile.persistent_key, link.clone())?;
        match self.modified_time(&link.spreadsheet_id) {
            Ok(current) if current == link.last_seen_modified_time => {
                self.push_and_record(&profile, link)
            }
            Ok(current) => {
                let id = self.remember_conflict(profile, Some(current), false);
                Ok(DriveBackupOutcomeDto::Conflict { resolution_id: id })
            }
            Err(DriveHttpError::Status(StatusCode::NOT_FOUND)) => {
                let id = self.remember_conflict(profile, None, true);
                Ok(DriveBackupOutcomeDto::Missing { resolution_id: id })
            }
            Err(error) => Err(error.into_qcm()),
        }
    }

    pub fn resolve(
        &self,
        raw_id: &str,
        choice: ConflictChoice,
    ) -> Result<DriveResolution, QcmError> {
        let id = resolution_id(raw_id)?;
        let pending = self
            .pending()
            .conflicts
            .remove(&id)
            .ok_or_else(unknown_resolution)?;
        let key = pending.profile.persistent_key.clone();
        match choice {
            ConflictChoice::Disable => {
                self.state().links.remove(&key);
                self.save_state()?;
                Ok(DriveResolution::Finished(DriveBackupOutcomeDto::Disabled))
            }
            ConflictChoice::Recreate => self
                .create_and_record(&pending.profile)
                .map(DriveResolution::Finished),
            ConflictChoice::ReplaceWithMine => {
                if pending.missing {
                    return self
                        .create_and_record(&pending.profile)
                        .map(DriveResolution::Finished);
                }
                let link = self
                    .state()
                    .links
                    .get(&key)
                    .cloned()
                    .ok_or_else(unknown_resolution)?;
                self.push_and_record(&pending.profile, link)
                    .map(DriveResolution::Finished)
            }
            ConflictChoice::KeepOnline => {
                if pending.missing {
                    return Err(drive_message("the Drive backup no longer exists"));
                }
                let mut link = self
                    .state()
                    .links
                    .get(&key)
                    .cloned()
                    .ok_or_else(unknown_resolution)?;
                let bytes = self.download_workbook(&link.spreadsheet_id)?;
                link.last_seen_modified_time = pending.remote_modified.unwrap_or_default();
                link.backup_dirty = false;
                self.set_link(&key, link)?;
                Ok(DriveResolution::KeepRemote {
                    name: xlsx_name(&pending.profile.display_name),
                    bytes,
                })
            }
        }
    }

    pub fn list_backups(&self) -> Result<Vec<DriveFileDto>, QcmError> {
        let q = "mimeType='application/vnd.google-apps.spreadsheet' and trashed=false";
        let mut page_token: Option<String> = None;
        let mut found = Vec::new();
        loop {
            let mut request = self.http.get(DRIVE_BASE).query(&[
                ("q", q),
                ("fields", "nextPageToken,files(id,name,modifiedTime)"),
            ]);
            if let Some(token) = page_token.as_deref() {
                request = request.query(&[("pageToken", token)]);
            }
            let value = self.send_json(request)?;
            if let Some(files) = value.get("files").and_then(Value::as_array) {
                for file in files {
                    let Some(id) = file.get("id").and_then(Value::as_str) else {
                        continue;
                    };
                    let name = file
                        .get("name")
                        .and_then(Value::as_str)
                        .unwrap_or("Google Sheet")
                        .to_owned();
                    let modified_time = file
                        .get("modifiedTime")
                        .and_then(Value::as_str)
                        .unwrap_or("")
                        .to_owned();
                    found.push((id.to_owned(), name, modified_time));
                }
            }
            page_token = value
                .get("nextPageToken")
                .and_then(Value::as_str)
                .map(ToOwned::to_owned);
            if page_token.is_none() {
                break;
            }
        }

        let mut pending = self.pending();
        pending.cloud_refs.clear();
        let mut result = Vec::with_capacity(found.len());
        for (id, name, modified_time) in found {
            pending.next = pending.next.saturating_add(1);
            let opaque = pending.next;
            pending.cloud_refs.insert(opaque, id);
            result.push(DriveFileDto {
                cloud_ref: format!("cloud-{opaque}"),
                name,
                modified_time,
            });
        }
        Ok(result)
    }

    pub fn restore_workbook(&self, cloud_ref: &str) -> Result<(String, Vec<u8>), QcmError> {
        let id = cloud_id(cloud_ref)?;
        let drive_id = self
            .pending()
            .cloud_refs
            .get(&id)
            .cloned()
            .ok_or_else(unknown_cloud_ref)?;
        let name = self
            .drive_name(&drive_id)
            .unwrap_or_else(|_| "Drive backup.xlsx".to_owned());
        let bytes = self.download_workbook(&drive_id)?;
        Ok((xlsx_name(&name), bytes))
    }

    pub fn share(&self, profile_key: &str) -> Result<DriveShareDto, QcmError> {
        let mut link = self
            .state()
            .links
            .get(profile_key)
            .cloned()
            .ok_or_else(|| drive_message("this profile has no Drive backup yet"))?;
        if !link.shared {
            let body = json!({ "role": "reader", "type": "anyone", "allowFileDiscovery": false });
            self.send_json(
                self.http
                    .post(format!("{DRIVE_BASE}/{}/permissions", link.spreadsheet_id))
                    .json(&body),
            )?;
            link.shared = true;
            self.set_link(profile_key, link.clone())?;
        }
        Ok(DriveShareDto {
            url: format!(
                "https://docs.google.com/spreadsheets/d/{}/edit?usp=sharing",
                link.spreadsheet_id
            ),
        })
    }

    fn remember_conflict(
        &self,
        profile: DriveProfileSnapshot,
        remote_modified: Option<String>,
        missing: bool,
    ) -> String {
        let mut pending = self.pending();
        if pending.conflicts.len() >= MAX_PENDING
            && let Some(oldest) = pending.conflicts.keys().next().copied()
        {
            pending.conflicts.remove(&oldest);
        }
        pending.next = pending.next.saturating_add(1);
        let id = pending.next;
        pending.conflicts.insert(
            id,
            PendingConflict {
                profile,
                remote_modified,
                missing,
            },
        );
        format!("drive-resolution-{id}")
    }

    fn create_and_record(
        &self,
        profile: &DriveProfileSnapshot,
    ) -> Result<DriveBackupOutcomeDto, QcmError> {
        let title = backup_title(profile);
        let body = json!({ "properties": { "title": title } });
        let created = self.send_json(self.http.post(SHEETS_BASE).json(&body))?;
        let id = created
            .get("spreadsheetId")
            .and_then(Value::as_str)
            .ok_or_else(|| drive_message("Google did not return a spreadsheet id"))?
            .to_owned();
        let link = DriveLink {
            spreadsheet_id: id.clone(),
            backup_dirty: true,
            ..DriveLink::default()
        };
        self.set_link(&profile.persistent_key, link.clone())?;
        self.push_tabs(&id, &profile.file)?;
        let modified = self.modified_time(&id).map_err(DriveHttpError::into_qcm)?;
        self.set_link(
            &profile.persistent_key,
            DriveLink {
                last_seen_modified_time: modified,
                backup_dirty: false,
                ..link
            },
        )?;
        Ok(DriveBackupOutcomeDto::Pushed {
            backup_dirty: false,
        })
    }

    fn push_and_record(
        &self,
        profile: &DriveProfileSnapshot,
        mut link: DriveLink,
    ) -> Result<DriveBackupOutcomeDto, QcmError> {
        self.push_tabs(&link.spreadsheet_id, &profile.file)?;
        link.last_seen_modified_time = self
            .modified_time(&link.spreadsheet_id)
            .map_err(DriveHttpError::into_qcm)?;
        link.backup_dirty = false;
        self.set_link(&profile.persistent_key, link)?;
        Ok(DriveBackupOutcomeDto::Pushed {
            backup_dirty: false,
        })
    }

    fn push_tabs(&self, id: &str, profile: &ProfileFile) -> Result<(), QcmError> {
        let tabs = drive_tabs(profile);
        if !tabs.iter().any(|tab| {
            tab.rows
                .iter()
                .flatten()
                .any(|cell| !cell.trim().is_empty())
        }) {
            return Err(drive_message(
                "refusing to replace a Drive backup with an empty profile",
            ));
        }
        self.shape_tabs(id, &tabs)?;
        let mut data = Vec::new();
        let mut clear_ranges = Vec::new();
        for tab in &tabs {
            let width = tab.rows.iter().map(Vec::len).max().unwrap_or(1).max(1);
            let grid = tab
                .rows
                .iter()
                .map(|row| {
                    let mut padded = row.clone();
                    padded.resize(width, String::new());
                    padded
                })
                .collect::<Vec<_>>();
            data.push(json!({ "range": format!("{}!A1", quoted(&tab.title)), "values": grid }));
            if grid.len() < LAST_ROW {
                clear_ranges.push(format!(
                    "{}!A{}:ZZ{LAST_ROW}",
                    quoted(&tab.title),
                    grid.len() + 1
                ));
            }
            if width < LAST_COLUMN {
                clear_ranges.push(format!(
                    "{}!{}1:ZZ{LAST_ROW}",
                    quoted(&tab.title),
                    column_name(width + 1)
                ));
            }
        }
        self.send_json(
            self.http
                .post(format!("{SHEETS_BASE}/{id}/values:batchUpdate"))
                .json(&json!({
                    "valueInputOption": "RAW",
                    "data": data,
                })),
        )?;
        if !clear_ranges.is_empty() {
            self.send_json(
                self.http
                    .post(format!("{SHEETS_BASE}/{id}/values:batchClear"))
                    .json(&json!({ "ranges": clear_ranges })),
            )?;
        }
        Ok(())
    }

    fn shape_tabs(&self, id: &str, tabs: &[DriveTab]) -> Result<(), QcmError> {
        let metadata = self.send_json(
            self.http
                .get(format!("{SHEETS_BASE}/{id}"))
                .query(&[("fields", "sheets.properties(sheetId,title)")]),
        )?;
        let existing = metadata
            .get("sheets")
            .and_then(Value::as_array)
            .map(|sheets| {
                sheets
                    .iter()
                    .filter_map(|sheet| {
                        let properties = sheet.get("properties")?;
                        Some((
                            properties.get("sheetId")?.as_i64()?,
                            properties.get("title")?.as_str()?.to_owned(),
                        ))
                    })
                    .collect::<Vec<_>>()
            })
            .unwrap_or_default();
        let wanted = tabs
            .iter()
            .map(|tab| tab.title.as_str())
            .collect::<Vec<_>>();
        if existing
            .iter()
            .map(|(_, title)| title.as_str())
            .eq(wanted.iter().copied())
        {
            return Ok(());
        }
        let mut requests = Vec::new();
        for (index, (sheet_id, _)) in existing.iter().enumerate() {
            requests.push(json!({ "updateSheetProperties": { "properties": { "sheetId": sheet_id, "title": format!("_qsc_{index}") }, "fields": "title" } }));
        }
        for (index, tab) in tabs.iter().enumerate() {
            if let Some((sheet_id, _)) = existing.get(index) {
                requests.push(json!({ "updateSheetProperties": { "properties": { "sheetId": sheet_id, "title": tab.title }, "fields": "title" } }));
            } else {
                requests.push(json!({ "addSheet": { "properties": { "title": tab.title } } }));
            }
        }
        for (sheet_id, _) in existing.iter().skip(tabs.len()) {
            requests.push(json!({ "deleteSheet": { "sheetId": sheet_id } }));
        }
        self.send_json(
            self.http
                .post(format!("{SHEETS_BASE}/{id}:batchUpdate"))
                .json(&json!({ "requests": requests })),
        )?;
        Ok(())
    }

    fn modified_time(&self, id: &str) -> Result<String, DriveHttpError> {
        let value = self.send_json_http(
            self.http
                .get(format!("{DRIVE_BASE}/{id}"))
                .query(&[("fields", "modifiedTime")]),
        )?;
        value
            .get("modifiedTime")
            .and_then(Value::as_str)
            .map(ToOwned::to_owned)
            .ok_or_else(|| DriveHttpError::Message("Drive did not return modifiedTime".to_owned()))
    }

    fn drive_name(&self, id: &str) -> Result<String, QcmError> {
        let value = self.send_json(
            self.http
                .get(format!("{DRIVE_BASE}/{id}"))
                .query(&[("fields", "name")]),
        )?;
        Ok(value
            .get("name")
            .and_then(Value::as_str)
            .unwrap_or("Drive backup")
            .to_owned())
    }

    fn download_workbook(&self, id: &str) -> Result<Vec<u8>, QcmError> {
        let request = self
            .http
            .get(format!("{DRIVE_BASE}/{id}/export"))
            .query(&[("mimeType", XLSX_MIME)]);
        let response = self.send(request).map_err(DriveHttpError::into_qcm)?;
        read_bounded(response, XLSX_MAX_WORKBOOK_BYTES)
            .map_err(|error| drive_internal("download Drive workbook", error))
    }

    fn send_json(&self, request: RequestBuilder) -> Result<Value, QcmError> {
        self.send_json_http(request)
            .map_err(DriveHttpError::into_qcm)
    }
    fn send_json_http(&self, request: RequestBuilder) -> Result<Value, DriveHttpError> {
        let response = self.send(request)?;
        let bytes = read_bounded(response, JSON_LIMIT)
            .map_err(|error| DriveHttpError::Message(error.to_string()))?;
        serde_json::from_slice(&bytes).map_err(|error| DriveHttpError::Message(error.to_string()))
    }
    fn send(&self, request: RequestBuilder) -> Result<Response, DriveHttpError> {
        let token = self
            .auth
            .access_token()
            .map_err(|error| DriveHttpError::Message(error.to_string()))?;
        let response = request
            .bearer_auth(token)
            .send()
            .map_err(|error| DriveHttpError::Message(error.to_string()))?;
        if response.status().is_success() {
            Ok(response)
        } else {
            Err(DriveHttpError::Status(response.status()))
        }
    }

    fn set_link(&self, key: &str, link: DriveLink) -> Result<(), QcmError> {
        self.state().links.insert(key.to_owned(), link);
        self.save_state()
    }
    fn save_state(&self) -> Result<(), QcmError> {
        let Some(path) = self.state_path.as_deref() else {
            return Err(drive_message(
                "this platform has no per-user config directory",
            ));
        };
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)
                .map_err(|error| drive_internal("create Drive state directory", error))?;
        }
        let text = serde_json::to_vec_pretty(&*self.state())
            .map_err(|error| drive_internal("serialize Drive state", error))?;
        let temp = path.with_extension("json.qscm-tmp");
        fs::write(&temp, text).map_err(|error| drive_internal("write Drive state", error))?;
        replace_file(&temp, path).map_err(|error| drive_internal("replace Drive state", error))
    }
}

#[derive(Debug)]
enum DriveHttpError {
    Status(StatusCode),
    Message(String),
}
impl DriveHttpError {
    fn into_qcm(self) -> QcmError {
        match self {
            Self::Status(status) => drive_message(&format!("Drive API returned {status}")),
            Self::Message(message) => drive_message(&message),
        }
    }
}

#[derive(Debug, Clone)]
struct DriveTab {
    title: String,
    rows: Vec<Vec<String>>,
}

fn drive_tabs(profile: &ProfileFile) -> Vec<DriveTab> {
    if profile.document.sheets.is_empty() {
        return vec![DriveTab {
            title: "Profile".to_owned(),
            rows: profile.grid.clone(),
        }];
    }
    let mut used = BTreeSet::new();
    profile
        .document
        .sheets
        .iter()
        .enumerate()
        .map(|(index, sheet)| {
            let start = if index == 0 && profile.document.has_version_header {
                0
            } else {
                sheet.start_row.saturating_sub(1).min(profile.grid.len())
            };
            let end = profile
                .document
                .sheets
                .get(index + 1)
                .map_or(profile.grid.len(), |next| {
                    next.start_row.saturating_sub(1).min(profile.grid.len())
                });
            let base = if !sheet.mode_name.trim().is_empty() {
                sheet.mode_name.as_str()
            } else {
                match sheet.sheet_type {
                    SheetType::ProfileName => "Mode",
                    SheetType::Preferences => "Preferences",
                    SheetType::Infrared => "Infrared",
                }
            };
            DriveTab {
                title: unique_title(base, &mut used),
                rows: profile.grid[start..end].to_vec(),
            }
        })
        .collect()
}

fn unique_title(raw: &str, used: &mut BTreeSet<String>) -> String {
    let cleaned: String = raw
        .chars()
        .filter(|c| !c.is_control() && !"[]:*?/\\".contains(*c))
        .take(31)
        .collect();
    let base = if cleaned.trim().is_empty() {
        "Profile"
    } else {
        cleaned.trim()
    };
    if used.insert(base.to_lowercase()) {
        return base.to_owned();
    }
    for suffix in 2..10_000usize {
        let tail = format!(" {suffix}");
        let keep = 31usize.saturating_sub(tail.chars().count());
        let candidate = format!("{}{}", base.chars().take(keep).collect::<String>(), tail);
        if used.insert(candidate.to_lowercase()) {
            return candidate;
        }
    }
    "Profile".to_owned()
}

fn backup_title(profile: &DriveProfileSnapshot) -> String {
    let title = profile.file.document.title().trim();
    if title.is_empty() {
        profile.display_name.trim_end_matches(".csv").to_owned()
    } else {
        title.to_owned()
    }
}
fn quoted(title: &str) -> String {
    format!("'{}'", title.replace('\'', "''"))
}
fn column_name(mut index: usize) -> String {
    let mut name = String::new();
    while index > 0 {
        index -= 1;
        name.insert(0, (b'A' + (index % 26) as u8) as char);
        index /= 26;
    }
    name
}
fn xlsx_name(name: &str) -> String {
    let stem = name
        .strip_suffix(".csv")
        .or_else(|| name.strip_suffix(".CSV"))
        .unwrap_or(name)
        .trim();
    format!(
        "{}.xlsx",
        if stem.is_empty() {
            "Drive backup"
        } else {
            stem
        }
    )
}
fn read_bounded(mut response: Response, limit: usize) -> std::io::Result<Vec<u8>> {
    let mut bytes = Vec::new();
    response
        .by_ref()
        .take(limit as u64 + 1)
        .read_to_end(&mut bytes)?;
    if bytes.len() > limit {
        return Err(std::io::Error::other("response exceeded size limit"));
    }
    Ok(bytes)
}
fn read_state(path: &Path) -> Option<DriveState> {
    serde_json::from_slice(&fs::read(path).ok()?).ok()
}
fn replace_file(temp: &Path, target: &Path) -> std::io::Result<()> {
    #[cfg(target_os = "windows")]
    if target.exists() {
        fs::remove_file(target)?;
    }
    fs::rename(temp, target)
}
fn config_dir() -> Option<PathBuf> {
    const APP_DIR: &str = "QuadStickConfigManagerRewrite";
    #[cfg(target_os = "windows")]
    let base = std::env::var_os("APPDATA").map(PathBuf::from);
    #[cfg(target_os = "macos")]
    let base = std::env::var_os("HOME")
        .map(PathBuf::from)
        .map(|home| home.join("Library").join("Application Support"));
    #[cfg(not(any(target_os = "windows", target_os = "macos")))]
    let base = std::env::var_os("XDG_CONFIG_HOME")
        .map(PathBuf::from)
        .or_else(|| std::env::var_os("HOME").map(|home| PathBuf::from(home).join(".config")));
    base.map(|dir| dir.join(APP_DIR))
}
fn resolution_id(raw: &str) -> Result<u64, QcmError> {
    raw.strip_prefix("drive-resolution-")
        .and_then(|value| value.parse().ok())
        .ok_or_else(unknown_resolution)
}
fn cloud_id(raw: &str) -> Result<u64, QcmError> {
    raw.strip_prefix("cloud-")
        .and_then(|value| value.parse().ok())
        .ok_or_else(unknown_cloud_ref)
}
fn unknown_resolution() -> QcmError {
    RequestError::OutOfRange {
        what: "Drive conflict resolution",
    }
    .into()
}
fn unknown_cloud_ref() -> QcmError {
    RequestError::OutOfRange {
        what: "Drive cloud reference",
    }
    .into()
}
fn drive_message(message: &str) -> QcmError {
    QcmError::Internal(InternalError {
        what: "Google Drive",
        detail: OsDetail::new(message),
    })
}
fn drive_internal(what: &'static str, error: impl std::fmt::Display) -> QcmError {
    QcmError::Internal(InternalError {
        what,
        detail: OsDetail::new(error.to_string()),
    })
}
