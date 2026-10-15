//! What replacing one profile with another would change, port of
//! `ProfileChanges.cs` (follow-link `7fc908a`).
//!
//! Modes are matched by type and name, and mappings within a mode as whole
//! lines (output, behaviour, action name, inputs). An edited mapping therefore
//! shows as one added and one removed. Anything else that differs
//! (preferences, notes, the header) is one `OtherChanged`, also when mappings
//! changed beside it. An empty list means the two are the same profile.
//! The words are the frontend's: this returns typed lines.

use crate::{ACTION_COLUMN, ModeSheet, NOTE_COLUMN, ProfileFile, SheetType};
use std::collections::BTreeMap;

const SEP: &str = "\u{1f}";

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ProfileChange {
    /// The copy on this computer could not be read.
    LocalUnreadable,
    ModeAdded(String),
    ModeRemoved(String),
    ModeChanged {
        mode: String,
        added: usize,
        removed: usize,
    },
    OtherChanged,
}

type Key = (u8, String);

fn key(sheet: &ModeSheet) -> Key {
    let kind = match sheet.sheet_type {
        SheetType::ProfileName => 0,
        SheetType::Preferences => 1,
        SheetType::Infrared => 2,
    };
    (kind, sheet.mode_name.clone())
}

/// `local` is `None` when the local copy could not be read.
#[must_use]
pub fn profile_changes(local: Option<&ProfileFile>, incoming: &ProfileFile) -> Vec<ProfileChange> {
    let Some(local) = local else {
        return vec![ProfileChange::LocalUnreadable];
    };
    let mine = by_name(&local.document.sheets);
    let theirs = by_name(&incoming.document.sheets);
    let has = |list: &[(Key, &ModeSheet)], k: &Key| list.iter().any(|(other, _)| other == k);
    let mut lines = Vec::new();

    for (k, sheet) in &theirs {
        if !has(&mine, k) {
            lines.push(ProfileChange::ModeAdded(label(sheet)));
        }
    }
    for (k, sheet) in &mine {
        if !has(&theirs, k) {
            lines.push(ProfileChange::ModeRemoved(label(sheet)));
        }
    }
    for (k, sheet) in &theirs {
        let Some((_, old)) = mine.iter().find(|(other, _)| other == k) else {
            continue;
        };
        let (added, removed) = difference(&mappings(old), &mappings(sheet));
        if added + removed > 0 {
            lines.push(ProfileChange::ModeChanged {
                mode: label(sheet),
                added,
                removed,
            });
        }
    }

    // Said whether or not mappings changed too: a note or a preference changed
    // beside a mapping is still a change the person is agreeing to.
    let other = if lines.is_empty() {
        rows(local) != rows(incoming)
    } else {
        rest(local, &theirs) != rest(incoming, &mine)
    };
    if other {
        lines.push(ProfileChange::OtherChanged);
    }
    lines
}

// First sheet of each name wins, as in the C# dictionary.
fn by_name(sheets: &[ModeSheet]) -> Vec<(Key, &ModeSheet)> {
    let mut list: Vec<(Key, &ModeSheet)> = Vec::new();
    for sheet in sheets {
        let k = key(sheet);
        if !list.iter().any(|(other, _)| *other == k) {
            list.push((k, sheet));
        }
    }
    list
}

fn label(sheet: &ModeSheet) -> String {
    if !sheet.mode_name.is_empty() {
        sheet.mode_name.clone()
    } else if !sheet.header_label.is_empty() {
        sheet.header_label.clone()
    } else {
        match sheet.sheet_type {
            SheetType::ProfileName => "ProfileName",
            SheetType::Preferences => "Preferences",
            SheetType::Infrared => "Infrared",
        }
        .to_owned()
    }
}

fn mappings(sheet: &ModeSheet) -> Vec<String> {
    sheet
        .bindings
        .iter()
        .map(|b| {
            let mut parts = vec![
                b.output.as_str(),
                b.function.as_str(),
                b.action_name.as_str(),
            ];
            parts.extend(b.inputs.iter().map(String::as_str));
            parts.join(SEP)
        })
        .collect()
}

// Multiset difference: two identical lines in one mode are two lines.
fn difference(before: &[String], after: &[String]) -> (usize, usize) {
    let mut counts: BTreeMap<&str, usize> = BTreeMap::new();
    for line in before {
        *counts.entry(line).or_default() += 1;
    }
    let mut added = 0;
    for line in after {
        match counts.get_mut(line.as_str()) {
            Some(n) if *n > 0 => *n -= 1,
            _ => added += 1,
        }
    }
    (added, counts.values().sum())
}

fn joined(row: &[String]) -> String {
    let end = row.iter().rposition(|c| !c.is_empty()).map_or(0, |i| i + 1);
    row[..end].join(SEP)
}

