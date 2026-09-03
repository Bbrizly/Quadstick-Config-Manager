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
use quick_xml::events::{BytesStart, Event};
use quick_xml::reader::Reader;
use quick_xml::XmlVersion;
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};
use std::fmt;
use std::io::{Cursor, Read};
use zip::result::ZipError;
use zip::ZipArchive;

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
    SheetCount { max: usize },
    SheetRows { tab: String, max: usize },
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
                write!(formatter, "workbook is too large ({actual} bytes; limit {limit})")
            }
            Self::PartTooLarge { limit, actual } => {
                write!(formatter, "workbook part is too large ({actual} bytes; limit {limit})")
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

    let mut limitation = sheet_cap_hit.then_some(WorkbookLimitation::SheetCount {
        max: MAX_SHEETS,
    });
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
        let flat_profile = !keyword
            && grid
                .first()
                .and_then(|row| row.first())
                .is_some_and(|cell| is_file_header(cell));

        if !keyword && !flat_profile && helper_named {
            skipped.push(SkippedTab {
                name: name.clone(),
                rows: Grid::new(),
                kind: SkippedTabKind::Helper,
            });
            continue;
        }

        let spent = grid.len();
        if keyword || flat_profile {
            if !rows.is_empty() {
                rows.push(Vec::new());
            }
            let keyword_row = rows.len();
            if keyword && keyword_to_type(grid[0][0].trim()) == SheetType::ProfileName {
                modes.push(ModeCandidate {
                    row: keyword_row,
                    tab: name.trim().to_owned(),
                    c1: grid[0].get(2).map_or("", String::as_str).trim().to_owned(),
                });
            }
            rows.extend(grid);
        } else if looks_like_bindings(&grid) {
            skipped.push(SkippedTab {
                name: name.clone(),
                rows: grid,
                kind: SkippedTabKind::UnreadableA1,
            });
        } else {
            continue;
        }

        kept_rows = kept_rows.saturating_add(spent);
        if kept_rows < MAX_WORKBOOK_ROWS {
            continue;
        }

        let remaining = parts.len().saturating_sub(index + 1);
        if remaining > 0 {
            limitation = Some(WorkbookLimitation::WorkbookRows {
                max: MAX_WORKBOOK_ROWS,
                remaining_tabs: if sheet_cap_hit { None } else { Some(remaining) },
            });
        }
        break;
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
        let worth_replacing = mode.c1.is_empty()
            || generic_mode_name(&mode.c1)
            || shared.contains(&fold(&mode.c1));
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
    let workbook = read_part(archive, "xl/workbook.xml")?
        .ok_or(WorkbookError::MissingWorkbookParts)?;
    let relationships = read_part(archive, "xl/_rels/workbook.xml.rels")?
        .ok_or(WorkbookError::MissingWorkbookParts)?;
    let targets = parse_relationships(&relationships)?;

    let mut reader = Reader::from_str(&workbook);
    let mut seen = BTreeSet::new();
    let mut result = Vec::new();
    let mut cap_hit = false;

    loop {
        match reader.read_event().map_err(|_| WorkbookError::InvalidXml)? {
            Event::Start(element) | Event::Empty(element)
                if local_name(element.name().as_ref()) == "sheet" =>
            {
                let Some(id) = attr_local(&element, "id")? else {
                    continue;
                };
                let Some(target) = targets.get(&id) else {
                    continue;
                };
                let part = workbook_part(target);
                if !seen.insert(part.clone()) {
                    continue;
                }
                let name = attr_local(&element, "name")?.unwrap_or_default();
                result.push((name, part));
                if result.len() > MAX_SHEETS {
                    cap_hit = true;
                    break;
                }
            }
            Event::DocType(_) => return Err(WorkbookError::InvalidXml),
            Event::Eof => break,
            _ => {}
        }
    }
    Ok((result, cap_hit))
}

