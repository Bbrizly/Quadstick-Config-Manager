//! Native-friendly XLSX export for QuadStick profiles.
//!
//! The writer deliberately emits a tiny OOXML package with inline strings only.
//! A profile value beginning with `=` therefore remains profile text and can
//! never become a spreadsheet formula. Raw grid columns, including K/L/M+ data,
//! are preserved by explicit cell references.

use crate::{Grid, ProfileFile, SheetType};
use std::collections::BTreeSet;
use std::io::{Cursor, Write};
use zip::write::SimpleFileOptions;
use zip::{CompressionMethod, ZipWriter};

const MAIN_NS: &str = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
const REL_NS: &str = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
const PKG_REL_NS: &str = "http://schemas.openxmlformats.org/package/2006/relationships";
const CONTENT_NS: &str = "http://schemas.openxmlformats.org/package/2006/content-types";

#[derive(Debug)]
pub enum WorkbookExportError {
    Archive(zip::result::ZipError),
    Io(std::io::Error),
}

impl std::fmt::Display for WorkbookExportError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str("could not create spreadsheet")
    }
}

impl std::error::Error for WorkbookExportError {}

impl From<zip::result::ZipError> for WorkbookExportError {
    fn from(error: zip::result::ZipError) -> Self {
        Self::Archive(error)
    }
}

impl From<std::io::Error> for WorkbookExportError {
    fn from(error: std::io::Error) -> Self {
        Self::Io(error)
    }
}

/// Export the exact canonical raw profile into a standards-compliant `.xlsx`.
///
/// One worksheet is emitted per parsed profile sheet. The first worksheet also
/// keeps a version header when the source has one, so export -> import retains
/// that metadata. Unknown wide columns stay in the raw grid and are written too.
pub fn export_xlsx(profile: &ProfileFile) -> Result<Vec<u8>, WorkbookExportError> {
    let sheets = worksheet_slices(profile);
    let names = worksheet_names(profile, sheets.len());
    let cursor = Cursor::new(Vec::new());
    let mut zip = ZipWriter::new(cursor);
    let options = SimpleFileOptions::default().compression_method(CompressionMethod::Deflated);

    write_part(
        &mut zip,
        options,
        "[Content_Types].xml",
        &content_types(sheets.len()),
    )?;
    write_part(&mut zip, options, "_rels/.rels", root_relationships())?;
    write_part(&mut zip, options, "xl/workbook.xml", &workbook_xml(&names))?;
    write_part(
        &mut zip,
        options,
        "xl/_rels/workbook.xml.rels",
        &workbook_relationships(sheets.len()),
    )?;

    for (index, rows) in sheets.iter().enumerate() {
        let path = format!("xl/worksheets/sheet{}.xml", index + 1);
        write_part(&mut zip, options, &path, &worksheet_xml(rows))?;
    }

    Ok(zip.finish()?.into_inner())
}

fn worksheet_slices(profile: &ProfileFile) -> Vec<Grid> {
    if profile.document.sheets.is_empty() {
        return vec![trim_trailing_blank(profile.grid.clone())];
    }

    let mut result = Vec::with_capacity(profile.document.sheets.len());
    for (index, sheet) in profile.document.sheets.iter().enumerate() {
        let start = if index == 0 {
            0
        } else {
            sheet.start_row.saturating_sub(1).min(profile.grid.len())
        };
        let end = profile
            .document
            .sheets
            .get(index + 1)
            .map_or(profile.grid.len(), |next| {
                next.start_row.saturating_sub(1).min(profile.grid.len())
            })
            .max(start);
        result.push(trim_trailing_blank(profile.grid[start..end].to_vec()));
    }
    result
}

fn trim_trailing_blank(mut rows: Grid) -> Grid {
    while rows
        .last()
        .is_some_and(|row| row.is_empty() || row.iter().all(String::is_empty))
    {
        rows.pop();
    }
    rows
}

fn worksheet_names(profile: &ProfileFile, count: usize) -> Vec<String> {
    let mut used = BTreeSet::<String>::new();
    (0..count)
        .map(|index| {
            let base = profile.document.sheets.get(index).map_or_else(
                || format!("Profile {}", index + 1),
                |sheet| match sheet.sheet_type {
                    SheetType::Preferences => "Preferences".to_owned(),
                    SheetType::Infrared => "Infrared".to_owned(),
                    SheetType::ProfileName if sheet.mode_name.trim().is_empty() => {
                        format!("Mode {}", index + 1)
                    }
                    SheetType::ProfileName => sheet.mode_name.clone(),
                },
            );
            unique_sheet_name(&base, &mut used)
        })
        .collect()
}

