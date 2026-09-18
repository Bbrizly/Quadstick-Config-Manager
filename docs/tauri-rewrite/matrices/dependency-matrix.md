# Dependency matrix

Versions are audit-time families; TASK-010/030 pins exact lockfiles.

| Dependency | Scope | Audit version/family | License check | Risk | Alternative |
|---|---|---|---|---|---|
| Rust | build | stable 1.98 | standard toolchain | low | newer stable after CI proof |
| Node | build | 24 LTS | standard | low | 22 LTS if tool constraints |
| Tauri | native shell | 2.11.x | verify package set | medium platform | Avalonia/Electron rejected ADR |
| React | UI | 19.2 | MIT | low | Svelte/Solid |
| Vite | build | 8.x supported | MIT | low/medium fast-moving | alternate bundler not needed |
| serde/serde_json | Rust DTO | current compatible | MIT/Apache | low | impractical custom |
| thiserror | Rust errors | current compatible | MIT/Apache | low | manual Error impl |
| tracing | logs | current compatible | MIT | low | log crate |
| uuid | opaque IDs | current compatible | MIT/Apache | low | monotonic random IDs custom |
| hidapi | HID candidate | 2.6.6 | verify transitive/backend | medium/native | platform APIs/other HID crate |
| serialport | serial candidate | 4.x | verify | medium maintenance; only if needed | tokio-serial/platform API |
| reqwest | native HTTP candidate | current compatible | MIT/Apache | medium transitive/TLS | ureq/Tauri HTTP, Google SDK |
| secure store/keyring | token candidate | TBD spike | verify | medium desktop variance | direct DPAPI/Keychain/libsecret |
| i18next/react-i18next | UI i18n candidate | pin at TASK-037 | MIT | low/medium | generated custom Intl runtime |
| Vitest | tests | Vite-compatible | MIT | low | Jest |
| React Testing Library | tests | current | MIT | low | DOM Testing Library |
| axe integration | a11y tests | current | MPL for axe-core; verify wrapper | low | Playwright axe |
| proptest | Rust property tests | current | MIT/Apache | low dev-only | quickcheck |
| cargo-fuzz/libFuzzer | fuzz | current | dev-only | low | AFL/honggfuzz |

No dependency graduates from candidate to shipped without license/platform/security review and ledger update.