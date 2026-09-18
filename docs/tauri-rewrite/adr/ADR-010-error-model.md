# ADR-010 — Stable domain error codes

**Status:** ACCEPTED

## Decision
Internal typed errors retain source detail; IPC receives stable `QcmErrorDto { code, message, recoverable, action, operationId }`. UI recovery is keyed by code, not Rust debug string.

## Why
OS/network errors vary and may leak user data. Stable codes enable localization/tests/support.

## Constraints
Recoverable I/O never panics. `QCM_INTERNAL` is last-resort bug boundary, not catch-all expected device failure.