//! Adaptive Profiles registry and `qcm://profile/<id>` links.
//!
//! Ported from `ProfileRegistryClient.cs`, `RegistryDeepLink.cs` and
//! `MainWindow.Registry.cs`. Network data is untrusted: size, schema, ids,
//! duplicates, source URLs and snapshot hashes are validated before anything
//! is used. The last valid catalog and each verified CSV snapshot are cached
//! for an outage. A link only ever opens a profile in the editor; it can never
//! write to a QuadStick.
//!
//! Registration: Store packages declare the `qcm` scheme in
//! `Package.appxmanifest`, and an unpackaged Windows launch passes the link in
//! argv, which [`launch_profile_id`] reads. The per-user registry write in
//! `ProtocolRegistration.cs` and macOS `CFBundleURLTypes`/Apple-event delivery
//! need `tauri-plugin-deep-link`, which is not a dependency yet.

use qcm_config::ProfileFile;
use qcm_core::error::{InternalError, OsDetail, QcmError, RequestError};
use reqwest::blocking::{Client, Response};
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::collections::HashSet;
use std::fs;
use std::io::Read;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, OnceLock};
use std::time::Duration;

pub const CATALOG_URL: &str =
    "https://raw.githubusercontent.com/Bbrizly/Adaptive-Profiles/main/generated/index.json";
const RAW_ROOT: &str = "https://raw.githubusercontent.com/Bbrizly/Adaptive-Profiles/main";
pub const MAX_CATALOG_BYTES: usize = 8 * 1024 * 1024;
pub const MAX_CSV_BYTES: usize = 512 * 1024;
pub const MAX_PROFILES: usize = 50_000;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RegistryProfile {
    pub id: String,
    pub game_id: String,
    pub platform: String,
    pub device_id: String,
    pub title: String,
    pub source_type: String,
    pub source_url: Option<String>,
    pub snapshot_sha256: String,
}

/// `^[a-z0-9]+(?:-+[a-z0-9]+)*$`
#[must_use]
pub fn is_profile_id(text: &str) -> bool {
    !text.is_empty()
        && text
            .bytes()
            .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'-')
        && !text.starts_with('-')
        && !text.ends_with('-')
}

/// `^[a-z0-9]+(?:-[a-z0-9]+)*$`
#[must_use]
pub fn is_slug(text: &str) -> bool {
    is_profile_id(text) && !text.contains("--")
}

fn is_sha256(text: &str) -> bool {
    text.len() == 64
        && text
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}

/// The profile id in a canonical `qcm://profile/<id>` link, or `None`.
///
/// Matched on the original spelling, case-insensitive for scheme and host
/// only, so `qcm://profile/../../bad` never normalizes into something else,
/// and anything with a query, fragment, user info or port is refused.
#[must_use]
pub fn profile_id_from_link(link: &str) -> Option<String> {
    let prefix = "qcm://profile/";
    if link.len() <= prefix.len() || !link.is_char_boundary(prefix.len()) {
        return None;
    }
    let (head, id) = link.split_at(prefix.len());
    if !head.eq_ignore_ascii_case(prefix) {
        return None;
    }
    // The C# regex is case-insensitive over the whole link, and System.Uri
    // keeps the path's case, so an upper-case id passes the first test and
    // fails the second. Same here: the id itself must already be lower case.
    is_profile_id(id).then(|| id.to_owned())
}

/// The first argument that is a profile link.
#[must_use]
pub fn profile_id_from_args<I: IntoIterator<Item = S>, S: AsRef<str>>(args: I) -> Option<String> {
    args.into_iter()
        .find_map(|arg| profile_id_from_link(arg.as_ref()))
}

static LAUNCH_LINK: OnceLock<Mutex<Option<String>>> = OnceLock::new();

/// Remember the link this process was launched with. Called once at startup.
pub fn remember_launch_args() {
    let id = profile_id_from_args(std::env::args().skip(1));
    let _ = LAUNCH_LINK.set(Mutex::new(id));
}

/// The launch link's profile id, handed out once.
#[must_use]
pub fn launch_profile_id() -> Option<String> {
    LAUNCH_LINK
        .get()
        .and_then(|slot| slot.lock().ok().and_then(|mut id| id.take()))
}

fn invalid(what: &'static str) -> String {
    what.to_owned()
}

