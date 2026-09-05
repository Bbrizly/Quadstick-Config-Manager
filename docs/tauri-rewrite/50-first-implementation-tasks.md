# Mechanical implementation tasks

These are deliberately explicit. Do not skip ahead because a later task looks more visible.

---

## TASK-001 — Freeze the exact implementation base

### Goal
Re-run the repository audit immediately before coding so this specification cannot silently target an older `main`.

### Prerequisites
This spec branch reviewed/merged or chosen as implementation source.

### Current evidence
Audit baseline `main@f7783944387202bcafaeb7ff3f67789098fa6a4e`.

### Files
Create `docs/tauri-rewrite/implementation-baseline.md`; update all ledgers if `main` moved.

### Steps
1. Fetch default branch; record SHA/tree/date.
2. List every tracked path under `src`, `tests`, `tools`, `agent`, `scripts`, `.github`, `docs`.
3. Diff baseline SHA→new SHA.
4. For every changed runtime/test file, update behavior/porting entries before coding.
5. Search `TODO|FIXME|HACK|SerialPort|Hid|DriveInfo|File\.|Directory\.|HttpClient|Process|DllImport` and record migration-relevant hits.
6. Mark every runtime file ASSESSED or explain E/tooling.

### Tests/verification
`git diff --check`; no production modification in this task.

### Acceptance
- [ ] implementation SHA recorded;
- [ ] no runtime-critical unassessed file;
- [ ] spec contradictions corrected.

### Rollback
Revert documentation commit only.

---

## TASK-002 — Build the fixture manifest

### Goal
Create one versioned corpus before porting format code.

### Files
`fixtures/manifest.json`, `fixtures/profiles/**`, `fixtures/xlsx/**`, `fixtures/hid/**`; do not move original tests yet.

### Steps
1. Enumerate all existing test/profile/template/XLSX assets.
2. Copy only repository-owned/public-safe fixtures; reference generated/embedded assets when duplication is undesirable.
3. Assign stable fixture IDs and behavior IDs.
4. SHA-256 every fixture.
5. Add edge fixtures listed in `37-test-fixture-plan.md` only when sourced/generated deterministically.
6. Add manifest validation script.

### Tests
Manifest parser validates path uniqueness, SHA, license/source, behavior IDs.

### Acceptance
- [ ] every format test fixture represented;
- [ ] no private Google/user files;
- [ ] hash mismatch fails CI.

### Rollback
Remove new fixture tree; original tests untouched.

---

## TASK-003 — Add deterministic C# oracle exporter

### Goal
Make current C# behavior machine-comparable.

### Current evidence
`FirmwareOracle.cs`, `DeviceAgreementTests.cs`, `ProfileFile`, `qsf`.

### Files
Prefer `tools/qsf` subcommands or new `tools/QcmOracle`; tests in `tests/QuadStick.Format.Tests`.

### Steps
1. Define `inspect-canonical`, `normalize-canonical`, `apply-canonical`, `firmware-canonical` commands.
2. Emit deterministic JSON: no timestamps/absolute paths/culture-dependent order.
3. Include raw grid, parsed projection, issues, serialized SHA/text metadata.
4. Pin invariant culture in oracle process.
5. Add command to generate all `fixtures/oracle/*` from manifest.
6. Record source SHA in outputs.

### Tests
Run twice and byte-compare outputs; run under at least en-US plus another culture.

### Acceptance
- [ ] deterministic outputs;
- [ ] existing .NET suite remains green;
- [ ] oracle generation one command.

### Rollback
Delete tool changes; no behavior changes to app.

---

## TASK-004 — Define parity schema

### Goal
Make C# and Rust compare the same canonical representation.

### Files
`fixtures/oracle/schema.json`, `docs/tauri-rewrite/parity-schema.md`.

### Steps
Define schema for raw rows/cells, header, sheets, bindings, issues, normalized bytes hash and operation result. Explicitly sort only semantically unordered sets; preserve row/input ordering.

