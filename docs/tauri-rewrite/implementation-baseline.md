# Implementation baseline

Status: **TASK-001 complete**

This file freezes the exact legacy implementation that the Rust/Tauri rewrite must match before any production behavior is retired.

## Immutable comparison base

- Legacy/default branch: `main`
- Legacy commit: `f7783944387202bcafaeb7ff3f67789098fa6a4e`
- Legacy root tree: `ae95291b4750d9d4d3ba2c9f464a56e74f344d73`
- Commit date: 2026-08-29
- Rewrite specification head used to start implementation: `100d7c68d3830b89e8b3bfcd23cb408898053bbe`
- Implementation branch: `rewrite/tauri-rust`

`main` was re-read immediately before implementation. It had not moved from the specification audit baseline, so there is no baseline-to-implementation runtime diff to reconcile.

## Tree fingerprints

These Git trees are the inventory anchors for Gate 0. Any later change to one of these legacy trees must be reviewed before claiming parity.

| Surface | Tree SHA | Classification |
|---|---|---|
| `src/` | `0637685c6d9b6ebe9daece9184db92c6a1a3ecae` | product runtime, **ASSESSED** |
| `tests/` | `326eb7504867fbea0433407ea5b368cb854b0cca` | executable legacy behavior oracle, **ASSESSED** |
| `tools/` | `568ddb473f346b60a3c5c54573de2eb2b00c2b43` | developer/agent tooling, **ASSESSED** |
| `agent/` | `a4ccec9e01f3c5fd3f9bb74665e52b6a196aa11b` | product-adjacent agent/eval pipeline, **ASSESSED; retain until Rust qsf parity** |
| `scripts/` | `e731bf83369225910fd809c6d055f366b55dfd23` | build/release/localization tooling, **ASSESSED** |
| `.github/` | `fc8270887e22c73d5743eedac0d67eae95b3ecf8` | CI/release contract, **ASSESSED** |
| `docs/` | `6af53fd363083bcc9f0c7a8b9b81f7683169bdde` | evidence/reference; authority varies by file, **ASSESSED** |

The detailed behavior ownership remains in `05-behavior-inventory.md`, `09-old-to-new-traceability.md`, the matrices, and the migration ledgers. This file is the SHA-level freeze, not a competing inventory.

## Runtime-critical assessment

### `QuadStick.Format`

All tracked runtime files are migration-critical or explicitly support them. The highest-risk contracts are:

- `Csv.cs` — exact quote/CR/LF/EOF behavior;
- `Parser.cs` — firmware-aware sheet discovery and row framing;
- `Validator.cs` — device-agreement warnings/errors and firmware limits;
- `ProfileFile.cs` — lossless raw grid, normalization, mutation, undo/dirty/revision semantics;
- `Vocab.cs`, `FunctionParameters.cs`, `PreferenceCatalog.cs` and embedded `Data/**` — legal firmware vocabulary and settings metadata;
- `Device.cs` — mounted-storage discovery and safe write/delete/order behavior;
- XLSX/import helpers and URL/safe-name helpers — import and sharing compatibility.

Disposition: **PORT/ORACLE.** Nothing in this project may be deleted until the matching Rust behavior IDs are green.

### `QuadStick.App`

The app is a large stateful Avalonia shell around the core. `MainWindow.axaml.cs` remains a major coupling hotspot; its partials and separate windows/pages own real behavior rather than decoration. Important non-UI subsystems include:

- live HID input (`LiveInput.cs`);
- Google OAuth/Drive backup and secure token storage;
- community catalog network/cache behavior;
- crash guard/reporting and opt-in telemetry;
- settings, localization, theming, update checks;
- device library/settings/install flows;
- agent bridge/window integration.

Disposition: **STRANGLE.** Preserve Avalonia while Rust core and Tauri/React features earn parity.

### tests

`tests/QuadStick.Format.Tests/corpus/` contains the repository-owned format corpus, including CSV, firmware tables, and XLSX assets. `FirmwareOracle.cs` and `DeviceAgreementTests.cs` are authoritative migration assets. App tests are also behavior evidence and remain enabled throughout migration.

Disposition: **KEEP RUNNING.** New Rust tests supplement; they do not replace the .NET suite until cutover.

### agent/tooling

The Python `agent/` pipeline and C# `qsf` are not rewritten just because the UI shell changes. Rust qsf must first match the covered JSON contract, then the agent eval pipeline is pointed at it and compared.

Disposition: **RETAIN/COMPARE.**

## Migration-sensitive source search

The implementation audit searched the named migration risks and cross-checked the known source files.

| Probe | Evidence/status | Rewrite consequence |
|---|---|---|
| `SerialPort` | repository code search at this base returned no production hit | **OQ-001 provisional resolution: serial is not a current A-behavior. Do not add a Rust serial dependency.** Re-open only with source/history/hardware evidence. |
| HID | `QuadStick.App` references HidSharp; `LiveInput.cs` is the current live-input implementation | HID is an A-behavior and requires descriptor-driven Rust/hardware parity. |
| mass storage / filesystem | `QuadStick.Format/Device.cs`, install/device-library tests and app install flows | filesystem access must remain native/core-scoped and transactional. |
| `HttpClient` / network | Google Drive/Auth, community catalog, telemetry/crash reporting and update checks | network remains native use-case code with the existing privacy contract; no generic frontend HTTP capability. |
| `DllImport` / platform secret storage | `TokenStore.cs` uses macOS Keychain and Windows DPAPI | secure-token migration requires OS-backed storage and explicit Linux policy. |
| direct `File`/`Directory` I/O | format atomic save, device management, rescue/crash/cache/settings flows | split by use case; never expose arbitrary paths to the WebView. |

## Evidence hierarchy for implementation

When documentation and implementation disagree, use this order:

1. current QuadStick firmware behavior / firmware oracle;
2. executable tests at the frozen SHA;
3. source at the frozen SHA;
4. `docs/FORMAT.md` and source-backed current docs;
5. README/older prose only as hints.

## Gate-0 result

- implementation SHA recorded: **yes**;
- `main` drift since spec audit: **none**;
- runtime-critical surface unassessed: **none at module/file-inventory level**;
- serial scope: **deferred/no current production evidence**;
- production code changed by TASK-001: **no**.

Any commit merged into legacy `main` after this freeze must be compared against `f7783944387202bcafaeb7ff3f67789098fa6a4e` and added to the porting/behavior ledgers before the rewrite can claim full parity.