fn text_field(
    row: &serde_json::Map<String, Value>,
    name: &str,
    max: usize,
) -> Result<String, String> {
    let text = row
        .get(name)
        .and_then(Value::as_str)
        .ok_or_else(|| format!("Profile is missing {name}."))?;
    let length = text.encode_utf16().count();
    if length == 0 || length > max {
        return Err(format!("Profile {name} is invalid."));
    }
    Ok(text.to_owned())
}

fn is_allowed_google_sheet(value: &str) -> bool {
    let Some(rest) = value.strip_prefix("https://") else {
        return false;
    };
    let (host, path) = rest.split_once('/').unwrap_or((rest, ""));
    if !host.eq_ignore_ascii_case("docs.google.com") {
        return false;
    }
    let path = path.split(['?', '#']).next().unwrap_or("");
    let parts: Vec<&str> = path.split('/').filter(|p| !p.is_empty()).collect();
    parts.len() >= 3
        && parts[0] == "spreadsheets"
        && parts[1] == "d"
        && (20..=200).contains(&parts[2].len())
        && parts[2]
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b == b'_' || b == b'-')
}

/// Validate the registry index exactly as `ProfileRegistryClient.Parse` does.
pub fn parse(json: &str) -> Result<Vec<RegistryProfile>, String> {
    let root: Value =
        serde_json::from_str(json).map_err(|_| invalid("Registry JSON is invalid."))?;
    let root = root
        .as_object()
        .ok_or_else(|| invalid("Registry has an unsupported shape."))?;
    if root.get("schemaVersion").and_then(Value::as_i64) != Some(1) {
        return Err(invalid("Registry has an unsupported shape."));
    }
    let rows = root
        .get("profiles")
        .and_then(Value::as_array)
        .ok_or_else(|| invalid("Registry has an unsupported shape."))?;
    if rows.len() > MAX_PROFILES {
        return Err(invalid("Registry has too many profiles."));
    }
    let mut seen = HashSet::new();
    let mut result = Vec::with_capacity(rows.len());
    for row in rows {
        let row = row
            .as_object()
            .ok_or_else(|| invalid("Registry profile is not an object."))?;
        let id = text_field(row, "id", 220)?;
        let game_id = text_field(row, "gameId", 220)?;
        let platform = text_field(row, "platform", 20)?;
        let device_id = text_field(row, "deviceId", 220)?;
        let title = text_field(row, "title", 100)?;
        if !is_profile_id(&id) || !is_slug(&game_id) || !is_slug(&device_id) {
            return Err(invalid("Registry profile contains an invalid id."));
        }
        if !matches!(platform.as_str(), "pc" | "xbox" | "playstation" | "switch") {
            return Err(format!("Profile {id} has an invalid platform."));
        }
        if !seen.insert(id.clone()) {
            return Err(format!("Duplicate registry profile id: {id}"));
        }
        let source = row
            .get("source")
            .and_then(Value::as_object)
            .ok_or_else(|| format!("Profile {id} has no supported source."))?;
        let source_type = source
            .get("type")
            .and_then(Value::as_str)
            .ok_or_else(|| format!("Profile {id} has no supported source."))?
            .to_owned();
        if !matches!(source_type.as_str(), "google-sheet" | "csv") {
            return Err(format!("Profile {id} has no supported source."));
        }
        let mut source_url = None;
        if source_type == "google-sheet" {
            let url = source
                .get("url")
                .and_then(Value::as_str)
                .ok_or_else(|| format!("Profile {id} has no Google Sheet URL."))?;
            if !is_allowed_google_sheet(url) {
                return Err(format!("Profile {id} has an invalid Google Sheet URL."));
            }
            source_url = Some(url.to_owned());
        }
        let snapshot = row
            .get("snapshot")
            .and_then(Value::as_object)
            .ok_or_else(|| format!("Profile {id} has an invalid snapshot."))?;
        if snapshot.get("file").and_then(Value::as_str) != Some("profile.csv") {
            return Err(format!("Profile {id} has an invalid snapshot."));
        }
        let sha = snapshot
            .get("sha256")
            .and_then(Value::as_str)
            .ok_or_else(|| format!("Profile {id} has an invalid snapshot."))?;
        if !is_sha256(sha) {
            return Err(format!("Profile {id} has an invalid snapshot hash."));
        }
        result.push(RegistryProfile {
            id,
            game_id,
            platform,
            device_id,
            title,
            source_type,
            source_url,
            snapshot_sha256: sha.to_owned(),
        });
    }
    Ok(result)
}

#[must_use]
pub fn csv_url(profile: &RegistryProfile) -> String {
    format!(
        "{RAW_ROOT}/data/profiles/{}/{}/{}/profile.csv",
        profile.game_id, profile.device_id, profile.id
    )
}