### Tests
Validate every generated oracle JSON against schema.

### Acceptance
Schema differentiates absent vs empty where current behavior does.

### Rollback
Schema-only revert.

---

## TASK-005 — Record Avalonia baseline

### Goal
Measure current app before performance claims.

### Files
`docs/tauri-rewrite/baseline-performance.md`, optional scripts under `tools/perf`.

### Steps
Measure cold start, representative profile open/parse/save, memory idle/live, HID visual latency where measurable, package size, 30-minute live session. Record machine/OS/app SHA and methodology.

### Acceptance
Numbers are reproducible enough for relative target budgets; unknown metrics are explicitly unknown.

### Rollback
Docs/tools only.

---

## TASK-006 — Close Phase-0 ledgers

### Goal
Account for everything before porting.

### Steps
1. Expand MainWindow partial behaviors, all App/Format tests, `agent/` pipeline, scripts/workflows.
2. Resolve production `System.IO.Ports` usage by source/history search.
3. Add every current feature/settings/privacy behavior.
4. Link every critical risk to test plan.
5. Gate 0 review by a second reviewer/agent.

### Acceptance
No required row is UNASSESSED; OQ-001 serial has evidence-based status.

---

## TASK-007 — Create Rust workspace without Tauri

### Goal
Start pure parity work before UI/shell complexity.

### Files
`Cargo.toml`, `rust-toolchain.toml`, `crates/qcm-config/Cargo.toml`, `crates/qcm-config/src/lib.rs`.

### Steps
1. Pin stable toolchain.
2. Workspace initially includes only `qcm-config` (+ test helper if needed).
3. Enable warnings/clippy policy.
4. Add CI cargo fmt/clippy/test while .NET tests remain.

### Tests
`cargo fmt --check`; `cargo clippy -D warnings`; `cargo test`.

### Acceptance
No Tauri/OS/network dependency in qcm-config.

### Rollback
Remove workspace files; legacy unaffected.

---

## TASK-008 — Port raw CSV parse/write

### Goal
Match `Csv.cs` exactly before semantic parsing.

### Files
`crates/qcm-config/src/csv.rs`, `tests/parity/csv.rs`.

### Steps
Port quote/doubled-quote/CR/LF/EOF behavior directly. Writer quotes same character classes and emits CRLF. Use `Vec<Vec<String>>`/newtype, not serde CSV DTO.

### Edge cases
Empty input, trailing LF/no LF, quoted CRLF, doubled quote, commas, blank row, comma-only row, Unicode.

### Tests
Golden C# parity + property no-panic.

### Acceptance
All CSV fixture outputs byte-match oracle.

---

## TASK-009 — Port model/issue types

### Goal
Represent current parsed document without I/O.

### Files
`model.rs`, `issue.rs`.

### Steps
Port `SheetType`, severity/kind, binding row/input columns, mode sheet, header/document metadata. Preserve row numbering convention and optional/empty distinctions.

### Tests
Serde/canonical conversion snapshots; compile-time no OS/Tauri imports.

---

## TASK-010 — Port vocab/catalog embedded data

### Goal
Use the same legal/legacy vocabulary/defaults/templates.

### Files
`vocab.rs`, `preferences.rs`, `data/**`, build script only if needed.

### Steps
Copy/generate from existing embedded CSV/JSON resources; never hand-retype hundreds of tokens. Add generation hash vs legacy source. Preserve firmware order where qsf exposes it.

### Tests
Counts/sets/order vs C# oracle and known firmware validation sources.

---

## TASK-011 — Port header/sheet parser

### Goal
Match firmware-aware section discovery.

### Steps
Port version header recognition, true blank separator semantics, Profile/Preferences/Infrared discovery, filename/channel/header fields. Keep exact/raw keyword acceptance helpers separate from display trimming.