fn parse_relationships(xml: &str) -> Result<BTreeMap<String, String>, WorkbookError> {
    let mut reader = Reader::from_str(xml);
    let mut targets = BTreeMap::new();
    loop {
        match reader.read_event().map_err(|_| WorkbookError::InvalidXml)? {
            Event::Start(element) | Event::Empty(element)
                if local_name(element.name().as_ref()) == "Relationship" =>
            {
                if let (Some(id), Some(target)) = (
                    attr_local(&element, "Id")?,
                    attr_local(&element, "Target")?,
                ) {
                    targets.insert(id, target);
                }
            }
            Event::DocType(_) => return Err(WorkbookError::InvalidXml),
            Event::Eof => break,
            _ => {}
        }
    }
    Ok(targets)
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
                    value.push_str(
                        &text
                            .xml_content(XmlVersion::Implicit1_0)
                            .map_err(|_| WorkbookError::InvalidXml)?,
                    );
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
                let decoded = text
                    .xml_content(XmlVersion::Implicit1_0)
                    .map_err(|_| WorkbookError::InvalidXml)?;
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
            }
            Event::DocType(_) => return Err(WorkbookError::InvalidXml),
            Event::Eof => break,
            _ => {}
        }
    }

    while rows.last().is_some_and(Vec::is_empty) {
        rows.pop();
    }
    Ok(SheetRead { rows, lost_rows })
}

#[derive(Debug)]
struct RowState {
    explicit_number: Option<i32>,
    cells: Vec<String>,
    next_col: usize,
}

impl RowState {
    fn from_element(element: &BytesStart<'_>) -> Result<Self, WorkbookError> {
        let explicit_number = attr_local(element, "r")?
            .map(|value| value.parse::<i32>().map_err(|_| WorkbookError::InvalidXml))
            .transpose()?;
        Ok(Self {
            explicit_number,
            cells: Vec::new(),
            next_col: 0,
        })
    }
}

#[derive(Debug)]
struct CellState {
    reference: String,
    cell_type: String,
    value: String,
    inline_text: String,
}

impl CellState {
    fn from_element(element: &BytesStart<'_>) -> Result<Self, WorkbookError> {
        Ok(Self {
            reference: attr_local(element, "r")?.unwrap_or_default(),
            cell_type: attr_local(element, "t")?.unwrap_or_default(),
            value: String::new(),
            inline_text: String::new(),
        })
    }

    fn resolved_value(self, shared: &[String]) -> String {
        if self.cell_type == "inlineStr" {
            return self.inline_text;
        }
        if self.value.is_empty() {
            return String::new();
        }
        if self.cell_type == "s" {
            return self
                .value
                .parse::<usize>()
                .ok()
                .and_then(|index| shared.get(index))
                .cloned()
                .unwrap_or_default();
        }
        if self.cell_type == "b" {
            return if self.value == "1" { "TRUE" } else { "FALSE" }.to_owned();
        }
        self.value
    }
}

fn place_cell(row: &mut RowState, cell: CellState, shared: &[String]) {
    let col = if cell
        .reference
        .chars()
        .next()
        .is_some_and(|character| character.is_ascii_alphabetic())
    {
        column_index(&cell.reference)
    } else {
        row.next_col
    };
    row.next_col = col.saturating_add(1);
    if col > MAX_COLUMN {
        return;
    }
    if row.cells.len() <= col {
        row.cells.resize(col + 1, String::new());
    }
    row.cells[col] = cell.resolved_value(shared);
}

fn finish_row(
    mut row: RowState,
    rows: &mut Grid,
    last_number: &mut i32,
    lost_rows: &mut bool,
) -> Result<(), WorkbookError> {
    while row.cells.last().is_some_and(String::is_empty) {
        row.cells.pop();
    }

    let number = match row.explicit_number {
        Some(number) => number,
        None => last_number.checked_add(1).ok_or(WorkbookError::InvalidXml)?,
    };
    *last_number = number;
    let Ok(number_usize) = usize::try_from(number) else {
        if !row.cells.is_empty() {
            *lost_rows = true;
        }
        return Ok(());
    };
    if number_usize == 0 || number_usize > MAX_ROWS {
        if !row.cells.is_empty() {
            *lost_rows = true;
        }
        return Ok(());
    }

    let index = number_usize - 1;
    if rows.len() <= index {
        rows.resize_with(index + 1, Vec::new);
    }
    rows[index] = row.cells;
    Ok(())
}

