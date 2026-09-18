//! Settings on disk.
//!
//! One JSON file in the per-user config directory, written the same way a
//! profile is: beside the target, then renamed. A settings file is small, but a
//! torn one is a person's theme, language and interface scale gone, and the
//! next launch would open at defaults with no way to say why.
//!
//! The rewrite writes its own file next to the shipped app's, under its own
//! bundle identifier. Reading the legacy `settings.json` is a migration and
//! belongs to the cutover, not here.

use crate::adapters::storage::map_io;
use qcm_core::error::{StorageError, StorageStage, TargetState};
use qcm_core::settings::{AppSettings, SettingsStore};
use std::fs;
use std::path::{Path, PathBuf};

const FILE_NAME: &str = "settings.json";
const TEMP_SUFFIX: &str = ".qscm-tmp";

/// The settings file for this install.
#[derive(Debug, Clone)]
pub enum SettingsFile {
    At(PathBuf),
    /// The platform would not say where per-user config lives. The app still
    /// runs, on defaults, and a change made in it is refused with a reason
    /// rather than accepted and lost.
    Nowhere,
}

impl SettingsFile {
    /// Point at an exact file. The test seam, and the only constructor that
    /// names a place.
    #[must_use]
    pub const fn at(path: PathBuf) -> Self {
        Self::At(path)
    }

    /// The per-user location for this install.
    #[must_use]
    pub fn default_location() -> Self {
        config_dir().map_or(Self::Nowhere, |dir| Self::at(dir.join(FILE_NAME)))
    }

    fn path(&self) -> Option<&Path> {
        match self {
            Self::At(path) => Some(path),
            Self::Nowhere => None,
        }
    }
}

impl SettingsStore for SettingsFile {
    fn load(&self) -> Option<AppSettings> {
        let text = fs::read_to_string(self.path()?).ok()?;
        // A value the file cannot legally hold takes the whole file down to
        // defaults rather than loading the rest around it. There is no nearest
        // legal interface scale, so there is nothing honest to keep.
        serde_json::from_str(&text).ok()
    }

    fn save(&self, settings: &AppSettings) -> Result<(), StorageError> {
        let Some(path) = self.path() else {
            return Err(StorageError::Io {
                stage: StorageStage::TempCreate,
                target: TargetState::Unchanged,
                detail: qcm_core::error::OsDetail::new(
                    "this platform did not say where per-user settings live".to_owned(),
                ),
            });
        };
        let text = serde_json::to_string_pretty(settings).map_err(|error| StorageError::Io {
            stage: StorageStage::TempWrite,
            target: TargetState::Unchanged,
            detail: qcm_core::error::OsDetail::new(error.to_string()),
        })?;

        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).map_err(|error| {
                map_io(&error, StorageStage::TempCreate, TargetState::Unchanged)
            })?;
        }

        let temp = temp_beside(path);
        fs::write(&temp, text.as_bytes()).map_err(|error| {
            let _ = fs::remove_file(&temp);
            map_io(&error, StorageStage::TempWrite, TargetState::Unchanged)
        })?;
        fs::rename(&temp, path).map_err(|error| {
            let _ = fs::remove_file(&temp);
            map_io(
                &error,
                StorageStage::ReplaceBeforeDisplace,
                TargetState::Unchanged,
            )
        })
    }
}

fn temp_beside(path: &Path) -> PathBuf {
    let mut name = path.file_name().unwrap_or_default().to_os_string();
    name.push(TEMP_SUFFIX);
    path.with_file_name(name)
}

/// Where this platform keeps a per-user config directory.
///
/// Read from the environment rather than through a crate, for the same reason
/// the storage adapter enumerates volumes by hand: every crate that asks the
/// operating system properly needs an `unsafe` call the workspace forbids.
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