### Tests
Headerless/wrong-case/comma-blank/missing-blank/mixed-section fixtures + FirmwareOracle canonical results.

---

## TASK-012 — Port binding/preferences/IR parsing

### Goal
Complete parsed projection.

### Steps
Port A output, B function, C..J inputs, K note ignored by device, L action-name extension, row limits, function parameters and preference/IR special rules.

### Tests
All current parser tests mapped; sequence order preserved.

---

## TASK-013 — Port validation + firmware limits

### Goal
Reproduce blocking errors/warnings.

### Steps
Port keyword exactness, safe 63-character and 1023-byte line rules, vocab/legacy warnings, function arity, preferences, action names and any DeviceAgreement rule. Give internal issue codes even if current UI only has text.

### Tests
C# issue parity by severity/cell/kind + boundary values.

---

## TASK-014 — Port serializer and normalization

### Goal
Generate device-safe CSV exactly.

### Steps
Implement stamped grid, A..J trim/newline flatten, CRLF output, Version 1.5 normalization, true blank sheet separators, idempotent normalization and bookkeeping Sheet ID semantics.

### Tests
Exact normalized bytes against oracle; normalization twice == once; K/L/M+ retained.

---

## TASK-015 — Port typed editor operations

### Goal
Replace UI cell manipulation with one canonical Rust mutation engine.

### Files
`profile.rs`, `editor_op.rs`.

### Steps
Port SetCell, SetOutput, SetBinding, Add/Delete/Move row, Add/Rename mode, preference sheet, heal/known current ProfileFile operations. Every op pushes raw-grid undo according to current semantics then reparses.

### Tests
Replay qsf/current test operation sequences C#↔Rust.

---

## TASK-016 — Port undo/dirty/revision/action-name semantics

### Goal
Freeze editor state contract.

### Steps
Max undo depth parity (current 200), Dirty/Revision increments, `MarkClean`, non-dirty Google Sheet ID stamping, action-name length/uniqueness/output-collision behavior.

### Tests
Exact revision/dirtiness sequence tests + undo restores raw grid.

---

## TASK-017 — Differential test runner

### Goal
Run all oracle fixtures automatically.

### Files
`tests/parity/**`, script `scripts/parity.sh`/cross-platform equivalent.

### Steps
Build qsf/oracle once, run fixture matrix, compare canonical JSON and bytes, emit minimal structural diff on failure. CI must show fixture ID and field.

### Acceptance
One command runs entire format parity suite locally and CI.

---

## TASK-018 — Property/fuzz suite

### Goal
Find behavior holes beyond hand fixtures.

### Steps
Add proptest generators for safe grids/extra columns and cargo-fuzz targets for parse/normalize/apply ops. Bound input size/time. Seed from malformed fixtures.

### Acceptance
No panic/OOM; invariants in `11-config-format-and-compatibility.md` hold.

---

## TASK-019 — Rust qsf parity

### Goal
Keep agent/tooling one-parser architecture.

### Files
Either `crates/qsf` or `qcm-config/src/bin/qsf.rs`.

### Steps
Implement inspect/vocab/validate/apply/diff JSON contract, XLSX deferred until its Rust import exists. Compare existing qsf outputs for corpus. Reject unknown bindings before write.

### Acceptance
Agent pipeline can point at Rust qsf without changing expected contract for covered commands.

---

## TASK-020 — Profile session manager

### Goal
Make Rust canonical state usable by any UI.

### Files
`crates/qcm-core/src/profiles/**`.

### Steps
Create opaque session IDs, source refs, snapshot DTO, expectedRevision mutation, save preparation, close/dirty policy. Keep raw path private.

### Tests
Two sessions, stale revision, concurrent mutation, save snapshot, close dirty behavior with fake local store.

---

## TASK-021 — Stable errors, operations and confirmations

### Goal
Remove stringly exception UX.

### Files
`qcm-core/src/error.rs`, `operation.rs`, `confirmation.rs`.

