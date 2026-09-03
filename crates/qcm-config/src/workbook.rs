//! Hardened `.xlsx` -> QuadStick CSV import.
//!
//! This is a Rust port of the shipping `QuadStick.Format.Xlsx` behavior. XLSX is
//! treated as an untrusted ZIP of XML: only the workbook parts we need are ever
//! inflated, each part is bounded before and while it is read, DTDs are refused,
//! row/column/workbook growth is capped, duplicate worksheet relationships are
//! deduplicated, and formulas are never evaluated. A formula cell contributes
//! only the cached `<v>` value already stored in the workbook.

use crate::csv::{Grid, write};
use crate::model::SheetType;
use crate::vocab::{function_arity, is_file_header, is_sheet_keyword, keyword_to_type};
use quick_xml::XmlVersion;
use quick_xml::events::{BytesStart, Event};
use quick_xml::reader::Reader;
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};
use std::fmt;
use std::io::{Cursor, Read};
use zip::ZipArchive;
use zip::result::ZipError;

pub const MAX_WORKBOOK_BYTES: usize = 32 * 1024 * 1024;
pub const MAX_PART_BYTES: u64 = 32 * 1024 * 1024;
pub const MAX_COLUMN: usize = 63;
pub const MAX_ROWS: usize = 20_000;
pub const MAX_SHEETS: usize = 64;
pub const MAX_WORKBOOK_ROWS: usize = 30_000;

const HELPER_TABS: [&str; 4] = ["Inputs", "Outputs", "Voice", "Reference Card"];
const GENERIC_MODE_NAMES: [&str; 8] = [
    "Left Joystick",
    "Right Joystick",
    "Mouse Mode",
    "Solo",
    "Mode",
    "Profile",
    "Profile Name",
    "Sheet1",
];

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SkippedTabKind {
    UnreadableA1,
    Helper,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SkippedTab {
    pub name: String,
    pub rows: Grid,
    pub kind: SkippedTabKind,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TabRename {
    pub mode_number: usize,
    pub tab_name: String,
    pub cell_c1: String,
}

/// Structured replacement for the legacy localized limitation string.
/// Presentation localizes this DTO; the parser never has to know UI language.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum WorkbookLimitation {
    SheetCount {
        max: usize,
    },
    SheetRows {
        tab: String,
        max: usize,
    },
    WorkbookRows {
        max: usize,
        /// `None` means the sheet-count cap fired too, so the true count beyond
        /// the row cap is deliberately unknown rather than understated.
        remaining_tabs: Option<usize>,
    },
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct WorkbookImport {
    pub csv: String,
    pub skipped: Vec<SkippedTab>,
    pub limitation: Option<WorkbookLimitation>,
    pub renamed: Vec<TabRename>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum WorkbookError {
    FileTooLarge { limit: usize, actual: usize },
    PartTooLarge { limit: u64, actual: u64 },
    InvalidArchive,
    InvalidXml,
    MissingWorkbookParts,
}

impl fmt::Display for WorkbookError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::FileTooLarge { limit, actual } => {
                write!(
                    formatter,
                    "workbook is too large ({actual} bytes; limit {limit})"
                )
            }
            Self::PartTooLarge { limit, actual } => {
                write!(
                    formatter,
                    "workbook part is too large ({actual} bytes; limit {limit})"
                )
            }
            Self::InvalidArchive | Self::InvalidXml | Self::MissingWorkbookParts => {
                formatter.write_str("not a readable spreadsheet")
            }
        }
    }
}

impl std::error::Error for WorkbookError {}

#[must_use]
pub fn looks_like_xlsx(content: &[u8]) -> bool {
    content.starts_with(b"PK")
}

