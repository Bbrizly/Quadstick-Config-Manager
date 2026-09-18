# ADR-011 — Structured local diagnostics with preserved privacy contract

**Status:** ACCEPTED

## Decision
Use structured native logging (`tracing` candidate), bounded local retention and explicit diagnostics export. Product analytics remains a separate consent-gated closed schema.

## Privacy constraints
No profile contents, filenames/paths, Google identity/tokens/sheet/share IDs, username/computer name. Preserve current source-build/CI zero-telemetry behavior and crash local-first/send-explicitly behavior.

## Why
The rewrite needs operation-stage diagnosis, especially storage/HID, without broadening data collection.