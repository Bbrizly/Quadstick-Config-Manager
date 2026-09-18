use crate::csv::{Grid, parse, write};
use crate::issue::{Issue, Severity};
use crate::model::{Binding, ModeSheet, ProfileDocument, SheetType};
use crate::vocab::{
    firmware_accepts_sheet_keyword, is_file_header, is_sheet_keyword, keyword_to_type,
};
use std::collections::BTreeSet;

const MAX_INPUT_COLUMNS: usize = 8; // C..J
const KEYWORD_COLUMNS: usize = 2 + MAX_INPUT_COLUMNS; // A..J
const ACTION_COLUMN: usize = 11; // L
const MAX_KEYWORD_LENGTH: usize = 64; // firmware accepts at most 63 UTF-16 code units
const MAX_LINE_BYTES: usize = 1023; // 1024-byte line buffer reserves one byte

/// Parse profile structure while preserving the frozen parser's issue ordering.
#[must_use]
pub fn parse_with_issues(csv_text: &str) -> (ProfileDocument, Vec<Issue>) {
    let grid = parse(csv_text);
    let mut document = ProfileDocument::default();
    let mut issues = Vec::new();

    let mut scan_from = 0;
    if is_file_header(cell(&grid, 0, 0)) {
        document.has_version_header = true;
        document.header_version = cell(&grid, 0, 1).trim().to_owned();
        document.header_source = cell(&grid, 0, 2).trim().to_owned();
        document.header_name = cell(&grid, 0, 3).trim().to_owned();
        scan_from = 1;
    }

    let starts = find_section_starts(&grid, scan_from);
    if starts.is_empty() {
        issues.push(Issue::new(
            Severity::Error,
            "A1",
            "The first device section could not be found.",
            "Start the file with a Profile, Preferences, or Infrared sheet.",
        ));
        return (document, issues);
    }
    if starts[0] != scan_from {
        issues.push(Issue::new(
            Severity::Warning,
            format!("A{}", scan_from + 1),
            "Rows before the first device sheet are ignored.",
            "Remove or move those rows.",
        ));
    }

    for (index, start) in starts.iter().copied().enumerate() {
        let end = starts.get(index + 1).copied().unwrap_or(grid.len());
        document
            .sheets
            .push(parse_sheet(&grid, start, end, index == 0, &mut issues));

        if !firmware_accepts_sheet_keyword(cell(&grid, start, 0)) {
            issues.push(Issue::new(
                Severity::Error,
                format!("A{}", start + 1),
                "The sheet keyword is accepted by the converter but skipped by firmware.",
                "Begin the raw cell with the exact firmware sheet keyword.",
            ));
        }
    }

    check_device_line_limits(&grid, &starts, &mut issues);
    (document, issues)
}

/// Parse only the document projection.
#[must_use]
pub fn parse_structure(csv_text: &str) -> ProfileDocument {
    parse_with_issues(csv_text).0
}

fn find_section_starts(grid: &Grid, scan_from: usize) -> Vec<usize> {
    let mut starts = Vec::new();
    let mut inside_sheet = false;
    let mut sheet_start = 0;

    for row in scan_from..grid.len() {
        if inside_sheet {
            if row <= sheet_start + 2 {
                continue;
            }
            if is_blank_line(&grid[row]) {
                inside_sheet = false;
                continue;
            }
        }

        if !is_sheet_keyword(cell(grid, row, 0).trim()) {
            continue;
        }
        let opens = if inside_sheet {
            opens_sheet_mid_mode(grid, row)
        } else {
            is_header_row(grid, row)
        };
        if !opens {
            continue;
        }

        starts.push(row);
        inside_sheet = true;
        sheet_start = row;
    }

    starts
}

fn opens_sheet_mid_mode(grid: &Grid, row: usize) -> bool {
    let a1 = cell(grid, row, 0);
    if !firmware_accepts_sheet_keyword(a1) {
        return false;
    }
    let word = a1.trim();
    word == "Preferences" || word == "Infrared" || cell(grid, row, 1).trim().is_empty()
}

fn is_header_row(grid: &Grid, row: usize) -> bool {
    firmware_accepts_sheet_keyword(cell(grid, row, 0)) || cell(grid, row, 1).trim().is_empty()
}