/// Import a complete XLSX workbook into the flat CSV shape the firmware reads.
///
/// The input is intentionally bytes rather than a filesystem path. Native code
/// owns file selection and can enforce opaque handles; this pure core only sees
/// the selected workbook's bounded contents.
pub fn import_xlsx(content: &[u8]) -> Result<WorkbookImport, WorkbookError> {
    if content.len() > MAX_WORKBOOK_BYTES {
        return Err(WorkbookError::FileTooLarge {
            limit: MAX_WORKBOOK_BYTES,
            actual: content.len(),
        });
    }
    if !looks_like_xlsx(content) {
        return Err(WorkbookError::InvalidArchive);
    }

    let mut archive =
        ZipArchive::new(Cursor::new(content)).map_err(|_| WorkbookError::InvalidArchive)?;
    let shared = shared_strings(&mut archive)?;
    let (mut parts, sheet_cap_hit) = sheet_parts(&mut archive)?;

    let mut limitation =
        sheet_cap_hit.then_some(WorkbookLimitation::SheetCount { max: MAX_SHEETS });
    if parts.len() > MAX_SHEETS {
        parts.truncate(MAX_SHEETS);
    }

    let mut rows = Grid::new();
    let mut skipped = Vec::new();
    let mut modes = Vec::new();
    let mut kept_rows = 0usize;

    for (index, (name, part)) in parts.iter().enumerate() {
        let helper_named = is_helper_tab(name.trim());
        let sheet = match parse_sheet(&mut archive, part, &shared) {
            Ok(sheet) => sheet,
            Err(_) if helper_named => {
                skipped.push(SkippedTab {
                    name: name.clone(),
                    rows: Grid::new(),
                    kind: SkippedTabKind::Helper,
                });
                continue;
            }
            Err(error) => return Err(error),
        };

        if sheet.lost_rows && limitation.is_none() {
            limitation = Some(WorkbookLimitation::SheetRows {
                tab: name.clone(),
                max: MAX_ROWS,
            });
        }

        let grid = sheet.rows;
        let keyword = grid
            .first()
            .and_then(|row| row.first())
            .is_some_and(|cell| is_sheet_keyword(cell.trim()));
        if !keyword {
            let kind = if helper_named {
                SkippedTabKind::Helper
            } else {
                SkippedTabKind::UnreadableA1
            };
            skipped.push(SkippedTab {
                name: name.clone(),
                rows: grid,
                kind,
            });
            continue;
        }

        if !rows.is_empty() && !rows.last().is_some_and(Vec::is_empty) {
            rows.push(Vec::new());
        }
        let start = rows.len();
        if grid
            .first()
            .and_then(|row| row.first())
            .is_some_and(|first| keyword_to_type(first.trim()) == Some(SheetType::ProfileName))
        {
            modes.push(ModeCandidate {
                row: start,
                tab: name.clone(),
                c1: grid
                    .first()
                    .and_then(|row| row.get(2))
                    .cloned()
                    .unwrap_or_default(),
            });
        }
        let available = MAX_WORKBOOK_ROWS.saturating_sub(kept_rows);
        if grid.len() > available {
            rows.extend(grid.into_iter().take(available));
            kept_rows = MAX_WORKBOOK_ROWS;
            if limitation.is_none() {
                limitation = Some(WorkbookLimitation::WorkbookRows {
                    max: MAX_WORKBOOK_ROWS,
                    remaining_tabs: if sheet_cap_hit {
                        None
                    } else {
                        Some(parts.len().saturating_sub(index + 1))
                    },
                });
            }
            break;
        }
        kept_rows += grid.len();
        rows.extend(grid);
    }

    let renamed = name_modes_from_tabs(&mut rows, &modes);
    Ok(WorkbookImport {
        csv: write(&rows),
        skipped,
        limitation,
        renamed,
    })
}

/// The review screen's repair action for a tab whose A1 was unreadable.
#[must_use]
pub fn repaired_as_mode(tab: &SkippedTab) -> Grid {
    let mut rows = tab.rows.clone();
    let Some(first) = rows.first_mut() else {
        return rows;
    };
    if first.len() < 3 {
        first.resize(3, String::new());
    }
    first[0] = "Profile Name".to_owned();
    if first[2].trim().is_empty() {
        first[2] = tab.name.clone();
    }
    rows
}

