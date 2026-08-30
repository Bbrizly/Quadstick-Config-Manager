# Open questions

Unknowns are not excuses to guess. Each has a resolution path and phase deadline.

## OQ-001 — Is serial communication part of current production parity?

**Why:** `System.IO.Ports` is referenced and product history discusses serial/Bluetooth, but audited critical paths found storage + HID.

**Know:** dependency exists; current live reader is HidSharp; current install/library uses mounted filesystem.

**Resolve:** exhaustive source/git-history/release-note search for `SerialPort`, COM/tty, RN-42, console protocol and user-visible serial flow; hardware/product confirmation only if source remains ambiguous.

**Blocks:** only serial feature design, not config/storage/HID rewrite.

**Deadline:** Gate 0.

**Status:** OPEN.

## OQ-002 — Can storage and HID interfaces be reliably correlated to one physical QuadStick?

**Why:** a unified device card could otherwise associate live state with wrong unit when two devices exist.

**Know:** current code handles mounted candidate and live HID independently.

**Resolve:** inspect platform USB topology/serial descriptors on two real units across Windows/macOS/Linux.

**Blocks:** automatic physical-unit aggregation, not independent capabilities.

**Deadline:** before multi-device visualizer claims.

## OQ-003 — Exact BOM/invalid UTF-8 behavior

**Why:** Rust `String` requires UTF-8; .NET readers may handle BOM/replacement differently.

**Resolve:** construct UTF-8 BOM, UTF-16, invalid byte fixtures and run current C# open/import/save paths. Choose reject vs preserve with compatibility classification.

**Blocks:** final native file decoder.

**Deadline:** TASK-008/14 before Gate 2.

## OQ-004 — Exact local save atomic/recovery contract versus device install

Current `ProfileFile.WriteAtomic` is simpler than `Device.Install`. Characterize external-change/replace/backup behavior and decide whether target intentionally improves local save. Keep improvement B separate.

**Deadline:** TASK-020/032.

**TASK-020 (2026-08-29): parity, improvement deferred.** Local save stays at `WriteAtomic` behavior, because `06-behavioral-compatibility-contract.md` governs and a stronger local contract is a class-B improvement. `ProfileSessions::prepare_save` returns a `SavePlan` carrying the exact bytes and the target, and `commit_save` is the only step that touches the store, so backup, read-back and restore land in one body without moving the command surface. Still open for TASK-032/040.

## OQ-005 — Device profile reordering mechanism/file-number semantics

Inspect `DeviceFilesWindow`, `Device.cs`, tests and any LED/current-file numbering rules end-to-end. Do not equate alphabetical display sort with firmware load order.

**Deadline:** TASK-025.

## OQ-006 — Linux secure token store

Current Google backup is unavailable on Linux. Decide whether initial Tauri parity preserves that or adds Secret Service/keyring as B improvement after KDE/GNOME reliability test.

**Deadline:** before TASK-044 release scope.

## OQ-007 — HID Rust descriptor API sufficiency

Can `hidapi` expose enough descriptor data and reliably cancel reads across all supported OSes/modes?

**Resolution:** TASK-026 physical spike.

**Fallback:** targeted platform adapter or report-descriptor parser; never fixed offsets without evidence.

## OQ-008 — Windows Store continuation

Current repo has Store workflow. Determine actual user/distribution importance, package identity and data location before Tauri Store packaging work.

**Deadline:** Phase 8; does not block normal signed installer.

## OQ-009 — Stable app bundle identifier/app-data migration

Inventory current IDs/settings folders and update behavior. Beta gets separate identity; stable cutover needs exact mapping.

**Deadline:** TASK-055.

## OQ-010 — Analytics provider continuation

Current PostHog behavior/privacy is contract; vendor is implementation. Decide keep current provider versus direct minimal HTTP/provider change separately. No provider switch may broaden payloads/consent.

**Deadline:** TASK-046.

## OQ-011 — Existing agent runtime/model/network architecture

`agent/` is substantial and must be fully inventoried: model calls, corpus licenses, cache size, eval/finalize assumptions, subprocess/qsf integration. Decide what stays Python sidecar/tooling and what moves to Rust/TS only after behavior map.

**Deadline:** Gate 0 for inventory; Phase 6 for product integration.

## OQ-012 — Community catalog exact endpoint/cache semantics

Current privacy says fetched only when Community opens/refreshes, cache enables offline. Confirm endpoint/cache TTL/invalid data behavior from source/tests before port.

**Deadline:** TASK-043.

## OQ-013 — Tauri updater rollout endpoint/channel

Decide GitHub Release static JSON versus dedicated endpoint, beta/stable channel representation, key rotation/recovery and rollout control. Signature verification is mandatory.

**Deadline:** TASK-047.

## OQ-014 — i18n library choice

Run conversion spike on current RESX plural/format placeholders and RTL/pseudo-loc. `i18next` is default candidate; choose another only if it better preserves semantics with less machinery.

**Deadline:** TASK-037.

## OQ-015 — Desktop minimum OS versions

Current .NET packages exist for three OSes but supported minimum versions must be made explicit for Tauri/WebView/HID. Derive from Tauri/WebView/dependency support and actual user needs.

**Deadline:** Phase 8 packaging.

## OQ-016 — Direct profile external-change detection

Determine current behavior when the local file changes after open. If absent, decide whether watchers are a B improvement or simple save-time metadata/hash check.

**Deadline:** TASK-040.

## OQ-017 — Current app settings storage schema

Locate/characterize Settings persistence, consent/install ID, recents, theme, scale, language, reduced motion and backup path map. Define one-time import vs fresh defaults for beta/stable.

**Deadline:** Gate 0/Task 055.

## OQ-018 — Device settings/preferences file lifecycle

Fully characterize reading `prefs.csv`, default fallback, saving/installing/reload expectations and Mode 7/flash-related device behavior from code/tests/firmware docs.

**Deadline:** TASK-025/042.

## OQ-019 — Release telemetry/privacy binary metadata

Current PostHog library may add runtime/library/OS metadata noted in `PRIVACY.md`. If transport changes, update behavior/policy/test so privacy document remains accurate; do not silently add geolocation/IP enrichment.

**Deadline:** TASK-046.

## OQ-020 — Mobile hardware viability

Physical iOS/Android proof required for USB/HID/storage/Bluetooth. Tauri support alone is not evidence.

**Deadline:** TASK-060; does not block desktop.