fn column_index(reference: &str) -> usize {
    let mut number = 0usize;
    for character in reference.chars() {
        let digit = if character.is_ascii_uppercase() {
            usize::from(character as u8 - b'A' + 1)
        } else if character.is_ascii_lowercase() {
            usize::from(character as u8 - b'a' + 1)
        } else {
            break;
        };
        number = number.saturating_mul(26).saturating_add(digit);
        if number > MAX_COLUMN + 1 {
            return MAX_COLUMN + 1;
        }
    }
    number.saturating_sub(1)
}

fn attr_local(element: &BytesStart<'_>, name: &str) -> Result<Option<String>, WorkbookError> {
    for attribute in element.attributes() {
        let attribute = attribute.map_err(|_| WorkbookError::InvalidXml)?;
        if local_name(attribute.key.as_ref()) == name {
            return attribute
                .normalized_value(XmlVersion::Implicit1_0)
                .map(|value| Some(value.into_owned()))
                .map_err(|_| WorkbookError::InvalidXml);
        }
    }
    Ok(None)
}

fn local_name(name: &str) -> &str {
    name.rsplit(':').next().unwrap_or(name)
}

fn read_part<R: Read + std::io::Seek>(
    archive: &mut ZipArchive<R>,
    path: &str,
) -> Result<Option<String>, WorkbookError> {
    let mut file = match archive.by_name(path) {
        Ok(file) => file,
        Err(ZipError::FileNotFound) => return Ok(None),
        Err(_) => return Err(WorkbookError::InvalidArchive),
    };
    if file.encrypted() {
        return Err(WorkbookError::InvalidArchive);
    }
    let declared = file.size();
    if declared > MAX_PART_BYTES {
        return Err(WorkbookError::PartTooLarge {
            limit: MAX_PART_BYTES,
            actual: declared,
        });
    }

    let capacity = usize::try_from(declared).map_err(|_| WorkbookError::PartTooLarge {
        limit: MAX_PART_BYTES,
        actual: declared,
    })?;
    let mut bytes = Vec::with_capacity(capacity);
    file.by_ref()
        .take(MAX_PART_BYTES + 1)
        .read_to_end(&mut bytes)
        .map_err(|_| WorkbookError::InvalidArchive)?;
    let actual = u64::try_from(bytes.len()).unwrap_or(u64::MAX);
    if actual > MAX_PART_BYTES {
        return Err(WorkbookError::PartTooLarge {
            limit: MAX_PART_BYTES,
            actual,
        });
    }
    decode_xml(bytes).map(Some)
}

fn decode_xml(bytes: Vec<u8>) -> Result<String, WorkbookError> {
    if bytes.starts_with(&[0xFF, 0xFE]) {
        return decode_utf16(&bytes[2..], true);
    }
    if bytes.starts_with(&[0xFE, 0xFF]) {
        return decode_utf16(&bytes[2..], false);
    }
    if bytes.starts_with(&[0x3C, 0x00, 0x3F, 0x00]) {
        return decode_utf16(&bytes, true);
    }
    if bytes.starts_with(&[0x00, 0x3C, 0x00, 0x3F]) {
        return decode_utf16(&bytes, false);
    }
    let decoded = bytes.strip_prefix(&[0xEF, 0xBB, 0xBF]).unwrap_or(&bytes);
    std::str::from_utf8(decoded)
        .map(str::to_owned)
        .map_err(|_| WorkbookError::InvalidXml)
}

