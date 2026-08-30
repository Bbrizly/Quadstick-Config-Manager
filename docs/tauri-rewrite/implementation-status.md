# Rewrite implementation status

Implementation branch: `rewrite/tauri-rust`
Draft PR: `#5`

This is the execution checkpoint. The numbered specification remains the source of task requirements; this file records what has actually been implemented.

| Task | State | Evidence |
|---|---|---|
| TASK-001 freeze implementation base | **DONE** | `implementation-baseline.md` |
| TASK-002 fixture manifest | **DONE** | manifest verification green in Rewrite parity CI |
| TASK-003 deterministic C# oracle | **DONE** | compile/selfcheck/generate green in Rewrite parity CI |
| TASK-004 parity schema | **DONE** | checked-in schema + `tools/oracle/validate.py` enforced in CI |
| TASK-005 Avalonia baseline | **DONE WITH EXPLICIT UNKNOWNS** | `baseline-performance.md` |
| TASK-006 close Phase-0 ledgers | **DONE** | no UNASSESSED rows; serial closed-deferred; `gate0-review.md` |
| TASK-007 Rust workspace | **DONE** | pinned Rust 1.98.0 + fmt/clippy/test gate green |
| TASK-008 exact CSV port | **DONE** | C# differential parse/write parity green |
| TASK-009 model/issue types | **DONE** | `model.rs`, `issue.rs`; no OS/Tauri dependencies |
| TASK-010 vocab/catalog embedded data | **DONE** | source fingerprints + exact C# catalog parity green |
| TASK-011 header/sheet parser | **DONE** | C# differential document/sheet structure parity across headerless, wrong-case, comma-blank, missing-blank, mixed-section and legacy profiles |
| TASK-012 binding/preferences/IR parsing | **DONE** | C# differential binding projection parity for row/output/function/C..J input columns/L action name across the profile corpus |
| TASK-013 validation + firmware limits | **DONE** | ordered C#↔Rust severity/cell/kind parity across the profile corpus; strict run `33277932680` green |
| TASK-014 serializer + normalization | **DONE** | exact normalized bytes, idempotence and comment-column retention differential-gated; run `33278517792` green |
| TASK-015 profile editor operations | **DONE** | typed editor ops with C# differential apply parity; run `33280142764` green |
| TASK-016 undo/dirty/revision/action names | **DONE** | undo baseline, dirty flag and revision counter match legacy on same-value edits; run `33280974525` green |
| TASK-017 C#<->Rust differential suite | **DONE** | one-command parity runner (`make parity`) drives every oracle stage; run `33282067180` green |
| TASK-018 property/fuzz/size-limit suite | **DONE** | proptest suite plus three cargo-fuzz targets with seed corpus; run `33282493293` green |
| TASK-019 qsf on the Rust core | **DONE** | Gate 0, one-command parity and fuzz smoke all green on `c44f968` in run `33284642619` |
| TASK-020 profile session manager | **DONE** | `qcm-core/src/profiles/**` + `ports/local.rs`; session/revision/close-dirty suite green against the fake library |
| TASK-021 stable errors/operations/confirmations | **DONE** | `qcm-core/src/{error,operation,confirmation,clock}.rs`; redaction suite covers every error family |
| TASK-022 storage port + fake device | **DONE** | `qcm-core/src/ports/storage.rs` + `qcm-testkit`; every fault stage exercised without hardware |
| TASK-023 mounted QuadStick discovery | **DONE (no hardware yet)** | `qcm-core/src/devices/discovery.rs` + `src-tauri/src/adapters/storage/**`; 3 s scan cache, opaque ids, generation on remount, marker rechecked on every lookup |
| TASK-024 safe install transaction | **DONE (no hardware yet)** | `qcm-core/src/devices/install.rs`; every stage fault-injected, restored/unchanged/uncertain asserted, no cancel inside the swap |
| TASK-025 device library/delete/order/preferences | **DONE (no hardware yet)** | `qcm-core/src/devices/library.rs`; `DeviceFileManagementTests` ported, LED table 1-32 exact, backup before delete |
| TASK-030 Tauri 2 + React + Vite scaffold | **DONE** | `src-tauri/` + root React app; `pnpm typecheck`/`pnpm test`/`pnpm lint`, `cargo clippy --workspace --all-targets --locked -- -D warnings`, `cargo test --workspace --locked` and `pnpm tauri build` (macOS `.app` + `.dmg`) all green locally |

### TASK-030 pins

Tauri 2.11.5 with tauri-build 2.6.3 and `@tauri-apps/cli` 2.11.4 / `@tauri-apps/api` 2.11.1, React 19.2.8, Vite 8.2.2, TypeScript 5.9.3, Vitest 4.1.11, oxlint 1.80.0, Node 24.19.0, pnpm 11.7.0. Every version is exact, and `Cargo.lock` and `pnpm-lock.yaml` are committed.

TypeScript is held at 5.9.3, not the 7.0.2 that is current. The type-aware lint and testing tools in use still cap the compiler below 6.1, so 7.x would mean dropping one of them.

