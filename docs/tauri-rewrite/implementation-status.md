# Rewrite implementation status

Implementation branch: `rewrite/tauri-rust`
Draft PR: `#5`

This is the execution checkpoint. The numbered specification remains the source of task requirements; this file records what has actually been implemented.

| Task | State | Evidence |
|---|---|---|
| TASK-001 freeze implementation base | **DONE** | `implementation-baseline.md` |
| TASK-002 fixture manifest | **DONE** | manifest verification green in Rewrite parity CI |
| TASK-003 deterministic C# oracle | **DONE** | compile/selfcheck/generate green in Rewrite parity CI |
| TASK-004 parity schema | **DONE; STRICT CI VERIFYING** | checked-in schema + `tools/oracle/validate.py` |
| TASK-005 Avalonia baseline | **DONE WITH EXPLICIT UNKNOWNS** | `baseline-performance.md` |
| TASK-006 close Phase-0 ledgers | **DONE** | no UNASSESSED rows; serial closed-deferred; `gate0-review.md` |
| TASK-007 Rust workspace | **IMPLEMENTED; CI VERIFYING** | root workspace + pinned Rust 1.98.0 + `qcm-config` + clippy/fmt/test gate |
| TASK-008 exact CSV port | NEXT | — |

## Gate 0 evidence

Rewrite parity CI on `f16fab62c8bfd1799f7b47491da3ed7c1da2bc3b` passed manifest verification, the full legacy test suite, oracle compilation/self-check and canonical generation. Subsequent CI validates generated JSON against the checked-in JSON Schema before allowing Rust work to pass.

## Toolchain evidence

Rust 1.98.0 was released by the Rust project on 2026-08-20 and is the current stable toolchain at implementation start. `rust-toolchain.toml` pins it exactly.

## Execution rule

A task is only marked DONE when its artifact exists and its non-hardware acceptance criteria have been implemented. Hardware-only verification can be recorded as `NEEDS HARDWARE` without pretending it ran.

The legacy .NET/Avalonia implementation remains buildable and untouched while parity infrastructure is established.