fn unique_sheet_name(given: &str, used: &mut BTreeSet<String>) -> String {
    let cleaned: String = given
        .chars()
        .filter(|character| !character.is_control() && !"[]:*?/\\".contains(*character))
        .take(31)
        .collect();
    let trimmed = cleaned.trim().trim_matches('\'').trim();
    let base = if trimmed.is_empty() || trimmed.eq_ignore_ascii_case("History") {
        "Profile".to_owned()
    } else {
        trimmed.to_owned()
    };
    if used.insert(base.to_lowercase()) {
        return base;
    }

    for suffix in 2..10_000usize {
        let tail = format!(" {suffix}");
        let keep = 31usize.saturating_sub(tail.chars().count());
        let stem: String = base.chars().take(keep).collect();
        let candidate = format!("{stem}{tail}");
        if used.insert(candidate.to_lowercase()) {
            return candidate;
        }
    }
    "Profile".to_owned()
}

fn worksheet_xml(rows: &Grid) -> String {
    let mut body = String::new();
    for (row_index, row) in rows.iter().enumerate() {
        let one_based = row_index + 1;
        let cells = row
            .iter()
            .enumerate()
            .filter(|(_, value)| !value.is_empty())
            .map(|(column, value)| {
                format!(
                    "<c r=\"{}{}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{}</t></is></c>",
                    column_name(column),
                    one_based,
                    xml_escape(value)
                )
            })
            .collect::<String>();
        if !cells.is_empty() {
            body.push_str(&format!("<row r=\"{one_based}\">{cells}</row>"));
        }
    }
    format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"{MAIN_NS}\"><sheetData>{body}</sheetData></worksheet>"
    )
}

fn workbook_xml(names: &[String]) -> String {
    let sheets = names
        .iter()
        .enumerate()
        .map(|(index, name)| {
            let number = index + 1;
            format!(
                "<sheet name=\"{}\" sheetId=\"{number}\" r:id=\"rId{number}\"/>",
                xml_escape(name)
            )
        })
        .collect::<String>();
    format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"{MAIN_NS}\" xmlns:r=\"{REL_NS}\"><sheets>{sheets}</sheets></workbook>"
    )
}

fn workbook_relationships(count: usize) -> String {
    let body = (1..=count)
        .map(|number| {
            format!(
                "<Relationship Id=\"rId{number}\" Type=\"{REL_NS}/worksheet\" Target=\"worksheets/sheet{number}.xml\"/>"
            )
        })
        .collect::<String>();
    format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"{PKG_REL_NS}\">{body}</Relationships>"
    )
}

fn root_relationships() -> &'static str {
    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"
}

fn content_types(count: usize) -> String {
    let sheets = (1..=count)
        .map(|number| {
            format!(
                "<Override PartName=\"/xl/worksheets/sheet{number}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
            )
        })
        .collect::<String>();
    format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"{CONTENT_NS}\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>{sheets}</Types>"
    )
}

fn write_part<W: Write + std::io::Seek>(
    zip: &mut ZipWriter<W>,
    options: SimpleFileOptions,
    path: &str,
    body: &str,
) -> Result<(), WorkbookExportError> {
    zip.start_file(path, options)?;
    zip.write_all(body.as_bytes())?;
    Ok(())
}

fn column_name(mut zero_based: usize) -> String {
    let mut chars = Vec::new();
    loop {
        chars.push((b'A' + (zero_based % 26) as u8) as char);
        if zero_based < 26 {
            break;
        }
        zero_based = zero_based / 26 - 1;
    }
    chars.into_iter().rev().collect()
}

fn xml_escape(value: &str) -> String {
    // XML 1.0 cannot carry most control characters even escaped; a cell
    // holding one would make a workbook Excel refuses to open.
    let value: String = value
        .chars()
        .filter(|c| !c.is_control() || matches!(c, '\t' | '\n' | '\r'))
        .collect();
    value
        .replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}

#[cfg(test)]
mod tests {
    use super::{column_name, export_xlsx};
    use crate::{ProfileFile, import_xlsx};

    #[test]
    fn column_names_cover_wide_profile_data() {
        assert_eq!(column_name(0), "A");
        assert_eq!(column_name(25), "Z");
        assert_eq!(column_name(26), "AA");
        assert_eq!(column_name(63), "BL");
    }

    #[test]
    fn export_round_trips_formula_like_text_and_wide_columns_without_execution() {
        let profile = ProfileFile::load(
            "QuadStick Configuration,Version 1.5,,Demo\nProfile Name,,Drive\ndemo.csv\nOutputs,Function,usb,,,,,,,,Note,Action,Opaque\ntriangle,normal,lip,,,,,,,,=NOT_A_FORMULA,Jump,tail\n",
        );
        let bytes = export_xlsx(&profile).expect("export");
        let imported = import_xlsx(&bytes).expect("reimport");
        assert!(imported.csv.contains("=NOT_A_FORMULA"));
        assert!(imported.csv.contains("Jump,tail"));
    }
}