#[derive(Debug)]
struct SheetRead {
    rows: Grid,
    lost_rows: bool,
}

#[derive(Debug)]
struct ModeCandidate {
    row: usize,
    tab: String,
    c1: String,
}

fn is_helper_tab(name: &str) -> bool {
    HELPER_TABS
        .iter()
        .any(|helper| helper.eq_ignore_ascii_case(name))
}

fn looks_like_bindings(grid: &Grid) -> bool {
    grid.iter().any(|row| {
        row.get(1)
            .is_some_and(|function| function_arity(function.trim()).is_some())
    })
}

fn name_modes_from_tabs(rows: &mut Grid, modes: &[ModeCandidate]) -> Vec<TabRename> {
    let mut counts = BTreeMap::<String, usize>::new();
    for mode in modes {
        *counts.entry(fold(&mode.c1)).or_default() += 1;
    }

    let shared: BTreeSet<String> = counts
        .into_iter()
        .filter_map(|(name, count)| (count > 1).then_some(name))
        .collect();
    let mut renamed = Vec::new();

    for (index, mode) in modes.iter().enumerate() {
        let worth_replacing =
            mode.c1.is_empty() || generic_mode_name(&mode.c1) || shared.contains(&fold(&mode.c1));
        if !worth_replacing
            || mode.tab.is_empty()
            || generic_tab_name(&mode.tab)
            || mode.tab.eq_ignore_ascii_case(&mode.c1)
        {
            continue;
        }

        let Some(row) = rows.get_mut(mode.row) else {
            continue;
        };
        if row.len() < 3 {
            row.resize(3, String::new());
        }
        row[2] = mode.tab.clone();
        renamed.push(TabRename {
            mode_number: index + 1,
            tab_name: mode.tab.clone(),
            cell_c1: mode.c1.clone(),
        });
    }
    renamed
}

fn generic_mode_name(name: &str) -> bool {
    GENERIC_MODE_NAMES
        .iter()
        .any(|generic| generic.eq_ignore_ascii_case(name.trim()))
}

fn generic_tab_name(name: &str) -> bool {
    let trimmed = name.trim();
    if generic_mode_name(trimmed) {
        return true;
    }
    let lower = trimmed.to_ascii_lowercase();
    ["sheet", "tab", "page"].iter().any(|prefix| {
        lower.strip_prefix(prefix).is_some_and(|rest| {
            let rest = rest.trim();
            rest.is_empty() || rest.chars().all(|character| character.is_ascii_digit())
        })
    })
}

fn fold(value: &str) -> String {
    value.to_lowercase()
}

fn shared_strings<R: Read + std::io::Seek>(
    archive: &mut ZipArchive<R>,
) -> Result<Vec<String>, WorkbookError> {
    let Some(xml) = read_part(archive, "xl/sharedStrings.xml")? else {
        return Ok(Vec::new());
    };
    parse_shared_strings(&xml)
}

fn sheet_parts<R: Read + std::io::Seek>(
    archive: &mut ZipArchive<R>,
) -> Result<(Vec<(String, String)>, bool), WorkbookError> {
    let workbook =
        read_part(archive, "xl/workbook.xml")?.ok_or(WorkbookError::MissingWorkbookParts)?;
    let rels = read_part(archive, "xl/_rels/workbook.xml.rels")?
        .ok_or(WorkbookError::MissingWorkbookParts)?;
    let relationships = relationship_targets(&rels)?;

    let mut reader = Reader::from_str(&workbook);
    let mut parts = Vec::new();
    let mut sheet_cap_hit = false;
    loop {
        match reader.read_event().map_err(|_| WorkbookError::InvalidXml)? {
            Event::Start(element) | Event::Empty(element)
                if local_name(element.name().as_ref()) == "sheet" =>
            {
                if parts.len() >= MAX_SHEETS + 1 {
                    sheet_cap_hit = true;
                    continue;
                }
                let name = attribute(&element, b"name")?.unwrap_or_default();
                let relationship = relationship_id(&element)?;
                if let Some(target) = relationships.get(&relationship) {
                    parts.push((name, workbook_part(target)));
                }
            }
            Event::DocType(_) => return Err(WorkbookError::InvalidXml),
            Event::Eof => break,
            _ => {}
        }
    }
    sheet_cap_hit |= parts.len() > MAX_SHEETS;
    Ok((parts, sheet_cap_hit))
}

