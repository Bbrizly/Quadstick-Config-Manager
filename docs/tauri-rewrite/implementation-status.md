# Rewrite implementation status

Implementation branch: `rewrite/tauri-rust`

This is the execution checkpoint. The numbered specification remains the source of task requirements; this file records what has actually been implemented.

| Task | State | Evidence |
|---|---|---|
| TASK-001 freeze implementation base | **DONE** | `implementation-baseline.md` |
| TASK-002 fixture manifest | NEXT | — |
| TASK-003 deterministic C# oracle | NEXT | — |
| TASK-004 parity schema | NEXT | — |
| TASK-005 Avalonia baseline | NEXT | — |
| TASK-006 close Phase-0 ledgers | NEXT | — |
| TASK-007+ | BLOCKED BY ORDER | do not skip Gate 0 |

## Execution rule

A task is only marked DONE when its artifact exists and its non-hardware acceptance criteria have been implemented. Hardware-only verification can be recorded as `NEEDS HARDWARE` without pretending it ran.

The legacy .NET/Avalonia implementation remains buildable and untouched while parity infrastructure is established.
