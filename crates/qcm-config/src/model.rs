//! Parsed QuadStick profile projection.
//!
//! The raw CSV grid remains separate. These types mirror the frozen C# model
//! and deliberately preserve its nullable/empty distinctions and one-based row
//! numbering.

/// Kind of exported QuadStick sheet.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum SheetType {
    ProfileName,
    Preferences,
    Infrared,
}

/// One binding row from a profile sheet.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Binding {
    /// One-based row number in the device-facing grid.
    pub row: usize,
    pub output: String,
    pub function: String,
    /// Filled input values, in sheet order.
    pub inputs: Vec<String>,
    /// Real zero-based grid columns corresponding to `inputs` (C..J may gap).
    pub input_cols: Vec<usize>,
    /// Profile-owned friendly action name from column L.
    pub action_name: String,
}

impl Binding {
    #[must_use]
    pub fn new(
        row: usize,
        output: impl Into<String>,
        function: impl Into<String>,
        inputs: Vec<String>,
        input_cols: Vec<usize>,
    ) -> Self {
        Self {
            row,
            output: output.into(),
            function: function.into(),
            inputs,
            input_cols,
            action_name: String::new(),
        }
    }
}

/// One Profile/Preferences/Infrared section.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModeSheet {
    pub sheet_type: SheetType,
    pub mode_name: String,
    /// Present only where the legacy projection exposes a filename; empty and
    /// absent are intentionally different.
    pub csv_file_name: Option<String>,
    pub header_label: String,
    pub channel: String,
    /// One-based row of the sheet keyword/header row.
    pub start_row: usize,
    pub bindings: Vec<Binding>,
}

impl ModeSheet {
    #[must_use]
    pub fn new(sheet_type: SheetType) -> Self {
        Self {
            sheet_type,
            mode_name: String::new(),
            csv_file_name: None,
            header_label: String::new(),
            channel: String::new(),
            start_row: 0,
            bindings: Vec::new(),
        }
    }
}

/// Parsed projection of a complete profile file.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ProfileDocument {
    pub sheets: Vec<ModeSheet>,
    pub has_version_header: bool,
    pub header_version: String,
    pub header_source: String,
    pub header_name: String,
}

impl ProfileDocument {
    /// Filename carried by the first sheet, preserving absent versus empty.
    #[must_use]
    pub fn csv_file_name(&self) -> Option<&str> {
        self.sheets
            .first()
            .and_then(|sheet| sheet.csv_file_name.as_deref())
    }

    /// One-based row where the first-sheet filename is stored.
    #[must_use]
    pub fn file_name_cell_row(&self) -> usize {
        self.sheets.first().map_or(2, |sheet| sheet.start_row + 1)
    }

    /// Human title: version-header name first, otherwise first profile mode.
    #[must_use]
    pub fn title(&self) -> &str {
        if !self.header_name.is_empty() {
            return &self.header_name;
        }
        self.sheets
            .iter()
            .find(|sheet| sheet.sheet_type == SheetType::ProfileName)
            .map_or("", |sheet| sheet.mode_name.as_str())
    }

    #[must_use]
    pub fn is_default_config(&self) -> bool {
        self.csv_file_name()
            .is_some_and(|name| name.eq_ignore_ascii_case("default.csv"))
    }

    #[must_use]
    pub fn is_device_preferences(&self) -> bool {
        self.csv_file_name()
            .is_some_and(|name| name.eq_ignore_ascii_case("prefs.csv"))
    }
}

#[cfg(test)]
mod tests {
    use super::{Binding, ModeSheet, ProfileDocument, SheetType};

    #[test]
    fn absent_filename_and_empty_filename_are_distinct() {
        let mut document = ProfileDocument::default();
        assert_eq!(document.csv_file_name(), None);
        assert_eq!(document.file_name_cell_row(), 2);

        let mut sheet = ModeSheet::new(SheetType::ProfileName);
        sheet.start_row = 4;
        sheet.csv_file_name = Some(String::new());
        document.sheets.push(sheet);
        assert_eq!(document.csv_file_name(), Some(""));
        assert_eq!(document.file_name_cell_row(), 5);
    }

    #[test]
    fn title_and_device_file_flags_match_legacy_rules() {
        let mut first = ModeSheet::new(SheetType::ProfileName);
        first.mode_name = "Fallback mode".into();
        first.csv_file_name = Some("DEFAULT.CSV".into());
        first.start_row = 1;

        let mut document = ProfileDocument {
            sheets: vec![first],
            ..ProfileDocument::default()
        };
        assert_eq!(document.title(), "Fallback mode");
        assert!(document.is_default_config());
        assert!(!document.is_device_preferences());

        document.header_name = "Header title".into();
        assert_eq!(document.title(), "Header title");
        document.sheets[0].csv_file_name = Some("Prefs.CsV".into());
        assert!(document.is_device_preferences());
    }

    #[test]
    fn binding_keeps_real_input_columns_and_one_based_row() {
        let binding = Binding::new(
            19,
            "mouse_left",
            "normal",
            vec!["lip".into(), "mp_left".into()],
            vec![2, 5],
        );
        assert_eq!(binding.row, 19);
        assert_eq!(binding.inputs, ["lip", "mp_left"]);
        assert_eq!(binding.input_cols, [2, 5]);
        assert!(binding.action_name.is_empty());
    }
}
