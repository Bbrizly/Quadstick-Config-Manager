
### Tests
Exact normalized bytes against oracle; normalization twice == once; K/L/M+ retained.

---

## TASK-015 — Port typed editor operations

### Status
DONE — verified on `1f4bdb4` with full legacy/oracle/rustfmt/clippy/Rust differential CI.

### Goal
Replace UI cell manipulation with one canonical Rust mutation engine.

### Files
`profile.rs`, `editor_op.rs`.

### Steps
Port SetCell, SetOutput, SetBinding, Add/Delete/Move row, Add/Rename mode, preference sheet, heal/known current ProfileFile operations. Every op pushes raw-grid undo according to current semantics then reparses.

### Tests
Replay qsf/current test operation sequences C#↔Rust.

---

## TASK-016 — Port undo/dirty/revision/action-name semantics

### Status
DONE — verified on `f30d779` with full legacy/oracle/rustfmt/clippy/Rust state-parity CI.

### Goal
Freeze editor state contract.

### Steps
Max undo depth parity (current 200), Dirty/Revision increments, clean-state handling, non-dirty Google Sheet ID stamping, action-name length/uniqueness/output-collision behavior.

### Tests
Exact revision/dirtiness sequence tests + undo restores raw grid.

---

## TASK-017 — Differential test runner

### Goal
Run all oracle fixtures automatically.

### Files
`tests/parity/**`, script `scripts/parity.sh`/cross-platform equivalent.

### Steps
Build qsf/oracle once, run fixture matrix, compare canonical JSON and bytes, emit minimal structural diff on failure. CI must show fixture ID and field.

### Acceptance
One command runs entire format parity suite locally and CI.

---

## TASK-018 — Property/fuzz suite

### Goal
Find behavior holes beyond hand fixtures.

### Steps
Add proptest generators for safe grids/extra columns and cargo-fuzz targets for parse/normalize/apply ops. Bound input size/time. Seed from malformed fixtures.

### Acceptance
No panic/OOM; invariants in `11-config-format-and-compatibility.md` hold.

---

## TASK-019 — Rust qsf parity