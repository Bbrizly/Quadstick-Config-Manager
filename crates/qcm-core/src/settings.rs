//! Application settings: the typed values, the revision, and the rule that
//! nothing is rounded on the user's behalf.
//!
//! Every setting here is a closed set or a shape, never a free number the app
//! narrows down later. A request carrying 137 for the interface scale is
//! refused with the field named, not turned into 125. The legacy app could get
//! away with clamping because a slider produced the value; a command can be
//! called by anything, and a setting quietly changed is the failure this whole
//! codebase is written against.
//!
//! Persistence is a port. The revision lives here, the file lives in the
//! adapter, and the order is deliberate: bytes reach disk before the value in
//! memory moves, so a save that failed cannot leave the window showing a
//! setting the app will not have next launch.

use crate::error::{ProfileError, QcmError, RequestError, StorageError};
use serde::{Deserialize, Serialize};

/// Which QuadStick the visualizer draws. The names are the legacy `QsModel`
/// enum; the value is a key, so it is not translated.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ModelChoice {
    Fps,
    Original,
    Singleton,
}

/// How deep the output and input pickers file their choices.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum PickerGrouping {
    Detailed,
    Wide,
    Flat,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ThemeChoice {
    System,
    Light,
    Dark,
}

/// The four sizes the legacy app offered, and no fifth.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(into = "u16", try_from = "u16")]
pub struct InterfaceScale(u16);

impl InterfaceScale {
    pub const ALLOWED: [u16; 4] = [100, 125, 150, 200];

    /// `None` for anything not in [`InterfaceScale::ALLOWED`]. There is no
    /// nearest-legal-value constructor on purpose.
    #[must_use]
    pub fn new(percent: u16) -> Option<Self> {
        Self::ALLOWED.contains(&percent).then_some(Self(percent))
    }

    #[must_use]
    pub const fn percent(self) -> u16 {
        self.0
    }
}

impl Default for InterfaceScale {
    fn default() -> Self {
        Self(100)
    }
}

impl From<InterfaceScale> for u16 {
    fn from(scale: InterfaceScale) -> Self {
        scale.0
    }
}

impl TryFrom<u16> for InterfaceScale {
    type Error = &'static str;

    fn try_from(percent: u16) -> Result<Self, Self::Error> {
        Self::new(percent).ok_or("interface scale is not one of 100, 125, 150 or 200")
    }
}

/// Which language the interface speaks, or the machine's own.
///
/// Shape, not membership. TASK-037 owns the catalog of shipped tags, and a tag
/// this build does not have falls back to English the way the legacy app did.
/// What is enforced here is that the value is a language tag at all, so a
/// settings file cannot carry a sentence, a path or a script into the UI.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(into = "String", try_from = "String")]
pub struct LanguageChoice(String);

impl LanguageChoice {
    /// Follow the machine. The legacy `Localization.FollowSystem`.
    pub const SYSTEM: &'static str = "system";
    const MAX: usize = 16;

    #[must_use]
    pub fn follow_system() -> Self {
        Self(Self::SYSTEM.to_owned())
    }

    /// `None` for anything that is not `system` or a plain BCP-47 style tag.
    #[must_use]
    pub fn new(tag: &str) -> Option<Self> {
        if tag.is_empty() || tag.len() > Self::MAX {
            return None;
        }
        let shaped = tag.chars().all(|c| c.is_ascii_alphanumeric() || c == '-')
            && tag.starts_with(|c: char| c.is_ascii_alphabetic())
            && !tag.ends_with('-');
        shaped.then(|| Self(tag.to_owned()))
    }

    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }

    #[must_use]
    pub fn follows_system(&self) -> bool {
        self.0 == Self::SYSTEM
    }
}

impl Default for LanguageChoice {
    fn default() -> Self {
        Self::follow_system()
    }
}

impl From<LanguageChoice> for String {
    fn from(language: LanguageChoice) -> Self {
        language.0
    }
}

impl TryFrom<String> for LanguageChoice {
    type Error = &'static str;

    fn try_from(tag: String) -> Result<Self, Self::Error> {
        Self::new(&tag).ok_or("language is not a plain tag")
    }
}

