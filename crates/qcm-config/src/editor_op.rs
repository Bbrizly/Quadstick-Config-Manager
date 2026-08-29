//! Typed, lossless profile editor operations.
//!
//! This ports the mutation portion of the frozen C# `ProfileFile` while keeping
//! OS/Tauri/UI concerns out of the format crate. TASK-016 adds undo/dirty/
//! revision bookkeeping around these same mutation points.

use crate::vocab::{is_sheet_keyword, load_validation};
use crate::{ProfileFile, SheetType};
use serde::{Deserialize, Serialize};

pub const ACTION_COLUMN: usize = 11; // L
pub const NOTE_COLUMN: usize = 10; // K
pub const MAX_ACTION_NAME: usize = 40; // UTF-16 code units, matching .NET String.Length
const FIRST_INPUT_COLUMN: usize = 2; // C
const LAST_INPUT_COLUMN: usize = 9; // J

/// Stable typed editor command used by parity tests and, later, IPC/qsf.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "op", rename_all = "snake_case")]
pub enum EditorOp {
    SetCell {
        row: usize,
        col: usize,
        value: String,
    },
    SetOutput {
        row: usize,
        token: String,
        #[serde(default)]
        action: String,
    },
    AddRow {
        sheet: usize,
    },
    DeleteRow {
        row: usize,
    },
    MoveRow {
        from: usize,
        to: usize,
    },
    AddMode {
        name: String,
    },
    RenameMode {
        sheet: usize,
        name: String,
    },
    SetModeChannel {
        sheet: usize,
        channel: String,
    },
    Normalize,
}

impl EditorOp {
    #[must_use]
    pub const fn name(&self) -> &'static str {
        match self {
            Self::SetCell { .. } => "set_cell",
            Self::SetOutput { .. } => "set_output",
            Self::AddRow { .. } => "add_row",
            Self::DeleteRow { .. } => "delete_row",
            Self::MoveRow { .. } => "move_row",
            Self::AddMode { .. } => "add_mode",
            Self::RenameMode { .. } => "rename_mode",
            Self::SetModeChannel { .. } => "set_mode_channel",
            Self::Normalize => "normalize",
        }
    }
}

impl ProfileFile {
    /// Apply one typed editor operation. The boolean matches the legacy oracle's
    /// `applied` result for the currently supported operation contract.
    pub fn apply_editor_op(&mut self, op: &EditorOp) -> bool {
        match op {
            EditorOp::SetCell { row, col, value } => self.set_cell(*row, *col, value.clone()),
            EditorOp::SetOutput { row, token, action } => self.set_output(*row, token, action),
            EditorOp::AddRow { sheet } => self.add_binding_row(*sheet).is_some(),
            EditorOp::DeleteRow { row } => self.delete_row(*row),
            EditorOp::MoveRow { from, to } => self.move_row(*from, *to),
            EditorOp::AddMode { name } => self.add_mode_sheet(name).is_some(),
            EditorOp::RenameMode { sheet, name } => self.rename_mode(*sheet, name),
            EditorOp::SetModeChannel { sheet, channel } => self.set_mode_channel(*sheet, channel),
            EditorOp::Normalize => self.normalize_for_device_csv(),
        }
    }

    /// Legacy `GetCell`: one-based row, zero-based column, trimmed projection.
    #[must_use]
    pub fn get_cell(&self, row: usize, col: usize) -> &str {
        if row == 0 || row > self.grid.len() {
            return "";
        }
        self.grid[row - 1].get(col).map_or("", |value| value.trim())
    }

    /// Set one raw grid cell and immediately reparse.
    pub fn set_cell(&mut self, row: usize, col: usize, value: String) -> bool {
        let Some(target) = self.widen(row, col) else {
            return false;
        };
        target[col] = value;
        self.reparse();
        true
    }