### Steps
Implement error families, recoverability/action mapping, OperationId, expiring confirmation requirement + operation fingerprint. Add redaction conversion tests.

### Acceptance
No raw OS exception is required for normal UI recovery.

---

## TASK-022 — Storage port + fake device

### Goal
Test device safety without hardware.

### Files
`qcm-core/src/ports/storage.rs`, `crates/qcm-testkit/src/storage.rs`.

### Steps
Define only scoped operations core needs. Fake models root marker/files, generation, read-only/full/unplug/fail-at-stage. Do not define arbitrary path API.

### Acceptance
All future storage transaction tests run against deterministic fake.

---

## TASK-023 — Mounted QuadStick discovery

### Goal
Port candidate detection with safer identity.

### Files
`src-tauri/src/adapters/storage/{mod,windows,macos,linux}.rs` later if shell not scaffolded; until TASK-030 keep adapter crate/module without Tauri dependency or schedule immediately after scaffold.

### Steps
Enumerate ready volumes, preserve current removable/macOS heuristic for parity, marker-check `default.csv`, create opaque ID/generation, sanitize display metadata, revalidate on lookup.

### Tests
Fake/OS temp integration + real one/two/no device.

---

## TASK-024 — Safe install transaction

### Goal
Reproduce current write integrity before UI.

### Steps
Implement revalidation, protected confirmation, off-device unique backup, temp write, flush/sync, close, exact readback, replacement, target verify where safe, nuanced restore and stage receipt. No cancellation inside unsafe swap section.

### Tests
Fault at every stage; real sacrificial file on QuadStick; unplug during temp and replace windows.

### Acceptance
Never reports success without verification; every failure reports restored/unchanged/uncertain truthfully.

---

## TASK-025 — Device library/delete/order/preferences core

### Goal
Port the rest of `Device.cs`/DeviceFiles behavior.

### Steps
List ordering/protected names, backup-before-delete, rename/order semantics from tests, LED file audited table, preference profile read/write through same transaction safety.

### Tests
Port DeviceFileManagement/EditInstall/etc behavior IDs.

---

## TASK-026 — HID implementation spike

### Goal
Prove Rust HID library before committing architecture.

### Files
`spikes/hid/README.md`, throwaway or retained diagnostic binary.

### Steps
Using actual QuadStick: enumerate all available VID/PID modes, inspect report descriptors, identify top-level gamepad collection, read X/Y/buttons, unplug cancellation, Windows/macOS/Linux where available. Compare `hidapi` behavior to HidSharp current reader.

### Acceptance
Evidence table says PASS/FAIL/limitation per platform/mode; dependency ADR updated.

### Rollback
Spike is removable; no product dependency if fail.

---

## TASK-027 — Descriptor-driven HID adapter

### Goal
Port `LiveInput.cs` without magic offsets.

### Steps
Implement known identity filter, top-level usage selection, descriptor field extraction, normalization, missing-axis handling, buttons, jitter/dedupe, blocking worker with cancellation/backoff.

### Tests
Captured descriptor/report fixtures + real device.

---

## TASK-028 — LiveInputManager

### Goal
Separate HID worker lifetime from WebView.

### Steps
Own one stream, latest/bounded frame sink, seq/timestamp/stale state, stop/restart, capability limitation including XInput-only state.

### Tests
Fast producer/slow consumer, disconnect active button clears, 2h fake soak.

---

## TASK-029 — Resolve serial scope

### Goal
Prevent accidental serial rewrite.

### Steps
Search source/history/docs/tests; interview requirement only if code cannot resolve; write `OQ-001` resolution. If no current A behavior, mark serial E/deferred and do not add crate. If required, create a separate dependency/hardware spike and full protocol spec before implementation.

### Acceptance
Evidence-backed decision, no speculative production serial code.

---

## TASK-030 — Scaffold Tauri + React

### Goal
Create shell only after core parity exists.