fn decode_utf16(bytes: &[u8], little_endian: bool) -> Result<String, WorkbookError> {
    if !bytes.len().is_multiple_of(2) {
        return Err(WorkbookError::InvalidXml);
    }
    let words = bytes.chunks_exact(2).map(|pair| {
        if little_endian {
            u16::from_le_bytes([pair[0], pair[1]])
        } else {
            u16::from_be_bytes([pair[0], pair[1]])
        }
    });
    char::decode_utf16(words)
        .collect::<Result<String, _>>()
        .map_err(|_| WorkbookError::InvalidXml)
}

#[cfg(test)]
mod tests {
    use super::{
        import_xlsx, repaired_as_mode, SkippedTabKind, WorkbookError, MAX_PART_BYTES,
    };
    use crate::{ProfileFile, Severity, SheetType};
    use std::io::{Cursor, Write};
    use zip::write::SimpleFileOptions;
    use zip::{CompressionMethod, ZipWriter};

    const MAIN_NS: &str = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    const REL_NS: &str =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    const PKG_REL_NS: &str = "http://schemas.openxmlformats.org/package/2006/relationships";

    #[test]
    fn real_multi_tab_workbook_matches_shipping_semantics() {
        let imported = import_xlsx(include_bytes!(
            "../../../tests/QuadStick.Format.Tests/corpus/multi-tab.xlsx"
        ))
        .expect("real workbook imports");
        let file = ProfileFile::load(&imported.csv);
        let names: Vec<_> = file
            .document
            .sheets
            .iter()
            .map(|sheet| sheet.mode_name.as_str())
            .collect();
        assert_eq!(names, ["Main", "Flight", "Mouse", ""]);
        let kinds: Vec<_> = file
            .document
            .sheets
            .iter()
            .map(|sheet| sheet.sheet_type)
            .collect();
        assert_eq!(
            kinds,
            [
                SheetType::ProfileName,
                SheetType::ProfileName,
                SheetType::ProfileName,
                SheetType::Preferences,
            ]
        );
        assert_eq!(file.document.sheets[0].bindings[0].output, "kb_w");
        assert!(
            file.document.sheets[2]
                .bindings
                .iter()
                .any(|binding| binding.output == "kb_keypad_1")
        );
        assert_eq!(file.document.csv_file_name(), Some("nomanssky.csv"));
        assert_eq!(
            imported
                .skipped
                .iter()
                .map(|tab| (tab.name.as_str(), tab.kind))
                .collect::<Vec<_>>(),
            [
                ("Inputs", SkippedTabKind::Helper),
                ("Outputs", SkippedTabKind::Helper),
            ]
        );
        assert!(
            file.issues
                .iter()
                .all(|issue| issue.severity != Severity::Error)
        );
    }

    #[test]
    fn real_single_tab_workbook_preserves_wide_user_data() {
        let imported = import_xlsx(include_bytes!(
            "../../../tests/QuadStick.Format.Tests/corpus/single-tab.xlsx"
        ))
        .expect("real workbook imports");
        let file = ProfileFile::load(&imported.csv);
        assert_eq!(file.document.sheets.len(), 1);
        assert_eq!(file.document.sheets[0].mode_name, "Keyboard & Mouse");
        assert_eq!(file.document.csv_file_name(), Some("div2.csv"));
        assert!(file.grid[3].iter().any(|cell| cell == "inventory"));
        assert!(imported.skipped.is_empty());
        assert!(
            file.issues
                .iter()
                .all(|issue| issue.severity != Severity::Error)
        );
    }