/// The bytes are the reviewed snapshot, or an error.
pub fn verify_snapshot(bytes: &[u8], expected: &str) -> Result<(), String> {
    let actual: String = Sha256::digest(bytes)
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect();
    // Constant time over equal-length ASCII, like FixedTimeEquals.
    let same = actual.len() == expected.len()
        && actual
            .bytes()
            .zip(expected.bytes())
            .fold(0_u8, |acc, (a, b)| acc | (a ^ b))
            == 0;
    if same {
        Ok(())
    } else {
        Err(invalid(
            "Profile CSV does not match the reviewed registry snapshot.",
        ))
    }
}

/// Where the network comes from. Tests hand in canned bytes.
pub trait Fetch: Send + Sync {
    fn get(&self, url: &str, max_bytes: usize) -> Result<Vec<u8>, String>;
}

pub struct HttpFetch {
    http: Client,
}

impl HttpFetch {
    pub fn native() -> Result<Self, QcmError> {
        let http = Client::builder()
            .timeout(Duration::from_secs(15))
            .user_agent("QuadStick-Config-Manager/1.0")
            .build()
            .map_err(|error| internal("build registry client", error.to_string()))?;
        Ok(Self { http })
    }
}

impl Fetch for HttpFetch {
    fn get(&self, url: &str, max_bytes: usize) -> Result<Vec<u8>, String> {
        let response = self
            .http
            .get(url)
            .send()
            .and_then(Response::error_for_status)
            .map_err(|error| error.to_string())?;
        if response
            .content_length()
            .is_some_and(|length| length > max_bytes as u64)
        {
            return Err(invalid("Registry response is too large."));
        }
        let mut bytes = Vec::new();
        response
            .take(max_bytes as u64 + 1)
            .read_to_end(&mut bytes)
            .map_err(|error| error.to_string())?;
        if bytes.len() > max_bytes {
            return Err(invalid("Registry response is too large."));
        }
        Ok(bytes)
    }
}

pub struct RegistryClient<F> {
    fetch: F,
    cache_path: PathBuf,
    snapshot_dir: PathBuf,
}

impl RegistryClient<HttpFetch> {
    pub fn native() -> Result<Self, QcmError> {
        let base = crate::community::app_data_dir();
        Ok(Self::new(
            HttpFetch::native()?,
            base.join("adaptive-profiles-index.json"),
            base.join("adaptive-profiles"),
        ))
    }
}

impl<F: Fetch> RegistryClient<F> {
    pub const fn new(fetch: F, cache_path: PathBuf, snapshot_dir: PathBuf) -> Self {
        Self {
            fetch,
            cache_path,
            snapshot_dir,
        }
    }

    /// The catalog, fresh when asked and reachable, else the last good one.
    pub fn load(&self, refresh: bool) -> Result<(Vec<RegistryProfile>, bool), String> {
        if !refresh && let Some(cached) = self.read_cache() {
            return Ok((cached, true));
        }
        let fresh = self
            .fetch
            .get(CATALOG_URL, MAX_CATALOG_BYTES)
            .and_then(|bytes| {
                String::from_utf8(bytes).map_err(|_| invalid("REGISTRY_UTF8_INVALID"))
            })
            .and_then(|json| parse(&json).map(|profiles| (json, profiles)));
        match fresh {
            Ok((json, profiles)) => {
                write_cache(&self.cache_path, json.as_bytes());
                Ok((profiles, false))
            }
            Err(error) => self.read_cache().map(|c| (c, true)).ok_or(error),
        }
    }

    /// The reviewed CSV snapshot, verified, falling back to a verified cache.
    pub fn download_csv(&self, profile: &RegistryProfile) -> Result<String, String> {
        let cache = self.snapshot_dir.join(format!("{}.csv", profile.id));
        let fresh = self
            .fetch
            .get(&csv_url(profile), MAX_CSV_BYTES)
            .and_then(|bytes| {
                verify_snapshot(&bytes, &profile.snapshot_sha256)?;
                String::from_utf8(bytes).map_err(|_| invalid("PROFILE_CSV_UTF8_INVALID"))
            });
        match fresh {
            Ok(csv) => {
                write_cache(&cache, csv.as_bytes());
                Ok(csv)
            }
            Err(error) => read_snapshot_cache(&cache, &profile.snapshot_sha256).ok_or(error),
        }
    }