The shell has no commands and no adapters. It opens a window, renders React and links `qcm-core` so the dependency direction is fixed from the first build. `src-tauri/capabilities/main.json` grants the window nothing: no filesystem, shell, opener, HTTP or process permission. The production CSP allows `'self'` only plus the Tauri IPC origin, with `object-src`, `frame-src`, `frame-ancestors` and `form-action` set to `'none'`. TASK-035 owns the negative permission tests that hold that line.

The shell's bundle identity is deliberately separate from the shipping app: identifier `com.bbrizly.quadstickconfigmanager.rewrite`, product name `QuadStick Config Manager Rewrite`, so both can be installed side by side until cutover. Vite writes to `dist-ui/` because `dist/` already holds the .NET installers.

## Current verified checkpoint

TASK-018 completed at `0e58d023d72563b1a1ad085735a9031937eb60c3`. Rewrite parity run `33282493293` passed the full legacy .NET suite, oracle generation and schema validation, Rustfmt, Clippy with warnings denied, and the whole Rust differential, property and fuzz-smoke suite.

Phase 2 of the checklist (TASK-007 through TASK-019) is therefore green up to and including TASK-018. `qcm-config` now covers CSV, model, catalog, parser, bindings, validation, serialization, normalization, typed editor operations and editor session state, each compared cell by cell against artifacts generated by the frozen C# oracle.

## Where the work actually stops

Phase 2 of the checklist is finished. The Rust `qcm-config` crate reads, validates, edits, normalizes and writes a QuadStick profile with cell-exact parity against the frozen C# implementation, and the `qsf` CLI on top of it matches the .NET tool's JSON contract.

Phase 3 has started at the contract end. `crates/` now holds three crates: `qcm-config`, `qcm-core` and `qcm-testkit`. `qcm-core` carries the error families, operation identity, expiring confirmations, the device storage port, the local profile store port and the profile session manager. `qcm-testkit` carries a fake QuadStick drive that can fail at any stage of the install sequence, and a fake profile library.

The session manager is the type any UI will drive: opaque session ids, an origin that is not the same thing as a save target, a revision-tagged snapshot, all-or-nothing typed edit batches under `expected_revision`, a prepare/commit save split, and an explicit answer required before unsaved work is dropped. No path crosses its boundary in either direction.

The device chain is now in. `qcm-core/src/devices/` holds one service that owns
the storage port, the off-device backup area and the confirmation ledger:
discovery with the shipped 3 second scan cache, the install transaction, and the
library operations. `src-tauri/src/adapters/storage/` is the only place in the
app that sees a path.

Live input is in as far as it can go without a HID decision. `qcm-core/src/live/`
holds the manager and the bounded stream over a `LiveInputPort`, and
`qcm-testkit` carries the fake that drives them. The manager owns the search,
the four backoff timers, the jitter filter, the stale window and the stop and
restart, all ported from `LiveInput.cs`. Nothing in it can fail: every way live
input goes wrong ends as a state the window renders, because the settings page
works without it.

Two things are load-bearing there. The stream holds one snapshot and the newest
wins, so a disconnect can never be overtaken by a pressed frame queued behind it
(ADR-015). And motion lives inside the `Reading` variant of the status, so no
other state can spell a held button: a stop, a stale window, an unplug and a
port failure all clear the visualizer by construction rather than by remembering
to. A stuck button here is somebody's input held down with nothing left that can
release it.

What is not in: no HID crate, no real adapter, no VID/PID table, no descriptor
parsing. TASK-026 and TASK-027 own those and need a dependency decision that has
not been made. TASK-028 built the port, the manager and the fake, and stops
there.

TASK-029 is resolved and implemented nothing, which was the right answer. The
premise of OQ-001 turned out to be false: `System.IO.Ports` has never been a
dependency of this app in any commit, and the only occurrence of `SerialPort`
outside the docs is a test asserting the device-files window does not contain
it. Serial and the Bluetooth console stay classified D. The evidence is in
`51-open-questions.md` and the stale dependency line that started it is
corrected in `02-current-system-inventory.md`.

There is still no frontend beyond the scaffold and no command surface: nothing
in `src-tauri` is wired to a Tauri command yet.

OQ-004 is answered for now and recorded in `51-open-questions.md`: local save stays at parity with the legacy `WriteAtomic`. `SavePlan` and `commit_save` are the seam a device-grade backup-and-read-back contract drops into without moving the command surface. Two smaller deferrals sit behind the same seam. The C1 Google-sheet stamp the legacy `SaveAsync` applied belongs to the Drive work in TASK-045, so nothing stamps it yet. And there is no size cap on opening a local profile, because the frozen implementation has none to match; `ConfigError::TooLarge` exists for whoever adds one.

One piece of scaffolding had to be taken out. Rust was not installable on the machine driving TASK-019, so a `qsf-diagnostic` job was added that patched `qsf.rs` inside CI, ran `cargo fmt --all`, and pushed the result back to the branch with `contents: write`. That is how `c44f968` was authored by `github-actions[bot]`, and why no workflow ran on it: a push made with `GITHUB_TOKEN` does not trigger one. Run `33284642619` was dispatched by hand and proved the real gates green on that commit. The job has been deleted and the workflow returned to `contents: read`. Never let the parity gate write to the branch it is judging.

