//! Google Sheets links: the pasted-link import and following it afterwards.
//!
//! Port of `SheetsUrl.cs` and the download half of `MainWindow.Follow.cs`
//! (follow-link `6bbcd05`, `3068515`, `7fc908a`). Three rules hold:
//!   - nothing reaches the network until the person presses a button: no
//!     check on open, on launch, or on a timer;
//!   - Get the latest downloads through the same path and refusals as Import;
//!   - nothing replaces a local file until the person has seen what changed
//!     and said yes, and the old copy goes to the rescue folder first.
//!
//! The remembered links live in a native-only file keyed by the profile's
//! private persistent key (its path), so no path reaches the WebView.

use qcm_config::{
    ProfileFile, SkippedTab, TabRename, WorkbookLimitation, XLSX_MAX_WORKBOOK_BYTES, import_xlsx,
    looks_like_xlsx, parse_csv,
};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::fs;
use std::io::Read;
use std::path::PathBuf;
use std::sync::{Mutex, MutexGuard, PoisonError};
use std::time::Duration;

/// Longest link kept. A real Sheets link is well under this.
pub const MAX_LINK: usize = 2048;

// ---- SheetsUrl.cs ----

const fn id_char(b: u8) -> bool {
    b.is_ascii_alphanumeric() || b == b'_' || b == b'-'
}

/// First `needle` followed by 20 or more id characters, as the C# regex finds it.
fn find_id<'a>(text: &'a str, needle: &str) -> Option<&'a str> {
    let mut from = 0;
    while let Some(at) = text[from..].find(needle) {
        let start = from + at + needle.len();
        let len = text.as_bytes()[start..]
            .iter()
            .take_while(|b| id_char(**b))
            .count();
        if len >= 20 {
            return Some(&text[start..start + len]);
        }
        from = from + at + 1;
    }
    None
}

fn find_key(text: &str) -> Option<&str> {
    find_id(text, "?key=").or_else(|| find_id(text, "&key="))
}

fn find_gid(text: &str) -> Option<&str> {
    for needle in ["#gid=", "?gid=", "&gid="] {
        if let Some(at) = text.find(needle) {
            let start = at + needle.len();
            let len = text.as_bytes()[start..]
                .iter()
                .take_while(|b| b.is_ascii_digit())
                .count();
            if len > 0 {
                return Some(&text[start..start + len]);
            }
        }
    }
    None
}

/// A pasted Google Sheets link, already proven to give export URLs.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SheetLink {
    link: String,
    published: Option<String>,
    id: Option<String>,
    gid: Option<String>,
}

impl SheetLink {
    /// `SheetsUrl.TryGetXlsxExportUrl` and `TryGetCsvExportUrl` both succeed.
    #[must_use]
    pub fn parse(pasted: &str) -> Option<Self> {
        let link = pasted.trim();
        if link.is_empty() || link.len() > MAX_LINK {
            return None;
        }
        let published = find_id(link, "/spreadsheets/d/e/").map(str::to_owned);
        let id = if published.is_some() {
            None
        } else {
            find_id(link, "/spreadsheets/d/")
                .or_else(|| find_key(link))
                .map(str::to_owned)
        };
        if published.is_none() && id.is_none() {
            return None;
        }
        Some(Self {
            link: link.to_owned(),
            published,
            id,
            gid: find_gid(link).map(str::to_owned),
        })
    }

    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.link
    }

    fn export(&self, format: &str, whole: bool) -> String {
        let gid = if whole {
            String::new()
        } else {
            self.gid
                .as_ref()
                .map_or_else(String::new, |g| format!("&gid={g}"))
        };
        match (&self.published, &self.id) {
            (Some(published), _) => format!(
                "https://docs.google.com/spreadsheets/d/e/{published}/pub?output={format}{gid}"
            ),
            (None, Some(id)) => {
                format!("https://docs.google.com/spreadsheets/d/{id}/export?format={format}{gid}")
            }
            (None, None) => String::new(),
        }
    }

    /// The whole workbook, every tab; the gid is dropped.
    #[must_use]
    pub fn xlsx_url(&self) -> String {
        self.export("xlsx", true)
    }

    /// One tab as CSV: the linked one, or the first.
    #[must_use]
    pub fn csv_url(&self) -> String {
        self.export("csv", false)
    }
}

