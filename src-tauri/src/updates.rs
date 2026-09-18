//! Soft update check: ask GitHub what the newest release is and open its page.
//!
//! Nothing is downloaded and nothing is replaced. Signed install_update stays
//! parked until code signing exists.

use crate::ipc::parse;
use qcm_core::error::{InternalError, OsDetail, QcmError, QcmErrorDto, RequestError};
use reqwest::blocking::Client;
use reqwest::header::{ACCEPT, USER_AGENT};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::time::Duration;

const LATEST_URL: &str =
    "https://api.github.com/repos/Bbrizly/Quadstick-Config-Manager/releases/latest";
const USER_AGENT_VALUE: &str = "QuadStickConfigManager";
const ACCEPT_VALUE: &str = "application/vnd.github+json";

type Failure = Box<QcmErrorDto>;

fn redact<T>(result: Result<T, QcmError>) -> Result<T, Failure> {
    result.map_err(|error| Box::new(QcmErrorDto::from(&error)))
}

/// What a check found, already in words the settings line can show.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateResultDto {
    pub message: String,
    pub download_url: Option<String>,
    pub is_newer: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct CheckForUpdateRequest {
    #[serde(default)]
    current_version: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct OpenExternalUrlRequest {
    url: String,
}

/// Strip a leading `v` the way Avalonia `UpdateCheck.Normalize` did.
#[must_use]
pub fn normalize_tag(tag: &str) -> String {
    let trimmed = tag.trim();
    trimmed
        .strip_prefix('v')
        .or_else(|| trimmed.strip_prefix('V'))
        .unwrap_or(trimmed)
        .to_owned()
}

/// Newest-first ordering of two dotted versions. Positive when `a` is newer.
/// A part that is not a number stops the comparison there and counts as equal.
#[must_use]
pub fn compare(a: &str, b: &str) -> i32 {
    let left: Vec<&str> = a.split('.').collect();
    let right: Vec<&str> = b.split('.').collect();
    let len = left.len().max(right.len());
    for i in 0..len {
        let x_raw = left.get(i).copied().unwrap_or("0");
        let y_raw = right.get(i).copied().unwrap_or("0");
        let Ok(x) = x_raw.parse::<i32>() else {
            return 0;
        };
        let Ok(y) = y_raw.parse::<i32>() else {
            return 0;
        };
        if x != y {
            return if x > y { 1 } else { -1 };
        }
    }
    0
}

fn latest_with_client(http: &Client, current: &str) -> UpdateResultDto {
    let response = match http
        .get(LATEST_URL)
        .header(USER_AGENT, USER_AGENT_VALUE)
        .header(ACCEPT, ACCEPT_VALUE)
        .send()
    {
        Ok(response) => response,
        Err(_) => {
            return UpdateResultDto {
                message: format!(
                    "Could not reach GitHub to check for updates. You are on {current}."
                ),
                download_url: None,
                is_newer: false,
            };
        }
    };

    let status = response.status().as_u16();
    if status == 403 || status == 429 {
        return UpdateResultDto {
            message:
                "GitHub is asking us to wait before checking again. Try again in a few minutes."
                    .to_owned(),
            download_url: None,
            is_newer: false,
        };
    }
    if !response.status().is_success() {
        return UpdateResultDto {
            message: format!(
                "Could not check for updates (GitHub answered {status}). You are on {current}."
            ),
            download_url: None,
            is_newer: false,
        };
    }

    let body = match response.text() {
        Ok(body) => body,
        Err(_) => {
            return UpdateResultDto {
                message: format!("GitHub sent something we could not read. You are on {current}."),
                download_url: None,
                is_newer: false,
            };
        }
    };

    let root: Value = match serde_json::from_str(&body) {
        Ok(root) => root,
        Err(_) => {
            return UpdateResultDto {
                message: format!("GitHub sent something we could not read. You are on {current}."),
                download_url: None,
                is_newer: false,
            };
        }
    };

    let tag = root.get("tag_name").and_then(Value::as_str).unwrap_or("");
    let page = root
        .get("html_url")
        .and_then(Value::as_str)
        .map(str::to_owned);
    let prerelease = root
        .get("prerelease")
        .and_then(Value::as_bool)
        .unwrap_or(false);
    let latest = normalize_tag(tag);

    if latest.is_empty() {
        return UpdateResultDto {
            message: format!("GitHub did not name a release. You are on {current}."),
            download_url: None,
            is_newer: false,
        };
    }

    if compare(&latest, current) <= 0 {
        return UpdateResultDto {
            message: format!("You are on {current}, which is the latest."),
            download_url: page,
            is_newer: false,
        };
    }

    let label = if prerelease {
        format!("{latest} (a preview release)")
    } else {
        latest
    };
    UpdateResultDto {
        message: format!("You are on {current}. {label} is out."),
        download_url: page,
        is_newer: true,
    }
}

/// Soft check against GitHub latest. Network failures become a message, never a panic.
pub fn latest_async(current: &str) -> UpdateResultDto {
    let Ok(http) = Client::builder().timeout(Duration::from_secs(15)).build() else {
        return UpdateResultDto {
            message: format!("Could not reach GitHub to check for updates. You are on {current}."),
            download_url: None,
            is_newer: false,
        };
    };
    latest_with_client(&http, current)
}

fn allowed_github_release_url(raw: &str) -> Result<&str, QcmError> {
    let url = reqwest::Url::parse(raw).map_err(|_| RequestError::OutOfRange {
        what: "external url",
    })?;
    if url.scheme() != "https" {
        return Err(RequestError::OutOfRange {
            what: "external url",
        }
        .into());
    }
    let host = url.host_str().unwrap_or("");
    if host != "github.com" && host != "www.github.com" {
        return Err(RequestError::OutOfRange {
            what: "external url",
        }
        .into());
    }
    let path = url.path();
    if !path.contains("/releases") {
        return Err(RequestError::OutOfRange {
            what: "external url",
        }
        .into());
    }
    Ok(raw)
}

#[tauri::command]
pub fn check_for_update(request: Value) -> Result<UpdateResultDto, Failure> {
    let request: CheckForUpdateRequest = redact(parse(request, "check_for_update request"))?;
    let current = request
        .current_version
        .filter(|value| !value.trim().is_empty())
        .unwrap_or_else(|| env!("CARGO_PKG_VERSION").to_owned());
    Ok(latest_async(&current))
}

#[tauri::command]
pub fn open_external_url(request: Value) -> Result<(), Failure> {
    let request: OpenExternalUrlRequest = redact(parse(request, "open_external_url request"))?;
    let url = redact(allowed_github_release_url(&request.url))?;
    open::that(url).map_err(|error| {
        Box::new(QcmErrorDto::from(&QcmError::Internal(InternalError {
            what: "open external url",
            detail: OsDetail::new(error.to_string()),
        })))
    })
}

#[cfg(test)]
mod tests {
    use super::{compare, normalize_tag};

    #[test]
    fn normalize_strips_a_leading_v() {
        assert_eq!(normalize_tag("v1.6.1"), "1.6.1");
        assert_eq!(normalize_tag("V2.0.0"), "2.0.0");
        assert_eq!(normalize_tag(" 1.2.3 "), "1.2.3");
        assert_eq!(normalize_tag("1.2.3"), "1.2.3");
    }

    #[test]
    fn compare_matches_avalonia_dotted_semantics() {
        let cases: &[(&str, &str, i32)] = &[
            ("1.6.1", "1.6.0", 1),
            ("1.6.0", "1.6.1", -1),
            ("1.6.0", "1.6.0", 0),
            ("2.0", "1.9.9", 1),
            ("1.6", "1.6.0", 0),
            ("1.6.0", "1.6", 0),
            ("1.6.a", "1.6.0", 0),
            ("1.6.0", "1.6.a", 0),
            ("10.0.0", "9.9.9", 1),
            ("1.0.0", "1.0.0-beta", 0),
        ];
        for &(a, b, expected) in cases {
            assert_eq!(compare(a, b), expected, "compare({a}, {b})");
        }
    }
}
