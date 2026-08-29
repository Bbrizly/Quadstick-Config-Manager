use crate::csv::{Grid, parse};
use crate::model::{Binding, ModeSheet, ProfileDocument};
use crate::vocab::{
    firmware_accepts_sheet_keyword, is_file_header, is_sheet_keyword, keyword_to_type,
};

const MAX_INPUT_COLUMNS: usize = 8; // C..J
const KEYWORD_COLUMNS: usize = 2 + MAX_INPUT_COLUMNS; // A..J
const ACTION_COLUMN: usize = 11; // L

/// Parse the current profile/header/sheet/binding projection.
///
/// Validation, firmware warnings and normalization deliberately live in later
/// rewrite tasks. This projection mirrors the frozen C# parser closely enough
/// that those later layers can be ported against the same document shape.
#[must_use]
pub fn parse_structure(csv_text: &str) -> ProfileDocument {
    let grid = parse(csv_text);
    let mut document = ProfileDocument::default();

    let mut scan_from = 0;
    if is_file_header(cell(&grid, 0, 0)) {
        document.has_version_header = true;
        document.header_version = cell(&grid, 0, 1).trim().to_owned();
        document.header_source = cell(&grid, 0, 2).trim().to_owned();
        document.header_name = cell(&grid, 0, 3).trim().to_owned();
        scan_from = 1;
    }

    let starts = find_section_starts(&grid, scan_from);
    for (index, start) in starts.iter().copied().enumerate() {
        let end = starts.get(index + 1).copied().unwrap_or(grid.len());
        document
            .sheets
            .push(parse_sheet(&grid, start, end, index == 0));
    }

    document
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

fn parse_sheet(grid: &Grid, start: usize, end: usize, is_first: bool) -> ModeSheet {
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
    use super::{is_blank_line, parse_structure};

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
}