    fn read_cache(&self) -> Option<Vec<RegistryProfile>> {
        let bytes = fs::read(&self.cache_path).ok()?;
        if bytes.is_empty() || bytes.len() > MAX_CATALOG_BYTES {
            return None;
        }
        parse(&String::from_utf8(bytes).ok()?).ok()
    }

    /// Resolve an id, fetch its snapshot and check it is a profile at all.
    pub fn resolve(&self, profile_id: &str) -> Result<(RegistryProfile, String), String> {
        if !is_profile_id(profile_id) {
            return Err(invalid("Registry profile id is invalid."));
        }
        let (profiles, _) = self.load(true)?;
        let profile = profiles
            .into_iter()
            .find(|p| p.id == profile_id)
            .ok_or_else(|| invalid("Registry profile not found."))?;
        let csv = self.download_csv(&profile)?;
        if ProfileFile::load(&csv).document.sheets.is_empty() {
            return Err(invalid(
                "Profile snapshot contains no configuration sheets.",
            ));
        }
        Ok((profile, csv))
    }
}

fn read_snapshot_cache(path: &Path, expected: &str) -> Option<String> {
    let bytes = fs::read(path).ok()?;
    if bytes.is_empty() || bytes.len() > MAX_CSV_BYTES {
        return None;
    }
    verify_snapshot(&bytes, expected).ok()?;
    String::from_utf8(bytes).ok()
}

/// A cache failure must never make a registry import fail.
fn write_cache(path: &Path, bytes: &[u8]) {
    let Some(parent) = path.parent() else { return };
    if fs::create_dir_all(parent).is_err() {
        return;
    }
    let temp = path.with_extension("tmp");
    if fs::write(&temp, bytes).is_ok() && fs::rename(&temp, path).is_err() {
        let _ = fs::remove_file(&temp);
    }
}

fn internal(what: &'static str, detail: impl Into<String>) -> QcmError {
    QcmError::Internal(InternalError {
        what,
        detail: OsDetail::new(detail.into()),
    })
}

// ---- commands ----

use crate::shell::ShellState;
use qcm_core::error::QcmErrorDto;
use qcm_core::profiles::EditorSnapshot;
use serde::Deserialize;
use tauri::State;

type Failure = Box<QcmErrorDto>;