### Files
`package.json`, `pnpm-lock.yaml`, `tsconfig*.json`, `vite.config.ts`, `/src`, `/src-tauri`, `tauri.conf.json`.

### Steps
Pin Node 24 LTS/pnpm/Tauri/Vite/React; add qcm-core path dependency; create minimal window; no fs/shell/http broad plugins; integrate existing CI without removing .NET jobs.

### Tests
`pnpm typecheck`, `pnpm test`, `cargo test`, `pnpm tauri build` current OS.

---

## TASK-031 — QcmClient + mock contract

### Goal
One frontend native boundary.

### Files
`src/platform/contracts.ts`, `qcmClient.ts`, `tauriQcmClient.ts`, `mockQcmClient.ts`.

### Steps
Define methods from API doc, DTO discriminated unions, cancellation/subscription handles. Add lint/import rule preventing `@tauri-apps` outside platform folder.

### Tests
Contract mock scenarios; import-boundary static test.

---

## TASK-032 — Profile/settings commands

### Goal
Wire non-device core through Tauri.

### Steps
Register get app/settings/new/open/apply/save/save-as/undo commands. Pickers are native command-internal. Convert stable errors. Validate payload lengths.

### Tests
Direct Rust command handler tests + frontend adapter tests.

---

## TASK-033 — Device commands + confirmation plans

### Goal
Expose safe device use cases without paths.

### Steps
Wire list/refresh/folder-picker/library/prepare+commit install/delete/rename/reorder/preferences. Core revalidates confirmation ID and device generation.

### Negative tests
Forged ID, stale generation, traversal filename, reused confirmation, expired confirmation, marker removed.

---

## TASK-034 — Channels and invalidation events

### Goal
Wire live/progress without event flood.

### Steps
Implement `start_live_input` Channel, install progress Channel, devices-changed invalidation. Add cleanup handles and StrictMode double-mount test.

### Acceptance
No global telemetry-frame event; listener/stream count returns to zero after unmount.

---

## TASK-035 — Capabilities and CSP

### Goal
Make WebView compromise low-privilege.

### Steps
Start default minimal capability; add only used core window permissions; leave filesystem/shell/http/secure-store/updater direct frontend permissions absent. Set production CSP/no remote navigation. Add static check against forbidden permissions/imports.

### Tests
Attempt forbidden plugin calls in security harness; domain commands still work.

---

## TASK-036 — App shell/design/accessibility primitives

### Goal
Build stable presentation substrate.

### Files
`src/app`, `components/primitives`, `styles/tokens.css`.

### Steps
Port semantic color/type/spacing tokens, theme/reduced-motion, shell navigation, dialogs/toasts/live regions, focus restoration helpers. No feature-specific config logic.

### Tests
Keyboard/axe/theme/high-contrast component tests.

---

## TASK-037 — Localization migration

### Goal
Preserve every current locale and pseudo-loc.

### Steps
Convert base and ar/de/es/fr/hi/it/ja/ko/nl/pl/pt/zh-Hans resources deterministically. Preserve format placeholders/plurals; generate pseudo locale; set `lang`/`dir`; migrate Rust error codes to frontend localized messages without logging user data.

### Tests
Key completeness, placeholder compatibility, pseudo overflow snapshots, Arabic RTL critical flow.

---

## TASK-038 — Editor parity UI

### Goal
Implement modes/bindings/issues/raw list against snapshots.

### Steps
Build controller and components; every edit calls typed EditorOp; implement revision conflict; raw grid view is projection; mode management parity; friendly/raw/controller labels.

### Tests
Mock E2E edit→undo→save, issue focus, mode reorder, keyboard.

---

## TASK-039 — Centered accessible QuadStick visualizer

### Goal
Deliver target visual architecture without making geometry mandatory.

### Steps
Create SVG/photo coordinate model from existing hotspots/assets, render assignments inside/around device, selected/active layers, mode context, keyboard hotspot navigation, synchronized semantic input/action list, reduced motion, live-frame overlay.

