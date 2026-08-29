# Dependency plan

## Version policy

At scaffold TASK-010, pin exact versions in `Cargo.lock` + `pnpm-lock.yaml`; commit toolchain/package-manager metadata. This spec records the current family/decision but does not freeze patch numbers months before implementation.

Current research snapshot (2026-08-29):
- Rust stable **1.98** (Rust Forge current release table, 2026-08-20).
- Node **24.x Krypton LTS**; Node 26 is Current. Use Node 24 LTS for build stability.
- Tauri latest researched stable line **2.11.x** (2.11.5 published 2026-07-01).
- React **19.2** stable line.
- Vite current supported line **8.2**; Vite 8.1 announced 2026-06-23.
- `hidapi` candidate **2.6.6**; hardware spike required.
- `serialport` current 4.x candidate only if serial is proven; maintenance risk noted.

## Proposed required dependencies

| Dependency | Purpose | Decision/constraint |
|---|---|---|
| `tauri` / `@tauri-apps/api` | shell/IPC | required, minimal plugins |
| React / React DOM | UI | selected ADR-003 |
| TypeScript | static frontend contracts | strict mode |
| Vite | frontend build | standard Tauri React path |
| `serde`, `serde_json` | IPC/config DTO | required |
| `thiserror` | internal typed error enums | candidate; avoid stringly errors |
| `tracing` | structured native logs | candidate |
| `uuid` | opaque session/operation IDs | candidate with minimal features |
| `sha2` | fixture/content fingerprints if needed | candidate; use std/OS where unnecessary |
| `hidapi` | live HID | candidate pending spike |
| HTTP client (`reqwest` likely) | Google/community/update-aux/telemetry | native-only; TLS feature audit |
| secure-store implementation | tokens | prefer OS native/keyring after platform spike |
| i18next + React integration | i18n | candidate pending RESX conversion test |
| Vitest / RTL / axe | frontend tests | dev-only |
| property/fuzz crates (`proptest`, cargo-fuzz/libFuzzer) | config assurance | dev/test only |

## Dependencies intentionally avoided initially

- Redux/Zustand/React Query — local native state does not justify them yet.
- Tailwind/large UI kits — design parity/polish can use project tokens; avoid framework lock-in.
- frontend filesystem/shell/http Tauri plugins — violate boundary.
- heavyweight Google SDK — current direct REST behavior is focused; evaluate only if it materially reduces auth/API correctness risk without huge surface.
- generic serial crate — until `OQ-001`.

## Evaluation checklist for every new dependency

- exact version/date;
- license compatible with project;
- active maintenance/recent security history;
- transitive/native dependencies;
- Windows/macOS/Linux support;
- binary-size impact;
- mobile implications;
- removal/replacement cost;
- why std/project code is insufficient.

Record all shipped dependencies in `matrices/dependency-matrix.md` and update ADR if architecture changes.

## Toolchain pinning

Use `rust-toolchain.toml` pinned to supported stable version (initially 1.98 unless Tauri/current crates require newer) and `.nvmrc`/`.node-version` for Node 24 LTS. Use `packageManager` in `package.json` to pin pnpm via Corepack after confirming current pnpm stable.