fn fail(error: &QcmError) -> Failure {
    Box::new(QcmErrorDto::from(error))
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct OpenRegistryRequest {
    profile_id: String,
}

/// The profile id this process was launched with (`qcm://profile/<id>` in
/// argv), handed out once so a reload does not reopen it.
#[tauri::command]
pub fn take_launch_profile_link() -> Option<String> {
    launch_profile_id()
}

/// Open a registry profile in the editor, unsaved, so Save asks where.
#[tauri::command]
pub async fn open_registry_profile(
    profiles: State<'_, ShellState>,
    request: Value,
) -> Result<EditorSnapshot, Failure> {
    let request: OpenRegistryRequest =
        crate::ipc::parse(request, "open_registry_profile request").map_err(|e| fail(&e))?;
    if !is_profile_id(&request.profile_id) {
        return Err(fail(&QcmError::Request(RequestError::OutOfRange {
            what: "registry profile id",
        })));
    }
    let id = request.profile_id;
    let (profile, csv) = tauri::async_runtime::spawn_blocking(move || {
        let client = RegistryClient::native()?;
        client
            .resolve(&id)
            .map_err(|detail| internal("open registry profile", detail))
    })
    .await
    .map_err(|error| fail(&internal("registry worker", error.to_string())))?
    .map_err(|e| fail(&e))?;
    let snapshot = profiles
        .open_registry_copy(&profile.id, &csv)
        .map_err(|e| fail(&e))?;
    crate::telemetry::global().opened(
        crate::telemetry::ProfileSource::File,
        Some(&profile.game_id),
    );
    Ok(snapshot)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;

    const SHA: &str = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    fn row(id: &str) -> Value {
        serde_json::json!({
            "id": id, "gameId": "minecraft", "platform": "pc", "deviceId": "quadstick-fps",
            "title": "Minecraft", "source": { "type": "csv" },
            "snapshot": { "file": "profile.csv", "sha256": SHA },
        })
    }

    fn index(rows: Vec<Value>) -> String {
        serde_json::json!({ "schemaVersion": 1, "profiles": rows }).to_string()
    }

    #[test]
    fn links_match_the_canonical_shape_only() {
        assert_eq!(
            profile_id_from_link("qcm://profile/minecraft-basic").as_deref(),
            Some("minecraft-basic")
        );
        assert_eq!(
            profile_id_from_link("QCM://Profile/minecraft").as_deref(),
            Some("minecraft")
        );
        for bad in [
            "qcm://profile/",
            "qcm://profile/../../bad",
            "qcm://profile/a/b",
            "qcm://profile/a?x=1",
            "qcm://profile/a#f",
            "qcm://user@profile/a",
            "qcm://profile:80/a",
            "qcm://profile/-a",
            "qcm://profile/a-",
            "qcm://profile/Upper",
            "https://profile/a",
            "qcm://other/a",
        ] {
            assert_eq!(profile_id_from_link(bad), None, "{bad}");
        }
        assert_eq!(
            profile_id_from_args(["--flag", "qcm://profile/a--b"]).as_deref(),
            Some("a--b")
        );
    }

    #[test]
    fn the_index_is_validated_like_the_shipped_client() {
        let parsed = parse(&index(vec![row("one"), row("two")])).expect("valid");
        assert_eq!(parsed.len(), 2);
        assert_eq!(
            csv_url(&parsed[0]),
            "https://raw.githubusercontent.com/Bbrizly/Adaptive-Profiles/main/data/profiles/minecraft/quadstick-fps/one/profile.csv"
        );
        assert!(parse(&index(vec![row("one"), row("one")])).is_err());
        assert!(parse(r#"{"schemaVersion":2,"profiles":[]}"#).is_err());
        let mut bad = row("x");
        bad["platform"] = "amiga".into();
        assert!(parse(&index(vec![bad])).is_err());
        let mut sheet = row("y");
        sheet["source"] = serde_json::json!({ "type": "google-sheet", "url": "https://evil.example/spreadsheets/d/abc" });
        assert!(parse(&index(vec![sheet])).is_err());
        let mut sheet = row("z");
        sheet["source"] = serde_json::json!({ "type": "google-sheet",
            "url": "https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOpQrStUvWx/edit" });
        assert_eq!(
            parse(&index(vec![sheet])).expect("valid")[0]
                .source_url
                .as_deref()
                .map(|u| u.starts_with("https://docs.google.com")),
            Some(true)
        );
        let mut hash = row("h");
        hash["snapshot"]["sha256"] = "ABC".into();
        assert!(parse(&index(vec![hash])).is_err());
    }

    struct Canned(Mutex<HashMap<String, Result<Vec<u8>, String>>>);

    impl Fetch for Canned {
        fn get(&self, url: &str, _max: usize) -> Result<Vec<u8>, String> {
            self.0
                .lock()
                .expect("canned")
                .get(url)
                .cloned()
                .unwrap_or_else(|| Err("offline".into()))
        }
    }

    #[test]
    fn a_snapshot_must_match_its_hash_and_the_cache_covers_an_outage() {
        let csv = "Profile Name,,Game\ngame.csv\nPlayStation Outputs,Function,usb\nx,normal,lip\n";
        let sha: String = Sha256::digest(csv.as_bytes())
            .iter()
            .map(|b| format!("{b:02x}"))
            .collect();
        let mut good = row("game");
        good["snapshot"]["sha256"] = sha.clone().into();
        let catalog = index(vec![good]);
        let dir = std::env::temp_dir().join(format!(
            "qcm-registry-{}",
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0)
        ));
        let profile_url = "https://raw.githubusercontent.com/Bbrizly/Adaptive-Profiles/main/data/profiles/minecraft/quadstick-fps/game/profile.csv";
        let canned = Canned(Mutex::new(HashMap::from([
            (CATALOG_URL.to_owned(), Ok(catalog.into_bytes())),
            (profile_url.to_owned(), Ok(b"tampered".to_vec())),
        ])));
        let client = RegistryClient::new(canned, dir.join("index.json"), dir.join("snaps"));
        assert!(client.resolve("game").is_err(), "tampered bytes refused");

        client
            .fetch
            .0
            .lock()
            .expect("canned")
            .insert(profile_url.to_owned(), Ok(csv.as_bytes().to_vec()));
        let (profile, text) = client.resolve("game").expect("verified");
        assert_eq!(profile.game_id, "minecraft");
        assert_eq!(text, csv);

        // Offline now: both come from the verified cache.
        client.fetch.0.lock().expect("canned").clear();
        let (_, cached) = client.resolve("game").expect("cache");
        assert_eq!(cached, csv);
        assert!(client.resolve("missing").is_err());
        let _ = fs::remove_dir_all(&dir);
    }
}