    #[test]
    fn formulas_are_never_executed_and_only_cached_values_import() {
        let rows = concat!(
            "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Profile Name</t></is></c>",
            "<c r=\"C1\" t=\"inlineStr\"><is><t>Formula Test</t></is></c></row>",
            "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>formula.csv</t></is></c></row>",
            "<row r=\"3\"><c r=\"A3\" t=\"inlineStr\"><is><t>Outputs</t></is></c>",
            "<c r=\"B3\" t=\"inlineStr\"><is><t>Function</t></is></c></row>",
            "<row r=\"4\"><c r=\"A4\" t=\"inlineStr\"><is><t>cross</t></is></c>",
            "<c r=\"B4\" t=\"inlineStr\"><is><t>normal</t></is></c>",
            "<c r=\"C4\" t=\"inlineStr\"><is><t>sip</t></is></c>",
            "<c r=\"M4\" t=\"str\"><f>WEBSERVICE(&quot;https://evil.invalid/&quot;)</f>",
            "<v>cached-only</v></c></row>"
        );
        let bytes = workbook(&[("Formula", rows)]);
        let imported = import_xlsx(&bytes).expect("formula workbook imports");
        assert!(imported.csv.contains("cached-only"));
        assert!(!imported.csv.contains("WEBSERVICE"));
        assert!(!imported.csv.contains("evil.invalid"));
        let file = ProfileFile::load(&imported.csv);
        assert_eq!(file.grid[3][12], "cached-only");
    }

    #[test]
    fn unreadable_mode_and_helper_tabs_are_distinguished_for_review() {
        let broken = concat!(
            "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Notes</t></is></c></row>",
            "<row r=\"4\"><c r=\"A4\" t=\"inlineStr\"><is><t>cross</t></is></c>",
            "<c r=\"B4\" t=\"inlineStr\"><is><t>normal</t></is></c>",
            "<c r=\"C4\" t=\"inlineStr\"><is><t>sip</t></is></c></row>"
        );
        let helper = "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Reference</t></is></c></row>";
        let bytes = workbook(&[("Broken Mode", broken), ("Outputs", helper)]);
        let imported = import_xlsx(&bytes).expect("reviewable workbook imports");
        assert_eq!(imported.skipped.len(), 2);
        assert_eq!(imported.skipped[0].kind, SkippedTabKind::UnreadableA1);
        assert_eq!(imported.skipped[1].kind, SkippedTabKind::Helper);
        assert!(imported.skipped[1].rows.is_empty());

        let repaired = repaired_as_mode(&imported.skipped[0]);
        assert_eq!(repaired[0][0], "Profile Name");
        assert_eq!(repaired[0][2], "Broken Mode");
    }

    #[test]
    fn malformed_non_workbook_and_dtd_are_rejected() {
        assert_eq!(
            import_xlsx(b"<html>nope</html>"),
            Err(WorkbookError::InvalidArchive)
        );

        let dtd = format!(
            "<!DOCTYPE workbook [<!ENTITY tabname \"Solo\">]><workbook xmlns=\"{MAIN_NS}\" xmlns:r=\"{REL_NS}\"><sheets><sheet name=\"&tabname;\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"
        );
        let sheet = concat!(
            "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Profile Name</t></is></c></row>"
        );
        let bytes = workbook_with_xml(&dtd, &relationships(1), &[("sheet1.xml", sheet)]);
        assert_eq!(import_xlsx(&bytes), Err(WorkbookError::InvalidXml));
    }

    #[test]
    fn declared_huge_sheet_is_rejected_before_inflation() {
        let huge = format!(
            "<worksheet xmlns=\"{MAIN_NS}\"><sheetData>{}</sheetData></worksheet>",
            " ".repeat(MAX_PART_BYTES as usize + 1)
        );
        let workbook_xml = workbook_xml(&[("Huge", "rId1")]);
        let bytes = workbook_with_xml(
            &workbook_xml,
            &relationships(1),
            &[("sheet1.xml", &huge)],
        );
        assert!(matches!(
            import_xlsx(&bytes),
            Err(WorkbookError::PartTooLarge { .. })
        ));
    }

