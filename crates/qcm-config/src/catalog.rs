use crate::preferences::load_preferences;
use crate::vocab::{
    CHANNELS, LEGACY_INPUTS, LEGACY_OUTPUTS, default_template, function_arity,
    functions_in_firmware_order, known_outputs, load_validation, preference_overrides,
};
use serde_json::{Value, json};
use std::collections::BTreeSet;

const SCHEMA_VERSION: &str = "qcm-parity-1";
const LEGACY_BASE: &str = "f7783944387202bcafaeb7ff3f67789098fa6a4e";

pub fn canonical_catalog() -> Result<Value, String> {
    let validation = load_validation()?;
    let preferences = load_preferences()?;
    let functions = functions_in_firmware_order(&validation);

    let function_arities = functions
        .iter()
        .map(|name| {
            let (min, max) = function_arity(name)
                .ok_or_else(|| format!("missing function arity for '{name}'"))?;
            Ok(json!({ "name": name, "min": min, "max": max }))
        })
        .collect::<Result<Vec<_>, String>>()?;

    let preference_values = preferences
        .iter()
        .enumerate()
        .map(|(index, preference)| {
            json!({
                "index": index,
                "name": preference.name,
                "label": preference.label,
                "category": preference.category,
                "editor": preference.editor.csharp_name(),
                "defaultValue": preference.default,
                "minimum": preference.minimum,
                "maximum": preference.maximum,
                "unit": preference.unit,
                "description": preference.description,
                "options": preference.options,
                "modeOverride": preference.mode_override,
                "risk": preference.risk,
                "source": preference.source,
                "optionLabels": preference.option_labels,
                "firmwareMayAddMore": preference.firmware_may_add_more,
                "alsoCalled": preference.also_called,
            })
        })
        .collect::<Vec<_>>();

    Ok(json!({
        "schemaVersion": SCHEMA_VERSION,
        "legacyBase": LEGACY_BASE,
        "command": "catalog-canonical",
        "vocab": {
            "inputs": sorted_set(&validation.inputs),
            "outputsPs3": sorted_set(&validation.outputs_ps3),
            "outputsXbox": sorted_set(&validation.outputs_xbox),
            "knownOutputs": known_outputs(&validation).into_iter().collect::<Vec<_>>(),
            "functionsInFirmwareOrder": functions,
            "functionArity": function_arities,
            "preferenceOverrides": preference_overrides(&preferences).into_iter().collect::<Vec<_>>(),
            "legacyInputs": sorted_static(&LEGACY_INPUTS),
            "legacyOutputs": sorted_static(&LEGACY_OUTPUTS),
            "channels": sorted_static(&CHANNELS),
        },
        "preferences": preference_values,
        "defaultTemplate": default_template(),
    }))
}

fn sorted_set(values: &[String]) -> Vec<String> {
    values
        .iter()
        .cloned()
        .collect::<BTreeSet<_>>()
        .into_iter()
        .collect()
}

fn sorted_static<const N: usize>(values: &[&str; N]) -> Vec<String> {
    values
        .iter()
        .map(|value| (*value).to_owned())
        .collect::<BTreeSet<_>>()
        .into_iter()
        .collect()
}
