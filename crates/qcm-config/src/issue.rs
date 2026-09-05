//! Validation issue types shared by parser and validator layers.

use std::fmt;

/// Legacy issue severity. The current format core has no informational level.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Severity {
    /// Blocks safe install/save flows that require a valid device profile.
    Error,
    /// Describes a compatibility or quality problem that does not block.
    Warning,
}

/// Machine-readable issue category used by one-click fixes.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum IssueKind {
    /// No specialized machine action is attached.
    #[default]
    None,
    /// Input token is not known to the accepted vocabulary.
    UnknownInput,
}

/// One parser/validator issue using the same five fields as the legacy record.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct Issue {
    pub severity: Severity,
    pub cell: String,
    pub message: String,
    pub fix: String,
    pub kind: IssueKind,
}

impl Issue {
    /// Construct a normal issue with no specialized machine fix kind.
    #[must_use]
    pub fn new(
        severity: Severity,
        cell: impl Into<String>,
        message: impl Into<String>,
        fix: impl Into<String>,
    ) -> Self {
        Self::with_kind(severity, cell, message, fix, IssueKind::None)
    }

    /// Construct an issue with an explicit machine-readable kind.
    #[must_use]
    pub fn with_kind(
        severity: Severity,
        cell: impl Into<String>,
        message: impl Into<String>,
        fix: impl Into<String>,
        kind: IssueKind,
    ) -> Self {
        Self {
            severity,
            cell: cell.into(),
            message: message.into(),
            fix: fix.into(),
            kind,
        }
    }
}

impl fmt::Display for Issue {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "{:?} {}: {} ({})",
            self.severity, self.cell, self.message, self.fix
        )
    }
}

#[cfg(test)]
mod tests {
    use super::{Issue, IssueKind, Severity};

    #[test]
    fn display_matches_legacy_record_format() {
        let issue = Issue::with_kind(
            Severity::Warning,
            "C4",
            "Unknown input",
            "Pick a known input",
            IssueKind::UnknownInput,
        );
        assert_eq!(
            issue.to_string(),
            "Warning C4: Unknown input (Pick a known input)"
        );
        assert_eq!(issue.kind, IssueKind::UnknownInput);
    }
}
