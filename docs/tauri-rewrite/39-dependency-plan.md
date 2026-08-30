# Dependency plan

## Version policy

Pin Rust and frontend dependency versions exactly on the rewrite integration branch. Commit `Cargo.lock`, `pnpm-lock.yaml`, toolchain metadata and package-manager metadata. New dependencies must be justified by an implemented boundary, not added speculatively.

Current pinned foundation:
- Rust **1.98.0**.
- Node **24.19.x** / pnpm **11.7.0**.
- Tauri **2.11.5**, tauri-build **2.6.3**, `@tauri-apps/cli` **2.11.4**, `@tauri-apps/api` **2.11.1**.
- React / React DOM **19.2.8**.
- Vite **8.2.2**.
- TypeScript **5.9.3**.
- `serde` **1.0.229**, `serde_json` **1.0.151**.
- `hidapi` **2.6.6** for the native live-input adapter.

## Required dependencies in the implemented rewrite

| Dependency | Purpose | Boundary |
|---|---|---|
| `tauri` / `@tauri-apps/api` | shell and typed IPC | frontend reaches it only through `src/platform/tauriQcmClient.ts` |
| React / React DOM | UI | presentation only |
| TypeScript | frontend contracts | strict platform boundary |
| Vite | frontend build | packaged UI only |
| `serde`, `serde_json` | DTO/config serialization | native/core contracts |
| `rfd` | native profile/device folder picker | called from Rust command; no WebView path API |
| `hidapi = 2.6.6` | enumerate/open HID, read report descriptor and blocking reports | `src-tauri/src/adapters/hid.rs` only; HID path/handle stays native |
| Vitest / RTL | frontend contract tests | dev-only |
| `proptest` / cargo-fuzz | config assurance | test/fuzz only |

## HID decision

TASK-027's descriptor-driven adapter is implemented against `hidapi 2.6.6` rather than inventing fixed report offsets or another OS abstraction. The adapter filters the nine HID identities already proven by the shipped HidSharp reader, then parses the actual report descriptor to select only Joystick/Game Pad/Multi-axis Application collections and extract Generic Desktop X/Y plus Button usages.

The dependency is pinned with `macos-shared-device` and `windows-native`. The live worker uses a bounded 100 ms HID read timeout so disposing the final subscriber can close the handle promptly instead of waiting a full second.

This is an **implementation decision, not the TASK-026 hardware verdict**. The actual QuadStick/platform matrix still has to prove enumeration, descriptor access, coexistence and unplug behavior on Windows/macOS/Linux. A failure in that physical spike means replacing the adapter/backend while keeping `LiveInputPort`, `LiveInputManager`, IPC channels and frontend contracts unchanged.

## Dependencies intentionally absent

- Redux/Zustand/React Query — current local/native state does not justify them.
- Tailwind/large UI kits — avoid UI-framework lock-in.
- Tauri filesystem/shell/http/opener/process/store/stronghold/updater plugins — generic WebView privilege violates the boundary.
- heavyweight Google SDK — evaluate only when TASK-045 proves a focused native REST implementation is insufficient.
- serial crate — OQ-001/TASK-029 resolved serial as no current app behavior.

## Evaluation checklist for every future dependency

- exact version and lockfile update;
- license compatibility;
- active maintenance/security history;
- transitive/native dependencies;
- Windows/macOS/Linux behavior;
- binary-size impact;
- mobile implications;
- removal/replacement cost;
- why std/project code is insufficient;
- which architectural layer owns it;
- whether it expands WebView capability or network/file/device privilege.

Record shipped dependency changes in the dependency matrix and update the relevant ADR when the architecture changes.
