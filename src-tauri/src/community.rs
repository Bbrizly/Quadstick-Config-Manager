//! Native Community catalog transport/cache.
//!
//! The legacy program evaluated the endpoint response. This service accepts
//! JSON only, bounds both transfer and render shape, caches only a fully parsed
//! reply and keeps workbook bytes/native paths out of the WebView.

use qcm_config::{XLSX_MAX_WORKBOOK_BYTES, is_invalid_filename_char};
use qcm_core::error::{InternalError, OsDetail, QcmError};
use reqwest::blocking::{Client, Response};
use reqwest::redirect::Policy;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::fs;
use std::io::Read;
use std::path::{Path, PathBuf};
use std::time::Duration;

pub const CATALOG_URL: &str = "https://bvhbml89uymwxubx.quadstick.com";
pub const MAX_REPLY_BYTES: usize = 4 * 1024 * 1024;
pub const MAX_ROWS: usize = 5_000;
pub const MAX_FIELD_CHARS: usize = 2_000;

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CommunityProfileDto {
    pub name: String,
    pub sheet_id: String,
    pub csv_name: String,
    pub connection: String,
    pub notes: String,
    pub pointer: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CommunityCatalogDto {
    pub profiles: Vec<CommunityProfileDto>,
    pub from_cache: bool,
    pub skipped_rows: usize,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CommunityLoadRequest {
    #[serde(default)]
    pub refresh: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CommunityImportRequest {
    pub sheet_id: String,
    pub csv_name: String,
}

#[derive(Debug)]
pub struct CommunityService {
    http: Client,
    cache: PathBuf,
}

impl CommunityService {
    pub fn native() -> Result<Self, QcmError> {
        let http = Client::builder()
            .timeout(Duration::from_secs(15))
            .redirect(Policy::limited(5))
            .user_agent("QuadStickConfigManager")
            .build()
            .map_err(|error| internal("build community client", error.to_string()))?;
        Ok(Self {
            http,
            cache: app_data_dir().join("community-catalog.json"),
        })
    }

    #[cfg(test)]
    pub fn with_client(http: Client, cache: PathBuf) -> Self {
        Self { http, cache }
    }

    pub fn load(&self, refresh: bool) -> Result<CommunityCatalogDto, QcmError> {
        if !refresh && let Some((profiles, skipped)) = read_cache(&self.cache) {
            return Ok(CommunityCatalogDto {
                profiles,
                from_cache: true,
                skipped_rows: skipped,
            });
        }

        let fetched = self
            .http
            .get(CATALOG_URL)
            .send()
            .and_then(Response::error_for_status)
            .map_err(|error| error.to_string())
            .and_then(read_bounded)
            .and_then(|bytes| String::from_utf8(bytes).map_err(|error| error.to_string()))
            .and_then(|body| {
                let body = body.trim_start_matches('\u{feff}').to_owned();
                parse_catalog(&body).map(|parsed| (body, parsed))
            });

        match fetched {
            Ok((body, (profiles, skipped))) => {
                save_cache(&self.cache, body.as_bytes());
                Ok(CommunityCatalogDto {
                    profiles,
                    from_cache: false,
                    skipped_rows: skipped,
                })
            }
            Err(detail) => read_cache(&self.cache).map_or_else(
                || Err(internal("read community catalog", detail)),
                |(profiles, skipped)| {
                    Ok(CommunityCatalogDto {
                        profiles,
                        from_cache: true,
                        skipped_rows: skipped,
                    })
                },
            ),
        }
    }

    pub fn download_workbook(&self, request: &CommunityImportRequest) -> Result<Vec<u8>, QcmError> {
        if !valid_sheet_id(&request.sheet_id) || !valid_csv_name(&request.csv_name) {
            return Err(internal("community import", "invalid catalog profile"));
        }
        let url = format!(
            "https://docs.google.com/spreadsheets/d/{}/export?format=xlsx",
            request.sheet_id
        );
        let response = self
            .http
            .get(url)
            .send()
            .and_then(Response::error_for_status)
            .map_err(|error| internal("download community workbook", error.to_string()))?;
        let mut bounded = response.take(XLSX_MAX_WORKBOOK_BYTES as u64 + 1);
        let mut bytes = Vec::new();
        bounded
            .read_to_end(&mut bytes)
            .map_err(|error| internal("read community workbook", error.to_string()))?;
        if bytes.len() > XLSX_MAX_WORKBOOK_BYTES {
            return Err(qcm_core::error::ConfigError::TooLarge {
                limit_bytes: XLSX_MAX_WORKBOOK_BYTES as u64,
            }
            .into());
        }
        Ok(bytes)
    }
}

fn read_bounded(response: Response) -> Result<Vec<u8>, String> {
    let mut bounded = response.take(MAX_REPLY_BYTES as u64 + 1);
    let mut bytes = Vec::new();
    bounded.read_to_end(&mut bytes).map_err(|error| error.to_string())?;
    if bytes.len() > MAX_REPLY_BYTES {
        return Err("community reply exceeded byte cap".to_owned());
    }
    Ok(bytes)
}

fn read_cache(path: &Path) -> Option<(Vec<CommunityProfileDto>, usize)> {
    let bytes = fs::read(path).ok()?;
    if bytes.len() > MAX_REPLY_BYTES {
        return None;
    }
    let body = String::from_utf8(bytes).ok()?;
    parse_catalog(body.trim_start_matches('\u{feff}')).ok()
}

fn save_cache(path: &Path, bytes: &[u8]) {
    let Some(parent) = path.parent() else { return };
    if fs::create_dir_all(parent).is_err() {
        return;
    }
    let temp = path.with_extension("json.tmp");
    if fs::write(&temp, bytes).is_ok() {
        if fs::rename(&temp, path).is_err() {
            let _ = fs::remove_file(&temp);
        }
    }
}

fn app_data_dir() -> PathBuf {
    #[cfg(target_os = "windows")]
    let base = std::env::var_os("APPDATA").map(PathBuf::from);
    #[cfg(target_os = "macos")]
    let base = std::env::var_os("HOME").map(|home| PathBuf::from(home).join("Library/Application Support"));
    #[cfg(not(any(target_os = "windows", target_os = "macos")))]
    let base = std::env::var_os("XDG_CONFIG_HOME")
        .map(PathBuf::from)
        .or_else(|| std::env::var_os("HOME").map(|home| PathBuf::from(home).join(".config")));
    base.unwrap_or_else(std::env::temp_dir).join("QuadStickConfigManager")
}

fn parse_catalog(body: &str) -> Result<(Vec<CommunityProfileDto>, usize), String> {
    let root: Value = serde_json::from_str(body).map_err(|_| "community catalog was not JSON".to_owned())?;
    let top = root.as_array().ok_or_else(|| "community catalog was not a list".to_owned())?;
    let games = top.first().and_then(Value::as_array).ok_or_else(|| "community catalog had no game list".to_owned())?;
    let mut profiles = Vec::new();
    let mut skipped = 0usize;
    for row in games {
        if profiles.len() >= MAX_ROWS {
            skipped += 1;
            continue;
        }
        match read_row(row) {
            Some(profile) => profiles.push(profile),
            None => skipped += 1,
        }
    }
    profiles.sort_by_key(|profile| profile.name.to_lowercase());
    Ok((profiles, skipped))
}

fn read_row(value: &Value) -> Option<CommunityProfileDto> {
    let row = value.as_array()?;
    if row.len() < 3 {
        return None;
    }
    let text = |index: usize| row.get(index).and_then(Value::as_str).unwrap_or("").to_owned();
    let profile = CommunityProfileDto {
        name: text(0),
        sheet_id: text(1),
        csv_name: text(2).trim().to_owned(),
        connection: text(4),
        notes: text(5),
        pointer: text(6),
    };
    if profile.name.trim().is_empty()
        || !valid_sheet_id(&profile.sheet_id)
        || !valid_csv_name(&profile.csv_name)
        || [
            profile.name.as_str(),
            profile.connection.as_str(),
            profile.notes.as_str(),
            profile.pointer.as_str(),
        ]
        .iter()
        .any(|field| field.chars().count() > MAX_FIELD_CHARS)
    {
        return None;
    }
    Some(profile)
}

fn valid_sheet_id(id: &str) -> bool {
    (20..=200).contains(&id.len())
        && id.bytes().all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_'))
}

fn valid_csv_name(name: &str) -> bool {
    name.len() > 4
        && name.len() <= 255
        && name == name.trim()
        && name.to_ascii_lowercase().ends_with(".csv")
        && !name.chars().any(is_invalid_filename_char)
}

fn internal(what: &'static str, detail: impl Into<String>) -> QcmError {
    QcmError::Internal(InternalError {
        what,
        detail: OsDetail::new(detail.into()),
    })
}

#[cfg(test)]
mod tests {
    use super::{MAX_FIELD_CHARS, MAX_ROWS, parse_catalog, valid_sheet_id};

    #[test]
    fn catalog_is_json_only_and_drops_bad_rows() {
        let good_id = "1AbCdEfGhIjKlMnOpQrStUvWxYz";
        let body = format!(
            r#"[[["Game","{good_id}","game.csv","ignored","USB","notes","pointer"],["Bad","javascript:alert(1)","bad.csv"]],[]]"#
        );
        let (rows, skipped) = parse_catalog(&body).expect("valid top-level catalog");
        assert_eq!(rows.len(), 1);
        assert_eq!(skipped, 1);
        assert_eq!(rows[0].sheet_id, good_id);
        assert!(parse_catalog("eval('boom')").is_err());
    }

    #[test]
    fn hostile_render_shapes_are_bounded() {
        let id = "1AbCdEfGhIjKlMnOpQrStUvWxYz";
        let long = "x".repeat(MAX_FIELD_CHARS + 1);
        let body = format!(r#"[[["{long}","{id}","game.csv"]],[]]"#);
        let (rows, skipped) = parse_catalog(&body).expect("valid JSON");
        assert!(rows.is_empty());
        assert_eq!(skipped, 1);
        assert!(MAX_ROWS >= 5_000);
        assert!(valid_sheet_id(id));
    }
}
