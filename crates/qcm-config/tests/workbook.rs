//! The XLSX importer against workbooks a spreadsheet writer, or an attacker,
//! can actually produce. Each case mirrors one of the legacy `XlsxTests` /
//! `XlsxJunkTests` rows the C# oracle still runs.

use qcm_config::{
    ProfileFile, SkippedTabKind, WorkbookError, WorkbookLimitation, export_xlsx, import_xlsx,
};
use std::io::{Cursor, Write};
use zip::ZipWriter;
use zip::write::SimpleFileOptions;

/// A workbook from raw worksheet XML bodies, one `<sheet>` entry per name.
/// `entries` may name the same part more than once on purpose.
fn workbook(entries: &[(&str, &str)], parts: &[(&str, &str)]) -> Vec<u8> {
    let mut zip = ZipWriter::new(Cursor::new(Vec::new()));
    let options = SimpleFileOptions::default();
    let sheets: String = entries
        .iter()
        .enumerate()
        .map(|(index, (name, part))| {
            format!(
                "<sheet name=\"{name}\" sheetId=\"{}\" r:id=\"rId{}\"/>",
                index + 1,
                part_number(part)
            )
        })
        .collect();
    let rels: String = parts
        .iter()
        .map(|(part, _)| {
            format!(
                "<Relationship Id=\"rId{}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/{part}\"/>",
                part_number(part)
            )
        })
        .collect();
    zip.start_file("xl/workbook.xml", options).unwrap();
    write!(zip, "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>{sheets}</sheets></workbook>").unwrap();
    zip.start_file("xl/_rels/workbook.xml.rels", options)
        .unwrap();
    write!(zip, "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">{rels}</Relationships>").unwrap();
    for (part, body) in parts {
        zip.start_file(format!("xl/worksheets/{part}"), options)
            .unwrap();
        write!(zip, "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>{body}</sheetData></worksheet>").unwrap();
    }
    zip.finish().unwrap().into_inner()
}

fn part_number(part: &str) -> usize {
    part.trim_start_matches("sheet")
        .trim_end_matches(".xml")
        .parse()
        .unwrap()
}

fn cell(reference: &str, text: &str) -> String {
    format!("<c r=\"{reference}\" t=\"inlineStr\"><is><t>{text}</t></is></c>")
}

fn row(number: usize, cells: &[(&str, &str)]) -> String {
    let body: String = cells.iter().map(|(r, t)| cell(r, t)).collect();
    format!("<row r=\"{number}\">{body}</row>")
}

fn mode_tab(name: &str) -> String {
    row(1, &[("A1", "Profile Name"), ("C1", name)])
        + &row(2, &[("A2", "Outputs"), ("B2", "Function")])
        + &row(3, &[("A3", "triangle"), ("B3", "normal"), ("C3", "lip")])
}

#[test]
fn a_part_listed_many_times_is_read_once() {
    let body = mode_tab("Drive");
    let entries: Vec<(&str, &str)> = (0..40).map(|_| ("Drive", "sheet1.xml")).collect();
    let import = import_xlsx(&workbook(&entries, &[("sheet1.xml", &body)])).unwrap();
    let profile = ProfileFile::load(&import.csv);
    assert_eq!(profile.document.sheets.len(), 1);
    assert!(import.limitation.is_none());
}

#[test]
fn a_cell_without_a_reference_follows_the_one_before_it() {
    let body = row(1, &[("A1", "Profile Name"), ("C1", "Drive")])
        + &row(2, &[("A2", "Outputs"), ("B2", "Function")])
        + "<row r=\"3\"><c r=\"A3\" t=\"inlineStr\"><is><t>triangle</t></is></c><c t=\"inlineStr\"><is><t>normal</t></is></c><c t=\"inlineStr\"><is><t>lip</t></is></c></row>";
    let import = import_xlsx(&workbook(
        &[("Drive", "sheet1.xml")],
        &[("sheet1.xml", &body)],
    ))
    .unwrap();
    assert!(import.csv.contains("triangle,normal,lip"), "{}", import.csv);
}

#[test]
fn a_flat_file_on_one_tab_is_a_profile_not_a_missing_tab() {
    let body = row(
        1,
        &[("A1", "QuadStick Configuration"), ("B1", "Version 1.5")],
    ) + &row(2, &[("A2", "Profile Name"), ("C2", "Drive")])
        + &row(3, &[("A3", "Outputs"), ("B3", "Function")])
        + &row(4, &[("A4", "triangle"), ("B4", "normal"), ("C4", "lip")]);
    let import = import_xlsx(&workbook(
        &[("Backup", "sheet1.xml")],
        &[("sheet1.xml", &body)],
    ))
    .unwrap();
    let profile = ProfileFile::load(&import.csv);
    assert_eq!(profile.document.sheets.len(), 1, "{}", import.csv);
    assert!(import.skipped.is_empty());
}

#[test]
fn tabs_that_do_not_come_in_are_named_or_dropped_the_way_the_shipped_app_does() {
    let mode = mode_tab("Drive");
    let notes = row(1, &[("A1", "Left Analog"), ("B1", "notes")])
        + &row(2, &[("A2", "triangle"), ("B2", "normal"), ("C2", "lip")]);
    let scratch = row(1, &[("A1", "todo"), ("B1", "buy milk")]);
    let helper = row(1, &[("A1", "Outputs")]) + &row(2, &[("A2", "triangle")]);
    let import = import_xlsx(&workbook(
        &[
            ("Drive", "sheet1.xml"),
            ("Left Analog", "sheet2.xml"),
            ("Scratch", "sheet3.xml"),
            ("Outputs", "sheet4.xml"),
        ],
        &[
            ("sheet1.xml", &mode),
            ("sheet2.xml", &notes),
            ("sheet3.xml", &scratch),
            ("sheet4.xml", &helper),
        ],
    ))
    .unwrap();
    let named: Vec<(&str, SkippedTabKind, usize)> = import
        .skipped
        .iter()
        .map(|tab| (tab.name.as_str(), tab.kind, tab.rows.len()))
        .collect();
    assert_eq!(
        named,
        vec![
            ("Left Analog", SkippedTabKind::UnreadableA1, 2),
            ("Outputs", SkippedTabKind::Helper, 0),
        ]
    );
}

#[test]
fn the_workbook_row_cap_stops_between_tabs_and_says_what_it_left() {
    let big: String = (1..=20_000)
        .map(|n| {
            if n == 1 {
                row(1, &[("A1", "Profile Name"), ("C1", "Big")])
            } else {
                row(
                    n,
                    &[(&format!("A{n}"), "triangle"), (&format!("B{n}"), "normal")],
                )
            }
        })
        .collect();
    let small = mode_tab("Small");
    let import = import_xlsx(&workbook(
        &[
            ("One", "sheet1.xml"),
            ("Two", "sheet2.xml"),
            ("Three", "sheet3.xml"),
        ],
        &[
            ("sheet1.xml", &big),
            ("sheet2.xml", &big),
            ("sheet3.xml", &small),
        ],
    ))
    .unwrap();
    let profile = ProfileFile::load(&import.csv);
    assert_eq!(profile.document.sheets.len(), 2);
    // The tab that crossed the cap came in whole, not cut part way down.
    assert_eq!(
        profile.document.sheets[1].bindings.len(),
        profile.document.sheets[0].bindings.len()
    );
    assert!(profile.document.sheets[1].bindings.len() > 19_000);
    assert_eq!(
        import.limitation,
        Some(WorkbookLimitation::WorkbookRows {
            max: 30_000,
            remaining_tabs: Some(1)
        })
    );
}

#[test]
fn a_document_type_declaration_is_refused() {
    let body = "<!DOCTYPE x [<!ENTITY a \"b\">]>".to_owned() + &mode_tab("Drive");
    assert_eq!(
        import_xlsx(&workbook(
            &[("Drive", "sheet1.xml")],
            &[("sheet1.xml", &body)]
        )),
        Err(WorkbookError::InvalidXml)
    );
}

#[test]
fn export_keeps_the_preamble_and_titles_the_settings_tab_by_its_keyword() {
    let profile = ProfileFile::load(
        "QuadStick Configuration,Version 1.5,,Demo\nProfile Name,,Drive\ndemo.csv\nOutputs,Function,usb\ntriangle,normal,lip\n\nPreferences,,Left Joystick\nOutputs,Function\n",
    );
    let bytes = export_xlsx(&profile).expect("export");
    let mut archive = zip::ZipArchive::new(Cursor::new(&bytes)).unwrap();
    let mut text = String::new();
    std::io::Read::read_to_string(&mut archive.by_name("xl/workbook.xml").unwrap(), &mut text)
        .unwrap();
    let import = import_xlsx(&bytes).expect("reimport");
    assert!(
        import
            .csv
            .starts_with("QuadStick Configuration,Version 1.5"),
        "{}",
        import.csv
    );
    assert!(text.contains("name=\"Preferences\""));
    assert!(!text.contains("name=\"Left Joystick\""));
}

#[test]
fn export_never_writes_a_character_xml_cannot_carry() {
    let profile = ProfileFile::load("Profile Name,,Dr\u{0007}ive\nOutputs,Function\n");
    let bytes = export_xlsx(&profile).expect("export");
    let import = import_xlsx(&bytes).expect("a workbook Excel would open");
    assert!(import.csv.contains("Drive"));
}
