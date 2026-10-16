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
    /// Opaque ref to (Drive id, name as listed).
    cloud_refs: BTreeMap<u64, (String, String)>,
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
    /// Already linked to a profile file that still exists here. Missing is
    /// not deleted (an unmounted drive), so only an existing file counts.
    pub already_linked: bool,
}

/// Why one pick of a bulk restore did not land (`RestoreSummary`).
#[derive(Debug, Clone, Copy, Serialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum RestoreReason {
    AlreadyExists,
    CouldNotReadTheSheet,
    NotAValidProfile,
    DownloadFailed,
    CouldNotWriteTheFile,
    CouldNotSaveTheLink,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RestoreItemDto {
    pub name: String,
    pub reason: RestoreReason,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct RestoreSummaryDto {
    pub imported: Vec<String>,
    pub skipped: Vec<RestoreItemDto>,
    pub failed: Vec<RestoreItemDto>,
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

/// Where Drive gets its token, and whether backup is on at all. Google
/// sign-in in the app; a fake in tests.
pub trait DriveAuth: Send + Sync {
    /// `DriveBackup` on, configured, and a token stored (`MainWindow.Backup`).
    fn connected(&self) -> bool;
    fn access_token(&self) -> Result<String, QcmError>;
}

impl DriveAuth for GoogleAuthService {
    fn connected(&self) -> bool {
        self.status().is_ok_and(|status| status.connected)
    }
    fn access_token(&self) -> Result<String, QcmError> {
        GoogleAuthService::access_token(self)
    }
}

pub struct DriveService {
    auth: Arc<dyn DriveAuth>,
    http: Client,
    sheets_base: String,
    drive_base: String,
    state_path: Option<PathBuf>,
    state: Mutex<DriveState>,
    pending: Mutex<PendingTable>,
    /// `DriveBackup.Locked`: one push at a time, so a save's background push
    /// and a Back up press never race on the same link.
    gate: Mutex<()>,
    /// Last ticket handed out, and the newest per profile key.
    tickets: Mutex<(u64, BTreeMap<String, u64>)>,
}

impl DriveService {
    pub fn native(auth: Arc<GoogleAuthService>) -> Result<Self, QcmError> {
        let state_path = config_dir().map(|dir| dir.join("drive-links.json"));
        Self::with_endpoints(auth, SHEETS_BASE, DRIVE_BASE, state_path)
    }

    pub(crate) fn with_endpoints(
        auth: Arc<dyn DriveAuth>,
        sheets_base: &str,
        drive_base: &str,
        state_path: Option<PathBuf>,
    ) -> Result<Self, QcmError> {
        let http = Client::builder()
            .timeout(Duration::from_secs(30))
            .user_agent("QuadStickConfigManager")
            .build()
            .map_err(|error| drive_internal("build Drive HTTP client", error))?;
        let state = state_path
            .as_deref()
            .and_then(read_state)
            .unwrap_or_default();
        Ok(Self {
            auth,
            http,
            sheets_base: sheets_base.to_owned(),
            drive_base: drive_base.to_owned(),
            state_path,
            state: Mutex::new(state),
            pending: Mutex::new(PendingTable::default()),
            gate: Mutex::new(()),
            tickets: Mutex::new((0, BTreeMap::new())),
        })
    }

    fn gate(&self) -> MutexGuard<'_, ()> {
        self.gate.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// Taken on the saving thread, in save order. A background push that
    /// finds a newer ticket for the same profile stands down, so threads that
    /// reach the gate out of order never put an older save over a newer one.
    pub fn ticket(&self, key: &str) -> u64 {
        let mut tickets = self.tickets.lock().unwrap_or_else(PoisonError::into_inner);
        tickets.0 = tickets.0.saturating_add(1);
        let ticket = tickets.0;
        tickets.1.insert(key.to_owned(), ticket);
        ticket
    }

    fn is_latest(&self, key: &str, ticket: u64) -> bool {
        self.tickets
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .1
            .get(key)
            .is_none_or(|latest| *latest == ticket)
    }

    /// `FireBackupPush`: every save hands its bytes to Drive when backup is
    /// on, the first one creating the sheet. `None` means backup is off, or a
    /// newer save of this profile is already on its way.
    pub fn backup_after_save(
        &self,
        profile: DriveProfileSnapshot,
        ticket: u64,
    ) -> Option<Result<DriveBackupOutcomeDto, QcmError>> {
        let _gate = self.gate();
        (self.is_latest(&profile.persistent_key, ticket) && self.auth.connected())
            .then(|| self.backup_locked(profile))
    }

    /// `RetryIfDirtyAsync`: a profile opened from a path whose last push
    /// failed tries again. `None` when there was nothing to retry.
    pub fn retry_if_dirty(
        &self,
        profile: DriveProfileSnapshot,
        ticket: u64,
    ) -> Option<Result<DriveBackupOutcomeDto, QcmError>> {
        let _gate = self.gate();
        let dirty = self
            .state()
            .links
            .get(&profile.persistent_key)
            .is_some_and(|link| link.backup_dirty);
        (dirty && self.is_latest(&profile.persistent_key, ticket) && self.auth.connected())
            .then(|| self.backup_locked(profile))
    }

    fn state(&self) -> MutexGuard<'_, DriveState> {
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }
    fn pending(&self) -> MutexGuard<'_, PendingTable> {
        self.pending.lock().unwrap_or_else(PoisonError::into_inner)
    }

    pub fn backup(&self, profile: DriveProfileSnapshot) -> Result<DriveBackupOutcomeDto, QcmError> {
        let _gate = self.gate();
        self.backup_locked(profile)
    }

    fn backup_locked(
        &self,
        profile: DriveProfileSnapshot,
    ) -> Result<DriveBackupOutcomeDto, QcmError> {
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
        let _gate = self.gate();
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
            let mut request = self.http.get(&self.drive_base).query(&[
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

        let linked: BTreeSet<String> = self
            .state()
            .links
            .iter()
            .filter(|(key, _)| Path::new(key).exists())
            .map(|(_, link)| link.spreadsheet_id.clone())
            .collect();
        let mut pending = self.pending();
        pending.cloud_refs.clear();
        let mut result = Vec::with_capacity(found.len());
        for (id, name, modified_time) in found {
            pending.next = pending.next.saturating_add(1);
            let opaque = pending.next;
            let already_linked = linked.contains(&id);
            pending.cloud_refs.insert(opaque, (id, name.clone()));
            result.push(DriveFileDto {
                cloud_ref: format!("cloud-{opaque}"),
                name,
                modified_time,
                already_linked,
            });
        }
        Ok(result)
    }

    pub fn restore_workbook(&self, cloud_ref: &str) -> Result<(String, Vec<u8>), QcmError> {
        let id = cloud_id(cloud_ref)?;
        let (drive_id, _) = self
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

    /// `DriveBackup.RestoreAsync`: import the picked sheets into `library`
    /// and link each. A name already there is a skip, never an overwrite;
    /// one pick failing never stops the batch; imported always means linked.
    pub fn restore_into_library(
        &self,
        cloud_refs: &[String],
        library: &Path,
    ) -> Result<RestoreSummaryDto, QcmError> {
        let mut picks = Vec::with_capacity(cloud_refs.len());
        {
            let pending = self.pending();
            for raw in cloud_refs {
                let id = cloud_id(raw)?;
                picks.push(
                    pending
                        .cloud_refs
                        .get(&id)
                        .cloned()
                        .ok_or_else(unknown_cloud_ref)?,
                );
            }
        }
        // A fresh machine has no library folder yet, and restore is exactly
        // the moment that must still work.
        fs::create_dir_all(library)
            .map_err(|error| drive_internal("create profile library", error))?;
        // A folder that cannot be listed is not an empty one: a collision
        // there would be an overwrite.
        let mut on_disk = BTreeSet::new();
        for entry in
            fs::read_dir(library).map_err(|error| drive_internal("list profile library", error))?
        {
            let entry = entry.map_err(|error| drive_internal("list profile library", error))?;
            let name = entry.file_name().to_string_lossy().to_lowercase();
            if name.ends_with(".csv") {
                on_disk.insert(name);
            }
        }
        let mut batch: BTreeSet<String> = BTreeSet::new();
        let mut summary = RestoreSummaryDto::default();
        for (drive_id, name) in picks {
            let first = safe_csv_name(&name);
            let mut report = first.trim_end_matches(".csv").to_owned();
            let fail = |summary: &mut RestoreSummaryDto, name: &str, reason| {
                summary.failed.push(RestoreItemDto {
                    name: name.to_owned(),
                    reason,
                });
            };
            let Ok(bytes) = self.download_workbook(&drive_id) else {
                fail(&mut summary, &report, RestoreReason::DownloadFailed);
                continue;
            };
            let Ok(read) = qcm_config::import_xlsx(&bytes) else {
                fail(&mut summary, &report, RestoreReason::CouldNotReadTheSheet);
                continue;
            };
            if ProfileFile::load(&read.csv).document.sheets.is_empty() {
                fail(&mut summary, &report, RestoreReason::NotAValidProfile);
                continue;
            }
            let file_name = safe_csv_name_free(&name, &batch);
            batch.insert(file_name.to_lowercase());
            report = file_name.trim_end_matches(".csv").to_owned();
            if on_disk.contains(&file_name.to_lowercase()) {
                summary.skipped.push(RestoreItemDto {
                    name: report,
                    reason: RestoreReason::AlreadyExists,
                });
                continue;
            }
            // Metadata before the write, so a network failure lands before
            // the disk has anything to undo.
            let Ok(modified) = self.modified_time(&drive_id) else {
                fail(&mut summary, &report, RestoreReason::DownloadFailed);
                continue;
            };
            let dest = library.join(&file_name);
            // Never over an existing file: created new, or not at all.
            let written = fs::OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(&dest)
                .map_err(|error| error.kind() == std::io::ErrorKind::AlreadyExists)
                .and_then(|mut file| {
                    use std::io::Write as _;
                    file.write_all(read.csv.as_bytes())
                        .and_then(|()| file.sync_all())
                        .map_err(|_| {
                            drop(file);
                            let _ = fs::remove_file(&dest);
                            false
                        })
                });
            match written {
                Ok(()) => {}
                Err(true) => {
                    summary.skipped.push(RestoreItemDto {
                        name: report,
                        reason: RestoreReason::AlreadyExists,
                    });
                    continue;
                }
                Err(false) => {
                    fail(&mut summary, &report, RestoreReason::CouldNotWriteTheFile);
                    continue;
                }
            }
            // The same key the profile library gives this file when opened.
            let key = fs::canonicalize(&dest)
                .unwrap_or_else(|_| dest.clone())
                .to_string_lossy()
                .into_owned();
            let link = DriveLink {
                spreadsheet_id: drive_id,
                last_seen_modified_time: modified,
                backup_dirty: false,
                shared: false,
            };
            let before = self.state().links.get(&key).cloned();
            if self.set_link(&key, link).is_err() {
                match before {
                    Some(previous) => self.state().links.insert(key.clone(), previous),
                    None => self.state().links.remove(&key),
                };
                let _ = fs::remove_file(&dest);
                fail(&mut summary, &report, RestoreReason::CouldNotSaveTheLink);
                continue;
            }
            on_disk.insert(file_name.to_lowercase());
            summary.imported.push(report);
        }
        Ok(summary)
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
                    .post(format!(
                        "{}/{}/permissions",
                        self.drive_base, link.spreadsheet_id
                    ))
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
        let created = self.send_json(self.http.post(&self.sheets_base).json(&body))?;
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
                .post(format!("{}/{id}/values:batchUpdate", self.sheets_base))
                .json(&json!({
                    "valueInputOption": "RAW",
                    "data": data,
                })),
        )?;
        if !clear_ranges.is_empty() {
            self.send_json(
                self.http
                    .post(format!("{}/{id}/values:batchClear", self.sheets_base))
                    .json(&json!({ "ranges": clear_ranges })),
            )?;
        }
        Ok(())
    }

    fn shape_tabs(&self, id: &str, tabs: &[DriveTab]) -> Result<(), QcmError> {
        let metadata = self.send_json(
            self.http
                .get(format!("{}/{id}", self.sheets_base))
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
                .post(format!("{}/{id}:batchUpdate", self.sheets_base))
                .json(&json!({ "requests": requests })),
        )?;
        Ok(())
    }

    fn modified_time(&self, id: &str) -> Result<String, DriveHttpError> {
        let value = self.send_json_http(
            self.http
                .get(format!("{}/{id}", self.drive_base))
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
                .get(format!("{}/{id}", self.drive_base))
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
            .get(format!("{}/{id}/export", self.drive_base))
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
    let stem = csv_stem(name).trim();
    format!(
        "{}.xlsx",
        if stem.is_empty() {
            "Drive backup"
        } else {
            stem
        }
    )
}
/// Avalonia `MainWindow.LibraryDir`: `MyDocuments`/QuadStick Profiles, so a
/// restore lands where the shipped app lists profiles. .NET 8 resolves
/// `MyDocuments` per OS (`Environment.GetFolderPathCore.Unix.cs`):
///
/// - Windows: the Documents known folder (follows OneDrive redirection).
/// - macOS: `NSDocumentDirectory`, which is `$HOME/Documents` (the container
///   path under the sandbox, where `HOME` points too).
/// - Linux: [`linux_documents_dir`], the .NET XDG lookup.
#[must_use]
pub fn library_dir() -> PathBuf {
    #[cfg(target_os = "windows")]
    let documents = dirs::document_dir()
        .or_else(|| {
            std::env::var_os("USERPROFILE").map(|home| PathBuf::from(home).join("Documents"))
        })
        .unwrap_or_else(std::env::temp_dir);
    #[cfg(target_os = "linux")]
    let documents = linux_documents_dir(
        &dotnet_home(),
        |key| std::env::var(key).ok(),
        |path| std::fs::read_to_string(path).ok(),
    );
    #[cfg(not(any(target_os = "windows", target_os = "linux")))]
    let documents = dotnet_home().join("Documents");
    documents.join("QuadStick Profiles")
}

/// `PersistedFiles.GetHomeDirectory`: a non-empty `HOME`, else the password
/// entry; .NET then uses `/` when both are missing.
#[cfg(not(target_os = "windows"))]
fn dotnet_home() -> PathBuf {
    std::env::var_os("HOME")
        .filter(|home| !home.is_empty())
        .map(PathBuf::from)
        .or_else(std::env::home_dir)
        .filter(|home| !home.as_os_str().is_empty())
        .unwrap_or_else(|| PathBuf::from("/"))
}

/// .NET 8 `ReadXdgDirectory(home, "XDG_DOCUMENTS_DIR", "Documents")`: an
/// absolute `XDG_DOCUMENTS_DIR`, else the first matching line of
/// `user-dirs.dirs` under an absolute `XDG_CONFIG_HOME` or `$HOME/.config`
/// (`$HOME/`-relative or absolute; other lines are skipped), else
/// `$HOME/Documents`. `XDG_CONFIG_DIRS` is not searched, as in .NET.
#[cfg_attr(not(any(target_os = "linux", test)), allow(dead_code))]
fn linux_documents_dir(
    home: &std::path::Path,
    env: impl Fn(&str) -> Option<String>,
    read: impl Fn(&std::path::Path) -> Option<String>,
) -> PathBuf {
    const KEY: &str = "XDG_DOCUMENTS_DIR";
    if let Some(path) = env(KEY).filter(|path| path.starts_with('/')) {
        return PathBuf::from(path);
    }
    let config = env("XDG_CONFIG_HOME")
        .filter(|path| path.starts_with('/'))
        .map_or_else(|| home.join(".config"), PathBuf::from);
    if let Some(text) = read(&config.join("user-dirs.dirs")) {
        for line in text.lines() {
            if let Some(path) = xdg_line_value(line, KEY) {
                return match path.strip_prefix("$HOME/") {
                    Some(rest) => home.join(rest),
                    None => PathBuf::from(path),
                };
            }
        }
    }
    home.join("Documents")
}

/// One `KEY = "value"` line, parsed the way .NET does: the key must be at the
/// start (after whitespace), the value quoted, `$HOME/`-relative or absolute,
/// and at least one character long.
fn xdg_line_value<'a>(line: &'a str, key: &str) -> Option<&'a str> {
    let rest = line.trim_start().strip_prefix(key)?;
    let rest = rest.trim_start().strip_prefix('=')?;
    let rest = rest.trim_start().strip_prefix('"')?;
    let body = rest.strip_prefix("$HOME/").unwrap_or(rest);
    if !rest.starts_with("$HOME/") && !rest.starts_with('/') {
        return None;
    }
    let end = body.find('"')?;
    if end == 0 {
        return None;
    }
    let value_len = rest.len() - body.len() + end;
    Some(&rest[..value_len])
}

const MAX_BASE: usize = qcm_config::MAX_DEVICE_FILE_NAME_LENGTH - 4;

fn bad_name_char(c: char) -> bool {
    c.is_control() || matches!(c, '/' | '\\' | ':' | '*' | '?' | '"' | '<' | '>' | '|')
}

fn trim_dot_space(text: &str) -> &str {
    text.trim_end_matches(['.', ' '])
}

/// The name without a trailing `.csv` in any case, as
/// `Path.GetFileNameWithoutExtension` gives it for a profile.
#[must_use]
pub fn csv_stem(name: &str) -> &str {
    without_extension(name, ".csv")
}

/// `name` without a trailing ASCII `extension` (dot included), any case.
#[must_use]
pub fn without_extension<'a>(name: &'a str, extension: &str) -> &'a str {
    let cut = name.len().saturating_sub(extension.len());
    match name.get(cut..) {
        Some(tail) if tail.eq_ignore_ascii_case(extension) => &name[..cut],
        _ => name,
    }
}