/// Everything the app remembers between launches that a person chose.
///
/// Deliberately smaller than the legacy `AppSettings`. Window geometry belongs
/// to TASK-036, the Drive links and recents to TASK-045, and the telemetry
/// consent to TASK-046. Each arrives with the feature that reads it rather than
/// sitting here as a field nothing honours.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct AppSettings {
    pub model: ModelChoice,
    pub theme: ThemeChoice,
    pub language: LanguageChoice,
    pub interface_scale: InterfaceScale,
    pub reduce_motion: bool,
    pub remember_window: bool,
    pub device_cards: bool,
    pub picker_grouping: PickerGrouping,
    pub tutorial_seen: bool,
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            model: ModelChoice::Fps,
            theme: ThemeChoice::System,
            language: LanguageChoice::follow_system(),
            interface_scale: InterfaceScale::default(),
            reduce_motion: false,
            remember_window: true,
            device_cards: true,
            picker_grouping: PickerGrouping::Detailed,
            tutorial_seen: false,
        }
    }
}

/// The settings a window is shown, with the revision it has to send back.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppSettingsDto {
    pub revision: u64,
    pub model: String,
    pub theme: String,
    pub language: String,
    pub interface_scale_percent: u16,
    pub reduce_motion: bool,
    pub remember_window: bool,
    pub device_cards: bool,
    pub picker_grouping: String,
    pub tutorial_seen: bool,
}

/// A change to some settings and not the others.
///
/// Every field is optional and every one is already typed: turning the wire's
/// strings into these is the command layer's job, and it is where an unknown
/// value becomes [`RequestError::OutOfRange`] instead of a default.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct SettingsPatch {
    pub model: Option<ModelChoice>,
    pub theme: Option<ThemeChoice>,
    pub language: Option<LanguageChoice>,
    pub interface_scale: Option<InterfaceScale>,
    pub reduce_motion: Option<bool>,
    pub remember_window: Option<bool>,
    pub device_cards: Option<bool>,
    pub picker_grouping: Option<PickerGrouping>,
    pub tutorial_seen: Option<bool>,
}

impl SettingsPatch {
    fn onto(&self, base: &AppSettings) -> AppSettings {
        let mut next = base.clone();
        if let Some(model) = self.model {
            next.model = model;
        }
        if let Some(theme) = self.theme {
            next.theme = theme;
        }
        if let Some(language) = &self.language {
            next.language = language.clone();
        }
        if let Some(scale) = self.interface_scale {
            next.interface_scale = scale;
        }
        if let Some(reduce_motion) = self.reduce_motion {
            next.reduce_motion = reduce_motion;
        }
        if let Some(remember_window) = self.remember_window {
            next.remember_window = remember_window;
        }
        if let Some(device_cards) = self.device_cards {
            next.device_cards = device_cards;
        }
        if let Some(grouping) = self.picker_grouping {
            next.picker_grouping = grouping;
        }
        if let Some(tutorial_seen) = self.tutorial_seen {
            next.tutorial_seen = tutorial_seen;
        }
        next
    }
}

/// Where settings are kept between launches.
///
/// No path crosses this, the same as every other port: the adapter knows where
/// the file is and nothing above it does.
pub trait SettingsStore {
    /// `None` when nothing has been saved yet, and also when what is there
    /// cannot be read. Settings are a convenience, so an unreadable file starts
    /// the app at defaults rather than refusing to open, which is what the
    /// legacy `Settings.Load` did.
    fn load(&self) -> Option<AppSettings>;

    /// Write the settings. Unlike the legacy `Settings.Save`, a failure is
    /// returned rather than swallowed: the value in memory does not move until
    /// this has succeeded, so a window is never showing a setting that will not
    /// survive a restart.
    fn save(&self, settings: &AppSettings) -> Result<(), StorageError>;
}

/// The settings the app is running on, and the only thing allowed to change
/// them.
#[derive(Debug)]
pub struct Settings<S: SettingsStore> {
    store: S,
    current: AppSettings,
    revision: u64,
}

impl<S: SettingsStore> Settings<S> {
    /// Read what was saved, or start at defaults.
    #[must_use]
    pub fn load(store: S) -> Self {
        let current = store.load().unwrap_or_default();
        Self {
            store,
            current,
            revision: 1,
        }
    }

    #[must_use]
    pub const fn revision(&self) -> u64 {
        self.revision
    }

    #[must_use]
    pub const fn current(&self) -> &AppSettings {
        &self.current
    }

