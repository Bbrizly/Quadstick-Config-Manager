# Target repository layout

Use a **small workspace**, not an enterprise monorepo.

```text
/
├── src/                         # React/TypeScript UI
│   ├── app/
│   ├── components/
│   ├── features/
│   │   ├── editor/
│   │   ├── device/
│   │   ├── live-input/
│   │   ├── import/
│   │   ├── community/
│   │   ├── backup/
│   │   ├── settings/
│   │   └── agent/
│   ├── i18n/
│   ├── styles/
│   ├── platform/
│   │   ├── qcmClient.ts
│   │   ├── tauriQcmClient.ts
│   │   ├── mockQcmClient.ts
│   │   └── contracts.ts
│   └── test/
├── src-tauri/
│   ├── Cargo.toml
│   ├── tauri.conf.json
│   ├── capabilities/
│   └── src/
│       ├── lib.rs
│       ├── commands/
│       ├── ipc/
│       └── adapters/
│           ├── storage/
│           ├── hid/
│           ├── serial/          # only after serial parity is proven
│           ├── secure_store/
│           ├── cloud/
│           └── diagnostics/
├── crates/
│   ├── qcm-config/
│   ├── qcm-core/
│   └── qcm-testkit/
├── tests/
│   ├── parity/
│   ├── e2e/
│   └── hardware/
├── fixtures/
│   ├── profiles/
│   ├── malformed/
│   └── oracle/
├── legacy-dotnet/               # optional only at final tree-move phase; initially leave existing src/tests in place
└── docs/tauri-rewrite/
```

## Dependency direction

```text
qcm-config <- qcm-core <- src-tauri
                   ^
                   |
             tests/testkit

React -> QcmClient contracts -> Tauri adapter -> commands -> qcm-core
```

`qcm-config` must compile/test without Tauri or OS-specific dependencies. `qcm-core` may use async/concurrency abstractions but cannot import React/Tauri concepts.

## Why only three crates initially

Splitting parser, validator, device model, cloud policy and telemetry into a dozen crates would add public APIs before we understand stable boundaries. Start with modules inside these crates; split only when independent version/build/reuse pressure is demonstrated.

## Legacy placement rule

Do **not** move existing C# source at TASK-001. Keeping paths stable makes differential tests and git archaeology simpler. Move to `legacy-dotnet/` only near cutover if doing so has a concrete build/release benefit.