    /// Set output token and optional profile-owned action name as one mutation.
    pub fn set_output(&mut self, row: usize, token: &str, action_name: &str) -> bool {
        let name = action_name.trim();
        if !name.is_empty() && !Self::is_legal_action_name(name) {
            return false;
        }
        if !name.is_empty() && self.sheet_type_at(row) != Some(SheetType::ProfileName) {
            return false;
        }
        if !name.is_empty()
            && self.nameable_bindings().any(|binding| {
                binding.row != row
                    && same_name(&binding.action_name, name)
                    && binding.output != token
            })
        {
            return false;
        }
        if self.get_cell(row, 0) == token && self.get_cell(row, ACTION_COLUMN) == name {
            return false;
        }

        let Some(target) = self.widen(row, ACTION_COLUMN) else {
            return false;
        };
        target[0] = token.to_owned();
        target[ACTION_COLUMN] = name.to_owned();
        if !name.is_empty() {
            self.label_action_column(row);
        }
        self.reparse();
        true
    }

    /// Profile action names are non-empty, at most 40 .NET-style UTF-16 units,
    /// and may not look like a real output token in the human picker.
    #[must_use]
    pub fn is_legal_action_name(name: &str) -> bool {
        let trimmed = name.trim();
        let len = trimmed.encode_utf16().count();
        if len == 0 || len > MAX_ACTION_NAME {
            return false;
        }
        let token = trimmed.replace(' ', "_");
        !is_known_output_loose(&token)
    }

    /// Action names in row order, de-duplicated case-insensitively.
    #[must_use]
    pub fn action_names(&self) -> Vec<String> {
        let mut result: Vec<String> = Vec::new();
        for binding in self.nameable_bindings() {
            if binding.action_name.is_empty()
                || result
                    .iter()
                    .any(|existing| same_name(existing, &binding.action_name))
            {
                continue;
            }
            result.push(binding.action_name.clone());
        }
        result
    }

    /// Retarget every row carrying an action name to a new output token.
    pub fn retarget_action(&mut self, name: &str, token: &str) -> bool {
        if name.is_empty() || token.is_empty() {
            return false;
        }
        let rows = self
            .nameable_bindings()
            .filter(|binding| same_name(&binding.action_name, name) && binding.output != token)
            .map(|binding| binding.row)
            .collect::<Vec<_>>();
        if rows.is_empty() {
            return false;
        }
        for row in rows {
            if let Some(target) = self.widen(row, 0) {
                target[0] = token.to_owned();
            }
        }
        self.reparse();
        true
    }

    /// Remove an action name everywhere without changing its output token.
    pub fn clear_action(&mut self, name: &str) -> bool {
        if name.is_empty() {
            return false;
        }
        let rows = self
            .nameable_bindings()
            .filter(|binding| same_name(&binding.action_name, name))
            .map(|binding| binding.row)
            .collect::<Vec<_>>();
        if rows.is_empty() {
            return false;
        }
        for row in rows {
            if let Some(target) = self.widen(row, ACTION_COLUMN) {
                target[ACTION_COLUMN].clear();
            }
        }
        self.reparse();
        true
    }

    /// Rename an action name everywhere it appears.
    pub fn rename_action(&mut self, old_name: &str, new_name: &str) -> bool {
        let to = new_name.trim();
        if old_name.is_empty() || to == old_name || !Self::is_legal_action_name(to) {
            return false;
        }
        let rows = self
            .nameable_bindings()
            .filter(|binding| same_name(&binding.action_name, old_name))
            .map(|binding| binding.row)
            .collect::<Vec<_>>();
        if rows.is_empty() {
            return false;
        }
        for row in rows {
            if let Some(target) = self.widen(row, ACTION_COLUMN) {
                target[ACTION_COLUMN] = to.to_owned();
            }
        }
        self.reparse();
        true
    }

    /// Insert a new binding/preferences row after the sheet's current bindings.
    /// Returns the new one-based row number.
    pub fn add_binding_row(&mut self, sheet_index: usize) -> Option<usize> {
        let sheet = self.document.sheets.get(sheet_index)?;
        let insert_at = sheet
            .bindings
            .last()
            .map_or(sheet.start_row + 2, |binding| binding.row);
        let row = if sheet.sheet_type == SheetType::ProfileName {
            vec![String::new(), "normal".to_owned(), String::new()]
        } else {
            vec![String::new(), "0".to_owned()]
        };
        if insert_at > self.grid.len() {
            return None;
        }
        self.grid.insert(insert_at, row);
        self.reparse();
        Some(insert_at + 1)
    }