    #[must_use]
    pub fn snapshot(&self) -> AppSettingsDto {
        AppSettingsDto {
            revision: self.revision,
            model: model_wire(self.current.model).to_owned(),
            theme: theme_wire(self.current.theme).to_owned(),
            language: self.current.language.as_str().to_owned(),
            interface_scale_percent: self.current.interface_scale.percent(),
            reduce_motion: self.current.reduce_motion,
            remember_window: self.current.remember_window,
            device_cards: self.current.device_cards,
            picker_grouping: grouping_wire(self.current.picker_grouping).to_owned(),
            tutorial_seen: self.current.tutorial_seen,
        }
    }

    /// Apply a patch made against `expected_revision`.
    ///
    /// A patch that changes nothing writes nothing and leaves the revision
    /// alone, so a window that echoes its whole form back on every keystroke
    /// does not rewrite the file each time.
    pub fn update(
        &mut self,
        expected_revision: u64,
        patch: &SettingsPatch,
    ) -> Result<AppSettingsDto, QcmError> {
        if expected_revision != self.revision {
            return Err(ProfileError::RevisionConflict {
                expected: expected_revision,
                actual: self.revision,
            }
            .into());
        }
        let candidate = patch.onto(&self.current);
        if candidate == self.current {
            return Ok(self.snapshot());
        }
        self.store.save(&candidate)?;
        self.current = candidate;
        self.revision = self.revision.saturating_add(1);
        Ok(self.snapshot())
    }
}

/// Read one of the model keys a window sends. `None` is a refused request.
#[must_use]
pub fn model_from_wire(value: &str) -> Option<ModelChoice> {
    match value {
        "fps" => Some(ModelChoice::Fps),
        "original" => Some(ModelChoice::Original),
        "singleton" => Some(ModelChoice::Singleton),
        _ => None,
    }
}

#[must_use]
pub const fn model_wire(model: ModelChoice) -> &'static str {
    match model {
        ModelChoice::Fps => "fps",
        ModelChoice::Original => "original",
        ModelChoice::Singleton => "singleton",
    }
}

#[must_use]
pub fn theme_from_wire(value: &str) -> Option<ThemeChoice> {
    match value {
        "system" => Some(ThemeChoice::System),
        "light" => Some(ThemeChoice::Light),
        "dark" => Some(ThemeChoice::Dark),
        _ => None,
    }
}

#[must_use]
pub const fn theme_wire(theme: ThemeChoice) -> &'static str {
    match theme {
        ThemeChoice::System => "system",
        ThemeChoice::Light => "light",
        ThemeChoice::Dark => "dark",
    }
}

#[must_use]
pub fn grouping_from_wire(value: &str) -> Option<PickerGrouping> {
    match value {
        "detailed" => Some(PickerGrouping::Detailed),
        "wide" => Some(PickerGrouping::Wide),
        "flat" => Some(PickerGrouping::Flat),
        _ => None,
    }
}

#[must_use]
pub const fn grouping_wire(grouping: PickerGrouping) -> &'static str {
    match grouping {
        PickerGrouping::Detailed => "detailed",
        PickerGrouping::Wide => "wide",
        PickerGrouping::Flat => "flat",
    }
}

/// The refusal a bad settings value earns, with the field named and the value
/// left out. The value came from outside; the field name did not.
#[must_use]
pub const fn out_of_range(what: &'static str) -> QcmError {
    QcmError::Request(RequestError::OutOfRange { what })
}

#[cfg(test)]
mod tests {
    use super::{InterfaceScale, LanguageChoice};

    #[test]
    fn an_interface_scale_is_refused_rather_than_rounded() {
        assert_eq!(
            InterfaceScale::new(125).map(InterfaceScale::percent),
            Some(125)
        );
        for rejected in [0, 99, 101, 137, 175, 201, u16::MAX] {
            assert!(InterfaceScale::new(rejected).is_none(), "{rejected}");
        }
    }

    #[test]
    fn a_language_has_to_look_like_a_tag() {
        for accepted in ["system", "en", "zh-Hans", "qps-ploc", "pt"] {
            assert!(LanguageChoice::new(accepted).is_some(), "{accepted}");
        }
        for rejected in [
            "",
            "-en",
            "en-",
            "en_US",
            "/Users/b",
            "<script>",
            "a-very-long-language-tag",
        ] {
            assert!(LanguageChoice::new(rejected).is_none(), "{rejected}");
        }
    }
}
