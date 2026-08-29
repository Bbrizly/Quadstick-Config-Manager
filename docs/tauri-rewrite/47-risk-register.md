# Risk register

Scale: Likelihood L/M/H; Impact L/M/H/Critical. IDs map to `matrices/risk-matrix.md`.

| ID | Risk | L | Impact | Prevention / detection | Contingency |
|---|---|---:|---:|---|---|
| R-001 | C#→Rust config semantic drift | H | Critical | differential oracle, golden/property/fuzz | block cutover; C# oracle remains |
| R-002 | raw K/L/M+ data lost by structured model | M | Critical | raw-grid canonical invariant, lossless fixtures | restore from backup; parser fix |
| R-003 | firmware blank-line/limit behavior changed | M | Critical | FirmwareOracle + boundary fixtures | no device install until fixed |
| R-004 | partial/corrupt USB write | M | Critical | backup/temp/readback/replace/restore + fault injection | restore receipt/backup; withdraw release |
| R-005 | stale mount path writes wrong drive | L/M | Critical | opaque ID + generation/fingerprint + marker revalidate | halt write; diagnostics |
| R-006 | removable filesystem rename assumptions false | M | H | real FAT hardware tests; conservative language | preserve backup/restore path |
| R-007 | HID fixed-offset shortcut breaks modes | M | H | descriptor parsing parity + captures/hardware | capability-disable live mode |
| R-008 | XInput limitation hidden | M | M | explicit capability state | document/add XInput adapter later |
| R-009 | serial scope creep without real use | M | M | OQ-001 evidence gate | omit serial from parity |
| R-010 | MainWindow god object recreated in React | H | H | feature/controller boundaries + review | refactor before feature expansion |
| R-011 | duplicate Rust/TS profile truth | H | Critical | native canonical session/revision API | conflict errors/refetch |
| R-012 | Tauri WebView gets generic system power | M | Critical | minimal capabilities + command audit | remove permission/hotfix |
| R-013 | OAuth token exposed/migrated insecurely | L/M | Critical | Keychain/DPAPI/secure adapter; redaction | revoke/reconnect; security release |
| R-014 | Google backup conflict loses local work | M | Critical | rescue local before replacement; fake cloud tests | restore rescue copy |
| R-015 | privacy contract regresses | M | H | `PRIVACY.md` as behavior tests; closed telemetry schema | disable analytics/update policy |
| R-016 | localization coverage collapses | H | M/H | migrate all catalogs + pseudo-loc/RTL CI | block stable UI cutover |
| R-017 | screen-reader/keyboard regression | H | H | semantic parallel visualizer + AT checklist | block release |
| R-018 | high-rate live stream rerenders whole app | M | M | Channel + local latest state + rAF | throttle/refactor |
| R-019 | HID worker/IPC listener leak | M | M/H | cancellation/StrictMode/soak | restart/patch; handle counters |
| R-020 | device operation races | M | Critical | per-device mutation gate + generation | typed busy/stale; no write |
| R-021 | shutdown interrupts unsafe storage stage | M | Critical | transaction-safe checkpoints; shutdown ordering | restore/diagnostic state |
| R-022 | Tauri/plugin dependency vulnerability | M | H | minimal deps, lock/audit | patch/replace dependency |
| R-023 | `hidapi` backend/packaging issue | M | H | early cross-platform spike | alternative adapter/native API |
| R-024 | Linux WebKitGTK/HID permissions vary | H | M | explicit distro baseline/udev tests | narrow Linux support docs |
| R-025 | updater/signing misconfiguration | M | H/Critical | signed release verification; keys isolated | freeze manifest; previous artifact |
| R-026 | settings/token migration traps downgrade | M | H | beta separate app-data; explicit migrators | reconnect/use previous app |
| R-027 | agent gains arbitrary native power | M | Critical | typed EditorOp-only bridge | disable agent feature |
| R-028 | malicious XLSX zip bomb | M | H | size/cell/decompression caps | reject typed import error |
| R-029 | rewrite delays useful product work | H | H | gates, parity slices, no mobile before desktop | stop after usable phase/reassess |
| R-030 | stale/missing repo docs create false assumptions | M | H | source evidence labels + second audit | open unresolved; do not implement guess |
| R-031 | existing agent corpus/evals are lost | M | M/H | inventory `agent/` scripts/corpus/eval and port contract | retain Python pipeline until replacement proven |
| R-032 | source-build network privacy changes | M | H | no secrets => network telemetry off; CI env test | fail release/security gate |

Every risk must gain an owner/status in the matrix before implementation; P0/Critical risks cannot be accepted implicitly.