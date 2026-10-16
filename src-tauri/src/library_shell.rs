//! The "Your profiles" library on Home and the saved templates
//! (`MainWindow.RefreshHomeCards`, `ProfileCard`, `SaveAsTemplateAsync`,
//! `UseTemplateAsync`).
//!
//! The folder is the one Avalonia lists, `Documents/QuadStick Profiles`, and
//! its `Templates` subfolder. The WebView gets an opaque card id per file, made
//! fresh on every listing; the path stays here.

use crate::adapters::storage::map_io;
use crate::ipc::{parse, session_id};
use crate::shell::ShellState;
use qcm_config::{ProfileFile, SheetType, parse_and_validate};
use qcm_core::error::{ProfileError, QcmError, RequestError, StorageStage, TargetState};
use qcm_core::profiles::EditorSnapshot;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, MutexGuard, PoisonError};
use std::time::UNIX_EPOCH;

/// One card on Home. Counts are modes, not sheets: a preferences sheet is
/// neither a mode nor a set of bindings (`CardSubtitle`).
#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct LibraryCardDto {
    pub id: String,
    /// The file name without `.csv`; the user never reads the extension.
    pub name: String,
    /// The profile's own title when it differs from the file name.
    pub title: String,
    pub modes: usize,
    pub bindings: usize,
    pub mode_names: Vec<String>,
    /// Last write, milliseconds since 1970. `None` when the OS will not say.
    pub edited_ms: Option<u64>,
    /// Remembers a Google Sheets link (`FollowedLink`).
    pub follows: bool,
    /// False when the file could not be read or parsed.
    pub readable: bool,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct TemplateDto {
    pub id: String,
    pub name: String,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct TemplateReceiptDto {
    /// The file name it was saved under, `.csv` included.
    pub name: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct CardRequest {
    id: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct SaveTemplateRequest {
    session_id: String,
    expected_revision: u64,
    name: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RenameTemplateRequest {
    id: String,
    name: String,
}

/// What a template rename did (`UseTemplateAsync`'s Rename button).
#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum TemplateRenameDto {
    Renamed {
        name: String,
    },
    /// Another template already has that name; nothing moved.
    Taken {
        name: String,
    },
}

const MAX_TEMPLATE_NAME: usize = 120;

/// Card id to path, replaced by every listing so an old id cannot reach a
/// file the window no longer shows.
#[derive(Debug, Default)]
pub struct LibraryCards {
    table: Mutex<CardTable>,
}

#[derive(Debug, Default)]
struct CardTable {
    generation: u64,
    paths: BTreeMap<String, PathBuf>,
}

impl LibraryCards {
    fn table(&self) -> MutexGuard<'_, CardTable> {
        self.table.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// Ids for `paths`, `prefix` keeping library and template ids apart.
    fn remember(&self, prefix: &str, paths: &[PathBuf]) -> Vec<String> {
        let mut table = self.table();
        table.generation = table.generation.saturating_add(1);
        let generation = table.generation;
        table.paths.retain(|id, _| !id.starts_with(prefix));
        paths
            .iter()
            .enumerate()
            .map(|(index, path)| {
                let id = format!("{prefix}{generation}-{index}");
                table.paths.insert(id.clone(), path.clone());
                id
            })
            .collect()
    }

    fn path(&self, id: &str) -> Result<PathBuf, QcmError> {
        self.table().paths.get(id).cloned().ok_or_else(|| {
            RequestError::OutOfRange {
                what: "library card",
            }
            .into()
        })
    }

    fn forget(&self, id: &str) {
        self.table().paths.remove(id);
    }
}

/// `*.csv` directly in `dir`, by file name, the order Home shows them in.
#[must_use]
pub fn profile_files(dir: &Path) -> Vec<PathBuf> {
    let Ok(entries) = fs::read_dir(dir) else {
        return Vec::new();
    };
    let mut files: Vec<PathBuf> = entries
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .filter(|path| {
            path.is_file()
                && path
                    .extension()
                    .is_some_and(|ext| ext.eq_ignore_ascii_case("csv"))
        })
        .collect();
    files.sort_by_key(|path| {
        path.file_name()
            .map(|name| name.to_string_lossy().into_owned())
    });
    files
}

fn bare_name(path: &Path) -> String {
    path.file_stem()
        .map(|stem| stem.to_string_lossy().into_owned())
        .unwrap_or_default()
}

/// The facts a card prints (`FactsFor`).
#[must_use]
pub fn card_facts(id: String, path: &Path, follows: bool) -> LibraryCardDto {
    let name = bare_name(path);
    let edited_ms = fs::metadata(path)
        .and_then(|meta| meta.modified())
        .ok()
        .and_then(|time| time.duration_since(UNIX_EPOCH).ok())
        .and_then(|age| u64::try_from(age.as_millis()).ok());
    let Ok(text) = fs::read_to_string(path) else {
        return LibraryCardDto {
            id,
            name,
            title: String::new(),
            modes: 0,
            bindings: 0,
            mode_names: Vec::new(),
            edited_ms,
            follows,
            readable: false,
        };
    };
    let (document, _) = parse_and_validate(&text);
    let modes: Vec<_> = document
        .sheets
        .iter()
        .filter(|sheet| sheet.sheet_type == SheetType::ProfileName)
        .collect();
    let title = document.title().to_owned();
    LibraryCardDto {
        id,
        title: if title.eq_ignore_ascii_case(&name) {
            String::new()
        } else {
            title
        },
        name,
        modes: modes.len(),
        bindings: modes.iter().map(|sheet| sheet.bindings.len()).sum(),
        mode_names: modes
            .iter()
            .map(|sheet| sheet.mode_name.trim().to_owned())
            .filter(|mode| !mode.is_empty())
            .collect(),
        edited_ms,
        follows,
        readable: true,
    }
}

/// `SafeTemplateName`: the platform's bad characters plus the separators and
/// drive colon that break a synced file elsewhere, and one `.csv`.
#[must_use]
pub fn safe_template_name(name: &str) -> String {
    let trimmed = name.trim();
    if trimmed.is_empty() {
        return String::new();
    }
    let mut cleaned: String = trimmed
        .chars()
        .map(|c| {
            if matches!(c, '/' | '\\' | ':' | '*' | '?' | '"' | '<' | '>' | '|') || c.is_control() {
                '_'
            } else {
                c
            }
        })
        .collect();
    if cleaned.to_ascii_lowercase().ends_with(".csv") {
        cleaned.truncate(cleaned.len() - 4);
    }
    format!("{cleaned}.csv")
}

fn io_error(error: &std::io::Error, stage: StorageStage) -> QcmError {
    map_io(error, stage, TargetState::Unchanged).into()
}

impl ShellState {
    fn follow_key(path: &Path) -> String {
        fs::canonicalize(path)
            .unwrap_or_else(|_| path.to_path_buf())
            .to_string_lossy()
            .into_owned()
    }

    /// Every profile in `dir`, as cards.
    pub fn library_cards(&self, cards: &LibraryCards, dir: &Path) -> Vec<LibraryCardDto> {
        let files = profile_files(dir);
        let ids = cards.remember("lib-", &files);
        files
            .iter()
            .zip(ids)
            .map(|(path, id)| {
                let follows = self.follow_links().get(&Self::follow_key(path)).is_some();
                card_facts(id, path, follows)
            })
            .collect()
    }

    /// Open a card. Save writes back to its file.
    pub fn open_library_card(
        &self,
        cards: &LibraryCards,
        raw: serde_json::Value,
    ) -> Result<EditorSnapshot, QcmError> {
        let request: CardRequest = parse(raw, "open_library_profile request")?;
        let path = cards.path(&request.id)?;
        let mut sessions = self.sessions();
        let target = sessions.store().adopt(&path);
        sessions.open_local(target)
    }

    /// Delete a card's file, then the link it followed (`DeleteProfileAsync`).
    /// The Google Sheet copy is left alone.
    pub fn delete_library_card(
        &self,
        cards: &LibraryCards,
        raw: serde_json::Value,
    ) -> Result<(), QcmError> {
        let request: CardRequest = parse(raw, "delete_library_profile request")?;
        let path = cards.path(&request.id)?;
        let key = Self::follow_key(&path);
        fs::remove_file(&path).map_err(|error| io_error(&error, StorageStage::Delete))?;
        cards.forget(&request.id);
        self.follow_links().remove(&key);
        Ok(())
    }

    /// The templates folder's files.
    pub fn template_list(&self, cards: &LibraryCards, dir: &Path) -> Vec<TemplateDto> {
        let files = profile_files(dir);
        let ids = cards.remember("tpl-", &files);
        files
            .iter()
            .zip(ids)
            .map(|(path, id)| TemplateDto {
                id,
                name: bare_name(path),
            })
            .collect()
    }

    /// A template opened as a new, untitled profile.
    pub fn open_template_card(
        &self,
        cards: &LibraryCards,
        raw: serde_json::Value,
    ) -> Result<EditorSnapshot, QcmError> {
        let request: CardRequest = parse(raw, "open_template request")?;
        let path = cards.path(&request.id)?;
        let text =
            fs::read_to_string(&path).map_err(|error| io_error(&error, StorageStage::ReadFile))?;
        Ok(self.sessions().open_template_copy(&text))
    }

    /// Write the open profile into `dir` as a template, normalized the way an
    /// installed file is so the two match byte for byte. The open profile is
    /// left as it is.
    pub fn save_template(
        &self,
        dir: &Path,
        raw: serde_json::Value,
    ) -> Result<TemplateReceiptDto, QcmError> {
        let request: SaveTemplateRequest = parse(raw, "save_template request")?;
        if request.name.len() > MAX_TEMPLATE_NAME {
            return Err(RequestError::TooLarge {
                what: "template name",
                limit: MAX_TEMPLATE_NAME,
                actual: request.name.len(),
            }
            .into());
        }
        let name = safe_template_name(&request.name);
        if name.is_empty() {
            return Err(RequestError::OutOfRange {
                what: "template name",
            }
            .into());
        }
        let session = session_id(&request.session_id)?;
        let text = {
            let sessions = self.sessions();
            let open = sessions.session(session)?;
            if open.revision() != request.expected_revision {
                return Err(ProfileError::RevisionConflict {
                    expected: request.expected_revision,
                    actual: open.revision(),
                }
                .into());
            }
            let mut copy: ProfileFile = open.file().clone();
            copy.normalize_for_device_csv();
            copy.to_csv_text()
        };
        fs::create_dir_all(dir).map_err(|error| io_error(&error, StorageStage::TempCreate))?;
        let target = dir.join(&name);
        let temp = dir.join(format!("{name}.qscm-tmp"));
        fs::write(&temp, text.as_bytes()).map_err(|error| {
            let _ = fs::remove_file(&temp);
            io_error(&error, StorageStage::TempWrite)
        })?;
        fs::rename(&temp, &target).map_err(|error| {
            let _ = fs::remove_file(&temp);
            io_error(&error, StorageStage::ReplaceBeforeDisplace)
        })?;
        Ok(TemplateReceiptDto { name })
    }
}

/// Rename a template file. A name another template has is refused, never
/// overwritten; the same name is a no-op.
pub fn rename_template_card(
    cards: &LibraryCards,
    dir: &Path,
    raw: serde_json::Value,
) -> Result<TemplateRenameDto, QcmError> {
    let request: RenameTemplateRequest = parse(raw, "rename_template request")?;
    if request.name.len() > MAX_TEMPLATE_NAME {
        return Err(RequestError::TooLarge {
            what: "template name",
            limit: MAX_TEMPLATE_NAME,
            actual: request.name.len(),
        }
        .into());
    }
    let name = safe_template_name(&request.name);
    if name.is_empty() {
        return Err(RequestError::OutOfRange {
            what: "template name",
        }
        .into());
    }
    if !request.id.starts_with("tpl-") {
        return Err(RequestError::OutOfRange { what: "template" }.into());
    }
    let old = cards.path(&request.id)?;
    let new = dir.join(&name);
    let bare = name.trim_end_matches(".csv").to_owned();
    if new == old {
        return Ok(TemplateRenameDto::Renamed { name: bare });
    }
    // A change of case only, on a volume that ignores case: the same file.
    if let (Ok(a), Ok(b)) = (fs::canonicalize(&new), fs::canonicalize(&old))
        && a == b
    {
        fs::rename(&old, &new)
            .map_err(|error| io_error(&error, StorageStage::ReplaceBeforeDisplace))?;
        cards.forget(&request.id);
        return Ok(TemplateRenameDto::Renamed { name: bare });
    }
    // No-clobber, as `File.Move` is: a hard link fails when the name is taken,
    // even by a file that appeared after any check.
    match fs::hard_link(&old, &new) {
        Ok(()) => {
            fs::remove_file(&old).map_err(|error| io_error(&error, StorageStage::Delete))?;
        }
        Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => {
            return Ok(TemplateRenameDto::Taken { name: bare });
        }
        // A volume without hard links: copy into a file that must not exist
        // yet, then remove the old one. Still never replaces anything.
        Err(_) => {
            use std::io::Write as _;
            let bytes = fs::read(&old).map_err(|error| io_error(&error, StorageStage::ReadFile))?;
            let mut file = match fs::OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(&new)
            {
                Ok(file) => file,
                Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => {
                    return Ok(TemplateRenameDto::Taken { name: bare });
                }
                Err(error) => return Err(io_error(&error, StorageStage::TempCreate)),
            };
            if let Err(error) = file.write_all(&bytes).and_then(|()| file.sync_all()) {
                drop(file);
                let _ = fs::remove_file(&new);
                return Err(io_error(&error, StorageStage::TempWrite));
            }
            fs::remove_file(&old).map_err(|error| io_error(&error, StorageStage::Delete))?;
        }
    }
    cards.forget(&request.id);
    Ok(TemplateRenameDto::Renamed { name: bare })
}

/// Delete a template file. Profiles made from it are their own files.
pub fn delete_template_card(cards: &LibraryCards, raw: serde_json::Value) -> Result<(), QcmError> {
    let request: CardRequest = parse(raw, "delete_template request")?;
    // Only an id from the templates listing: a library card id here would
    // delete a profile.
    if !request.id.starts_with("tpl-") {
        return Err(RequestError::OutOfRange { what: "template" }.into());
    }
    let path = cards.path(&request.id)?;
    fs::remove_file(&path).map_err(|error| io_error(&error, StorageStage::Delete))?;
    cards.forget(&request.id);
    Ok(())
}

/// Avalonia `TemplatesDir`.
#[must_use]
pub fn templates_dir() -> PathBuf {
    crate::drive::library_dir().join("Templates")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn template_names_are_safe_on_every_os_and_end_in_one_csv() {
        assert_eq!(safe_template_name("  Racing  "), "Racing.csv");
        assert_eq!(safe_template_name("a/b\\c:d"), "a_b_c_d.csv");
        assert_eq!(safe_template_name("Drive.CSV"), "Drive.csv");
        assert_eq!(safe_template_name("   "), "");
    }

    #[test]
    fn cards_count_modes_not_sheets_and_skip_the_repeated_title() {
        let dir = std::env::temp_dir().join(format!("qcm-library-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        fs::write(
            dir.join("Racing.csv"),
            "Profile Name,,Racing\nRacing.csv\nPlayStation Outputs,Function,usb\nx,,lip\ncircle,,mp_left_sip\n\n\
             Profile Name,,Menu\n\nPlayStation Outputs,Function,usb\nsquare,,lip\n",
        )
        .unwrap();
        fs::write(dir.join("notes.txt"), "not a profile").unwrap();
        let files = profile_files(&dir);
        assert_eq!(files.len(), 1);
        let card = card_facts("lib-1-0".to_owned(), &files[0], false);
        assert_eq!(card.name, "Racing");
        assert_eq!(card.title, "");
        assert_eq!((card.modes, card.bindings), (2, 3));
        assert_eq!(
            card.mode_names,
            vec!["Racing".to_owned(), "Menu".to_owned()]
        );
        assert!(card.readable && card.edited_ms.is_some());
        let cards = LibraryCards::default();
        let ids = cards.remember("lib-", &files);
        assert_eq!(cards.path(&ids[0]).unwrap(), files[0]);
        // A new listing retires the old ids.
        let _ = cards.remember("lib-", &files);
        assert!(cards.path(&ids[0]).is_err());
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn templates_rename_without_overwriting_and_delete_only_by_template_id() {
        let dir = std::env::temp_dir().join(format!("qcm-templates-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        fs::write(dir.join("A.csv"), "a").unwrap();
        fs::write(dir.join("B.csv"), "b").unwrap();
        let cards = LibraryCards::default();
        let ids = cards.remember("tpl-", &profile_files(&dir));
        let taken =
            rename_template_card(&cards, &dir, serde_json::json!({"id": ids[0], "name": "B"}));
        assert_eq!(
            taken.unwrap(),
            TemplateRenameDto::Taken {
                name: "B".to_owned()
            }
        );
        assert_eq!(fs::read_to_string(dir.join("B.csv")).unwrap(), "b");
        let renamed = rename_template_card(
            &cards,
            &dir,
            serde_json::json!({"id": ids[0], "name": "C/1"}),
        );
        assert_eq!(
            renamed.unwrap(),
            TemplateRenameDto::Renamed {
                name: "C_1".to_owned()
            }
        );
        assert!(dir.join("C_1.csv").is_file() && !dir.join("A.csv").exists());
        // A library card id never reaches a template delete.
        let lib = cards.remember("lib-", &[dir.join("B.csv")]);
        assert!(delete_template_card(&cards, serde_json::json!({"id": lib[0]})).is_err());
        delete_template_card(&cards, serde_json::json!({"id": ids[1]})).unwrap();
        assert!(!dir.join("B.csv").exists());
        let _ = fs::remove_dir_all(&dir);
    }
}
