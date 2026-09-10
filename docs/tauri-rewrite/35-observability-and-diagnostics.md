# Observability and diagnostics

This document covers logs/product analytics/crash reporting, not local HID live frames.

## Structured local logging

Use Rust `tracing` (candidate) and a small frontend logger that forwards only deliberate diagnostic events. Every operation can carry `OperationId`.

Recommended fields:
- app version/build channel;
- OS/arch;
- operation ID;
- subsystem;
- stable error code;
- duration/stage;
- storage/HID **ephemeral ID**, not raw device path;
- firmware/version only if actually observed.

## Redaction

Default logs must not contain:
- OAuth/access/refresh/client secrets;
- profile CSV contents;
- Google sheet contents;
- full home/user paths;
- email/account identity;
- arbitrary remote response bodies.

Implement a redaction test suite.

## Product analytics parity

Current `Telemetry.cs` uses a closed event/property allowlist, consent gates, scrub-before-send, CI disable and bounded flush. Target preserves policy independent of transport vendor.

Rules:
- analytics off until persisted consent;
- crash data has its own explicit consent/flow as current design requires;
- no event may add arbitrary property dictionaries from UI;
- CI sets `QSCM_TELEMETRY=0` equivalent and tests zero network initialization;
- local/dev builds without token are safely analytics-off.

## Diagnostics bundle

User-triggered `export_diagnostics_bundle` writes a zip/text bundle through native save picker containing:
- app version/build;
- OS/arch/WebView version where obtainable;
- sanitized capability snapshot;
- recent bounded local logs;
- stable errors/recent operation stages;
- optional user-selected profile diagnostic summary (issues/hash, not contents by default).

Show exactly what will be included before export when sensitive paths/content could appear.

## Crash rescue

Port `CrashGuard` behavior as an early lifecycle service. Rescue unsaved/canonical profile state locally before offering network report. A frontend rendering crash should not make recovery dependent on React still working; Rust session state is an advantage of target architecture.

## Health counters

Local-only useful counters: HID reconnects, dropped live frames, device discovery errors, install verify failures, bounded log drops. Include in diagnostics; do not automatically upload unless analytics schema explicitly allows.