// Everything the mode lines do not already say: each mapping row with its
// mapping cells blanked (the notes beside it stay), the header row of every
// mode both copies have, and every row outside a mode.
fn rest(file: &ProfileFile, other: &[(Key, &ModeSheet)]) -> Vec<String> {
    let mut mapping = std::collections::BTreeSet::new();
    let mut one_sided = std::collections::BTreeSet::new();
    for sheet in &file.document.sheets {
        for bind in &sheet.bindings {
            mapping.insert(bind.row.saturating_sub(1));
        }
        // A mode only one copy has is already its own line: its three header
        // rows (keyword, file name, label) are not said again.
        let k = key(sheet);
        if !other.iter().any(|(o, _)| *o == k) {
            for h in 0..3 {
                one_sided.insert(sheet.start_row.saturating_sub(1) + h);
            }
        }
    }
    let first = usize::from(file.document.has_version_header);
    let mut lines = Vec::new();
    for (i, row) in file.grid.iter().enumerate().skip(first) {
        if one_sided.contains(&i) {
            continue;
        }
        let line = if mapping.contains(&i) {
            let blanked: Vec<String> = row
                .iter()
                .enumerate()
                .map(|(col, c)| {
                    if col < NOTE_COLUMN || col == ACTION_COLUMN {
                        String::new()
                    } else {
                        c.clone()
                    }
                })
                .collect();
            joined(&blanked)
        } else {
            joined(row)
        };
        if !line.is_empty() {
            lines.push(line);
        }
    }
    lines
}

// The header row is left out: its last cell names the sheet a file is backed
// up to, which differs between two people's copies of the same profile.
fn rows(file: &ProfileFile) -> Vec<String> {
    let first = usize::from(file.document.has_version_header);
    let mut lines: Vec<String> = file.grid.iter().skip(first).map(|r| joined(r)).collect();
    while lines.last().is_some_and(String::is_empty) {
        lines.pop();
    }
    lines
}

#[cfg(test)]
mod tests {
    use super::{ProfileChange, profile_changes};
    use crate::ProfileFile;

    fn profile(modes: &[&str]) -> String {
        modes
            .iter()
            .map(|m| {
                format!(
                    "Profile Name,,{m}\r\ncatalog.csv\r\nPlayStation Outputs,Function,usb\r\ndpad_N,normal,right_sip\r\n"
                )
            })
            .collect::<Vec<_>>()
            .join("\r\n")
    }

    // FollowLinkTests.The_same_profile_has_no_changes_even_with_a_different_backup_id
    #[test]
    fn same_profile_with_a_different_backup_id_has_no_changes() {
        let a = ProfileFile::load(&format!(
            "QuadStick Configuration,Version 1.5,aaaaaaaaaaaaaaaaaaaaaaaaa,Walk\r\n{}",
            profile(&["Walking"])
        ));
        let b = ProfileFile::load(&format!(
            "QuadStick Configuration,Version 1.5,bbbbbbbbbbbbbbbbbbbbbbbbb,Walk\r\n{}",
            profile(&["Walking"])
        ));
        assert!(profile_changes(Some(&a), &b).is_empty());
    }

    #[test]
    fn an_edited_mapping_is_one_added_and_one_removed() {
        let a = ProfileFile::load(&profile(&["Walking"]));
        let b = ProfileFile::load(&profile(&["Walking"]).replace("right_sip", "left_puff"));
        assert_eq!(
            profile_changes(Some(&a), &b),
            vec![ProfileChange::ModeChanged {
                mode: "Walking".into(),
                added: 1,
                removed: 1
            }]
        );
    }

    #[test]
    fn modes_that_come_and_go_are_named() {
        let a = ProfileFile::load(&profile(&["Walking", "Flying"]));
        let b = ProfileFile::load(&profile(&["Walking", "Driving"]));
        let lines = profile_changes(Some(&a), &b);
        assert!(lines.contains(&ProfileChange::ModeAdded("Driving".into())));
        assert!(lines.contains(&ProfileChange::ModeRemoved("Flying".into())));
        assert!(!lines.contains(&ProfileChange::OtherChanged));
    }

    #[test]
    fn a_note_changed_beside_a_mapping_is_still_listed() {
        let two = profile(&["Walking"])
            .replace("right_sip\r\n", "right_sip\r\ndpad_S,normal,left_sip\r\n");
        let a = ProfileFile::load(&two);
        let b = ProfileFile::load(
            &two.replace("right_sip", "left_puff")
                .replace("left_sip\r\n", "left_sip,,,,,,,,call Anna\r\n"),
        );
        let lines = profile_changes(Some(&a), &b);
        assert!(lines.contains(&ProfileChange::ModeChanged {
            mode: "Walking".into(),
            added: 1,
            removed: 1
        }));
        assert!(lines.contains(&ProfileChange::OtherChanged));
    }

    #[test]
    fn only_a_note_changed_is_other() {
        let a = ProfileFile::load(&profile(&["Walking"]));
        let b = ProfileFile::load(
            &profile(&["Walking"]).replace("right_sip\r\n", "right_sip,,,,,,,,call Anna\r\n"),
        );
        assert_eq!(
            profile_changes(Some(&a), &b),
            vec![ProfileChange::OtherChanged]
        );
    }

    #[test]
    fn an_unreadable_local_copy_says_so() {
        let b = ProfileFile::load(&profile(&["Walking"]));
        assert_eq!(
            profile_changes(None, &b),
            vec![ProfileChange::LocalUnreadable]
        );
    }
}
