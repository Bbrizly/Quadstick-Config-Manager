use crate::model::SheetType;
use crate::preferences::PreferenceDefinition;
use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;

pub const VALIDATION_BLOB_SHA: &str = "dd793c1cd768f703dd3b6255c990f2e1c8ed2332";
pub const TEMPLATE_BLOB_SHA: &str = "21f23998d00de044a9bd1bc0899809c3f95a49b8";
pub const FIRMWARE_2373_BLOB_SHA: &str = "7f90d32e6efa819c8eacbfa0eab9184bfccb509b";

const VALIDATION_JSON: &str = include_str!("../../../src/QuadStick.Format/Data/validation.json");
const DEFAULT_TEMPLATE: &str =
    include_str!("../../../src/QuadStick.Format/Templates/default-template.csv");

pub const LEGACY_INPUTS: [&str; 5] = [
    "push",
    "lip_soft",
    "right_sip_long",
    "right_puff_long",
    "bluetooth_status",
];
pub const LEGACY_OUTPUTS: [&str; 2] = ["gyroscope_cw", "gyroscope_ccw"];
pub const CHANNELS: [&str; 4] = ["none", "usb", "bluetooth", "both"];
pub const NONE_INPUT: &str = "none";
pub const FILE_HEADER_KEYWORD: &str = "QuadStick Configuration";

pub const FIRMWARE_FUNCTION_ORDER: [&str; 14] = [
    "normal",
    "toggle",
    "repeat",
    "pulse",
    "duty",
    "greater_than",
    "less_than",
    "force_off",
    "delayed_latch",
    "delay_off",
    "delay_on",
    "tap",
    "increment_value",
    "decrement_value",
];

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ValidationCatalog {
    pub inputs: Vec<String>,
    pub outputs_ps3: Vec<String>,
    pub outputs_xbox: Vec<String>,
    pub functions: Vec<String>,
}

pub fn load_validation() -> Result<ValidationCatalog, String> {
    serde_json::from_str(VALIDATION_JSON)
        .map_err(|error| format!("validation.json is invalid: {error}"))
}

pub fn default_template() -> &'static str {
    DEFAULT_TEMPLATE
}

pub fn known_outputs(validation: &ValidationCatalog) -> BTreeSet<String> {
    validation
        .outputs_ps3
        .iter()
        .chain(&validation.outputs_xbox)
        .cloned()
        .collect()
}

pub fn functions_in_firmware_order(validation: &ValidationCatalog) -> Vec<String> {
    FIRMWARE_FUNCTION_ORDER
        .iter()
        .filter(|name| {
            validation
                .functions
                .iter()
                .any(|function| function == **name)
        })
        .map(|name| (*name).to_owned())
        .collect()
}

pub fn function_arity(function: &str) -> Option<(usize, usize)> {
    match function {
        "normal" | "toggle" => Some((0, 0)),
        "duty" | "less_than" | "force_off" | "delayed_latch" | "delay_off" => Some((0, 1)),
        "repeat" | "pulse" | "greater_than" | "delay_on" | "tap" | "increment_value"
        | "decrement_value" => Some((0, 2)),
        _ => None,
    }
}

pub fn preference_overrides(preferences: &[PreferenceDefinition]) -> BTreeSet<String> {
    preferences
        .iter()
        .filter(|preference| preference.mode_override)
        .map(|preference| preference.name.clone())
        .collect()
}

#[must_use]
pub fn is_sheet_keyword(a1: &str) -> bool {
    contains_ascii_ignore_case(a1, "Profile")
        || a1.trim().eq_ignore_ascii_case("Preferences")
        || a1.trim().eq_ignore_ascii_case("Infrared")
}

#[must_use]
pub fn is_file_header(a1: &str) -> bool {
    starts_with_ascii_ignore_case(a1.trim_start(), FILE_HEADER_KEYWORD)
}

#[must_use]
pub fn firmware_accepts_sheet_keyword(raw_a1: &str) -> bool {
    raw_a1.starts_with("Profile")
        || raw_a1.starts_with("Preferences")
        || raw_a1.starts_with("Infrared")
}

#[must_use]
pub fn keyword_to_type(a1: &str) -> SheetType {
    if contains_ascii_ignore_case(a1, "Profile") {
        SheetType::ProfileName
    } else if a1.trim().eq_ignore_ascii_case("Preferences") {
        SheetType::Preferences
    } else {
        SheetType::Infrared
    }
}

fn contains_ascii_ignore_case(haystack: &str, needle: &str) -> bool {
    haystack
        .as_bytes()
        .windows(needle.len())
        .any(|window| window.eq_ignore_ascii_case(needle.as_bytes()))
}

fn starts_with_ascii_ignore_case(value: &str, prefix: &str) -> bool {
    value
        .as_bytes()
        .get(..prefix.len())
        .is_some_and(|start| start.eq_ignore_ascii_case(prefix.as_bytes()))
}

#[cfg(test)]
mod tests {
    use super::{
        firmware_accepts_sheet_keyword, is_file_header, is_sheet_keyword, keyword_to_type,
    };
    use crate::model::SheetType;

    #[test]
    fn converter_and_firmware_keyword_rules_stay_separate() {
        assert!(is_sheet_keyword("profile name"));
        assert!(!firmware_accepts_sheet_keyword("profile name"));
        assert!(is_sheet_keyword("GTA Profile"));
        assert!(!firmware_accepts_sheet_keyword("GTA Profile"));
        assert!(firmware_accepts_sheet_keyword("Profile Name"));
        assert!(firmware_accepts_sheet_keyword("Infrared,Samsung"));
    }

    #[test]
    fn file_header_and_sheet_type_match_legacy_helpers() {
        assert!(is_file_header("  quadstick configuration"));
        assert_eq!(keyword_to_type("My Profile"), SheetType::ProfileName);
        assert_eq!(keyword_to_type(" preferences "), SheetType::Preferences);
        assert_eq!(keyword_to_type("Infrared"), SheetType::Infrared);
    }
}
