//! What a settings change is allowed to do.
//!
//! Two promises. A value the user did not choose is never written, so an
//! out-of-range request is refused rather than rounded. And a save that did not
//! reach disk does not move the value in memory, so the window and the next
//! launch always agree.

use qcm_core::error::ErrorCode;
use qcm_core::settings::{
    AppSettings, InterfaceScale, LanguageChoice, ModelChoice, PickerGrouping, Settings,
    SettingsPatch, ThemeChoice, grouping_from_wire, model_from_wire, theme_from_wire,
};
use qcm_testkit::FakeSettingsFile;

fn code(error: &qcm_core::QcmError) -> &'static str {
    error.code().as_str()
}

#[test]
fn a_fresh_install_starts_at_the_shipped_defaults() {
    let settings = Settings::load(FakeSettingsFile::new());
    let dto = settings.snapshot();
    assert_eq!(dto.revision, 1);
    assert_eq!(dto.model, "fps");
    assert_eq!(dto.theme, "system");
    assert_eq!(dto.language, "system");
    assert_eq!(dto.interface_scale_percent, 100);
    assert_eq!(dto.picker_grouping, "detailed");
    assert!(dto.remember_window);
    assert!(dto.device_cards);
    assert!(!dto.reduce_motion);
    assert!(!dto.tutorial_seen);
}

// The legacy `Settings.Load` swallowed every exception and returned defaults.
// A settings file is a convenience, and refusing to start over one would lock
// somebody out of their own controller.
#[test]
fn an_unreadable_settings_file_opens_the_app_at_defaults() {
    let settings = Settings::load(FakeSettingsFile::unreadable());
    assert_eq!(settings.current(), &AppSettings::default());
}

#[test]
fn what_was_saved_is_what_comes_back() {
    let saved = AppSettings {
        theme: ThemeChoice::Dark,
        model: ModelChoice::Singleton,
        picker_grouping: PickerGrouping::Flat,
        ..AppSettings::default()
    };
    let settings = Settings::load(FakeSettingsFile::with(saved.clone()));
    assert_eq!(settings.current(), &saved);
}

#[test]
fn a_patch_changes_only_what_it_names() {
    let file = FakeSettingsFile::new();
    let mut settings = Settings::load(file);
    let dto = settings
        .update(
            1,
            &SettingsPatch {
                theme: Some(ThemeChoice::Dark),
                ..SettingsPatch::default()
            },
        )
        .expect("theme is a legal change");
    assert_eq!(dto.theme, "dark");
    assert_eq!(dto.revision, 2);
    assert_eq!(dto.model, "fps");
    assert_eq!(dto.interface_scale_percent, 100);
}

#[test]
fn an_edit_made_against_a_stale_revision_is_refused() {
    let mut settings = Settings::load(FakeSettingsFile::new());
    settings
        .update(
            1,
            &SettingsPatch {
                reduce_motion: Some(true),
                ..SettingsPatch::default()
            },
        )
        .expect("first change lands");
    let stale = settings
        .update(
            1,
            &SettingsPatch {
                theme: Some(ThemeChoice::Light),
                ..SettingsPatch::default()
            },
        )
        .expect_err("a patch made against revision 1 is stale now");
    assert_eq!(code(&stale), ErrorCode::ProfileRevisionConflict.as_str());
    assert_eq!(settings.snapshot().theme, "system");
    assert_eq!(settings.revision(), 2);
}

// The whole point of the enum types: 137 is not a scale, and nothing in this
// crate can turn it into 125.
#[test]
fn an_illegal_interface_scale_cannot_even_be_built() {
    assert!(InterfaceScale::new(137).is_none());
    assert!(InterfaceScale::new(150).is_some());
}

#[test]
fn an_unknown_wire_value_is_refused_rather_than_defaulted() {
    assert_eq!(model_from_wire("fps"), Some(ModelChoice::Fps));
    assert_eq!(model_from_wire("FPS"), None);
    assert_eq!(model_from_wire("quadstick"), None);
    assert_eq!(theme_from_wire("dark"), Some(ThemeChoice::Dark));
    assert_eq!(theme_from_wire("Dark"), None);
    assert_eq!(grouping_from_wire("wide"), Some(PickerGrouping::Wide));
    assert_eq!(grouping_from_wire("tree"), None);
}

// A window that echoes its whole form back on every keystroke must not rewrite
// the file each time, and must not walk the revision away from itself.
#[test]
fn a_patch_that_changes_nothing_writes_nothing() {
    let mut settings = Settings::load(FakeSettingsFile::new());
    let before = settings.snapshot();
    let after = settings
        .update(
            1,
            &SettingsPatch {
                theme: Some(ThemeChoice::System),
                model: Some(ModelChoice::Fps),
                ..SettingsPatch::default()
            },
        )
        .expect("a no-op patch is not an error");
    assert_eq!(after, before);
    assert_eq!(after.revision, 1);
}

// The legacy `Settings.Save` swallowed a failed write, which left the window
// showing a setting the next launch would not have.
#[test]
fn a_setting_that_could_not_be_saved_is_not_reported_as_saved() {
    let store = FakeSettingsFile::new();
    store.fail_next_save();
    let mut settings = Settings::load(store);
    let failed = settings
        .update(
            1,
            &SettingsPatch {
                theme: Some(ThemeChoice::Dark),
                ..SettingsPatch::default()
            },
        )
        .expect_err("the write failed");
    assert_eq!(code(&failed), ErrorCode::StoragePermissionDenied.as_str());
    assert_eq!(settings.snapshot().theme, "system");
    assert_eq!(settings.revision(), 1);
}

#[test]
fn a_language_tag_survives_a_round_trip_through_the_file() {
    let file = FakeSettingsFile::new();
    let mut settings = Settings::load(file);
    let language = LanguageChoice::new("zh-Hans").expect("a shipped tag");
    let dto = settings
        .update(
            1,
            &SettingsPatch {
                language: Some(language),
                ..SettingsPatch::default()
            },
        )
        .expect("a plain tag is a legal change");
    assert_eq!(dto.language, "zh-Hans");
    let json = serde_json::to_string(settings.current()).expect("settings serialize");
    let back: AppSettings = serde_json::from_str(&json).expect("settings deserialize");
    assert_eq!(&back, settings.current());
}

// A file hand-edited to an illegal value must not load that value. Defaults are
// the only other answer, because there is no legal nearest one.
#[test]
fn a_hand_edited_file_with_an_illegal_value_does_not_load_it() {
    let poisoned = r#"{"model":"fps","theme":"dark","language":"en","interfaceScale":137}"#;
    assert!(serde_json::from_str::<AppSettings>(poisoned).is_err());
    let poisoned = r#"{"language":"/Users/bassam"}"#;
    assert!(serde_json::from_str::<AppSettings>(poisoned).is_err());
}
