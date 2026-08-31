# Rewrite implementation status

Integration branch: `rewrite/tauri-rust`

This file is the current execution checkpoint. `50-first-implementation-tasks.md` remains the requirement source. Git history and GitHub Actions remain the detailed evidence trail.

## Status vocabulary

Tasks are tracked on independent axes so hardware or credential debt is not confused with missing implementation:

- **Implementation** — code/spec work exists.
- **Automated verification** — exact-head tests/gates have passed.
- **Physical validation** — real QuadStick/OS validation where required.
- **Human/credential/release validation** — tracked separately when a later task requires it.

## Current task state

| Task | Implementation | Automated verification | Physical validation | Evidence |
|---|---|---|---|---|
| TASK-001–019 parity foundation | **DONE** | **PASS** | N/A | frozen C# oracle, qcm-config differential/property/fuzz suite, Rust qsf |
| TASK-020 profile session manager | **DONE** | **PASS** | N/A | `crates/qcm-core/src/profiles/**` |
| TASK-021 errors/ops/confirmations | **DONE** | **PASS** | N/A | `qcm-core/src/{error,operation,confirmation,clock}.rs` |
| TASK-022 storage port + fake | **DONE** | **PASS** | N/A | `qcm-core/src/ports/storage.rs`, `qcm-testkit` |
| TASK-023 discovery | **DONE** | **PASS** | **PENDING** real-device rows | discovery + storage adapters |
| TASK-024 safe install | **DONE** | **PASS** | **PENDING** sacrificial/unplug rows | stage-fault matrix in `qcm-core`/`qcm-testkit` |
| TASK-025 device library/delete/order/preferences | **DONE** | **PASS** | **PENDING** final real-device pass | device library operations/tests |
| TASK-026 Rust HID backend spike | **PENDING PHYSICAL SPIKE** | N/A | **PENDING** Windows/macOS/Linux + modes | task is intrinsically physical |
| TASK-027 descriptor-driven HID adapter | **DONE** | **PASS fixture/CI** | **PENDING** actual QuadStick/backend validation | `src-tauri/src/adapters/hid.rs` |
| TASK-028 LiveInputManager | **DONE** | **PASS** | **PENDING** final live-device pass | `qcm-core/src/live/**` |
| TASK-029 serial scope | **DONE — DEFERRED BY EVIDENCE** | **PASS audit** | N/A | no current serial behavior to port |
| TASK-030 Tauri + React scaffold | **DONE** | **PASS** | N/A | `src-tauri/`, React/Vite root |
| TASK-031 QcmClient + mock | **DONE** | **PASS** | N/A | `src/platform/**` |
| TASK-032 profile/settings commands | **DONE** | **PASS** | N/A | Tauri command/IPC shell |
| TASK-033 device commands + confirmation plans | **DONE** | **PASS** | covered later by device matrix | opaque device/plan/confirmation API |
| TASK-034 bounded channels + invalidation | **DONE** | **PASS** | live-device close/reconnect later | typed Channels + invalidation |
| TASK-035 capabilities + CSP | **DONE** | **PASS** | N/A | exact-head run `33338800870` |
| TASK-036 accessible app shell/design tokens | **DONE** | **PASS** | final manual AT later | exact-head run `33338801740` |
| TASK-037 localization migration | **IN PROGRESS** | candidate includes deterministic catalog/RTL/pseudo/error tests; exact-head gate pending | final manual locale/AT hardening later | generated from shipping RESX keyspace |

The TASK-035/TASK-036 integration was also exercised by draft PR #5's synthetic merge run `33339138829`, which passed Gate 0, Rust parity, frontend checks and fuzz smoke against then-current `main`.

## HID state

The known QuadStick HID contract is encoded in Rust: shipped HID identities are filtered, application collections are descriptor-selected, X/Y and buttons are usage-driven, report IDs/missing axes are handled, reads have bounded cancellation latency, and native paths remain redacted. Native Xbox/XInput-only detection remains a platform-specific physical limitation rather than something faked through HID.

TASK-026 and the physical row of TASK-027 remain validation debt. A backend limitation found with real hardware may replace the native adapter without changing `LiveInputPort`, `LiveInputManager`, Channels, or the UI contract.

## WebView/device boundary

Device discovery/library/install/delete/open-preferences use opaque IDs/generations and process-local one-shot plans. Device writes are still safe core transactions. Live input uses a capacity-one latest-wins stream. The main capability has an empty permission set; generic Tauri filesystem/shell/http/process/store/updater capability is not exposed to React.

## Localization boundary

TASK-037 uses `src/QuadStick.App/Strings*.resx` as the shipping translation source of truth. The React catalogs are generated deterministically; absent satellite entries fall back to English, placeholders must remain compatible, pseudo-loc is generated from English, Arabic sets `dir=rtl`, and firmware/file tokens are not translated by config serialization. Stable Rust error codes are mapped to localized frontend messages instead of treating Rust fallback English as presentation authority.

The normal rewrite gate regenerates catalogs and fails if committed generated output is stale.

## Remaining validation debt

- real QuadStick discovery/install/unplug/HID/reconnect rows;
- TASK-026 physical HID backend matrix;
- final VoiceOver/NVDA/Narrator/zoom/high-contrast/RTL manual matrix;
- code-signing/notarization/update credentials and platform release artifacts;
- exact release-candidate physical and rollback matrices.

These are not blanket blockers for implementing later product slices.

## Next implementation boundary

Finish and promote TASK-037 after its exact-head gate, then build TASK-038 editor parity UI on the proven integration head.