fn parse_sheet(
    grid: &Grid,
    start: usize,
    end: usize,
    is_first: bool,
    issues: &mut Vec<Issue>,
) -> ModeSheet {
    let value = |offset: usize, column: usize| cell(grid, start + offset, column).trim();
    let mut sheet = ModeSheet::new(keyword_to_type(value(0, 0)));
    sheet.mode_name = value(0, 2).to_owned();
    sheet.csv_file_name = is_first.then(|| value(1, 0).to_owned());
    sheet.header_label = value(2, 0).to_owned();
    sheet.channel = value(2, 2).to_owned();
    sheet.start_row = start + 1;

    let mut terminated = false;
    for row in start + 3..end {
        let has_content =
            (0..KEYWORD_COLUMNS).any(|column| !cell(grid, row, column).trim().is_empty());
        if !has_content {
            if is_blank_line(&grid[row]) {
                terminated = true;
            }
            continue;
        }
        if terminated {
            issues.push(Issue::new(
                Severity::Warning,
                format!("A{}", row + 1),
                "This row appears after the blank line that ends the mode.",
                "Move it above the first true blank line.",
            ));
            continue;
        }

        let mut inputs = Vec::new();
        let mut input_cols = Vec::new();
        for column in 2..KEYWORD_COLUMNS {
            let input = cell(grid, row, column).trim();
            if !input.is_empty() {
                inputs.push(input.to_owned());
                input_cols.push(column);
            }
        }

        let mut binding = Binding::new(
            row + 1,
            cell(grid, row, 0).trim(),
            cell(grid, row, 1).trim(),
            inputs,
            input_cols,
        );
        binding.action_name = cell(grid, row, ACTION_COLUMN).trim().to_owned();
        sheet.bindings.push(binding);
    }

    sheet
}

fn check_device_line_limits(grid: &Grid, section_starts: &[usize], issues: &mut Vec<Issue>) {
    let starts = section_starts.iter().copied().collect::<BTreeSet<_>>();
    let mut sheet_type = SheetType::ProfileName;
    let mut sheet_start = 0;

    for (row_index, row) in grid.iter().enumerate() {
        if starts.contains(&row_index) {
            sheet_type = keyword_to_type(cell(grid, row_index, 0).trim());
            sheet_start = row_index;
        }

        if row
            .iter()
            .any(|value| value.chars().any(|c| matches!(c, '\n' | '\r')))
        {
            issues.push(Issue::new(
                Severity::Warning,
                format!("A{}", row_index + 1),
                "A quoted cell contains a line break that firmware reads as another row.",
                "Keep device CSV cells on one physical line.",
            ));
        }

        if write(std::slice::from_ref(row)).len() > MAX_LINE_BYTES {
            issues.push(Issue::new(
                Severity::Error,
                format!("A{}", row_index + 1),
                "The encoded CSV row exceeds the firmware line buffer.",
                "Shorten the row below 1024 bytes.",
            ));
        }

        let keyword_columns = if sheet_type == SheetType::Preferences {
            2
        } else {
            KEYWORD_COLUMNS
        };
        for (column, value) in row.iter().take(keyword_columns).enumerate() {
            let header_row_exempt = row_index == 0
                && row
                    .first()
                    .is_some_and(|first| first.starts_with("QuadStick"));
            if utf16_len(value) >= MAX_KEYWORD_LENGTH && !header_row_exempt {
                issues.push(Issue::new(
                    Severity::Warning,
                    cell_ref(column, row_index + 1),
                    "The device keyword parser reads at most 63 characters.",
                    "Shorten the cell to 63 characters or fewer.",
                ));
            }

            if sheet_type != SheetType::Infrared
                && row_index >= sheet_start + 2
                && split_point(value).is_some()
            {
                issues.push(Issue::new(
                    Severity::Warning,
                    cell_ref(column, row_index + 1),
                    "Firmware stops reading this cell at an unsupported character.",
                    "Use only characters the firmware keyword reader accepts.",
                ));
            }
        }
    }
}

fn split_point(value: &str) -> Option<usize> {
    value
        .chars()
        .take(MAX_KEYWORD_LENGTH)
        .position(|c| !(c.is_ascii_alphanumeric() || matches!(c, '_' | '.' | ' ' | '-')))
}

fn utf16_len(value: &str) -> usize {
    value.encode_utf16().count()
}

fn cell_ref(column: usize, row: usize) -> String {
    let letter =
        char::from_u32(u32::from(b'A') + u32::try_from(column).expect("small grid column"))
            .expect("ASCII cell column");
    format!("{letter}{row}")
}

fn is_blank_line(row: &[String]) -> bool {
    row.is_empty() || (row.len() == 1 && row[0].trim().is_empty())
}

fn cell(grid: &Grid, row: usize, column: usize) -> &str {
    grid.get(row)
        .and_then(|values| values.get(column))
        .map_or("", String::as_str)
}

#[cfg(test)]
mod tests {
    use super::{is_blank_line, parse_structure, parse_with_issues};
    use crate::issue::Severity;