### Tests
Every hotspot has semantic counterpart; screen-reader list can perform same edit; active state clears stale/disconnect; zoom/RTL/pseudo-loc.

---

## TASK-040 — Local/import/export parity

### Goal
Complete file-facing workflows before device write UI.

### Steps
Native pickers → opaque refs; CSV open/save-as; external-change conflict if current behavior requires; XLSX parser/import review; skipped-tab/limitation model; export. Add import size/decompression caps.

### Tests
Malicious/huge XLSX, formulas not executed, K/L/M+ preserved, cancel paths.

---

## TASK-041 — Install/device library UI

### Goal
Expose safe core transaction.

### Steps
Device status/select, prepare install, exact default/prefs confirmation, named-stage progress, receipt with backup location, focus restoration. Library list/delete/rename/reorder with generation refresh.

### Tests
No/one/two device; unplug; stale generation; backup/verify errors; full keyboard/AT.

---

## TASK-042 — Device settings + visual band

### Goal
Port preference editing and explanatory live visualization.

### Steps
Preference catalog groups/controls/defaults, current device prefs source, joystick dead/full rings, photo hotspots, live joystick/button text with live-region off, install/write through canonical profile transaction.

### Tests
Fallback defaults for malformed numeric display, accessibility text equivalent, live reader absent/product/pressed states.

---

## TASK-043 — Community profiles

### Goal
Port on-demand community catalog workflow.

### Steps
Native/core allowlisted fetch, local cache, no fetch at startup/home, offline cached open, validate downloaded profile before editor/install, sanitize metadata.

### Tests
Privacy network spy asserts only Community action fetches; malformed catalog/profile; offline cache.

---

## TASK-044 — Google auth + secure tokens

### Goal
Port current supported Windows/macOS auth safely.

### Steps
Native system browser, random loopback, PKCE/state/2m timeout, drive.file scope, token refresh, Keychain/DPAPI or approved secure adapter. Linux stays unavailable until secure-store B improvement passes.

### Tests
state mismatch, timeout, invalid_grant/revoked, token redaction, source build unconfigured state.

---

## TASK-045 — Google backup/restore/share

### Goal
Port DriveClient/DriveBackup semantics.

### Steps
Direct REST/approved client, one workbook tab per mode, RAW values, stale-tail clear, temporary rename reshape preserving formatting, linked Sheet ID, async push after local save, dirty retry, precheck/cherry-pick, rescue local before keep-online replace, link sharing confirmation.

### Tests
HTTP mock snapshots; local save succeeds when backup fails; conflict recovery; xlsx export behavior; no profile/path/token analytics.

---

## TASK-046 — Privacy, crash rescue, diagnostics, feedback

### Goal
Make existing `PRIVACY.md` promises true in target.

### Steps
Port consent state/install ID, closed analytics event schema, source-build/CI hard-off, crash local-first rescue/report prompt, omit error message from report if current privacy promise says so, diagnostics bundle redaction, feedback explicit send.

### Tests
Network spy from launch/home/community/analytics-off; exact telemetry allowlist; reset deletes pending report per policy.

---

## TASK-047 — Signed updater

### Goal
Replace UpdateCheck with safe Tauri update flow.

### Steps
Native wrapper, signed manifest/artifact, check nonfatal/offline, download/install disabled while dirty/unsafe device transaction, release channel separation, rollback docs.

### Tests
bad signature rejected, network error nonfatal, dirty profile blocks restart, old version artifact retained.

---

## TASK-048 — Agent pipeline parity

### Goal
Retain the substantial `agent/` corpus/eval/finalize workflow without giving AI native privileges.

### Steps
Inventory `agent/README.md`, corpus/cache/charts/eval scripts and expected qsf JSON. Point agent execution to Rust qsf/typed operations. Any model/network process remains explicitly feature-scoped and cannot call generic Tauri shell. Preserve eval corpus/results needed to prove 21-control behavior or other current metrics.

