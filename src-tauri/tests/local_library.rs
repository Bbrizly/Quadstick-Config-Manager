//! The local library and the settings file, against a real filesystem.
//!
//! Same trick the device adapter tests use: a temp directory with a
//! `default.csv` in it is a QuadStick as far as every rule in the app is
//! concerned. No hardware, and the file operations are the real ones.

use qcm_core::error::ErrorCode;
use qcm_core::ports::local::LocalProfileStore;
use qcm_core::settings::{AppSettings, SettingsStore, ThemeChoice};
use qcm_tauri_lib::adapters::library::FileSystemProfileLibrary;
use qcm_tauri_lib::adapters::settings::SettingsFile;
use qcm_tauri_lib::adapters::storage::volumes::FixedVolumes;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

/// A temp directory that removes itself.
struct Scratch {
    path: PathBuf,
}

impl Scratch {
    fn new(tag: &str) -> Self {
        static NEXT: AtomicU64 = AtomicU64::new(0);
        let unique = format!(
            "qcm-{tag}-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::SeqCst)
        );
        let path = std::env::temp_dir().join(unique);
        fs::create_dir_all(&path).expect("a scratch directory");
        Self { path }
    }

    fn path(&self) -> &Path {
        &self.path
    }
}

impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.path);
    }
}

fn library(roots: Vec<PathBuf>) -> FileSystemProfileLibrary<FixedVolumes> {
    FileSystemProfileLibrary::new(FixedVolumes::new(roots))
}

#[test]
fn a_picked_file_becomes_an_id_that_prints_only_its_name() {
    let scratch = Scratch::new("adopt");
    let file = scratch.path().join("Racing.csv");
    fs::write(&file, "Profile Name,\n").expect("a profile");

    let store = library(Vec::new());
    let target = store.adopt(&file);
    assert_eq!(target.display_name().as_str(), "Racing.csv");
    assert_eq!(
        store.read(&target).expect("the file reads"),
        "Profile Name,\n"
    );
}

#[test]
fn a_write_lands_whole_and_leaves_no_scratch_file_behind() {
    let scratch = Scratch::new("write");
    let file = scratch.path().join("Racing.csv");
    fs::write(&file, "old\n").expect("something to overwrite");

    let store = library(Vec::new());
    let target = store.adopt(&file);
    let receipt = store
        .write(&target, "new,bytes\n")
        .expect("the write lands");

    assert_eq!(receipt.bytes, "new,bytes\n".len());
    assert_eq!(fs::read_to_string(&file).expect("read back"), "new,bytes\n");
    let leftovers: Vec<String> = fs::read_dir(scratch.path())
        .expect("list")
        .filter_map(|entry| entry.ok())
        .map(|entry| entry.file_name().to_string_lossy().into_owned())
        .filter(|name| name.contains(".qscm-tmp"))
        .collect();
    assert!(leftovers.is_empty(), "{leftovers:?}");
}

// An id this library never handed out is a refusal, not a write somewhere. The
// only thing that mints one is the picker.
#[test]
fn an_id_this_library_never_minted_writes_nothing() {
    let scratch = Scratch::new("forged");
    let real = library(Vec::new());
    let elsewhere = library(Vec::new());
    let file = scratch.path().join("Racing.csv");
    fs::write(&file, "old\n").expect("something to overwrite");
    let forged = elsewhere.adopt(&file);

    let error = real
        .write(&forged, "new\n")
        .expect_err("that id belongs to another table");
    assert_eq!(qcm_core::QcmError::from(error).code(), ErrorCode::StorageIo);
    assert_eq!(fs::read_to_string(&file).expect("read back"), "old\n");
}

// A folder becomes a device folder the moment a stick is plugged in, so this is
// asked immediately before every save rather than once when the file was
// picked. Save never writes to the QuadStick: only the install transaction
// carries the backup, the read-back and the `default.csv` confirmation.
#[test]
fn a_file_sitting_on_a_mounted_quadstick_is_recognized_as_one() {
    let scratch = Scratch::new("device");
    let device = scratch.path().join("QUADSTICK");
    fs::create_dir_all(&device).expect("a device root");
    fs::write(
        device.join("default.csv"),
        "QuadStick Configuration File,\n",
    )
    .expect("the marker");
    let on_device = device.join("Racing.csv");
    fs::write(&on_device, "Profile Name,\n").expect("a profile on the stick");

    let elsewhere = scratch.path().join("Racing.csv");
    fs::write(&elsewhere, "Profile Name,\n").expect("a profile in the library");

    let store = library(vec![device.clone()]);
    assert!(
        store
            .is_on_quadstick(&store.adopt(&on_device))
            .expect("the check answers")
    );
    assert!(
        !store
            .is_on_quadstick(&store.adopt(&elsewhere))
            .expect("the check answers")
    );
}

// Somebody with a `default.csv` in their own documents folder must not be told
// their whole library is a QuadStick.
#[test]
fn a_marker_file_outside_a_mounted_volume_does_not_make_a_folder_a_device() {
    let scratch = Scratch::new("lookalike");
    fs::write(
        scratch.path().join("default.csv"),
        "QuadStick Configuration File,\n",
    )
    .expect("a lookalike marker");
    let file = scratch.path().join("Racing.csv");
    fs::write(&file, "Profile Name,\n").expect("a profile");

    let store = library(Vec::new());
    assert!(
        !store
            .is_on_quadstick(&store.adopt(&file))
            .expect("the check answers")
    );
}

#[test]
fn settings_survive_a_round_trip_through_a_real_file() {
    let scratch = Scratch::new("settings");
    let file = SettingsFile::at(scratch.path().join("nested").join("settings.json"));
    assert!(file.load().is_none());

    let saved = AppSettings {
        theme: ThemeChoice::Dark,
        ..AppSettings::default()
    };
    file.save(&saved).expect("the save lands");
    assert_eq!(file.load(), Some(saved));
}

// A file hand-edited to a value the app does not offer opens at defaults rather
// than loading it. There is no nearest legal interface scale.
#[test]
fn a_settings_file_with_an_illegal_value_reads_as_nothing_saved() {
    let scratch = Scratch::new("poisoned");
    let path = scratch.path().join("settings.json");
    fs::write(&path, r#"{"theme":"dark","interfaceScale":137}"#).expect("a hand edit");
    assert!(SettingsFile::at(path).load().is_none());
}

// The app still runs where the platform will not say where config lives. What
// it must not do is accept a change and lose it.
#[test]
fn a_settings_change_with_nowhere_to_write_is_refused_rather_than_lost() {
    let nowhere = SettingsFile::Nowhere;
    assert!(nowhere.load().is_none());
    let error = nowhere
        .save(&AppSettings::default())
        .expect_err("there is nowhere to write");
    assert_eq!(qcm_core::QcmError::from(error).code(), ErrorCode::StorageIo);
}