    /// Append an empty mode header using the first profile's label/channel.
    pub fn add_mode_sheet(&mut self, mode_name: &str) -> Option<usize> {
        let first = self
            .document
            .sheets
            .iter()
            .find(|sheet| sheet.sheet_type == SheetType::ProfileName);
        let label = first
            .filter(|sheet| !sheet.header_label.is_empty())
            .map_or("PlayStation Outputs", |sheet| sheet.header_label.as_str())
            .to_owned();
        let channel = first.map_or(String::new(), |sheet| sheet.channel.clone());

        self.grid.push(vec![
            "Profile Name".to_owned(),
            String::new(),
            mode_name.to_owned(),
        ]);
        self.grid.push(Vec::new());
        self.grid.push(vec![label, "Function".to_owned(), channel]);
        self.reparse();
        self.document.sheets.len().checked_sub(1)
    }

    /// Append externally supplied sheet rows as one unit.
    pub fn append_sheet_rows(&mut self, rows: &[Vec<String>]) -> Option<usize> {
        let first = rows.first()?.first()?;
        if !is_sheet_keyword(first.trim()) {
            return None;
        }
        if !self.grid.is_empty() {
            self.grid.push(Vec::new());
        }
        self.grid.extend(rows.iter().cloned());
        self.reparse();
        self.document.sheets.len().checked_sub(1)
    }

    /// Add the one allowed Preferences sheet.
    pub fn add_preferences_sheet(&mut self) -> Option<usize> {
        if self
            .document
            .sheets
            .iter()
            .any(|sheet| sheet.sheet_type == SheetType::Preferences)
        {
            return None;
        }
        self.grid.push(vec!["Preferences".to_owned()]);
        self.grid.push(Vec::new());
        self.grid.push(vec![
            "Preference".to_owned(),
            "Value".to_owned(),
            "Units".to_owned(),
            "Description".to_owned(),
        ]);
        self.reparse();
        self.document.sheets.len().checked_sub(1)
    }

    /// Move one whole raw row to another one-based row position.
    pub fn move_row(&mut self, from_row: usize, to_row: usize) -> bool {
        if from_row == to_row
            || from_row == 0
            || to_row == 0
            || from_row > self.grid.len()
            || to_row > self.grid.len()
        {
            return false;
        }
        let moved = self.grid.remove(from_row - 1);
        self.grid.insert(to_row - 1, moved);
        self.reparse();
        true
    }

    /// Move several rows as one contiguous block using the legacy landing rule.
    pub fn move_rows(&mut self, from_rows: &[usize], to_row: usize) -> bool {
        let mut moving = from_rows
            .iter()
            .copied()
            .filter(|row| *row >= 1 && *row <= self.grid.len())
            .collect::<Vec<_>>();
        moving.sort_unstable();
        moving.dedup();
        if moving.is_empty() || to_row == 0 || to_row > self.grid.len() || moving.contains(&to_row)
        {
            return false;
        }

        let block = moving
            .iter()
            .map(|row| self.grid[*row - 1].clone())
            .collect::<Vec<_>>();
        for row in moving.iter().rev() {
            self.grid.remove(*row - 1);
        }
        let index = (to_row - 1).min(self.grid.len());
        self.grid.splice(index..index, block);
        self.reparse();
        true
    }

    pub fn move_rows_before(&mut self, from_rows: &[usize], anchor_row: usize) -> bool {
        self.move_rows_at(from_rows, anchor_row, false)
    }

    pub fn move_rows_after(&mut self, from_rows: &[usize], anchor_row: usize) -> bool {
        self.move_rows_at(from_rows, anchor_row, true)
    }

    fn move_rows_at(&mut self, from_rows: &[usize], anchor_row: usize, after: bool) -> bool {
        if anchor_row == 0 || anchor_row > self.grid.len() {
            return false;
        }
        let mut moving = from_rows
            .iter()
            .copied()
            .filter(|row| *row >= 1 && *row <= self.grid.len() && *row != anchor_row)
            .collect::<Vec<_>>();
        moving.sort_unstable();
        moving.dedup();
        if moving.is_empty() {
            return false;
        }
        let block = moving
            .iter()
            .map(|row| self.grid[*row - 1].clone())
            .collect::<Vec<_>>();
        for row in moving.iter().rev() {
            self.grid.remove(*row - 1);
        }
        let removed_before_anchor = moving.iter().filter(|row| **row < anchor_row).count();
        let index = anchor_row - 1 - removed_before_anchor + usize::from(after);
        let index = index.min(self.grid.len());
        self.grid.splice(index..index, block);
        self.reparse();
        true
    }

