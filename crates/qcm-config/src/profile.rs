//! Lossless profile container and device-safe serialization.
//!
//! This ports the serialization/normalization portion of the frozen C#
//! `ProfileFile`. Editor mutation, undo, dirty and revision semantics remain in
//! later rewrite tasks.

use crate::csv::{Grid, parse, write};
use crate::{Issue, ProfileDocument, parse_and_validate};

const HEADER_KEYWORD: &str = "QuadStick Configuration";
const KEYWORD_COLUMNS: usize = 10; // A..J

/// Parsed profile plus the lossless raw CSV grid.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProfileFile {
    pub grid: Grid,
    pub document: ProfileDocument,
    pub issues: Vec<Issue>,
    header_sheet_id: Option<String>,
}

impl ProfileFile {
    /// Load through the same two-step shape as the legacy implementation:
    /// parse to a raw grid, then reparse the grid's canonical CSV projection.
    #[must_use]
    pub fn load(csv_text: &str) -> Self {
        let mut profile = Self {
            grid: parse(csv_text),
            document: ProfileDocument::default(),
            issues: Vec::new(),
            header_sheet_id: None,
        };
        profile.reparse();
        profile
    }

    /// The Google Sheet id stamped into C1 on output only.
    #[must_use]
    pub fn header_sheet_id(&self) -> Option<&str> {
        self.header_sheet_id.as_deref()
    }

    /// Set output-only bookkeeping for C1. `None` leaves the raw header alone.
    pub fn set_header_sheet_id(&mut self, sheet_id: Option<String>) {
        self.header_sheet_id = sheet_id;
    }

    /// Serialize exactly as bytes sent to disk/device: stamp C1 when requested,
    /// trim A..J, flatten embedded newlines in every column, then emit CRLF CSV.
    #[must_use]
    pub fn to_csv_text(&self) -> String {
        let safe = self
            .stamped_grid()
            .into_iter()
            .map(|row| device_safe(&row))
            .collect::<Grid>();
        write(&safe)
    }

    /// Shape the raw grid the way the firmware expects it.
    ///
    /// Returns whether the raw grid changed. Editor dirty/revision/undo effects
    /// deliberately belong to TASK-016, not this format-only port.
    pub fn normalize_for_device_csv(&mut self) -> bool {
        let wrong_case = self.document.has_version_header && !self.header_cased_for_device();
        if self.document.has_version_header
            && !wrong_case
            && self.sheets_missing_separator().is_empty()
        {
            return false;
        }

        if !self.document.has_version_header {
            let source_name = self.document.csv_file_name().unwrap_or("config");
            let name = file_name_without_extension(source_name);
            self.grid.insert(
                0,
                vec![
                    HEADER_KEYWORD.to_owned(),
                    "Version 1.5".to_owned(),
                    String::new(),
                    name,
                ],
            );
            self.reparse();
        } else if wrong_case {
            let trimmed = self.grid[0][0].trim_start();
            let rest = &trimmed[HEADER_KEYWORD.len()..];
            self.grid[0][0] = format!("{HEADER_KEYWORD}{rest}");
        }

        let mut missing = self.sheets_missing_separator();
        missing.sort_unstable_by(|left, right| right.cmp(left));
        for row in missing {
            let preceding = row - 2;
            if self.grid[preceding]
                .iter()
                .all(|cell| cell.trim().is_empty())
            {
                self.grid[preceding] = Vec::new();
            } else {
                self.grid.insert(row - 1, Vec::new());
            }
        }

        self.reparse();
        true
    }

    fn reparse(&mut self) {
        let raw_csv = write(&self.grid);
        let (document, issues) = parse_and_validate(&raw_csv);
        self.document = document;
        self.issues = issues;
    }

    fn stamped_grid(&self) -> Grid {
        let Some(sheet_id) = self.header_sheet_id.as_deref() else {
            return self.grid.clone();
        };
        if self.grid.is_empty() || !self.document.has_version_header {
            return self.grid.clone();
        }

        let header = &self.grid[0];
        if header.get(2).is_some_and(|current| current == sheet_id) {
            return self.grid.clone();
        }

        let mut stamped = header.clone();
        if stamped.len() < 4 {
            stamped.resize(4, String::new());
        }
        stamped[2] = sheet_id.to_owned();

        let mut rows = self.grid.clone();
        rows[0] = stamped;
        rows
    }

