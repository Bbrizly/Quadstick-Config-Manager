#![forbid(unsafe_code)]

pub mod catalog;
pub mod csv;
pub mod issue;
pub mod model;
pub mod parser;
pub mod preferences;
pub mod vocab;

pub use catalog::canonical_catalog;
pub use csv::{Grid, parse as parse_csv, write as write_csv};
pub use issue::{Issue, IssueKind, Severity};
pub use model::{Binding, ModeSheet, ProfileDocument, SheetType};
pub use parser::parse_structure;
pub use preferences::{PreferenceDefinition, PreferenceEditor, load_preferences};
pub use vocab::{ValidationCatalog, load_validation};

pub const FORMAT_CRATE_POLICY: &str = "pure-rust-no-os-ui-network";