    pub fn swap_rows(&mut self, row_a: usize, row_b: usize) -> bool {
        if row_a == row_b
            || row_a == 0
            || row_b == 0
            || row_a > self.grid.len()
            || row_b > self.grid.len()
        {
            return false;
        }
        self.grid.swap(row_a - 1, row_b - 1);
        self.reparse();
        true
    }

    /// Move a stray input-column word to K, joining an existing note with `; `.
    pub fn move_input_to_notes(&mut self, row: usize, col: usize) -> bool {
        let value = self.get_cell(row, col).to_owned();
        if value.is_empty() || !(FIRST_INPUT_COLUMN..=LAST_INPUT_COLUMN).contains(&col) {
            return false;
        }
        let existing = self.get_cell(row, NOTE_COLUMN).to_owned();
        let Some(target) = self.widen(row, NOTE_COLUMN) else {
            return false;
        };
        target[NOTE_COLUMN] = if existing.is_empty() {
            value.clone()
        } else {
            format!("{existing}; {value}")
        };
        target[col].clear();
        self.reparse();
        true
    }

    #[must_use]
    pub fn can_move_input_to_action_name(&self, row: usize, col: usize) -> bool {
        let value = self.get_cell(row, col).trim();
        !value.is_empty()
            && (FIRST_INPUT_COLUMN..=LAST_INPUT_COLUMN).contains(&col)
            && self.can_name_row(row, value)
    }

    pub fn move_input_to_action_name(&mut self, row: usize, col: usize) -> bool {
        let value = self.get_cell(row, col).trim().to_owned();
        if value.is_empty()
            || !(FIRST_INPUT_COLUMN..=LAST_INPUT_COLUMN).contains(&col)
            || !self.can_name_row(row, &value)
        {
            return false;
        }
        let Some(target) = self.widen(row, ACTION_COLUMN) else {
            return false;
        };
        target[ACTION_COLUMN] = value;
        target[col].clear();
        self.label_action_column(row);
        self.reparse();
        true
    }

    #[must_use]
    pub fn can_swap_inputs(&self, row: usize, a: usize, b: usize) -> bool {
        if a == b
            || row == 0
            || row > self.grid.len()
            || !(FIRST_INPUT_COLUMN..=LAST_INPUT_COLUMN).contains(&a)
            || !(FIRST_INPUT_COLUMN..=LAST_INPUT_COLUMN).contains(&b)
        {
            return false;
        }
        !self.get_cell(row, a).is_empty() || !self.get_cell(row, b).is_empty()
    }

    pub fn swap_inputs(&mut self, row: usize, a: usize, b: usize) -> bool {
        if !self.can_swap_inputs(row, a, b) {
            return false;
        }
        let left = self.get_cell(row, a).to_owned();
        let right = self.get_cell(row, b).to_owned();
        let Some(target) = self.widen(row, a.max(b)) else {
            return false;
        };
        target[a] = right;
        target[b] = left;
        self.reparse();
        true
    }

    #[must_use]
    pub fn can_move_cell(&self, row: usize, from_col: usize, to_col: usize) -> bool {
        if from_col == to_col || row == 0 || row > self.grid.len() {
            return false;
        }
        let value = self.get_cell(row, from_col).trim();
        if value.is_empty() {
            return false;
        }
        if to_col == ACTION_COLUMN && !self.can_name_row(row, value) {
            return false;
        }
        self.get_cell(row, to_col).is_empty() || to_col == NOTE_COLUMN
    }

