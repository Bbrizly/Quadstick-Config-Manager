//! A profile as spreadsheet tabs, the layout a Google Sheet copy of it uses.

use crate::{ProfileFile, SheetType};
use serde::Serialize;
use std::collections::BTreeSet;

/// One tab of a profile laid out as a spreadsheet: a mode's rows under its
/// name, as a Google Sheet backup or share holds them.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct SheetTab {
    pub title: String,
    pub rows: Vec<Vec<String>>,
}

/// The profile as spreadsheet tabs, one per sheet, with titles a sheet
/// accepts (31 characters, no `[]:*?/\\`, unique ignoring case).
#[must_use]
pub fn sheet_tabs(profile: &ProfileFile) -> Vec<SheetTab> {
    if profile.document.sheets.is_empty() {
        return vec![SheetTab {
            title: "Profile".to_owned(),
            rows: profile.grid.clone(),
        }];
    }
    let mut used = BTreeSet::new();
    profile
        .document
        .sheets
        .iter()
        .enumerate()
        .map(|(index, sheet)| {
            let start = if index == 0 && profile.document.has_version_header {
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
                });
            let base = if !sheet.mode_name.trim().is_empty() {
                sheet.mode_name.as_str()
            } else {
                match sheet.sheet_type {
                    SheetType::ProfileName => "Mode",
                    SheetType::Preferences => "Preferences",
                    SheetType::Infrared => "Infrared",
                }
            };
            SheetTab {
                title: unique_title(base, &mut used),
                rows: profile.grid[start..end].to_vec(),
            }
        })
        .collect()
}

fn unique_title(raw: &str, used: &mut BTreeSet<String>) -> String {
    let cleaned: String = raw
        .chars()
        .filter(|c| !c.is_control() && !"[]:*?/\\".contains(*c))
        .take(31)
        .collect();
    let base = if cleaned.trim().is_empty() {
        "Profile"
    } else {
        cleaned.trim()
    };
    if used.insert(base.to_lowercase()) {
        return base.to_owned();
    }
    for suffix in 2..10_000usize {
        let tail = format!(" {suffix}");
        let keep = 31usize.saturating_sub(tail.chars().count());
        let candidate = format!("{}{}", base.chars().take(keep).collect::<String>(), tail);
        if used.insert(candidate.to_lowercase()) {
            return candidate;
        }
    }
    "Profile".to_owned()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn titles_are_cleaned_cut_and_unique() {
        let mut used = BTreeSet::new();
        assert_eq!(unique_title("a/b:c", &mut used), "abc");
        assert_eq!(unique_title("ABC", &mut used), "ABC 2");
        assert_eq!(unique_title("  ", &mut used), "Profile");
        let long = "x".repeat(40);
        assert_eq!(unique_title(&long, &mut used).chars().count(), 31);
    }

    #[test]
    fn a_profile_with_no_sheets_is_one_tab() {
        let tabs = sheet_tabs(&ProfileFile::load(""));
        assert_eq!(tabs.len(), 1);
        assert_eq!(tabs[0].title, "Profile");
    }
}
