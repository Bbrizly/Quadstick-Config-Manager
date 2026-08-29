# ADR-002 — Rust owns native/core behavior

**Status:** ACCEPTED

## Problem
A TypeScript-heavy Tauri app could put format semantics/state in JS and use Rust only as thin file/HID wrappers, recreating split-brain state and exposing generic native capabilities.

## Decision
Rust owns canonical profile sessions, config semantics, validation, device transactions, cloud policy and stable errors. TypeScript owns presentation/ephemeral drafts.

## Alternatives
- TS domain + Rust adapters: rejected because device-safe CSV and write safety are high-value native contracts and agent/browser consumers would drift.
- Keep C# core behind sidecar: useful temporary oracle, rejected as final architecture due runtime/package complexity.

## Consequences
More Rust porting upfront; much stronger one-parser/one-writer model. C# stays as oracle until parity.

## Revisit
Only if differential port proves infeasible; then a supported C# sidecar would be explicit architecture, not hidden fallback.