    #[test]
    fn only_a_true_empty_line_is_a_sheet_break() {
        assert!(is_blank_line(&[]));
        assert!(is_blank_line(&[String::new()]));
        assert!(is_blank_line(&["  ".to_owned()]));
        assert!(!is_blank_line(&[String::new(), String::new()]));
    }

    #[test]
    fn profile_named_binding_does_not_invent_a_sheet() {
        let document = parse_structure(
            "Profile Name,,Mode\nfile.csv\nOutputs,Function,usb\nx,normal,lip\nProfile switch,normal,lip\n",
        );
        assert_eq!(document.sheets.len(), 1);
        assert_eq!(document.sheets[0].bindings.len(), 2);
    }

    #[test]
    fn missing_blank_can_still_recover_an_obvious_header() {
        let document = parse_structure(
            "Profile Name,,One\nfile.csv\nOutputs,Function,usb\nx,normal,lip\nProfile Name,,Two\n\nOutputs,Function,bluetooth\n",
        );
        assert_eq!(document.sheets.len(), 2);
        assert_eq!(document.sheets[1].mode_name, "Two");
        assert_eq!(document.sheets[1].channel, "bluetooth");
    }

    #[test]
    fn binding_projection_keeps_input_gaps_and_action_name() {
        let document = parse_structure(
            "Profile Name,,Mode\nfile.csv\nOutputs,Function,usb\nx,normal,lip,,,mp_left_sip,,,,,note,Action X,tail\n",
        );
        let binding = &document.sheets[0].bindings[0];
        assert_eq!(binding.row, 4);
        assert_eq!(binding.inputs, ["lip", "mp_left_sip"]);
        assert_eq!(binding.input_cols, [2, 5]);
        assert_eq!(binding.action_name, "Action X");
    }

    #[test]
    fn comma_row_does_not_terminate_but_true_blank_does() {
        let comma = parse_structure(
            "Profile Name,,Mode\nfile.csv\nOutputs,Function,usb\nx,normal,lip\n,,\ncircle,normal,lip\n",
        );
        assert_eq!(comma.sheets[0].bindings.len(), 2);

        let blank = parse_structure(
            "Profile Name,,Mode\nfile.csv\nOutputs,Function,usb\nx,normal,lip\n\ncircle,normal,lip\n",
        );
        assert_eq!(blank.sheets[0].bindings.len(), 1);
        assert_eq!(blank.sheets[0].bindings[0].output, "x");
    }

    #[test]
    fn columns_after_j_do_not_make_a_binding_row() {
        let document = parse_structure(
            "Profile Name,,Mode\nfile.csv\nOutputs,Function,usb\n,,,,,,,,,,note only,Action only,opaque\nx,normal,lip\n",
        );
        assert_eq!(document.sheets[0].bindings.len(), 1);
        assert_eq!(document.sheets[0].bindings[0].output, "x");
    }

    #[test]
    fn firmware_keyword_limit_is_63_utf16_units() {
        let safe = "x".repeat(63);
        let unsafe_value = "x".repeat(64);
        let safe_csv =
            format!("Profile Name,,Mode\nfile.csv\nOutputs,Function,usb\n{safe},normal,lip\n");
        let unsafe_csv = format!(
            "Profile Name,,Mode\nfile.csv\nOutputs,Function,usb\n{unsafe_value},normal,lip\n"
        );
        assert!(
            !parse_with_issues(&safe_csv)
                .1
                .iter()
                .any(|issue| issue.cell == "A4")
        );
        assert!(
            parse_with_issues(&unsafe_csv)
                .1
                .iter()
                .any(|issue| issue.cell == "A4" && issue.severity == Severity::Warning)
        );
    }

    #[test]
    fn firmware_line_limit_is_1023_encoded_bytes() {
        let safe_comment = "n".repeat(1011);
        let unsafe_comment = "n".repeat(1012);
        let safe_csv = format!(
            "Profile Name,,Mode\nfile.csv\nOutputs,Function,usb\n,,,,,,,,,,{safe_comment}\n"
        );
        let unsafe_csv = format!(
            "Profile Name,,Mode\nfile.csv\nOutputs,Function,usb\n,,,,,,,,,,{unsafe_comment}\n"
        );
        assert!(
            !parse_with_issues(&safe_csv)
                .1
                .iter()
                .any(|issue| issue.cell == "A4" && issue.severity == Severity::Error)
        );
        assert!(
            parse_with_issues(&unsafe_csv)
                .1
                .iter()
                .any(|issue| issue.cell == "A4" && issue.severity == Severity::Error)
        );
    }
}
