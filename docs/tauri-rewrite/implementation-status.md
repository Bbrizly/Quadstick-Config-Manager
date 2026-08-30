# Rewrite implementation status

Integration branch: `rewrite/tauri-rust`

This file is the current execution checkpoint. `50-first-implementation-tasks.md` remains the requirement source. Git history remains the detailed evidence trail.

## Status vocabulary

A task is tracked on independent axes so hardware or CI debt is not confused with missing implementation:

- **Implementation** — code/spec work exists.
- **Automated verification** — tests/gates exist and have been executed on the current head.
- **Physical validation** — real QuadStick/OS/manual validation where required.

The current TASK-033 through TASK-035 wave was explicitly requested to proceed **without running CI yet**. Those tasks therefore remain `CI DEFERRED`, not `CI GREEN`.

## Current task state

| Task | Implementation | Automated verification | Physical validation | Evidence |
|---|---|---|---|---|
| TASK-001–019 parity foundation | **DONE** | **GREEN on recorded historical runs** | N/A | frozen C# oracle, qcm-config differential/property/fuzz suite, qsf |
| TASK-020 profile session manager | **DONE** | implemented tests | N/A | `crates/qcm-core/src/profiles/**` |
| TASK-021 errors/ops/confirmations | **DONE** | implemented tests | N/A | `qcm-core/src/{error,operation,confirmation,clock}.rs` |
| TASK-022 storage port + fake | **DONE** | implemented fault tests | N/A | `qcm-core/src/ports/storage.rs`, `qcm-testkit` |
| TASK-023 discovery | **DONE** | fake/temp tests implemented | **PENDING** real-device rows | `qcm-core/src/devices/discovery.rs`, storage adapters |
| TASK-024 safe install | **DONE** | stage-fault tests implemented | **PENDING** sacrificial/unplug rows | `qcm-core/src/devices/install.rs` |
| TASK-025 device library/delete/order/preferences | **DONE** | behavior ports/tests implemented | **PENDING** final real-device pass | `qcm-core/src/devices/library.rs` |
| TASK-026 Rust HID backend spike | **PENDING PHYSICAL SPIKE** | N/A | **PENDING** Windows/macOS/Linux + modes | architecture isolates this verdict from the rest of live input |
| TASK-027 descriptor-driven HID adapter | **DONE** | descriptor/report fixture tests implemented; **CI DEFERRED** | **PENDING** actual QuadStick/backend validation | `src-tauri/src/adapters/hid.rs` |
| TASK-028 LiveInputManager | **DONE** | fake/backpressure/stop/restart tests implemented | **PENDING** final live-device pass | `qcm-core/src/live/**` |
| TASK-029 serial scope | **DONE — DEFERRED BY EVIDENCE** | source/history review complete | N/A | no current serial behavior to port |
| TASK-030 Tauri + React scaffold | **DONE** | previously locally verified | N/A | `src-tauri/`, React/Vite root |
| TASK-031 QcmClient + mock | **DONE** | contract/import-boundary tests | N/A | `src/platform/**` |
| TASK-032 profile/settings commands | **DONE** | direct command/adapter tests | N/A | `src-tauri/src/{commands,ipc,shell}.rs` |
| TASK-033 device commands + confirmation plans | **DONE** | tests implemented; **CI DEFERRED** | covered later by device matrix | native plan/commit device command surface |
| TASK-034 bounded channels + invalidation | **DONE** | streaming/cleanup tests implemented; **CI DEFERRED** | live-device close/reconnect later | `streaming.rs`, install progress, QcmClient/Tauri/mock streams |
| TASK-035 capabilities + CSP | **DONE** | security/navigation tests implemented; **CI DEFERRED** | N/A | zero-permission capability, strict CSP, navigation guard, security harness |

## TASK-027 — HID implementation state

The known QuadStick HID contract is now encoded in Rust instead of being rediscovered:

- all nine HID VID/PID identities from the shipped HidSharp reader are filtered;
- Xbox 360 native mode remains explicitly outside HID/XInput-only;
- matching composite interfaces are opened and their HID report descriptors inspected;
- only Joystick, Game Pad or Multi-axis top-level Application collections are accepted;
- X/Y are extracted by Generic Desktop usages and normalized from descriptor logical ranges;
- buttons are extracted from Button-page usages;
- report IDs and missing-axis layouts are handled;
- no report uses a hard-coded byte offset;
- device paths and path-shaped product strings remain native/redacted;
- the blocking read timeout is 100 ms so final-subscription disposal has bounded handle-release latency.

`hidapi = 2.6.6` is the selected removable backend. TASK-026 still owns the real-hardware verdict. If physical validation exposes a backend limitation, replace the native adapter without changing `LiveInputPort`, `LiveInputManager`, channel contracts or UI code.

## TASK-033 — device command boundary

The WebView no longer needs a path-shaped device API. Device discovery/library/install/delete/open-preferences flows use opaque device ids/generations plus native one-shot operation/confirmation plans. Install/delete authority stays process-local; confirmation IDs are bound to the prepared operation. Device files opened for editing become working copies and cannot be written back except through the safe install transaction.

## TASK-034 — bounded streaming boundary

TASK-034 is implemented end to end:

- live input uses the core capacity-one latest-wins stream;
- one native live worker is shared instead of one reader per component;
- Tauri typed `Channel<T>` values are caller-scoped; no global telemetry event bus was introduced;
- subscription IDs are explicit and disposal is idempotent;
- dead/dropped listeners are removed;
- React StrictMode-style mount/cleanup/remount/cleanup has deterministic mock coverage;
- device-change messages are invalidations, not a second source of device truth;
- install progress is emitted from the core transaction only after a stage actually completed;
- install receipt/failure remains authoritative;
- cancellation semantics were not weakened inside the unsafe swap region.

The frontend platform boundary, Tauri adapter and browser mock expose the same subscription/progress contract.

## TASK-035 — WebView least privilege

The main capability grants an empty permission set. No generic Tauri fs/shell/http/opener/process/store/stronghold/updater plugin is installed for frontend use. Native file/device/HID work stays behind QCM commands.

Production CSP is local/IPC-only, the Tauri global is disabled, prototype freezing is enabled, and asset protocol access is disabled. The shell now rejects document navigation outside QCM's packaged origins; debug development admits only the pinned Vite origin.

`src/platform/securityBoundary.test.ts` pins the capability, CSP, manifests and command ledger. Rust tests pin navigation origins and reject `plugin:` commands. `src/platform/importBoundary.test.ts` continues to enforce the single Tauri frontend adapter.

## Deliberately deferred verification

The following are **not implementation blockers** and are intentionally not being represented as complete:

1. **Current-head CI sweep** — run Rust fmt/clippy/test, frontend lint/typecheck/test, parity/legacy gates and Tauri build after this implementation wave. Deferred by explicit request.
2. **Cargo lock regeneration check** — the native `hidapi` manifest addition must be reflected in the generated root `Cargo.lock` before a `--locked` sweep is green. This is generated-state debt, not an architectural unknown.
3. **TASK-026 physical HID matrix** — actual QuadStick modes/platforms, descriptor access, coexistence and unplug/cancel behavior.
4. **Device physical rows** — sacrificial install/unplug/reconnect scenarios from TASK-023/024/025 and later release matrices.

No task is to be described as CI-green or hardware-validated until the corresponding evidence exists.

## Next implementation boundary

TASK-036 is the next numbered implementation task: app shell and accessibility primitives. TASK-035 closes the native/WebView security boundary required before building the production UI on top of it.
