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