// ---- the download ----

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FetchFailure {
    TimedOut,
    Failed(String),
}

/// The one road to a Sheets export. Tests swap it to count every request.
pub trait SheetFetcher: Send + Sync {
    fn get(&self, url: &str) -> Result<Vec<u8>, FetchFailure>;
}

#[derive(Debug)]
pub struct NativeSheetFetcher {
    http: reqwest::blocking::Client,
}

impl NativeSheetFetcher {
    pub fn new() -> Result<Self, qcm_core::QcmError> {
        let http = reqwest::blocking::Client::builder()
            .timeout(Duration::from_secs(30))
            .redirect(reqwest::redirect::Policy::limited(5))
            .user_agent("QuadStickConfigManager")
            .build()
            .map_err(|error| {
                qcm_core::QcmError::Internal(qcm_core::error::InternalError {
                    what: "build Sheets client",
                    detail: qcm_core::error::OsDetail::new(error.to_string()),
                })
            })?;
        Ok(Self { http })
    }
}

impl SheetFetcher for NativeSheetFetcher {
    fn get(&self, url: &str) -> Result<Vec<u8>, FetchFailure> {
        let failed = |error: reqwest::Error| {
            if error.is_timeout() {
                FetchFailure::TimedOut
            } else {
                FetchFailure::Failed(error.without_url().to_string())
            }
        };
        let response = self
            .http
            .get(url)
            .send()
            .and_then(reqwest::blocking::Response::error_for_status)
            .map_err(failed)?;
        let mut bytes = Vec::new();
        response
            .take(XLSX_MAX_WORKBOOK_BYTES as u64 + 1)
            .read_to_end(&mut bytes)
            .map_err(|error| FetchFailure::Failed(error.to_string()))?;
        if bytes.len() > XLSX_MAX_WORKBOOK_BYTES {
            return Err(FetchFailure::Failed("the sheet is too large".to_owned()));
        }
        Ok(bytes)
    }
}

/// What a Sheets download came back as. `file` is `None` when Google
/// answered with a web page.
#[derive(Debug, Clone)]
pub struct SheetDownload {
    pub text: String,
    pub file: Option<ProfileFile>,
    pub skipped: Vec<SkippedTab>,
    pub renamed: Vec<TabRename>,
    pub limitation: Option<WorkbookLimitation>,
    /// The workbook export was not available, so only one tab came (C#
    /// `Main_ThisLinkIsAPublished`).
    pub single_tab: bool,
}

/// Why a link could not become a profile. The words are the frontend's.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum SheetRefusal {
    NotALink,
    WebPage,
    Unreadable,
    TimedOut,
    DownloadFailed {
        detail: String,
    },
    /// `MainWindow.NoProfileTab`: what was actually there.
    NoProfileTab {
        skipped: Vec<String>,
        empty: bool,
        a1: String,
    },
}

/// `FetchSheetAsync`: workbook first, so a profile split across mode tabs
/// arrives whole; a link that cannot give one falls back to one CSV tab.
pub fn fetch_sheet(
    link: &SheetLink,
    fetcher: &dyn SheetFetcher,
) -> Result<SheetDownload, SheetRefusal> {
    let refused = |failure: FetchFailure| match failure {
        FetchFailure::TimedOut => SheetRefusal::TimedOut,
        FetchFailure::Failed(detail) => SheetRefusal::DownloadFailed {
            detail: detail
                .chars()
                .filter(|c| !c.is_control())
                .take(200)
                .collect(),
        },
    };
    let bytes = fetcher.get(&link.xlsx_url()).map_err(refused)?;
    let (text, skipped, renamed, limitation, single_tab) = if looks_like_xlsx(&bytes) {
        let read = import_xlsx(&bytes).map_err(|_| SheetRefusal::Unreadable)?;
        (read.csv, read.skipped, read.renamed, read.limitation, false)
    } else {
        let csv = fetcher.get(&link.csv_url()).map_err(refused)?;
        (
            String::from_utf8_lossy(&csv).into_owned(),
            Vec::new(),
            Vec::new(),
            None,
            true,
        )
    };
    let file = if text.trim_start().starts_with('<') {
        None
    } else {
        Some(ProfileFile::load(&text))
    };
    Ok(SheetDownload {
        text,
        file,
        skipped,
        renamed,
        limitation,
        single_tab,
    })
}