    fn header_cased_for_device(&self) -> bool {
        self.grid
            .first()
            .and_then(|row| row.first())
            .is_some_and(|first| first.trim_start().starts_with(HEADER_KEYWORD))
    }

    /// One-based keyword rows of every later sheet lacking a true empty line.
    fn sheets_missing_separator(&self) -> Vec<usize> {
        self.document
            .sheets
            .iter()
            .skip(1)
            .map(|sheet| sheet.start_row)
            .filter(|row| *row >= 2 && self.grid[*row - 2].len() > 0)
            .collect()
    }
}

fn device_safe(row: &[String]) -> Vec<String> {
    row.iter()
        .enumerate()
        .map(|(column, value)| {
            let mut safe = if column < KEYWORD_COLUMNS {
                value.trim().to_owned()
            } else {
                value.clone()
            };
            if safe.contains(['\n', '\r']) {
                safe = safe
                    .split(|character| character == '\n' || character == '\r')
                    .filter(|part| !part.is_empty())
                    .map(str::trim)
                    .collect::<Vec<_>>()
                    .join(" ")
                    .trim()
                    .to_owned();
            }
            safe
        })
        .collect()
}

fn file_name_without_extension(name: &str) -> String {
    let file_name = name.rsplit(['/', '\\']).next().unwrap_or(name);
    file_name
        .rfind('.')
        .map_or_else(|| file_name.to_owned(), |dot| file_name[..dot].to_owned())
}

#[cfg(test)]
mod tests {
    use super::ProfileFile;

    #[test]
    fn normalization_is_idempotent_and_repairs_header_and_separator() {
        let mut profile = ProfileFile::load(
            "profile name,,One\nconfig.csv\nOutputs,Function,usb\nx,normal,lip\nProfile Name,,Two\n\nOutputs,Function,usb\n",
        );
        assert!(profile.normalize_for_device_csv());
        let once_grid = profile.grid.clone();
        let once_text = profile.to_csv_text();
        assert!(!profile.normalize_for_device_csv());
        assert_eq!(profile.grid, once_grid);
        assert_eq!(profile.to_csv_text(), once_text);
        assert!(once_text.starts_with("QuadStick Configuration,Version 1.5,,config\r\n"));
        assert!(once_text.contains("x,normal,lip\r\n\r\nProfile Name,,Two"));
    }

    #[test]
    fn device_safe_trims_only_a_through_j_but_flattens_newlines_everywhere() {
        let profile = ProfileFile::load(
            "QuadStick Configuration,Version 1.5,,Name\nProfile Name,,Mode\nfile.csv\nOutputs,Function,usb\n x , normal , lip ,,,,,,,,  note  ,Action,  opaque  \n",
        );
        let text = profile.to_csv_text();
        assert!(text.contains("x,normal,lip,,,,,,,,  note  ,Action,  opaque  \r\n"));

        let multiline = ProfileFile::load(
            "QuadStick Configuration,Version 1.5,,Name\nProfile Name,,Mode\nfile.csv\nOutputs,Function,usb\n\" x\n y \" ,normal,lip,,,,,,,,\" note\n  second \"\n",
        );
        let text = multiline.to_csv_text();
        assert!(text.contains("x y,normal,lip,,,,,,,,note second\r\n"));
    }

    #[test]
    fn sheet_id_is_output_only_and_only_stamps_an_existing_header() {
        let mut profile = ProfileFile::load(
            "QuadStick Configuration,Version 1.5,old,Name\nProfile Name,,Mode\nfile.csv\nOutputs,Function,usb\n",
        );
        let raw = profile.grid.clone();
        profile.set_header_sheet_id(Some("sheet-123".to_owned()));
        assert_eq!(profile.grid, raw);
        assert!(profile.to_csv_text().starts_with(
            "QuadStick Configuration,Version 1.5,sheet-123,Name\r\n"
        ));

        profile.set_header_sheet_id(None);
        assert!(profile
            .to_csv_text()
            .starts_with("QuadStick Configuration,Version 1.5,old,Name\r\n"));

        let mut headerless = ProfileFile::load(
            "Profile Name,,Mode\nfile.csv\nOutputs,Function,usb\n",
        );
        headerless.set_header_sheet_id(Some("sheet-123".to_owned()));
        assert!(headerless.to_csv_text().starts_with("Profile Name,,Mode\r\n"));
    }
}