fn relationship_targets(xml: &str) -> Result<BTreeMap<String, String>, WorkbookError> {
    let mut reader = Reader::from_str(xml);
    let mut result = BTreeMap::new();
    loop {
        match reader.read_event().map_err(|_| WorkbookError::InvalidXml)? {
            Event::Start(element) | Event::Empty(element)
                if local_name(element.name().as_ref()) == "Relationship" =>
            {
                let id = attribute(&element, b"Id")?.unwrap_or_default();
                let target = attribute(&element, b"Target")?.unwrap_or_default();
                if !id.is_empty() && !target.is_empty() {
                    result.entry(id).or_insert(target);
                }
            }
            Event::DocType(_) => return Err(WorkbookError::InvalidXml),
            Event::Eof => break,
            _ => {}
        }
    }
    Ok(result)
}

fn workbook_part(target: &str) -> String {
    let target = target.trim_start_matches('/');
    if target.starts_with("xl/") {
        target.to_owned()
    } else {
        format!("xl/{target}")
    }
}

fn parse_shared_strings(xml: &str) -> Result<Vec<String>, WorkbookError> {
    let mut reader = Reader::from_str(xml);
    let mut result = Vec::new();
    let mut current: Option<String> = None;
    let mut in_text = false;

    loop {
        match reader.read_event().map_err(|_| WorkbookError::InvalidXml)? {
            Event::Start(element) if local_name(element.name().as_ref()) == "si" => {
                current = Some(String::new());
            }
            Event::Empty(element) if local_name(element.name().as_ref()) == "si" => {
                result.push(String::new());
            }
            Event::Start(element) if local_name(element.name().as_ref()) == "t" => {
                in_text = current.is_some();
            }
            Event::End(element) if local_name(element.name().as_ref()) == "t" => {
                in_text = false;
            }
            Event::Text(text) if in_text => {
                if let Some(value) = current.as_mut() {
                    value.push_str(&text.xml_content(XmlVersion::Implicit1_0));
                }
            }
            Event::CData(text) if in_text => {
                if let Some(value) = current.as_mut() {
                    value.push_str(text.as_ref());
                }
            }
            Event::End(element) if local_name(element.name().as_ref()) == "si" => {
                if let Some(value) = current.take() {
                    result.push(value);
                }
            }
            Event::DocType(_) => return Err(WorkbookError::InvalidXml),
            Event::Eof => break,
            _ => {}
        }
    }
    Ok(result)
}