### Tests
Run existing pipeline/evals against old/new qsf on same corpus; compare accepted/rejected edit operations; validate every final profile through qcm-config.

---

## TASK-049 — Accessibility release hardening

### Goal
Pass real assistive technology, not only automated tests.

### Steps
Execute `25-accessibility.md` matrix, record versions/results, fix focus/semantics/zoom/RTL/reduced-motion, verify device operations and visualizer entirely keyboard/nonvisual.

### Acceptance
No P0/P1 AT issue; signed RC artifact attached to test ledger.

---

## TASK-050 — Windows release matrix

### Goal
Prove packaged Windows release.

### Steps
Signed installer/WebView2 clean VM; storage full/read-only/unplug; all practical HID modes; Google DPAPI migration/reconnect; NVDA/Narrator; update install/rollback.

### Acceptance
All Windows critical matrix rows HARDWARE-VERIFIED/PASS.

---

## TASK-051 — macOS release matrix

### Goal
Prove both architectures, signing/notarization, storage/HID/Keychain/accessibility.

### Steps
arm64+x64 builds, Gatekeeper/notarization, `/Volumes` discovery, unplug, HID, Keychain migration/reconnect, VoiceOver, updater.

### Acceptance
macOS matrix pass; old Skia workaround correctly retired E.

---

## TASK-052 — Linux release matrix

### Goal
Define and prove honest Linux support.

### Steps
Clean supported distro, WebKitGTK packages, mount discovery, HID/udev, package install/uninstall, keyboard/AT smoke. Google remains unavailable unless secure-store improvement separately passes.

### Acceptance
Supported distro and limitations documented; no hidden permission setup.

---

## TASK-053 — Performance/reliability soak

Run baselines against target, 2h live stream, repeated discovery/stream start-stop, >=100 physical reconnects primary OS, repeated sacrificial installs with hashes, sleep/wake, open-handle/memory checks. Fix leaks before beta.

---

## TASK-054 — Adversarial security/privacy review

Enumerate registered commands/capabilities/plugins/network endpoints. Act as compromised WebView: forge IDs, traversal names, oversized IPC, confirmations, remote content. Verify `PRIVACY.md` network matrix with traffic capture/test server. Remove unnecessary permission/dependency.

---

## TASK-055 — Side-by-side beta

Create beta bundle/app-data/update channel, import/reconnect settings deliberately, no overwrite of stable data, publish prerelease with fallback instructions. Test both apps installed; handle device busy safely.

---

## TASK-056 — Full hardware RC

Use `tests/hardware/<rc>.md`: local edit/install/readback/delete/order/preferences/live input, all supported OSes, multiple/incorrect drives, unplug at write stages, Google where supported, localization/a11y. Attach hashes/log bundle sanitized.

---

## TASK-057 — Rollback rehearsal

From a machine upgraded to Tauri RC, close it and return to previous stable. Confirm profile/backups remain usable, Google token behavior known, updater channel can be withdrawn, no manual database conversion required.

---

## TASK-058 — Stable cutover

Only after DoD sign-off. Build/tag/sign/notarize, verify artifacts/signatures, publish stable, then update stable updater manifest. Monitor only existing-consent diagnostics/support. Keep previous stable.

---

## TASK-059 — Legacy retirement decision

After a proven stable cycle, review porting ledger. Preserve oracle fixture outputs and useful history/tools. Move/delete Avalonia only in a dedicated PR whose rollback is repository-only and which does not alter profile data formats.

---

## TASK-060 — Mobile feasibility

On physical iPhone/iPad + Android devices, test actual QuadStick USB/HID/storage/Bluetooth capabilities with tiny native/Tauri-plugin spikes. Produce proven/unsupported/bridge-required matrix. No production mobile UI/device promise before this report.