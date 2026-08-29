# Rewrite implementation status

Implementation branch: `rewrite/tauri-rust`
Draft PR: `#5`

This is the execution checkpoint. The numbered specification remains the source of task requirements; this file records what has actually been implemented.

| Task | State | Evidence |
|---|---|---|
| TASK-001 freeze implementation base | **DONE** | `implementation-baseline.md` |
| TASK-002 fixture manifest | **DONE; CI VERIFYING** | `fixtures/manifest.json`, `tools/fixtures/manifest.py`, `fixture-corpus.md` |
| TASK-003 deterministic C# oracle | **IMPLEMENTED; CI VERIFYING** | `tools/QcmOracle/**` |
| TASK-004 parity schema | **IMPLEMENTED; CI VERIFYING** | `fixtures/oracle/schema.json`, `parity-schema.md` |
| TASK-005 Avalonia baseline | NEXT | release package baseline known; runtime measurements still required |
| TASK-006 close Phase-0 ledgers | NEXT | — |
| TASK-007+ | BLOCKED BY GATE 0 | do not skip compatibility gate |

## Execution rule

A task is only marked DONE when its artifact exists and its non-hardware acceptance criteria have been implemented. Hardware-only verification can be recorded as `NEEDS HARDWARE` without pretending it ran.

The legacy .NET/Avalonia implementation remains buildable and untouched while parity infrastructure is established.
