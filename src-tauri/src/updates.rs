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

/// What a check found. `message` is the English line; the window shows the
/// catalog string `message_key` with `message_args` instead, so the line is in
/// the person's language (`UpdateCheck.cs` uses the same `Update_*` strings).
/// `preview` means the second argument is a preview release's version.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateResultDto {
    pub message: String,
    pub message_key: String,
    pub message_args: Vec<String>,
    pub preview: bool,
    pub download_url: Option<String>,
    pub is_newer: bool,
}

fn said(key: &str, args: &[&str], message: String) -> UpdateResultDto {
    UpdateResultDto {
        message,
        message_key: key.to_owned(),
        message_args: args.iter().map(|arg| (*arg).to_owned()).collect(),
        preview: false,
        download_url: None,
        is_newer: false,
    }
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
            return said(
                "Update_CouldNotReachGitHubTo",
                &[current],
                format!("Could not reach GitHub to check for updates. You are on {current}."),
            );
        }
    };

    let status = response.status().as_u16();
    if status == 403 || status == 429 {
        return said(
            "Update_GitHubIsAskingUsTo",
            &[],
            "GitHub is asking us to wait before checking again. Try again in a few minutes."
                .to_owned(),
        );
    }
    if !response.status().is_success() {
        return said(
            "Update_CouldNotCheckForUpdates",
            &[&status.to_string(), current],
            format!(
                "Could not check for updates (GitHub answered {status}). You are on {current}."
            ),
        );
    }

    let body = match response.text() {
        Ok(body) => body,
        Err(_) => {
            return unreadable(current);
        }
    };

    let root: Value = match serde_json::from_str(&body) {
        Ok(root) => root,
        Err(_) => {
            return unreadable(current);
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
        return said(
            "Update_GitHubDidNotNameA",
            &[current],
            format!("GitHub did not name a release. You are on {current}."),
        );
    }

    if compare(&latest, current) <= 0 {
        return UpdateResultDto {
            download_url: page,
            ..said(
                "Update_YouAreOnCurrentWhich",
                &[current],
                format!("You are on {current}, which is the latest."),
            )
        };
    }

    let label = if prerelease {
        format!("{latest} (a preview release)")
    } else {
        latest.clone()
    };
    UpdateResultDto {
        preview: prerelease,
        download_url: page,
        is_newer: true,
        ..said(
            "Update_YouAreOnCurrentLabel",
            &[current, &latest],
            format!("You are on {current}. {label} is out."),
        )
    }
}

fn unreadable(current: &str) -> UpdateResultDto {
    said(
        "Update_GitHubSentSomethingWeCould",
        &[current],
        format!("GitHub sent something we could not read. You are on {current}."),
    )
}

/// Soft check against GitHub latest. Network failures become a message, never a panic.
pub fn latest_async(current: &str) -> UpdateResultDto {
    let Ok(http) = Client::builder().timeout(Duration::from_secs(15)).build() else {
        return said(
            "Update_CouldNotReachGitHubTo",
            &[current],
            format!("Could not reach GitHub to check for updates. You are on {current}."),
        );
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

// Up to 15 s on a slow network, so off the main thread.
#[tauri::command]
pub async fn check_for_update(request: Value) -> Result<UpdateResultDto, Failure> {
    let request: CheckForUpdateRequest = redact(parse(request, "check_for_update request"))?;
    let current = request
        .current_version
        .filter(|value| !value.trim().is_empty())
        .unwrap_or_else(|| env!("CARGO_PKG_VERSION").to_owned());
    tauri::async_runtime::spawn_blocking(move || latest_async(&current))
        .await
        .map_err(|error| {
            Box::new(QcmErrorDto::from(&QcmError::Internal(InternalError {
                what: "update worker",
                detail: OsDetail::new(error.to_string()),
            })))
        })
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

/// The privacy policy the first-run notice and Settings link to. Fixed here
/// so no URL crosses the boundary (`MainWindow.PrivacyPolicyUrl`).
pub const PRIVACY_POLICY_URL: &str =
    "https://bassamkamal.dev/Quadstick-Config-Manager/privacy.html";

#[tauri::command]
pub fn open_privacy_policy() -> Result<(), Failure> {
    open::that(PRIVACY_POLICY_URL).map_err(|error| {
        Box::new(QcmErrorDto::from(&QcmError::Internal(InternalError {
            what: "open privacy policy",
            detail: OsDetail::new(error.to_string()),
        })))
    })
}

#[cfg(test)]
mod tests {
    use super::{compare, normalize_tag};

    // Every key the check answers with is a string the window can show.
    #[test]
    fn every_update_message_key_is_in_the_catalog() {
        let catalog: serde_json::Value =
            serde_json::from_str(include_str!("../../src/i18n/catalogs/en.json"))
                .expect("en catalog");
        let source = include_str!("updates.rs");
        let keys: Vec<&str> = source
            .split('"')
            .filter(|part| part.len() > 7 && part.starts_with("Update_") && !part.contains(' '))
            .collect();
        assert!(keys.len() >= 7, "found {keys:?}");
        for key in keys {
            assert!(catalog.get(key).is_some(), "{key} is not in en.json");
        }
    }

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
