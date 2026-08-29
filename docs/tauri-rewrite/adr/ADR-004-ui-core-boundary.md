# ADR-004 — UI/core boundary

**Status:** ACCEPTED

## Decision
Frontend sees domain DTOs and opaque IDs. It cannot access native paths/handles, serialize profile CSV, open devices, store tokens or execute shell/network requests.

Canonical persisted state lives in Rust `ProfileSession`. React can hold temporary invalid text drafts but commits via typed `EditorOp` + expected revision.

## Why
Current `MainWindow` coupling is the central architecture problem. Moving the same filesystem/profile logic into React would only change syntax.

## Consequences
Every feature needs a QcmClient method/use case. Some seemingly trivial UI actions require a Rust op; that is intentional for data-integrity semantics.

## Revisit
Only for genuinely presentation-only transforms that cannot affect persistence/device meaning.