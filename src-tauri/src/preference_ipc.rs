//! Typed device-preference metadata exposed to the WebView.
//!
//! The catalog remains native-owned and comes from the same audited
//! `preferences.json` the Rust validator uses.

use qcm_config::{PreferenceDefinition, PreferenceEditor, load_preferences};
use serde::Serialize;

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PreferenceCatalogDto {
    pub categories: Vec<String>,
    pub definitions: Vec<PreferenceDefinitionDto>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PreferenceDefinitionDto {
    pub name: String,
    pub label: String,
    pub category: String,
    pub editor: String,
    pub default: Option<String>,
    pub minimum: Option<i32>,
    pub maximum: Option<i32>,
    pub unit: String,
    pub description: String,
    pub options: Vec<PreferenceOptionDto>,
    pub mode_override: bool,
    pub risk: String,
    pub source: String,
    pub firmware_may_add_more: bool,
    pub also_called: String,
    pub advanced: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PreferenceOptionDto {
    pub value: String,
    pub label: String,
}

impl From<PreferenceDefinition> for PreferenceDefinitionDto {
    fn from(definition: PreferenceDefinition) -> Self {
        let labels = definition.option_labels.clone();
        let options = definition
            .options
            .iter()
            .enumerate()
            .map(|(index, value)| PreferenceOptionDto {
                value: value.clone(),
                label: labels.get(index).cloned().unwrap_or_else(|| value.clone()),
            })
            .collect();
        Self {
            name: definition.name,
            label: definition.label,
            category: definition.category,
            editor: match definition.editor {
                PreferenceEditor::Integer => "integer",
                PreferenceEditor::Toggle => "toggle",
                PreferenceEditor::Choice => "choice",
                PreferenceEditor::Text => "text",
            }
            .to_owned(),
            default: definition.default,
            minimum: definition.minimum,
            maximum: definition.maximum,
            unit: definition.unit,
            description: definition.description,
            options,
            mode_override: definition.mode_override,
            risk: definition.risk,
            source: definition.source,
            firmware_may_add_more: definition.firmware_may_add_more,
            also_called: definition.also_called,
            advanced: definition.advanced,
        }
    }
}

#[must_use]
pub fn preference_catalog() -> PreferenceCatalogDto {
    let definitions = load_preferences()
        .expect("embedded preferences.json is validated by the parity gate")
        .into_iter()
        .map(PreferenceDefinitionDto::from)
        .collect();
    PreferenceCatalogDto {
        categories: qcm_config::preferences::CATEGORY_ORDER
            .iter()
            .map(|category| (*category).to_owned())
            .collect(),
        definitions,
    }
}

#[cfg(test)]
mod tests {
    use super::preference_catalog;

    #[test]
    fn catalog_is_native_owned_and_typed() {
        let catalog = preference_catalog();
        assert_eq!(catalog.categories.len(), 9);
        assert!(catalog.definitions.len() >= 50);
        assert!(catalog.definitions.iter().any(|definition| {
            definition.editor == "integer"
                && definition.minimum.is_some()
                && definition.maximum.is_some()
        }));
        assert!(
            catalog
                .definitions
                .iter()
                .any(|definition| definition.editor == "toggle")
        );
        assert!(
            catalog.definitions.iter().any(|definition| {
                definition.editor == "choice" && !definition.options.is_empty()
            })
        );
        assert!(
            catalog
                .definitions
                .iter()
                .any(|definition| definition.editor == "text")
        );
    }
}
