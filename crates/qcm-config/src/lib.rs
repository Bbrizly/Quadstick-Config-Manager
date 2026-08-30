#![forbid(unsafe_code)]

pub mod catalog;
pub mod csv;
pub mod editor_op;
pub mod issue;
pub mod model;
pub mod parser;
pub mod preferences;
pub mod profile;
pub mod validation;
pub mod vocab;

pub use catalog::canonical_catalog;
pub use csv::{Grid, parse as parse_csv, write as write_csv};
pub use editor_op::{ACTION_COLUMN, EditorOp, MAX_ACTION_NAME, NOTE_COLUMN};
pub use issue::{Issue, IssueKind, Severity};
pub use model::{Binding, ModeSheet, ProfileDocument, SheetType};
pub use parser::{parse_structure, parse_with_issues};
pub use preferences::{PreferenceDefinition, PreferenceEditor, load_preferences};
pub use profile::ProfileFile;
pub use validation::{
    MAX_DEVICE_FILE_NAME_LENGTH, is_invalid_filename_char, is_reserved_windows_name,
    is_too_long_for_device, validate,
};
pub use vocab::{ValidationCatalog, load_validation};

/// Parse and validate in the same order as legacy `ProfileFile.Load`:
/// parser issues first, validator issues second.
#[must_use]
pub fn parse_and_validate(csv_text: &str) -> (ProfileDocument, Vec<Issue>) {
    let (document, mut issues) = parse_with_issues(csv_text);
    issues.extend(validate(&document));
    (document, issues)
}

pub const FORMAT_CRATE_POLICY: &str = "pure-rust-no-os-ui-network";
