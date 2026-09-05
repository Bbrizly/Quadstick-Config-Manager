//! Lossless profile container, editor state and device-safe serialization.
//!
//! The raw grid is canonical mutable state. Parsed document/issues are always a
//! projection of that grid. Undo stores exact raw-grid snapshots so odd columns,
//! comments and formatting survive round-trips exactly like the frozen C# core.

use crate::csv::{Grid, parse, write};
use crate::{Issue, ProfileDocument, SheetType, parse_and_validate};

const HEADER_KEYWORD: &str = "QuadStick Configuration";
const KEYWORD_COLUMNS: usize = 10; // A..J
const MAX_UNDO: usize = 200;

/// Parsed profile plus the lossless raw CSV grid and legacy editor state.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProfileFile {
    pub grid: Grid,
    pub document: ProfileDocument,
    pub issues: Vec<Issue>,
    header_sheet_id: Option<String>,
    undo: Vec<Grid>,
    dirty: bool,
    revision: u64,
    // The raw grid associated with the last completed reparse. Editor methods
    // mutate `grid` and then call `reparse()`: that call snapshots this baseline
    // first. This preserves the C# rule that even a same-value SetCell consumes
    // one undo/revision step, while true no-op commands never call reparse.
    reparse_baseline: Grid,
}

impl ProfileFile {
    /// Load through the same two-step shape as the legacy implementation:
    /// parse to a raw grid, then parse/validate that exact grid without creating
    /// editor history.
    #[must_use]
    pub fn load(csv_text: &str) -> Self {
        let grid = parse(csv_text);
        let mut profile = Self {
            reparse_baseline: grid.clone(),
            grid,
            document: ProfileDocument::default(),
            issues: Vec::new(),
            header_sheet_id: None,
            undo: Vec::new(),
            dirty: false,
            revision: 0,
        };
        profile.parse_current();
        profile
    }

    /// The Google Sheet id stamped into C1 on output only.
    #[must_use]
    pub fn header_sheet_id(&self) -> Option<&str> {
        self.header_sheet_id.as_deref()
    }

    /// Set output-only bookkeeping for C1. This is deliberately not an editor
    /// mutation: no dirty bit, revision or undo entry changes.
    pub fn set_header_sheet_id(&mut self, sheet_id: Option<String>) {
        self.header_sheet_id = sheet_id;
    }

    #[must_use]
    pub const fn dirty(&self) -> bool {
        self.dirty
    }

    #[must_use]
    pub const fn revision(&self) -> u64 {
        self.revision
    }

    #[must_use]
    pub fn can_undo(&self) -> bool {
        !self.undo.is_empty()
    }

    /// Target-side convenience for the legacy `Dirty = false` save boundary.
    /// Saving does not erase undo history and does not change revision.
    pub fn mark_clean(&mut self) {
        self.dirty = false;
    }

    pub fn clear_undo(&mut self) {
        self.undo.clear();
    }

    /// Restore one exact raw-grid snapshot. Undo itself is a content mutation,
    /// so legacy semantics mark dirty and bump revision again.
    pub fn undo(&mut self) -> bool {
        let Some(previous) = self.undo.pop() else {
            return false;
        };
        self.grid = previous;
        self.dirty = true;
        self.revision = self.revision.saturating_add(1);
        self.parse_current();
        self.reparse_baseline = self.grid.clone();
        true
    }

    /// The output-token mapping behind action names. First row wins when the
    /// same case-insensitive name appears more than once, matching the legacy
    /// Dictionary.TryAdd behavior. Row order is retained for deterministic use.
    #[must_use]
    pub fn action_tokens(&self) -> Vec<(String, String)> {
        let mut result: Vec<(String, String)> = Vec::new();
        for binding in self
            .document
            .sheets
            .iter()
            .filter(|sheet| sheet.sheet_type == SheetType::ProfileName)
            .flat_map(|sheet| sheet.bindings.iter())
        {
            if binding.action_name.is_empty() || binding.output.is_empty() {
                continue;
            }
            if result
                .iter()
                .any(|(name, _)| same_name(name, &binding.action_name))
            {
                continue;
            }
            result.push((binding.action_name.clone(), binding.output.clone()));
        }
        result
    }