fn parse_sheet<R: Read + std::io::Seek>(
    archive: &mut ZipArchive<R>,
    part: &str,
    shared: &[String],
) -> Result<SheetRead, WorkbookError> {
    let Some(xml) = read_part(archive, part)? else {
        return Ok(SheetRead {
            rows: Grid::new(),
            lost_rows: false,
        });
    };

    let mut reader = Reader::from_str(&xml);
    let mut rows = Grid::new();
    let mut last_number = 0i32;
    let mut lost_rows = false;
    let mut row: Option<RowState> = None;
    let mut cell: Option<CellState> = None;
    let mut in_value = false;
    let mut in_text = false;

    loop {
        match reader.read_event().map_err(|_| WorkbookError::InvalidXml)? {
            Event::Start(element) if local_name(element.name().as_ref()) == "row" => {
                row = Some(RowState::from_element(&element)?);
            }
            Event::Empty(element) if local_name(element.name().as_ref()) == "row" => {
                let blank = RowState::from_element(&element)?;
                finish_row(blank, &mut rows, &mut last_number, &mut lost_rows)?;
            }
            Event::Start(element) if local_name(element.name().as_ref()) == "c" => {
                if row.is_some() {
                    cell = Some(CellState::from_element(&element)?);
                    in_value = false;
                    in_text = false;
                }
            }
            Event::Empty(element) if local_name(element.name().as_ref()) == "c" => {
                if let Some(current_row) = row.as_mut() {
                    place_cell(current_row, CellState::from_element(&element)?, shared);
                }
            }
            Event::Start(element) if local_name(element.name().as_ref()) == "v" => {
                in_value = cell.is_some();
            }
            Event::End(element) if local_name(element.name().as_ref()) == "v" => {
                in_value = false;
            }
            Event::Start(element) if local_name(element.name().as_ref()) == "t" => {
                in_text = cell.is_some();
            }
            Event::End(element) if local_name(element.name().as_ref()) == "t" => {
                in_text = false;
            }
            Event::Text(text) if cell.is_some() && (in_value || in_text) => {
                let decoded = text.xml_content(XmlVersion::Implicit1_0);
                if let Some(current) = cell.as_mut() {
                    if in_value {
                        current.value.push_str(&decoded);
                    }
                    if in_text {
                        current.inline_text.push_str(&decoded);
                    }
                }
            }
            Event::CData(text) if cell.is_some() && (in_value || in_text) => {
                if let Some(current) = cell.as_mut() {
                    if in_value {
                        current.value.push_str(text.as_ref());
                    }
                    if in_text {
                        current.inline_text.push_str(text.as_ref());
                    }
                }
            }
            Event::End(element) if local_name(element.name().as_ref()) == "c" => {
                if let (Some(current_row), Some(current_cell)) = (row.as_mut(), cell.take()) {
                    place_cell(current_row, current_cell, shared);
                }
                in_value = false;
                in_text = false;
            }
            Event::End(element) if local_name(element.name().as_ref()) == "row" => {
                if let Some(current_row) = row.take() {
                    finish_row(current_row, &mut rows, &mut last_number, &mut lost_rows)?;
                }
                cell = None;
                in_value = false;
                in_text = false;
            }
            Event::DocType(_) => return Err(WorkbookError::InvalidXml),
            Event::Eof => break,
            _ => {}
        }
    }

    while rows.last().is_some_and(|row| row.is_empty()) {
        rows.pop();
    }
    Ok(SheetRead { rows, lost_rows })
}

#[derive(Debug)]
struct RowState {
    number: i32,
    cells: Vec<String>,
    has_cells: bool,
}

impl RowState {
    fn from_element(element: &BytesStart<'_>) -> Result<Self, WorkbookError> {
        let number = attribute(element, b"r")?
            .and_then(|value| value.parse::<i32>().ok())
            .unwrap_or(0);
        Ok(Self {
            number,
            cells: Vec::new(),
            has_cells: false,
        })
    }
}

