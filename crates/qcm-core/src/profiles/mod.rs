//! Canonical profile session state.
//!
//! The type any UI drives. A window opens a profile and gets back a session id
//! and a revision-tagged picture; it edits by sending typed operations with the
//! revision it saw. Rust owns the raw grid, the parsed document, the issues,
//! the dirty flag, the revision and the undo stack. Nothing above this layer
//! rewrites CSV of its own, and nothing above it works out dirty by comparing
//! objects.
//!
//! Two rules shape the surface. Every mutation carries `expected_revision`, so
//! an edit made against a picture that has moved on is refused instead of
//! silently winning. And no path crosses the boundary: a session knows an
//! opaque id and a name it may print, never a place on the machine.

pub mod manager;
pub mod session;
pub mod snapshot;

pub use manager::{ProfileSessions, SavePlan};
pub use session::{
    CloseOutcome, CloseRequest, ProfileOrigin, ProfileSession, SaveReceipt, SessionId,
};
pub use snapshot::{EditorSnapshot, IssueDto, ModeDto, ProfileSourceDto, SaveReceiptDto};
