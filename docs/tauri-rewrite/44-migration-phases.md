# Migration phases and gates

The migration is a **strangler**, not a delete-and-rebuild weekend.

## Phase 0 — Forensic freeze

### Prerequisites
Audited source SHA.

### Tasks
- enumerate every source/test/tool/asset behavior;
- resolve serial-use evidence enough to classify;
- build behavior/porting/test ledgers;
- record Avalonia performance/hardware baseline;
- create fixture manifest and oracle export.

### Outputs
Complete inventory, baseline, oracle corpus.

### GO
No important current module remains `UNASSESSED`; every behavior that can change bytes/device/user data has ID/class.

### NO-GO
“Probably unused” critical code without evidence.

## Phase 1 — Freeze config compatibility

Port/characterize CSV + parser + validator + normalization + mutations in isolation.

### GO
Rust test harness exists; C# oracle outputs deterministic; all fixtures classified.

## Phase 2 — Rust `qcm-config`

Implement pure config semantics in port order.

### GO
Differential parser/serializer/edit tests pass; firmware-oracle agreement passes; fuzz/property suite has no blockers.

## Phase 3 — Rust core + device adapters

Implement session manager, opaque IDs, discovery, storage transaction, library and fake adapters; HID spike/adapter.

### GO
Fault-injection transaction tests pass; real hardware storage install/readback/delete/library and live HID smoke pass on primary OS.

## Phase 4 — Tauri shell/API

Scaffold Tauri 2, least privilege, typed commands/channels/errors, mock/native clients.

### GO
A minimal web UI can open/edit/save via native core; malicious IPC negative tests pass; capability audit passes.

## Phase 5 — Frontend foundation

Tokens, i18n, shell, accessibility primitives, QcmClient, editor state controller, visualizer skeleton.

### GO
Mock-browser critical path is keyboard/axe/pseudo-loc clean; no feature code imports Tauri outside platform adapter.

## Phase 6 — Feature parity

Suggested order:
1. local new/open/edit/save;
2. modes/bindings/raw list/issues;
3. mounted device install/library/preferences;
4. live HID/device band;
5. import/XLSX/community;
6. settings/theme/localization/tutorial;
7. Google backup/share;
8. crash/telemetry/update;
9. agent/qsf parity.

Every feature must map behavior IDs + tests before parity sign-off.

### GO
Feature-parity matrix has no required red rows.

## Phase 7 — Intended new experience

Now implement centered visualizer/minimal layout, richer practice presentation, other B improvements. Do not mix format semantics changes into visual redesign PRs.

## Phase 8 — Platform hardening

Windows/macOS/Linux packaged testing, filesystem/HID quirks, signing, accessibility, soak, privacy, updater.

### GO
All release-blocking matrix cells pass.

## Phase 9 — Beta side-by-side

Ship separate Tauri beta identity/channel; collect explicit feedback/diagnostics under existing privacy contract.

### GO
No critical data-loss/device-write/accessibility regressions; rollback rehearsal works; required hardware matrix passed.

## Phase 10 — Stable cutover

Update stable bundle/update channel only after sign-off. Preserve backups and legacy installer.

## Phase 11 — Legacy retirement

Remove/move Avalonia/C# only when ledgers terminal and at least one stable Tauri release proves rollback unnecessary for normal users. Keep oracle fixtures/history.

## Phase 12 — Mobile

Run iOS/Android physical hardware feasibility and implement separate adapters/bridge if proven.

## Gate format for every phase PR

```text
Prerequisites: met/not met
Behavior IDs: ...
Porting ledger rows: ...
Tests automated: ...
Tests manual/hardware: ...
Known failures: ...
Rollback: ...
GO/NO-GO: ...
```