    #[test]
    fn duplicate_sheet_relationships_are_read_once() {
        let mut sheets = String::new();
        for index in 1..=400 {
            sheets.push_str(&format!(
                "<sheet name=\"Tab{index}\" sheetId=\"{index}\" r:id=\"rId1\"/>"
            ));
        }
        let workbook_xml = format!(
            "<workbook xmlns=\"{MAIN_NS}\" xmlns:r=\"{REL_NS}\"><sheets>{sheets}</sheets></workbook>"
        );
        let rows = concat!(
            "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Profile Name</t></is></c>",
            "<c r=\"C1\" t=\"inlineStr\"><is><t>Solo</t></is></c></row>",
            "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>solo.csv</t></is></c></row>"
        );
        let bytes = workbook_with_xml(
            &workbook_xml,
            &relationships(1),
            &[("sheet1.xml", rows)],
        );
        let imported = import_xlsx(&bytes).expect("duplicate refs are bounded");
        assert_eq!(ProfileFile::load(&imported.csv).document.sheets.len(), 1);
        assert!(imported.csv.len() < 5_000);
    }

    fn workbook(sheets: &[(&str, &str)]) -> Vec<u8> {
        let refs: Vec<_> = sheets
            .iter()
            .enumerate()
            .map(|(index, (name, _))| (*name, format!("rId{}", index + 1)))
            .collect();
        let refs_view: Vec<_> = refs
            .iter()
            .map(|(name, id)| (*name, id.as_str()))
            .collect();
        let workbook_xml = workbook_xml(&refs_view);
        let rels = relationships(sheets.len());
        let parts: Vec<_> = sheets
            .iter()
            .enumerate()
            .map(|(index, (_, rows))| (format!("sheet{}.xml", index + 1), *rows))
            .collect();
        let parts_view: Vec<_> = parts
            .iter()
            .map(|(name, rows)| (name.as_str(), *rows))
            .collect();
        workbook_with_xml(&workbook_xml, &rels, &parts_view)
    }

    fn workbook_xml(sheets: &[(&str, &str)]) -> String {
        let body = sheets
            .iter()
            .enumerate()
            .map(|(index, (name, id))| {
                format!(
                    "<sheet name=\"{name}\" sheetId=\"{}\" r:id=\"{id}\"/>",
                    index + 1
                )
            })
            .collect::<String>();
        format!(
            "<workbook xmlns=\"{MAIN_NS}\" xmlns:r=\"{REL_NS}\"><sheets>{body}</sheets></workbook>"
        )
    }

    fn relationships(count: usize) -> String {
        let body = (1..=count)
            .map(|index| {
                format!(
                    "<Relationship Id=\"rId{index}\" Type=\"{REL_NS}/worksheet\" Target=\"worksheets/sheet{index}.xml\"/>"
                )
            })
            .collect::<String>();
        format!("<Relationships xmlns=\"{PKG_REL_NS}\">{body}</Relationships>")
    }

    fn workbook_with_xml(
        workbook_xml: &str,
        rels: &str,
        sheets: &[(&str, &str)],
    ) -> Vec<u8> {
        let cursor = Cursor::new(Vec::new());
        let mut archive = ZipWriter::new(cursor);
        let options = SimpleFileOptions::default().compression_method(CompressionMethod::Deflated);
        put(&mut archive, "xl/workbook.xml", workbook_xml, options);
        put(&mut archive, "xl/_rels/workbook.xml.rels", rels, options);
        for (name, body) in sheets {
            put(
                &mut archive,
                &format!("xl/worksheets/{name}"),
                &format!(
                    "<worksheet xmlns=\"{MAIN_NS}\"><sheetData>{body}</sheetData></worksheet>"
                ),
                options,
            );
        }
        archive.finish().expect("finish workbook").into_inner()
    }

    fn put(
        archive: &mut ZipWriter<Cursor<Vec<u8>>>,
        path: &str,
        body: &str,
        options: SimpleFileOptions,
    ) {
        archive.start_file(path, options).expect("start zip part");
        archive.write_all(body.as_bytes()).expect("write zip part");
    }
}