    /// The file as it goes to disk and to the device.
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
    /// One normalization call is exactly one editor mutation even when adding a
    /// header requires an intermediate parse before separator repair.
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
            // C# reparses here so later-sheet row numbers reflect the inserted
            // header, but it already took its single Snapshot before mutation.
            self.parse_current();
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

    /// Complete one successful editor mutation. All current editor operations
    /// call this exactly once after their no-op guards. Snapshotting the previous
    /// reparse baseline (rather than diffing bytes) preserves same-value edits.
    pub(crate) fn reparse(&mut self) {
        self.snapshot_baseline();
        self.parse_current();
        self.reparse_baseline = self.grid.clone();
    }

    fn snapshot_baseline(&mut self) {
        self.dirty = true;
        self.revision = self.revision.saturating_add(1);
        self.undo.push(self.reparse_baseline.clone());
        if self.undo.len() > MAX_UNDO {
            self.undo.remove(0);
        }
    }

    fn parse_current(&mut self) {
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
            .filter(|row| *row >= 2 && !self.grid[*row - 2].is_empty())
            .collect()
    }
}

fn same_name(left: &str, right: &str) -> bool {
    left.eq_ignore_ascii_case(right) || left.to_lowercase() == right.to_lowercase()
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
            if safe.contains('\n') || safe.contains('\r') {
                safe = safe
                    .split(['\n', '\r'])
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
        assert!(!profile.dirty());
        assert_eq!(profile.revision(), 0);
        assert!(!profile.can_undo());
        assert!(
            profile
                .to_csv_text()
                .starts_with("QuadStick Configuration,Version 1.5,sheet-123,Name\r\n")
        );

        profile.set_header_sheet_id(None);
        assert!(
            profile
                .to_csv_text()
                .starts_with("QuadStick Configuration,Version 1.5,old,Name\r\n")
        );

        let mut headerless =
            ProfileFile::load("Profile Name,,Mode\nfile.csv\nOutputs,Function,usb\n");
        headerless.set_header_sheet_id(Some("sheet-123".to_owned()));
        assert!(
            headerless
                .to_csv_text()
                .starts_with("Profile Name,,Mode\r\n")
        );
    }

    #[test]
    fn clean_boundary_keeps_undo_and_undo_makes_the_profile_dirty_again() {
        let mut profile = ProfileFile::load(
            "Profile Name,,Mode\nfile.csv\nOutputs,Function,usb\ntriangle,normal,lip\n",
        );
        assert!(!profile.dirty());
        assert_eq!(profile.revision(), 0);
        profile.grid[3][0] = "circle".to_owned();
        profile.reparse();
        assert!(profile.dirty());
        assert_eq!(profile.revision(), 1);
        assert!(profile.can_undo());

        profile.mark_clean();
        assert!(!profile.dirty());
        assert_eq!(profile.revision(), 1);
        assert!(profile.can_undo());

        assert!(profile.undo());
        assert!(profile.dirty());
        assert_eq!(profile.revision(), 2);
        assert_eq!(profile.grid[3][0], "triangle");
    }

    #[test]
    fn undo_depth_is_capped_at_200_snapshots() {
        let mut profile = ProfileFile::load(
            "Profile Name,,Mode\nfile.csv\nOutputs,Function,usb\ntriangle,normal,lip\n",
        );
        for index in 0..201 {
            profile.grid[3][0] = format!("token-{index}");
            profile.reparse();
        }
        assert_eq!(profile.revision(), 201);
        for _ in 0..200 {
            assert!(profile.undo());
        }
        assert!(!profile.undo());
        assert_eq!(profile.revision(), 401);
        // The oldest of 201 snapshots was discarded, so we cannot return to
        // the original triangle state; token-0 is the oldest retained state.
        assert_eq!(profile.grid[3][0], "token-0");
    }

    #[test]
    fn action_tokens_are_first_row_wins_case_insensitively() {
        let profile = ProfileFile::load(
            "Profile Name,,Mode\nfile.csv\nOutputs,Function,usb,,,,,,,,,Action\ntriangle,normal,lip,,,,,,,,,Jump\ncircle,normal,mp_center_sip,,,,,,,,,jump\nsquare,normal,mp_center_puff,,,,,,,,,Use\n",
        );
        assert_eq!(
            profile.action_tokens(),
            vec![
                ("Jump".to_owned(), "triangle".to_owned()),
                ("Use".to_owned(), "square".to_owned()),
            ]
        );
    }
}
