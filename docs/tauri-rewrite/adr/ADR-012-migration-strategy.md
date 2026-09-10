# ADR-012 — Strangler migration, parity before redesign

**Status:** ACCEPTED

## Decision
Keep Avalonia/C# runnable while pure Rust config is oracle-tested, then device core, then Tauri shell/frontend, then parity, then intended visual improvements, beta, cutover, legacy retirement.

## Rejected
Big-bang delete/rewrite: makes every bug ambiguous and removes known-good fallback.

## Consequence
Temporary dual-stack CI and repo complexity are accepted as insurance against device/config regressions.