/// `Refusal`: the same refusals for Import and for Get the latest.
#[must_use]
pub fn refusal(download: &SheetDownload) -> Option<SheetRefusal> {
    let file = download.file.as_ref()?;
    if !file.document.sheets.is_empty() {
        return None;
    }
    let a1 = parse_csv(&download.text)
        .first()
        .and_then(|row| row.first())
        .map(|cell| cell.trim().to_owned())
        .unwrap_or_default();
    let a1 = if a1.chars().count() <= 60 {
        a1
    } else {
        format!("{}...", a1.chars().take(57).collect::<String>())
    };
    Some(SheetRefusal::NoProfileTab {
        skipped: download
            .skipped
            .iter()
            .map(|tab| tab.name.chars().take(100).collect())
            .collect(),
        empty: download.text.trim().is_empty(),
        a1,
    })
}

/// The download is usable, or why not.
pub fn usable(download: SheetDownload) -> Result<SheetDownload, SheetRefusal> {
    if download.file.is_none() {
        return Err(SheetRefusal::WebPage);
    }
    match refusal(&download) {
        Some(why) => Err(why),
        None => Ok(download),
    }
}

// ---- the remembered links ----

#[derive(Debug, Default, Serialize, Deserialize)]
struct LinkFile {
    links: BTreeMap<String, String>,
}

/// Profile key to the link it follows. Read once, written on change.
#[derive(Debug)]
pub struct FollowLinks {
    path: Option<PathBuf>,
    links: Mutex<Option<BTreeMap<String, String>>>,
}

impl FollowLinks {
    /// Kept for the run only. What tests and a fresh shell use.
    #[must_use]
    pub const fn in_memory() -> Self {
        Self {
            path: None,
            links: Mutex::new(None),
        }
    }

    #[must_use]
    pub const fn file(path: PathBuf) -> Self {
        Self {
            path: Some(path),
            links: Mutex::new(None),
        }
    }

    fn loaded(&self) -> MutexGuard<'_, Option<BTreeMap<String, String>>> {
        let mut guard = self.links.lock().unwrap_or_else(PoisonError::into_inner);
        if guard.is_none() {
            let read = self
                .path
                .as_ref()
                .and_then(|path| fs::read(path).ok())
                .and_then(|bytes| serde_json::from_slice::<LinkFile>(&bytes).ok())
                .map(|file| file.links)
                .unwrap_or_default();
            *guard = Some(read);
        }
        guard
    }

    #[must_use]
    pub fn get(&self, key: &str) -> Option<String> {
        self.loaded()
            .as_ref()
            .and_then(|links| links.get(key).cloned())
    }

    pub fn set(&self, key: &str, link: &str) -> bool {
        let mut guard = self.loaded();
        let links = guard.get_or_insert_with(BTreeMap::new);
        links.insert(key.to_owned(), link.to_owned());
        self.persist(links)
    }

    pub fn remove(&self, key: &str) -> bool {
        let mut guard = self.loaded();
        let links = guard.get_or_insert_with(BTreeMap::new);
        if links.remove(key).is_none() {
            return true;
        }
        self.persist(links)
    }

    fn persist(&self, links: &BTreeMap<String, String>) -> bool {
        let Some(path) = &self.path else { return true };
        let Some(parent) = path.parent() else {
            return false;
        };
        let Ok(bytes) = serde_json::to_vec_pretty(&LinkFile {
            links: links.clone(),
        }) else {
            return false;
        };
        let temp = path.with_extension("json.tmp");
        let written = fs::create_dir_all(parent).is_ok() && fs::write(&temp, bytes).is_ok();
        // `fs::rename` replaces an existing file on every platform (Windows
        // too, MOVEFILE_REPLACE_EXISTING), so the old copy is never deleted
        // first and a crash leaves one whole file or the other.
        if written && fs::rename(&temp, path).is_ok() {
            return true;
        }
        let _ = fs::remove_file(&temp);
        false
    }
}

