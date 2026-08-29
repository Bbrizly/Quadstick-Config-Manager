use crate::csv::{Grid, parse};
use crate::model::{ModeSheet, ProfileDocument};
use crate::vocab::{
    firmware_accepts_sheet_keyword, is_file_header, is_sheet_keyword, keyword_to_type,
};

/// Parse only the profile/header/sheet structure.
///
/// Binding projection, validation and normalization deliberately live in later
/// rewrite tasks. This function exists so section discovery can be proven
/// independently against the frozen C# parser.
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
        document.sheets.push(parse_sheet(&grid, start, index == 0));
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

fn parse_sheet(grid: &Grid, start: usize, is_first: bool) -> ModeSheet {
    let value = |offset: usize, column: usize| cell(grid, start + offset, column).trim();
    let mut sheet = ModeSheet::new(keyword_to_type(value(0, 0)));
    sheet.mode_name = value(0, 2).to_owned();
    sheet.csv_file_name = is_first.then(|| value(1, 0).to_owned());
    sheet.header_label = value(2, 0).to_owned();
    sheet.channel = value(2, 2).to_owned();
    sheet.start_row = start + 1;
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
}