## Branch layout

Each task is its own branch, stacked in order, `task10` through `task19`. Every branch is an ancestor of the next, so the head branch carries all earlier work. `rewrite/tauri-rust` (draft PR #5) still points at the TASK-018 head and has to be moved forward once TASK-019 is green.

`main` is six commits ahead of the rewrite chain with unrelated Avalonia UI work. The rewrite branches do not need those commits to build, but the chain will have to be rebased before any merge.

## Gate 0 evidence

The frozen C#/.NET implementation remains the compatibility oracle. Every rewrite run keeps the full legacy test suite green, self-checks the oracle under multiple cultures, generates canonical artifacts, validates the parity schema, and only then runs Rust checks.

One existing Avalonia selection test was observed to fail nondeterministically during TASK-011. The same unchanged rewrite candidate subsequently passed the full legacy suite, and an untouched `1b9c8ca` control rerun passed as well. No legacy application behavior was changed to accommodate the timing failure.

## Rust parity rule

A Rust implementation is not called parity-tested merely because its unit tests pass. CI generates expected artifacts from the frozen C# implementation and transfers them to the Rust job. CSV compares every parsed cell and exact writer bytes. Catalog parity compares vocabulary sets/order, function arity/order, ordered preference metadata, and the default template. Parser parity compares document metadata, sheet discovery/order, and every projected binding field across the profile corpus. Validation parity compares the ordered severity/cell/kind issue projection; localized issue text is deliberately not used as a cross-language compatibility key.

## Dependency boundary

TASK-010 introduced only `serde 1.0.229` and `serde_json 1.0.151` to `qcm-config`, with the exact Cargo.lock generated by the pinned Rust 1.98.0 toolchain. Through TASK-013 the crate still has no Tauri, OS, device, filesystem-write, or network dependency.

TASK-023, TASK-024 and TASK-025 added no external crate either. The storage
adapter is written against `std` alone. That has one consequence worth writing
down rather than hiding: Windows cannot be asked whether a drive is removable
without `GetDriveTypeW`, and every crate that can ask needs an `unsafe` call the
workspace forbids. So the Windows enumeration probes drive letters, excludes
`%SystemDrive%`, and lets the `default.csv` marker decide. That is broader than
the shipped rule, which accepts removable drives only: a second fixed disk with a
`default.csv` in its root would be offered where the Avalonia app would not offer
it. Closing the gap is a dependency decision, recorded in `blockers.md`, not
something to paper over.

Free space is reported as `None` on every platform for the same reason. The core
already treats that as "the platform will not say" rather than as zero, so a
drive that cannot report its size is never shown as full.

TASK-028 added no external crate either. The live input port, manager, bounded
stream and fake are `std` alone: a `Mutex` for the one-slot buffer, an `Arc` for
the shared read side, and the existing `Clock` for every deadline. The port is
blocking for the reason the storage port already records, and the manager is
pumped by whoever owns a thread rather than owning one itself, which is what
keeps `qcm-core` free of a runtime.

The port's fallible calls return the existing `DeviceError` family rather than a
new one. That flattens "the stick was unplugged" and "the operating system would
not hand it over" onto `NotFound`, which is deliberate: the shipped reader draws
the same distinction, which is none, because both mean no live reading and the
page still works. If TASK-027 finds a case where the two need different words on
screen, `DeviceError` grows a variant then, with the evidence for it.

TASK-020, TASK-021 and TASK-022 added no external crate at all. `qcm-core` depends on `qcm-config` and `serde`, with `serde_json` and `qcm-testkit` for tests; `qcm-testkit` depends on `qcm-core` alone. Neither has Tauri, an OS crate, a network client or anything that writes a file: both ports are traits, and the adapters that touch a real volume or a real folder arrive later.

The dev-dependency from `qcm-core` back on `qcm-testkit` is a cycle in the test graph only, which Cargo allows. It is there so the session tests drive the ports through the fakes that already exist instead of a second copy of them growing inside `qcm-core`.

Two boundary decisions worth writing down.

The port is blocking, not `async`. `13-device-discovery.md` sketched an `async_trait`, but an async port would pull a runtime into a crate whose whole claim is that it has no OS dependency, and the install sequence is a sequence rather than a race. The adapter runs it on a worker.

`QcmErrorDto` carries two fields beyond the sketch in `33-error-model-and-recovery.md`: `target_state` and `backup`. The failure matrix in `17-mass-storage-and-filesystem.md` requires the window to say restored, unchanged or uncertain, and it cannot say any of them from a code and a message alone.

## Execution rule

A task is only marked DONE when its artifact exists and its non-hardware acceptance criteria have been implemented. Hardware-only verification can be recorded as `NEEDS HARDWARE` without pretending it ran.

The legacy .NET/Avalonia implementation remains buildable while parity-first Rust replacement proceeds.