    pub fn move_cell(&mut self, row: usize, from_col: usize, to_col: usize) -> bool {
        if !self.can_move_cell(row, from_col, to_col) {
            return false;
        }
        let value = self.get_cell(row, from_col).trim().to_owned();
        let target_value = self.get_cell(row, to_col).trim().to_owned();
        let Some(target) = self.widen(row, from_col.max(to_col)) else {
            return false;
        };
        target[to_col] = if to_col == NOTE_COLUMN && !target_value.is_empty() {
            format!("{target_value}; {value}")
        } else {
            value
        };
        target[from_col].clear();
        if to_col == ACTION_COLUMN {
            self.label_action_column(row);
        }
        self.reparse();
        true
    }

    pub fn delete_rows(&mut self, rows: &[usize]) -> bool {
        let mut valid = rows
            .iter()
            .copied()
            .filter(|row| *row >= 1 && *row <= self.grid.len())
            .collect::<Vec<_>>();
        valid.sort_unstable();
        valid.dedup();
        if valid.is_empty() {
            return false;
        }
        for row in valid.iter().rev() {
            self.grid.remove(*row - 1);
        }
        self.reparse();
        true
    }

    pub fn delete_row(&mut self, row: usize) -> bool {
        if row == 0 || row > self.grid.len() {
            return false;
        }
        self.grid.remove(row - 1);
        self.reparse();
        true
    }

    /// Remove one non-empty input by parsed input index, then repack C..J.
    pub fn remove_input(&mut self, row: usize, input_index: usize) -> bool {
        let binding = self
            .document
            .sheets
            .iter()
            .flat_map(|sheet| &sheet.bindings)
            .find(|binding| binding.row == row)
            .cloned();
        let Some(binding) = binding else {
            return false;
        };
        if input_index >= binding.inputs.len() {
            return false;
        }
        let remaining = binding
            .inputs
            .into_iter()
            .enumerate()
            .filter_map(|(index, input)| (index != input_index).then_some(input))
            .collect::<Vec<_>>();
        let Some(target) = self.widen(row, 1 + remaining.len()) else {
            return false;
        };
        for column in FIRST_INPUT_COLUMN..=LAST_INPUT_COLUMN {
            if column >= target.len() {
                break;
            }
            target[column] = remaining
                .get(column - FIRST_INPUT_COLUMN)
                .cloned()
                .unwrap_or_default();
        }
        self.reparse();
        true
    }

    pub fn rename_mode(&mut self, sheet_index: usize, name: &str) -> bool {
        let Some(sheet) = self.document.sheets.get(sheet_index) else {
            return false;
        };
        if sheet.sheet_type != SheetType::ProfileName {
            return false;
        }
        let trimmed = name.trim();
        if trimmed.is_empty() || trimmed == sheet.mode_name {
            return false;
        }
        self.set_cell(sheet.start_row, 2, trimmed.to_owned())
    }

    pub fn set_mode_channel(&mut self, sheet_index: usize, channel: &str) -> bool {
        let Some(sheet) = self.document.sheets.get(sheet_index) else {
            return false;
        };
        if sheet.sheet_type != SheetType::ProfileName || channel == sheet.channel {
            return false;
        }
        self.set_cell(sheet.start_row + 2, 2, channel.to_owned())
    }

    pub fn duplicate_mode(&mut self, sheet_index: usize, new_name: &str) -> Option<usize> {
        let sheet = self.document.sheets.get(sheet_index)?;
        if sheet.sheet_type != SheetType::ProfileName {
            return None;
        }
        let trimmed = new_name.trim();
        if trimmed.is_empty() {
            return None;
        }
        let (start, end) = self.sheet_row_range(sheet_index)?;
        let mut clones = self.grid[start - 1..end].to_vec();
        let header = clones.first_mut()?;
        if header.len() < 3 {
            header.resize(3, String::new());
        }
        header[2] = trimmed.to_owned();
        if let Some(filename_row) = clones.get_mut(1)
            && !filename_row.is_empty()
        {
            filename_row[0].clear();
        }
        self.grid.extend(clones);
        self.reparse();
        self.document.sheets.len().checked_sub(1)
    }