#[derive(Debug)]
struct CellState {
    column: usize,
    kind: CellKind,
    value: String,
    inline_text: String,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum CellKind {
    Shared,
    Inline,
    Boolean,
    Raw,
}

impl CellState {
    fn from_element(element: &BytesStart<'_>) -> Result<Self, WorkbookError> {
        let reference = attribute(element, b"r")?.unwrap_or_default();
        let kind = match attribute(element, b"t")?.as_deref() {
            Some("s") => CellKind::Shared,
            Some("inlineStr") => CellKind::Inline,
            Some("b") => CellKind::Boolean,
            _ => CellKind::Raw,
        };
        Ok(Self {
            column: column_index(&reference),
            kind,
            value: String::new(),
            inline_text: String::new(),
        })
    }
}

fn place_cell(row: &mut RowState, cell: CellState, shared: &[String]) {
    row.has_cells = true;
    if cell.column > MAX_COLUMN {
        return;
    }
    if row.cells.len() <= cell.column {
        row.cells.resize(cell.column + 1, String::new());
    }
    row.cells[cell.column] = match cell.kind {
        CellKind::Shared => cell
            .value
            .parse::<usize>()
            .ok()
            .and_then(|index| shared.get(index))
            .cloned()
            .unwrap_or_default(),
        CellKind::Inline => cell.inline_text,
        CellKind::Boolean => {
            if cell.value == "1" {
                "TRUE".to_owned()
            } else {
                "FALSE".to_owned()
            }
        }
        CellKind::Raw => cell.value,
    };
}

fn finish_row(
    mut row: RowState,
    rows: &mut Grid,
    last_number: &mut i32,
    lost_rows: &mut bool,
) -> Result<(), WorkbookError> {
    let number = if row.number > 0 {
        row.number
    } else {
        last_number.saturating_add(1)
    };
    *last_number = number;
    if number < 1 || number as usize > MAX_ROWS {
        *lost_rows |= row.has_cells;
        return Ok(());
    }
    while rows.len() < number as usize {
        rows.push(Vec::new());
    }
    while row.cells.last().is_some_and(String::is_empty) {
        row.cells.pop();
    }
    rows[number as usize - 1] = row.cells;
    Ok(())
}

fn read_part<R: Read + std::io::Seek>(
    archive: &mut ZipArchive<R>,
    path: &str,
) -> Result<Option<String>, WorkbookError> {
    let mut entry = match archive.by_name(path) {
        Ok(entry) => entry,
        Err(ZipError::FileNotFound) => return Ok(None),
        Err(_) => return Err(WorkbookError::InvalidArchive),
    };
    if entry.size() > MAX_PART_BYTES {
        return Err(WorkbookError::PartTooLarge {
            limit: MAX_PART_BYTES,
            actual: entry.size(),
        });
    }
    let mut bytes = Vec::with_capacity(entry.size() as usize);
    entry
        .by_ref()
        .take(MAX_PART_BYTES + 1)
        .read_to_end(&mut bytes)
        .map_err(|_| WorkbookError::InvalidArchive)?;
    if bytes.len() as u64 > MAX_PART_BYTES {
        return Err(WorkbookError::PartTooLarge {
            limit: MAX_PART_BYTES,
            actual: bytes.len() as u64,
        });
    }
    String::from_utf8(bytes)
        .map(Some)
        .map_err(|_| WorkbookError::InvalidXml)
}

fn relationship_id(element: &BytesStart<'_>) -> Result<String, WorkbookError> {
    for attribute in element.attributes().with_checks(false) {
        let attribute = attribute.map_err(|_| WorkbookError::InvalidXml)?;
        let key = local_name(attribute.key.as_ref());
        if key == "id" {
            return attribute
                .unescape_value()
                .map(|value| value.into_owned())
                .map_err(|_| WorkbookError::InvalidXml);
        }
    }
    Ok(String::new())
}

fn attribute(element: &BytesStart<'_>, wanted: &[u8]) -> Result<Option<String>, WorkbookError> {
    for attribute in element.attributes().with_checks(false) {
        let attribute = attribute.map_err(|_| WorkbookError::InvalidXml)?;
        if local_name(attribute.key.as_ref()).as_bytes() == wanted {
            return attribute
                .unescape_value()
                .map(|value| Some(value.into_owned()))
                .map_err(|_| WorkbookError::InvalidXml);
        }
    }
    Ok(None)
}

fn local_name(name: &[u8]) -> String {
    let text = String::from_utf8_lossy(name);
    text.rsplit(':').next().unwrap_or(&text).to_owned()
}

fn column_index(reference: &str) -> usize {
    let mut number = 0usize;
    for character in reference.chars() {
        if !character.is_ascii_alphabetic() {
            break;
        }
        let upper = character.to_ascii_uppercase();
        number = number
            .saturating_mul(26)
            .saturating_add((upper as u8 - b'A' + 1) as usize);
        if number > MAX_COLUMN + 1 {
            return MAX_COLUMN + 1;
        }
    }
    number.saturating_sub(1)
}

#[cfg(test)]
mod tests {
    use super::{
        MAX_COLUMN, MAX_PART_BYTES, MAX_ROWS, MAX_SHEETS, MAX_WORKBOOK_BYTES, MAX_WORKBOOK_ROWS,
        SkippedTabKind, WorkbookError, WorkbookLimitation, column_index, import_xlsx,
        looks_like_bindings, name_modes_from_tabs, repaired_as_mode,
    };
    use crate::csv::Grid;