#[cfg(test)]
mod tests {
    use super::{FetchFailure, SheetFetcher, SheetLink, SheetRefusal, fetch_sheet, usable};
    use std::sync::atomic::{AtomicUsize, Ordering};

    const ID: &str = "1AbCdEfGhIjKlMnOpQrStUvWxYz012345";

    #[test]
    fn links_give_the_same_export_urls_as_sheets_url() {
        let edit = SheetLink::parse(&format!(
            " https://docs.google.com/spreadsheets/d/{ID}/edit#gid=42 "
        ))
        .expect("edit link");
        assert_eq!(
            edit.xlsx_url(),
            format!("https://docs.google.com/spreadsheets/d/{ID}/export?format=xlsx")
        );
        assert_eq!(
            edit.csv_url(),
            format!("https://docs.google.com/spreadsheets/d/{ID}/export?format=csv&gid=42")
        );
        let published = SheetLink::parse(&format!(
            "https://docs.google.com/spreadsheets/d/e/2PACX-{ID}/pubhtml"
        ))
        .expect("published link");
        assert_eq!(
            published.csv_url(),
            format!("https://docs.google.com/spreadsheets/d/e/2PACX-{ID}/pub?output=csv")
        );
        let key = SheetLink::parse(&format!("https://docs.google.com/ccc?key={ID}")).expect("key");
        assert!(key.xlsx_url().contains(ID));
        for bad in [
            "",
            "hello",
            "https://docs.google.com/spreadsheets/d/short/edit",
        ] {
            assert!(SheetLink::parse(bad).is_none(), "{bad}");
        }
    }

    struct Sheet {
        csv: String,
        requests: AtomicUsize,
    }
    impl SheetFetcher for Sheet {
        fn get(&self, url: &str) -> Result<Vec<u8>, FetchFailure> {
            self.requests.fetch_add(1, Ordering::SeqCst);
            Ok(if url.contains("format=csv") {
                self.csv.clone().into_bytes()
            } else {
                b"not a workbook".to_vec()
            })
        }
    }

    fn link() -> SheetLink {
        SheetLink::parse(&format!("https://docs.google.com/spreadsheets/d/{ID}/edit"))
            .expect("link")
    }

    #[test]
    fn a_link_without_the_workbook_falls_back_to_one_csv_tab() {
        let sheet = Sheet {
            csv: "Profile Name,,Walking\r\ngame.csv\r\nPlayStation Outputs,Function,usb\r\ndpad_N,normal,right_sip\r\n".into(),
            requests: AtomicUsize::new(0),
        };
        let got = usable(fetch_sheet(&link(), &sheet).expect("downloads")).expect("usable");
        assert!(got.single_tab);
        assert_eq!(sheet.requests.load(Ordering::SeqCst), 2);
    }

    // FollowLinkTests.A_sheet_that_would_not_import_is_refused
    #[test]
    fn a_sign_in_page_or_a_sheet_without_a_profile_tab_is_refused() {
        let page = Sheet {
            csv: "<!doctype html><html>sign in</html>".into(),
            requests: AtomicUsize::new(0),
        };
        assert_eq!(
            usable(fetch_sheet(&link(), &page).expect("downloads")).err(),
            Some(SheetRefusal::WebPage)
        );
        let notes = Sheet {
            csv: "My notes,about\r\nthings\r\n".into(),
            requests: AtomicUsize::new(0),
        };
        assert_eq!(
            usable(fetch_sheet(&link(), &notes).expect("downloads")).err(),
            Some(SheetRefusal::NoProfileTab {
                skipped: Vec::new(),
                empty: false,
                a1: "My notes".into()
            })
        );
    }
}