    pub fn delete_mode(&mut self, sheet_index: usize) -> bool {
        let Some(sheet) = self.document.sheets.get(sheet_index) else {
            return false;
        };
        if sheet.sheet_type == SheetType::Infrared {
            return false;
        }
        if sheet.sheet_type == SheetType::ProfileName
            && self
                .document
                .sheets
                .iter()
                .filter(|candidate| candidate.sheet_type == SheetType::ProfileName)
                .count()
                <= 1
        {
            return false;
        }
        if sheet_index == 0 {
            let Some((next_start, next_end)) = self.sheet_row_range(1) else {
                return false;
            };
            if next_end.saturating_sub(next_start) < 1 {
                return false;
            }
        }

        let Some((start, end)) = self.sheet_row_range(sheet_index) else {
            return false;
        };
        if sheet_index == 0 {
            let filename = self
                .grid
                .get(start)
                .and_then(|row| row.first())
                .cloned()
                .unwrap_or_default();
            let slot = end + 1;
            let Some(row) = self.grid.get_mut(slot) else {
                return false;
            };
            if row.is_empty() {
                *row = vec![filename];
            } else {
                row[0] = filename;
            }
        }
        self.grid.drain(start - 1..end);
        self.reparse();
        true
    }

    /// Move a visible mode/preferences sheet one slot, stepping over Infrared.
    pub fn move_mode(&mut self, sheet_index: usize, delta: isize) -> bool {
        let sheets = &self.document.sheets;
        if delta == 0
            || sheet_index >= sheets.len()
            || sheets[sheet_index].sheet_type == SheetType::Infrared
        {
            return false;
        }
        let step: isize = if delta > 0 { 1 } else { -1 };
        let mut other = None;
        let mut index = sheet_index as isize + step;
        while index >= 0 && (index as usize) < sheets.len() {
            let candidate = index as usize;
            if sheets[candidate].sheet_type != SheetType::Infrared {
                other = Some(candidate);
                break;
            }
            index += step;
        }
        let Some(other) = other else {
            return false;
        };
        let lo = sheet_index.min(other);
        let hi = sheet_index.max(other);
        let Some((lo_start, lo_end)) = self.sheet_row_range(lo) else {
            return false;
        };
        let Some((hi_start, hi_end)) = self.sheet_row_range(hi) else {
            return false;
        };
        if lo == 0 && hi_end.saturating_sub(hi_start) < 1 {
            return false;
        }

        let mut lo_block = self.grid[lo_start - 1..lo_end].to_vec();
        let mut hi_block = self.grid[hi_start - 1..hi_end].to_vec();
        let mid_block = self.grid[lo_end..hi_start - 1].to_vec();
        if lo == 0 {
            let filename = lo_block
                .get(1)
                .and_then(|row| row.first())
                .cloned()
                .unwrap_or_default();
            if let Some(row) = lo_block.get_mut(1)
                && !row.is_empty()
            {
                row[0].clear();
            }
            let Some(row) = hi_block.get_mut(1) else {
                return false;
            };
            if row.is_empty() {
                *row = vec![filename];
            } else {
                row[0] = filename;
            }
        }
        let replacement = hi_block
            .into_iter()
            .chain(mid_block)
            .chain(lo_block)
            .collect::<Vec<_>>();
        self.grid.splice(lo_start - 1..hi_end, replacement);
        self.reparse();
        true
    }

    fn widen(&mut self, row: usize, col: usize) -> Option<&mut Vec<String>> {
        if row == 0 {
            return None;
        }
        while self.grid.len() < row {
            self.grid.push(Vec::new());
        }
        let target = &mut self.grid[row - 1];
        if target.len() <= col {
            target.resize(col + 1, String::new());
        }
        Some(target)
    }

    fn sheet_type_at(&self, row: usize) -> Option<SheetType> {
        self.document
            .sheets
            .iter()
            .rev()
            .find(|sheet| sheet.start_row <= row)
            .map(|sheet| sheet.sheet_type)
    }

    fn label_action_column(&mut self, row: usize) {
        let Some(sheet) = self
            .document
            .sheets
            .iter()
            .rev()
            .find(|sheet| sheet.start_row <= row)
        else {
            return;
        };
        if sheet.sheet_type != SheetType::ProfileName {
            return;
        }
        let label_row = sheet.start_row + 2;
        if label_row >= row || !self.get_cell(label_row, ACTION_COLUMN).is_empty() {
            return;
        }
        if let Some(target) = self.widen(label_row, ACTION_COLUMN) {
            target[ACTION_COLUMN] = "Action".to_owned();
        }
    }

