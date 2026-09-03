use serde::Serialize;
use serde_json::{Map, Value};
use std::collections::BTreeSet;

pub const PREFERENCES_BLOB_SHA: &str = "71914f9691d890d4ab8a6f32fc76d7e8005c8f68";
pub const PREFERENCES_JSON: &str =
    include_str!("../../../src/QuadStick.Format/Data/preferences.json");

pub const CATEGORY_ORDER: [&str; 9] = [
    "Joystick",
    "Sip and puff",
    "Lip sensor",
    "Mouse",
    "Sound and lights",
    "Bluetooth",
    "Inputs and outputs",
    "USB and compatibility",
    "Advanced",
];

const KNOWN_FIELDS: [&str; 17] = [
    "name",
    "label",
    "category",
    "editor",
    "default",
    "minimum",
    "maximum",
    "unit",
    "description",
    "options",
    "optionLabels",
    "modeOverride",
    "risk",
    "source",
    "firmwareMayAddMore",
    "alsoCalled",
    "advanced",
];

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub enum PreferenceEditor {
    Integer,
    Toggle,
    Choice,
    Text,
}

impl PreferenceEditor {
    pub const fn csharp_name(self) -> &'static str {
        match self {
            Self::Integer => "Integer",
            Self::Toggle => "Toggle",
            Self::Choice => "Choice",
            Self::Text => "Text",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct PreferenceDefinition {
    pub name: String,
    pub label: String,
    pub category: String,
    pub editor: PreferenceEditor,
    pub default: Option<String>,
    pub minimum: Option<i32>,
    pub maximum: Option<i32>,
    pub unit: String,
    pub description: String,
    pub options: Vec<String>,
    pub mode_override: bool,
    pub risk: String,
    pub source: String,
    pub option_labels: Vec<String>,
    pub firmware_may_add_more: bool,
    pub also_called: String,
    pub advanced: bool,
}

pub fn load_preferences() -> Result<Vec<PreferenceDefinition>, String> {
    let value: Value = serde_json::from_str(PREFERENCES_JSON)
        .map_err(|error| format!("preferences.json is invalid JSON: {error}"))?;
    let entries = value
        .as_array()
        .ok_or_else(|| "preferences.json must be a JSON array.".to_owned())?;

    let mut seen = BTreeSet::new();
    let mut definitions = Vec::with_capacity(entries.len());
    for entry in entries {
        let object = entry
            .as_object()
            .ok_or_else(|| "Each preference must be a JSON object.".to_owned())?;
        let definition = read_one(object)?;
        if !seen.insert(definition.name.clone()) {
            return Err(format!("Duplicate preference name '{}'.", definition.name));
        }
        definitions.push(definition);
    }
    Ok(definitions)
}

fn read_one(object: &Map<String, Value>) -> Result<PreferenceDefinition, String> {
    for field in object.keys() {
        if !KNOWN_FIELDS.contains(&field.as_str()) {
            return Err(format!("Unknown preference field '{field}'."));
        }
    }

    let name = required(object, "name", None)?;
    let label = required(object, "label", Some(&name))?;
    let category = required(object, "category", Some(&name))?;
    if !CATEGORY_ORDER.contains(&category.as_str()) {
        return Err(format!(
            "Preference '{name}' has unknown category '{category}'."
        ));
    }

    let editor_text = required(object, "editor", Some(&name))?;
    let editor = match editor_text.as_str() {
        "integer" => PreferenceEditor::Integer,
        "toggle" => PreferenceEditor::Toggle,
        "choice" => PreferenceEditor::Choice,
        "text" => PreferenceEditor::Text,
        _ => {
            return Err(format!(
                "Preference '{name}' has unknown editor '{editor_text}'."
            ));
        }
    };

    let source = required(object, "source", Some(&name))?;
    let unit = optional_text(object, "unit", &name)?;
    let description = optional_text(object, "description", &name)?;
    let risk = optional_text(object, "risk", &name)?;
    let default = optional_default(object, &name)?;
    let minimum = optional_i32(object, "minimum", &name)?;
    let maximum = optional_i32(object, "maximum", &name)?;
    let options = optional_strings(object, "options", &name)?;
    let option_labels = optional_strings(object, "optionLabels", &name)?;
    let mode_override = optional_bool(object, "modeOverride", &name)?;
    let firmware_may_add_more = optional_bool(object, "firmwareMayAddMore", &name)?;
    let also_called = optional_text(object, "alsoCalled", &name)?;
    let advanced = optional_bool(object, "advanced", &name)?;

    if editor != PreferenceEditor::Integer && (minimum.is_some() || maximum.is_some()) {
        return Err(format!(
            "Preference '{name}' is not an integer, so it cannot carry bounds."
        ));
    }
    if minimum.zip(maximum).is_some_and(|(min, max)| min > max) {
        return Err(format!("Preference '{name}' has minimum above maximum."));
    }
    if editor != PreferenceEditor::Choice && !options.is_empty() {
        return Err(format!(
            "Preference '{name}' is not a choice, so it cannot carry options."
        ));
    }
    if editor == PreferenceEditor::Choice && options.is_empty() {
        return Err(format!("Preference '{name}' is a choice with no options."));
    }
    if !option_labels.is_empty() && option_labels.len() != options.len() {
        return Err(format!(
            "Preference '{name}' has {} option labels for {} options.",
            option_labels.len(),
            options.len()
        ));
    }
    if firmware_may_add_more && editor != PreferenceEditor::Choice {
        return Err(format!(
            "Preference '{name}' is not a choice, so 'firmwareMayAddMore' means nothing on it."
        ));
    }

    if let Some(default_value) = default.as_deref() {
        match editor {
            PreferenceEditor::Integer => {
                let number = default_value.trim().parse::<i32>().map_err(|_| {
                    format!("Preference '{name}' has a default that is not a whole number.")
                })?;
                if minimum.is_some_and(|min| number < min)
                    || maximum.is_some_and(|max| number > max)
                {
                    return Err(format!(
                        "Preference '{name}' has a default outside its bounds."
                    ));
                }
            }
            PreferenceEditor::Toggle if default_value != "0" && default_value != "1" => {
                return Err(format!(
                    "Preference '{name}' is a toggle, so its default must be 0 or 1."
                ));
            }
            PreferenceEditor::Choice if !options.iter().any(|option| option == default_value) => {
                return Err(format!(
                    "Preference '{name}' has a default that is not one of its options."
                ));
            }
            _ => {}
        }
    }

    Ok(PreferenceDefinition {
        name,
        label,
        category,
        editor,
        default,
        minimum,
        maximum,
        unit,
        description,
        options,
        mode_override,
        risk,
        source,
        option_labels,
        firmware_may_add_more,
        also_called,
        advanced,
    })
}

fn required(
    object: &Map<String, Value>,
    field: &str,
    name: Option<&str>,
) -> Result<String, String> {
    let who = name
        .map(|name| format!("Preference '{name}'"))
        .unwrap_or_else(|| "A preference".to_owned());
    let value = object
        .get(field)
        .ok_or_else(|| format!("{who} is missing the '{field}' field."))?;
    let text = value
        .as_str()
        .ok_or_else(|| format!("{who} is missing the '{field}' field."))?;
    if text.is_empty() {
        return Err(format!("{who} has an empty '{field}' field."));
    }
    Ok(text.to_owned())
}

fn optional_text(object: &Map<String, Value>, field: &str, name: &str) -> Result<String, String> {
    let Some(value) = object.get(field) else {
        return Ok(String::new());
    };
    let text = value
        .as_str()
        .ok_or_else(|| format!("Preference '{name}' has a non-text '{field}' field."))?;
    if text.is_empty() {
        return Err(format!(
            "Preference '{name}' has an empty '{field}' field. Leave it out instead."
        ));
    }
    Ok(text.to_owned())
}

fn optional_default(object: &Map<String, Value>, name: &str) -> Result<Option<String>, String> {
    let Some(value) = object.get("default") else {
        return Ok(None);
    };
    value
        .as_str()
        .map(|text| Some(text.to_owned()))
        .ok_or_else(|| format!("Preference '{name}' has a non-text 'default' field."))
}

fn optional_i32(
    object: &Map<String, Value>,
    field: &str,
    name: &str,
) -> Result<Option<i32>, String> {
    let Some(value) = object.get(field) else {
        return Ok(None);
    };
    let number = value.as_i64().and_then(|number| i32::try_from(number).ok());
    number.map(Some).ok_or_else(|| {
        format!("Preference '{name}' has a '{field}' field that is not a whole number.")
    })
}

fn optional_bool(object: &Map<String, Value>, field: &str, name: &str) -> Result<bool, String> {
    let Some(value) = object.get(field) else {
        return Ok(false);
    };
    value
        .as_bool()
        .ok_or_else(|| format!("Preference '{name}' has a non-boolean '{field}' field."))
}

fn optional_strings(
    object: &Map<String, Value>,
    field: &str,
    name: &str,
) -> Result<Vec<String>, String> {
    let Some(value) = object.get(field) else {
        return Ok(Vec::new());
    };
    let values = value.as_array().ok_or_else(|| {
        format!("Preference '{name}' has a '{field}' field that is not an array.")
    })?;
    let mut seen = BTreeSet::new();
    let mut result = Vec::with_capacity(values.len());
    for value in values {
        let token = value
            .as_str()
            .ok_or_else(|| format!("Preference '{name}' has a non-text entry in '{field}'."))?;
        if token.is_empty() {
            return Err(format!(
                "Preference '{name}' has an empty entry in '{field}'."
            ));
        }
        if !seen.insert(token.to_owned()) {
            return Err(format!(
                "Preference '{name}' repeats '{token}' in '{field}'."
            ));
        }
        result.push(token.to_owned());
    }
    Ok(result)
}