/// The longest prefix of whole characters that fits in `units` UTF-16 code
/// units: the device slot is measured that way (`SafeFileName` uses C#
/// string length), so an emoji counts twice.
fn take_utf16(text: &str, units: usize) -> String {
    let mut used = 0;
    text.chars()
        .take_while(|c| {
            used += c.len_utf16();
            used <= units
        })
        .collect()
}

/// `SafeFileName.ForCsv`: a sheet name as a CSV name safe on macOS and
/// Windows and short enough for firmware 2373 (31 characters).
#[must_use]
pub fn safe_csv_name(name: &str) -> String {
    let trimmed = name.trim();
    if trimmed.is_empty() {
        return "Untitled.csv".to_owned();
    }
    let trimmed = csv_stem(trimmed);
    let cleaned: String = trimmed
        .chars()
        .map(|c| if bad_name_char(c) { '_' } else { c })
        .collect();
    let mut base = trim_dot_space(&cleaned).to_owned();
    if base.is_empty() {
        base = "Untitled".to_owned();
    }
    if qcm_config::is_reserved_windows_name(&base) {
        base = match base.find('.') {
            None => format!("{base}_file"),
            Some(dot) => format!("{}_file{}", &base[..dot], &base[dot..]),
        };
    }
    if base.encode_utf16().count() > MAX_BASE {
        let cut = take_utf16(&base, MAX_BASE);
        base = trim_dot_space(&cut).to_owned();
    }
    if base.is_empty() {
        base = "Untitled".to_owned();
    }
    format!("{base}.csv")
}