    #[test]
    fn column_reference_is_bounded() {
        assert_eq!(column_index("A1"), 0);
        assert_eq!(column_index("AB12"), 27);
        assert_eq!(column_index("BL2"), MAX_COLUMN);
        assert_eq!(column_index("BM2"), MAX_COLUMN + 1);
    }

    #[test]
    fn helper_tabs_and_binding_detection_are_case_insensitive_and_structural() {
        assert!(super::is_helper_tab("reference card"));
        let grid = vec![vec!["note".into()], vec!["out".into(), "normal".into()]];
        assert!(looks_like_bindings(&grid));
    }

    #[test]
    fn repair_turns_a_skipped_tab_into_a_mode_without_touching_its_other_cells() {
        let tab = super::SkippedTab {
            name: "Driving".into(),
            rows: vec![vec!["Outputs".into(), "Function".into()], vec!["x".into()]],
            kind: SkippedTabKind::UnreadableA1,
        };
        let repaired = repaired_as_mode(&tab);
        assert_eq!(repaired[0][0], "Profile Name");
        assert_eq!(repaired[0][2], "Driving");
        assert_eq!(repaired[1][0], "x");
    }

    #[test]
    fn import_limits_are_intentionally_defensive_not_profile_sized() {
        assert_eq!(MAX_WORKBOOK_BYTES, 32 * 1024 * 1024);
        assert_eq!(MAX_PART_BYTES, 32 * 1024 * 1024);
        assert_eq!(MAX_ROWS, 20_000);
        assert_eq!(MAX_SHEETS, 64);
        assert_eq!(MAX_WORKBOOK_ROWS, 30_000);
    }

    #[test]
    fn rejected_non_zip_is_not_treated_as_a_csv() {
        assert_eq!(
            import_xlsx(b"not a workbook"),
            Err(WorkbookError::InvalidArchive)
        );
    }

    #[test]
    fn mode_naming_only_replaces_generic_or_shared_names() {
        let mut rows: Grid = vec![
            vec!["Profile Name".into(), "".into(), "Mode".into()],
            vec![],
            vec!["Profile Name".into(), "".into(), "Precise".into()],
        ];
        let modes = vec![
            super::ModeCandidate {
                row: 0,
                tab: "Driving".into(),
                c1: "Mode".into(),
            },
            super::ModeCandidate {
                row: 2,
                tab: "Aim".into(),
                c1: "Precise".into(),
            },
        ];
        let renamed = name_modes_from_tabs(&mut rows, &modes);
        assert_eq!(renamed.len(), 1);
        assert_eq!(rows[0][2], "Driving");
        assert_eq!(rows[2][2], "Precise");
    }

    #[test]
    fn limitation_type_keeps_unknown_remaining_count_explicit() {
        let limitation = WorkbookLimitation::WorkbookRows {
            max: MAX_WORKBOOK_ROWS,
            remaining_tabs: None,
        };
        assert_eq!(
            limitation,
            WorkbookLimitation::WorkbookRows {
                max: 30_000,
                remaining_tabs: None
            }
        );
    }
}
