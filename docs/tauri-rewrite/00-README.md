# QCM Tauri 2 + Rust rewrite — master index

## Mission

Rebuild QuadStick Config Manager from .NET 8/Avalonia 11.1.3 into **Tauri 2 + React 19.2 + TypeScript + Rust**, without losing firmware compatibility, device-write safety, accessibility behavior, localization, backup/recovery guarantees, or test evidence.

The C#/Avalonia application remains the production fallback while implementation advances in isolation on `rewrite/tauri-rust`. The rewrite does not replace `main` until the cutover gates pass.

## Audit baseline

- Repository: `Bbrizly/Quadstick-Config-Manager`
- Source branch: `main`
- Source commit: `f7783944387202bcafaeb7ff3f67789098fa6a4e`
- Source tree: `ae95291b4750d9d4d3ba2c9f464a56e74f344d73`
- Audit date: 2026-08-29

All `CURRENT FACT` statements in this spec must trace back to this revision unless marked otherwise.

## Target in one picture

```text
React/TypeScript UI
      |
      | typed QcmClient
      v
Tauri commands + low-rate events + streaming Channels
      |
      v
Rust application/core
  |          |           |
qcm-config  device ports cloud/settings ports
  |          |           |
  +------ native adapters in src-tauri ------+
             |
     filesystem / HID / serial / OS / network
             |
          QuadStick
```

### Hard boundaries

1. TS never performs arbitrary filesystem, serial, HID, keychain, shell, or credential work.
2. `qcm-config` is Tauri-free and OS-free.
3. Rust owns canonical config semantics and editor revision state; TS owns presentation and ephemeral form state.
4. UI references opaque `ProfileSessionId`, `DeviceId`, `OperationId`; native code owns real paths/handles.
5. High-rate live input uses `tauri::ipc::Channel`, not global events.
6. Existing C# + `FirmwareOracle` remain an oracle until Rust parity is proven.

## Read order

**Everyone:** 01 → 06 → 07 → 11 → 44 → 48 → 52.

**Rust/config:** 10, 11, 12–19, 33–39.  
**Frontend:** 20–25.  
**Device/platform:** 12–18, 28–32.  
**Release/security:** 26–27, 40–47.  
**Agents:** `AGENTS.md`, then 52 → 49 → 50 and all ledgers.

## Execution authority

`52-final-product-execution-spec.md` governs dependency ordering, safe parallelism, consolidation cadence, remote-checkpoint policy, and the separate implementation/automated/physical/credential/human/release validation states. `49-implementation-checklist.md` remains the canonical phase/task acceptance checklist, and `50-first-implementation-tasks.md` remains the detailed task contract. If task ordering or blocker classification conflicts, use 52; stricter behavioral or safety requirements elsewhere still win.

## Critical findings

- `src/QuadStick.App/MainWindow.axaml.cs` is a ~317 KB orchestration/UI code-behind and currently owns editor state, settings, backup orchestration, filesystem calls, dialogs and substantial feature logic.
- `QuadStick.Format` is valuable but not actually a pure format library: `Device.cs` performs mounted-drive discovery, install, backup, read-back verification and deletion.
- `ProfileFile` deliberately preserves the raw CSV grid, comments/extra columns and quirks; a lossy DTO rewrite would break compatibility.
- The firmware does not use a conventional CSV parser. Blank-line, 63-character keyword, 1023-byte line and A..J semantics are safety-critical.
- `FirmwareOracle.cs` + `DeviceAgreementTests.cs` already encode unusually strong device-vs-app compatibility evidence. Reuse them.
- `LiveInput.cs` reads gamepad HID reports by HID report descriptor/usage, not fixed byte offsets. HID is required for live/practice parity.
- Current production mounted-storage installs already use backup → temp write → exact read-back → replace with restore handling. Rust must not regress this.
- Google Drive backup uses installed-app OAuth/PKCE and OS-protected refresh-token storage; Linux is intentionally disabled today because there is no persistent secure store.
- Localization currently includes base English plus ar/de/es/fr/hi/it/ja/ko/nl/pl/pt/zh-Hans and pseudo-localization.
- CI suppresses telemetry, tests on every PR/push, and packages Windows/macOS/Linux on version tags.

## What is deliberately not decided by taste

Every nontrivial decision has an ADR. Unknown hardware/mobile/serial facts remain explicit open questions. `System.IO.Ports` is a current dependency, but no production serial path is considered parity-required until code usage is proven; see `15-serial-transport.md`.

## Completion navigation

- Behavior inventory: `05-behavior-inventory.md`
- Compatibility contract: `06-behavioral-compatibility-contract.md`
- Architecture: `07-target-architecture.md`
- Old → new mapping: `09-old-to-new-traceability.md`
- Device/file safety: `17-mass-storage-and-filesystem.md`
- IPC: `20-tauri-command-api.md`, `21-tauri-event-api.md`
- Risks: `47-risk-register.md`
- Definition of done: `48-definition-of-done.md`
- Master checklist: `49-implementation-checklist.md`
- Mechanical tasks: `50-first-implementation-tasks.md`
- Open questions: `51-open-questions.md`
- **Execution authority: `52-final-product-execution-spec.md`**
- Port status: `ledgers/PORTING_LEDGER.md`

## Rule for continuing implementation

Use `52-final-product-execution-spec.md` to select the next unmet dependency and safe parallel wave. A task with pending physical, credential, human, or release validation is not automatically implementation-blocked; keep that validation debt explicit instead of stopping buildable work.