    fn nameable_bindings(&self) -> impl Iterator<Item = &crate::Binding> {
        self.document
            .sheets
            .iter()
            .filter(|sheet| sheet.sheet_type == SheetType::ProfileName)
            .flat_map(|sheet| sheet.bindings.iter())
    }

    fn can_name_row(&self, row: usize, value: &str) -> bool {
        Self::is_legal_action_name(value)
            && self.sheet_type_at(row) == Some(SheetType::ProfileName)
            && self.get_cell(row, ACTION_COLUMN).is_empty()
            && !self.nameable_bindings().any(|binding| {
                binding.row != row
                    && same_name(&binding.action_name, value)
                    && binding.output != self.get_cell(row, 0)
            })
    }

    fn sheet_row_range(&self, sheet_index: usize) -> Option<(usize, usize)> {
        let sheet = self.document.sheets.get(sheet_index)?;
        let start = sheet.start_row;
        let end = self
            .document
            .sheets
            .get(sheet_index + 1)
            .map_or(self.grid.len(), |next| next.start_row - 1);
        Some((start, end))
    }
}

fn same_name(left: &str, right: &str) -> bool {
    left.eq_ignore_ascii_case(right) || left.to_lowercase() == right.to_lowercase()
}

fn is_known_output_loose(candidate: &str) -> bool {
    load_validation().is_ok_and(|validation| {
        validation
            .outputs_ps3
            .iter()
            .chain(&validation.outputs_xbox)
            .any(|known| known.eq_ignore_ascii_case(candidate))
    })
}

#[cfg(test)]
mod tests {
    use super::{ACTION_COLUMN, EditorOp, NOTE_COLUMN};
    use crate::ProfileFile;

    #[test]
    fn action_name_rules_and_healers_match_legacy_shape() {
        let mut profile = ProfileFile::load(
            "Profile Name,,Mode\nfile.csv\nOutputs,Function,usb\ntriangle,normal,lip,Aim\n",
        );
        assert!(ProfileFile::is_legal_action_name("Jump"));
        assert!(!ProfileFile::is_legal_action_name("Triangle"));
        assert!(profile.move_input_to_notes(4, 3));
        assert_eq!(profile.get_cell(4, NOTE_COLUMN), "Aim");
        assert!(profile.set_cell(4, 3, "Shoot".to_owned()));
        assert!(profile.move_input_to_action_name(4, 3));
        assert_eq!(profile.get_cell(4, ACTION_COLUMN), "Shoot");
    }

    #[test]
    fn typed_core_sequence_stays_lossless() {
        let mut profile = ProfileFile::load(
            "Profile Name,,Mode 1\nconfig.csv\nPlayStation Outputs,Function,usb\ntriangle,normal,lip\n",
        );
        let operations = [
            EditorOp::SetCell {
                row: 4,
                col: 1,
                value: "toggle".to_owned(),
            },
            EditorOp::SetOutput {
                row: 4,
                token: "circle".to_owned(),
                action: "Jump".to_owned(),
            },
            EditorOp::AddRow { sheet: 0 },
            EditorOp::AddMode {
                name: "Mode 2".to_owned(),
            },
        ];
        for operation in &operations {
            assert!(profile.apply_editor_op(operation));
        }
        assert_eq!(profile.document.sheets.len(), 2);
        assert_eq!(profile.document.sheets[0].bindings[0].output, "circle");
        assert_eq!(profile.document.sheets[0].bindings[0].action_name, "Jump");
        assert!(
            profile
                .grid
                .iter()
                .any(|row| row.get(ACTION_COLUMN).is_some_and(|v| v == "Action"))
        );
    }

    #[test]
    fn deleting_first_mode_hands_filename_to_new_first_mode() {
        let mut profile = ProfileFile::load(
            "Profile Name,,One\nconfig.csv\nOutputs,Function,usb\nx,normal,lip\n\nProfile Name,,Two\n\nOutputs,Function,usb\ncircle,normal,lip\n",
        );
        assert!(profile.delete_mode(0));
        assert_eq!(profile.document.sheets[0].mode_name, "Two");
        assert_eq!(profile.document.csv_file_name(), Some("config.csv"));
    }
}