/// `SafeFileName.ForCsv(name, taken)`: never a name in `taken` (lower case).
#[must_use]
pub fn safe_csv_name_free(name: &str, taken: &BTreeSet<String>) -> String {
    let first = safe_csv_name(name);
    if !taken.contains(&first.to_lowercase()) {
        return first;
    }
    let stem = &first[..first.len() - 4];
    for n in 2u32.. {
        let suffix = format!(" ({n})");
        let room = MAX_BASE.saturating_sub(suffix.len());
        let cut = take_utf16(stem, room);
        let mut head = trim_dot_space(&cut).to_owned();
        if head.is_empty() {
            head = "Untitled".to_owned();
        }
        let candidate = format!("{head}{suffix}.csv");
        if !taken.contains(&candidate.to_lowercase()) {
            return candidate;
        }
    }
    first
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

#[cfg(test)]
mod tests {
    use super::{csv_stem, linux_documents_dir, safe_csv_name, safe_csv_name_free, xlsx_name};
    use std::collections::BTreeSet;
    use std::path::{Path, PathBuf};

    /// The Linux lookup with a fake environment and one readable file at
    /// the path the .NET rules pick (absolute XDG_CONFIG_HOME or ~/.config).
    fn docs(env: &[(&str, &str)], file: Option<&str>) -> PathBuf {
        let get = |key: &str| {
            env.iter()
                .find(|(k, _)| *k == key)
                .map(|(_, v)| (*v).to_owned())
        };
        let config = get("XDG_CONFIG_HOME")
            .filter(|v| v.starts_with('/'))
            .unwrap_or_else(|| "/home/u/.config".to_owned());
        let expected = Path::new(&config).join("user-dirs.dirs");
        linux_documents_dir(Path::new("/home/u"), get, |path| {
            (path == expected)
                .then(|| file.map(str::to_owned))
                .flatten()
        })
    }

    // UNRESOLVED-FL1: .NET 8 MyDocuments on Linux (ReadXdgDirectory).
    #[test]
    fn linux_documents_follow_the_dotnet_xdg_lookup() {
        assert_eq!(docs(&[], None), Path::new("/home/u/Documents"));
        assert_eq!(
            docs(&[("XDG_DOCUMENTS_DIR", "/data/docs")], None),
            Path::new("/data/docs")
        );
        // A relative env value is ignored, the file is read instead.
        assert_eq!(
            docs(
                &[("XDG_DOCUMENTS_DIR", "docs")],
                Some("XDG_DOCUMENTS_DIR=\"$HOME/Dokumente\"\n")
            ),
            Path::new("/home/u/Dokumente")
        );
        assert_eq!(
            docs(&[], Some("# c\n  XDG_DOCUMENTS_DIR = \"/abs/Docs\"\n")),
            Path::new("/abs/Docs")
        );
        // `$HOME` alone, a relative value and an empty value do not count.
        assert_eq!(
            docs(
                &[],
                Some(
                    "XDG_DOCUMENTS_DIR=\"$HOME\"\nXDG_DOCUMENTS_DIR=\"rel\"\nXDG_DOCUMENTS_DIR=\"\"\n"
                )
            ),
            Path::new("/home/u/Documents")
        );
        // The first good line wins; other keys are skipped.
        assert_eq!(
            docs(
                &[],
                Some(
                    "XDG_DESKTOP_DIR=\"$HOME/Desk\"\nXDG_DOCUMENTS_DIR=\"$HOME/A\"\nXDG_DOCUMENTS_DIR=\"$HOME/B\"\n"
                )
            ),
            Path::new("/home/u/A")
        );
        // An absolute XDG_CONFIG_HOME moves the file; a relative one does not.
        assert_eq!(
            docs(
                &[("XDG_CONFIG_HOME", "/cfg")],
                Some("XDG_DOCUMENTS_DIR=\"$HOME/C\"\n")
            ),
            Path::new("/home/u/C")
        );
        assert_eq!(
            docs(
                &[("XDG_CONFIG_HOME", "cfg")],
                Some("XDG_DOCUMENTS_DIR=\"$HOME/D\"\n")
            ),
            Path::new("/home/u/D")
        );
    }

    // SafeFileNameTests: what a sheet name becomes on disk.
    #[test]
    fn sheet_names_become_safe_short_csv_names() {
        assert_eq!(safe_csv_name("  "), "Untitled.csv");
        assert_eq!(safe_csv_name("Mine.CSV"), "Mine.csv");
        assert_eq!(safe_csv_name("a/b:c?"), "a_b_c_.csv");
        assert_eq!(safe_csv_name("CON"), "CON_file.csv");
        assert_eq!(safe_csv_name("con.old"), "con_file.old.csv");
        assert_eq!(safe_csv_name("trailing. "), "trailing.csv");
        let long = safe_csv_name("abcdefghijklmnopqrstuvwxyz0123456789");
        assert_eq!(long.chars().count(), 31);
    }

    // The device slot is 31 UTF-16 units, the unit the validator measures.
    // A tab named with emoji must still come out installable, whole
    // characters only.
    #[test]
    fn names_with_emoji_are_cut_to_the_device_slot_in_utf16_units() {
        let name = safe_csv_name(&"\u{1F3AE}".repeat(30));
        assert!(!qcm_config::is_too_long_for_device(&name), "{name}");
        assert!(name.ends_with(".csv"));
        let mut taken = BTreeSet::new();
        taken.insert(name.to_lowercase());
        let next = safe_csv_name_free(&"\u{1F3AE}".repeat(30), &taken);
        assert!(!qcm_config::is_too_long_for_device(&next), "{next}");
        assert_eq!(
            safe_csv_name("Caf\u{e9} \u{540d}\u{524d}"),
            "Caf\u{e9} \u{540d}\u{524d}.csv"
        );
    }

    #[test]
    fn xlsx_names_drop_csv_in_any_case() {
        assert_eq!(xlsx_name("Racing.Csv"), "Racing.xlsx");
        assert_eq!(xlsx_name("Racing.CSV"), "Racing.xlsx");
        assert_eq!(csv_stem("\u{540d}\u{524d}.cSv"), "\u{540d}\u{524d}");
        assert_eq!(csv_stem("plain"), "plain");
    }

    #[test]
    fn a_name_taken_in_the_batch_gets_a_number_inside_the_limit() {
        let mut taken = BTreeSet::new();
        taken.insert("game.csv".to_owned());
        assert_eq!(safe_csv_name_free("Game", &taken), "Game (2).csv");
        taken.insert("game (2).csv".to_owned());
        assert_eq!(safe_csv_name_free("game", &taken), "game (3).csv");
        let long = "abcdefghijklmnopqrstuvwxyz0123456789";
        let mut taken = BTreeSet::new();
        taken.insert(safe_csv_name(long).to_lowercase());
        let next = safe_csv_name_free(long, &taken);
        assert!(next.ends_with(" (2).csv"));
        assert!(next.chars().count() <= 31);
    }
}
