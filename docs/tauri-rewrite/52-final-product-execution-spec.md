# QCM Rust/Tauri rewrite — final product execution spec

## Status and authority

This document is the orchestration authority for completing the Rust/Tauri rewrite from the current post-format-parity state through stable release.

It does **not** replace the detailed behavior, architecture, security, accessibility, test, or per-task acceptance documents. In particular:

- `48-definition-of-done.md` defines what finished means.
- `49-implementation-checklist.md` defines the canonical phase/task inventory.
- `50-first-implementation-tasks.md` defines detailed task-level implementation and acceptance requirements.
- this document defines **execution order, dependency rules, parallelism, validation classification, consolidation policy, and release sequencing**.

If this document conflicts with a safety or behavioral requirement elsewhere, the stricter safety/behavioral requirement wins. If another document merely implies a different task order or treats a pending physical test as a blanket implementation blocker, this document governs orchestration.

---

# 1. Mission

Finish QuadStick Config Manager as a production-quality **Tauri 2 + React + TypeScript + Rust** desktop application while preserving the current product's:

- firmware/config compatibility;
- raw-grid fidelity;
- device-write safety;
- recovery/backup behavior;
- live QuadStick input behavior;
- accessibility;
- localization;
- privacy/security;
- Google/community/update workflows;
- agent/qsf behavior;
- cross-platform release support.

The rewrite is not complete when the UI looks finished. It is complete only when the applicable automated, physical, credential, human, security, packaging, rollback, and release evidence is complete on the exact release candidate.

`main` remains the known-good Avalonia product until stable cutover. Deleting Avalonia is a later decision.

---

# 2. Starting point

Treat the latest **consolidated rewrite commit that passes the full parity gate** as the implementation base.

TASK-001 through TASK-019 are considered complete only on a head that passes the complete deterministic gate:

1. legacy .NET tests;
2. fixture manifest validation;
3. deterministic C# oracle generation;
4. oracle JSON schema validation;
5. `cargo fmt --all -- --check`;
6. `cargo clippy --workspace --all-targets --locked -- -D warnings`;
7. `cargo test --workspace --locked`;
8. C# ↔ Rust differential parity;
9. property/fuzz invariants required by the existing task spec.

Do not infer completion because an agent reports that code exists or compiles.

---

# 3. Architecture that must not drift

The target architecture remains:

```text
React / TypeScript
        |
     QcmClient
        |
   Tauri IPC boundary
 commands / events / Channels
        |
     qcm-core
      /    \
qcm-config   ports
               |
        native adapters
 filesystem / HID / secure store / HTTP / updater
```

Hard rules:

1. `qcm-config` is deterministic, OS-free, network-free, and Tauri-free.
2. `qcm-core` owns application state and use cases.
3. React does not own canonical config state.
4. React does not receive real device paths, native handles, secure tokens, or secrets.
5. Only the frontend platform boundary may import Tauri APIs.
6. No generic frontend `read_file`, `write_file`, `exec`, `shell`, `open_hid`, `open_serial`, or `http(url)` capability exists.
7. Device mutation is serialized per logical device.
8. UI disabled state is advisory, not synchronization.
9. No operation lock is held while waiting for user confirmation or slow HTTP.
10. Live input uses bounded/latest-frame streaming, not unbounded global events.
11. Errors crossing IPC are stable structured DTOs, not raw OS exceptions.
12. No TypeScript path may independently serialize a device-safe CSV.
13. `qcm-testkit` is never a production dependency.

Architecture changes require an explicit ADR and must not be introduced casually by task agents.

---

# 4. Agent and branch protocol

## Canonical branches

- `main` — current stable Avalonia product.
- `rewrite/tauri-rust` — canonical rewrite integration branch / draft PR.
- `rewrite/taskXX-*` — temporary isolated task branches/worktrees.

## Task-agent contract

Every task agent must:

1. start from the latest accepted `rewrite/tauri-rust` head;
2. own a clearly defined file/module set;
3. avoid unrelated refactors;
4. add tests with implementation;
5. run the smallest relevant loop while developing;
6. run the task's required gate before reporting completion;
7. report:
   - commit SHA;
   - files changed;
   - tests run;
   - acceptance evidence;
   - remaining physical/manual/credential/release validation debt;
   - known limitations.

## Orchestrator contract

Before accepting a task, the orchestrator must:

1. inspect the diff;
2. reject scope creep;
3. confirm the branch started from the intended base;
4. integrate only verified commits;
5. rerun the consolidated gate;
6. update the execution/porting/test ledgers;
7. create a remote checkpoint after each meaningful dependency wave.

Do not let the local agent fleet become multiple dependency waves ahead of the last remotely verified integration commit.

Independent work may continue in isolated branches while CI runs, but an unverified integration head must not silently become the dependency base for the entire fleet.

---

# 5. Validation model — do not use one `BLOCKED` boolean

Every remaining task tracks these dimensions independently:

```text
IMPLEMENTATION:
  NOT_STARTED | READY | IN_PROGRESS | DONE

AUTOMATED_VERIFICATION:
  NOT_STARTED | PARTIAL | PASS

PHYSICAL_VALIDATION:
  N/A | PENDING | PASS | FAIL

CREDENTIAL_VALIDATION:
  N/A | PENDING | PASS | FAIL

HUMAN_VALIDATION:
  N/A | PENDING | PASS | FAIL

RELEASE_VALIDATION:
  N/A | PENDING | PASS | FAIL
```

Example:

```text
TASK-024
IMPLEMENTATION: DONE
AUTOMATED_VERIFICATION: PASS
PHYSICAL_VALIDATION: PENDING
```

That is **validation debt**, not an implementation blocker.

This distinction is mandatory for device, HID, accessibility, credential, and release work.

TASK-022 exists specifically so device safety can be developed and heavily verified without physical hardware.

---

# 6. CI structure

Keep one complete integration gate, but do not unnecessarily couple pure Rust/config parity to graphical shell dependencies.

## Gate A — deterministic core parity

Must not require WebKitGTK merely to run:

- legacy .NET tests;
- fixture manifest validation;
- C# oracle generation;
- oracle schema validation;
- qcm-config/qcm-core/qcm-testkit formatting;
- clippy with warnings denied;
- Rust tests;
- differential parity.

## Gate B — frontend

- locked dependency install;
- typecheck;
- lint/static boundaries;
- unit/component tests;
- accessibility automation;
- pseudo-loc/RTL tests where applicable.

## Gate C — Tauri/platform builds

- Windows;
- macOS;
- Linux with required WebKitGTK/system packages.

After each dependency wave, the exact consolidated SHA must pass the relevant remote CI before it becomes the long-lived integration base.

---

# 7. Wave 1 — qcm-core foundation

## TASK-020 — profile session manager

Implement the canonical application-level profile state.

Required behavior:

- opaque `ProfileSessionId`;
- opaque source reference;
- profile snapshot DTO;
- revision number;
- expected-revision mutation;
- dirty state;
- undo integration;
- save preparation;
- save completion / mark-clean behavior;
- close-with-dirty policy;
- multiple simultaneous sessions;
- no raw path exposure outside Rust/native boundaries.

Required tests:

- two simultaneous sessions;
- stale revision rejection;
- concurrent mutation attempts;
- edit → snapshot;
- save preparation;
- save completion;
- dirty close;
- undo/revision sequence.

Done means a headless test can new/open/edit/undo/save/close through `qcm-core` without React or Tauri.

## TASK-021 + TASK-022 — stable errors/operations/confirmations + storage port/testkit

These may remain one implementation unit because the storage interfaces use the stable error/operation model.

Implement:

- stable error families;
- recoverability/action hints;
- `OperationId`;
- operation fingerprint;
- expiring confirmation;
- single-use confirmation;
- replay rejection;
- error/path/secret redaction.

The storage port exposes only the scoped operations required by core use cases.

`qcm-testkit` fake storage must model:

- root marker present/missing;
- files;
- logical device generation;
- read-only media;
- storage full;
- unplug;
- fail-at-stage injection.

It must not become a generic arbitrary filesystem API.

### Wave 1 acceptance

- qcm-core has no Tauri dependency;
- qcm-testkit is not linked by production crates;
- session tests pass;
- confirmation/error tests pass;
- fake-device fault injection passes;
- the full deterministic parity gate remains green.

---

# 8. Parallel shell track — TASK-030

TASK-030 may run alongside late Wave 1 once qcm-core's crate/interface shape is stable.

This is **shell-only**.

Implement:

- pinned Node 24 toolchain;
- pnpm lockfile;
- React;
- TypeScript;
- Vite;
- Tauri 2;
- minimal window;
- Rust workspace integration;
- qcm-core path dependency;
- frontend test harness.

Do not:

- invent alternate domain APIs;
- move canonical state into React;
- add broad filesystem/shell/http plugins;
- implement feature business logic in the shell.

Required shell checks:

```text
pnpm typecheck
pnpm test
cargo test
pnpm tauri build
```

---

# 9. Wave 2 — device storage core

TASK-023, TASK-024, and TASK-025 are implementation work even if final physical rows remain pending.

## TASK-023 — mounted QuadStick discovery

Implement:

- ready-volume enumeration;
- current removable/macOS parity heuristic;
- `default.csv` marker recognition;
- opaque `DeviceId`;
- logical generation;
- sanitized display metadata;
- revalidation on lookup;
- zero/one/multiple candidate behavior;
- stale-device behavior.

Automated tests:

- fake candidates;
- OS temp/integration coverage where practical;
- marker disappears;
- stale generation;
- duplicate-looking candidates;
- inaccessible candidate.

Physical validation debt:

- real zero-device scenario;
- real one-device scenario;
- real multiple-device scenario.

Physical rows do not prevent implementation from reaching DONE.

## TASK-024 — safe install transaction

This is safety-critical.

Required transaction shape:

```text
revalidate device
↓
validate target
↓
protected confirmation if required
↓
create unique OFF-DEVICE backup
↓
write temporary file
↓
flush/sync
↓
close
↓
exact readback
↓
enter non-cancellable unsafe swap section
↓
replace target
↓
verify target where safe
↓
restore if required/possible
↓
return truthful receipt
```

The result must distinguish at least:

```text
SUCCESS_VERIFIED
FAILED_UNCHANGED
FAILED_RESTORED
FAILED_UNCERTAIN
```

Never report success merely because an OS write/move call returned success.

Fault-inject every meaningful stage using TASK-022.

Required automated cases include:

- backup failure;
- temp creation failure;
- partial write;
- storage full;
- temp readback mismatch;
- unplug/failure before swap;
- failure during replacement;
- target disappearance;
- restore success;
- restore failure;
- marker removal;
- stale generation.

Physical validation debt:

- sacrificial real QuadStick write/readback;
- representative physical unplug windows.

## TASK-025 — device library/delete/order/preferences

Implement:

- protected `default.csv`;
- protected `prefs.csv`;
- filename validation;
- list profiles;
- default-first ordering;
- remaining ordering according to current contract;
- backup-before-delete;
- safe rename;
- reorder semantics;
- audited LED/file-number table;
- preference read;
- preference write through TASK-024 transaction safety.

Port the current C# device-management behavior tests instead of inventing new semantics.

### Device-core milestone

At the end of TASK-023 through TASK-025, every behavior that fundamentally treats the QuadStick as a filesystem root must work against deterministic fake storage and pass automated fault testing.

---

# 10. TASK-029 — resolve serial scope

Run this independently as a source/history/tests audit.

Determine whether serial is currently parity-required production behavior.

If not required:

```text
OQ-001 = DEFERRED / NOT PARITY REQUIRED
```

and add no serial crate.

If required, produce a separate protocol/dependency/hardware specification before any production serial implementation.

Do not add speculative serial code because `System.IO.Ports` exists somewhere in the legacy project.

---

# 11. Wave 3 — HID and live input

## TASK-026 — Rust HID backend proof

This task's purpose itself requires real hardware.

Using a physical QuadStick, prove the selected Rust/native HID backend can:

- find the known VID/PID modes available for testing;
- inspect/report usable descriptor information;
- locate the correct top-level joystick/gamepad collection;
- read X/Y;
- read buttons;
- handle cancellation/unplug;
- behave correctly on available target OSes.

Compare against the shipped HidSharp behavior.

Output an evidence table containing:

```text
OS
VID/PID/mode
enumeration
descriptor access
axes
buttons
unplug
reconnect
limitation
PASS / FAIL
```

Freeze the dependency ADR only after evidence exists.

## TASK-027 — descriptor-driven HID adapter

Treat this as two subphases.

### 027A — pure descriptor/report interpretation

Build now from captured fixtures:

- known identity filtering;
- top-level usage selection;
- descriptor field extraction;
- X/Y normalization;
- missing-axis behavior;
- button extraction;
- jitter/dedupe behavior.

Automated tests use captured descriptors/reports and malformed variants.

### 027B — real backend binding

After TASK-026 selects/proves the backend:

- enumeration;
- open/read worker;
- cancellation;
- unplug/backoff;
- reconnect behavior.

Physical validation remains a separate row.

## TASK-028 — LiveInputManager

Build primarily with fake streams.

Own:

- exactly one active source;
- bounded/latest-frame transport;
- sequence;
- timestamp;
- stale state;
- stop/restart;
- disconnect handling;
- XInput-only capability limitation;
- complete cleanup.

Tests:

- producer faster than consumer;
- pressed button → disconnect → state clears;
- repeated start/stop;
- fake reconnect;
- 2-hour fake soak;
- zero remaining listeners/workers after shutdown.

No unbounded telemetry queue is acceptable.

---

# 12. Wave 4 — Tauri boundary

## TASK-031 — QcmClient and mock contract

Create the single frontend-native boundary, conceptually:

```text
src/platform/contracts.ts
src/platform/qcmClient.ts
src/platform/tauriQcmClient.ts
src/platform/mockQcmClient.ts
```

Strongly type:

- profile/session DTOs;
- device DTOs;
- errors;
- operation IDs;
- confirmation plans;
- live frames;
- progress;
- cancellation/subscription handles.

Add a static rule: any `@tauri-apps/*` import outside the allowed platform boundary fails CI.

## TASK-032 — profile/settings commands

Wire:

- app/settings info;
- new;
- open;
- snapshot;
- apply typed EditorOp;
- undo;
- save;
- save as;
- close.

Native pickers remain native-side. Validate IPC payload sizes. Never accept arbitrary paths from JavaScript.

## TASK-033 — device commands and confirmation plans

Wire:

- list/refresh devices;
- library;
- prepare install;
- commit install;
- delete;
- rename;
- reorder;
- preference read/write;
- folder picker only where the domain requires it.

Mandatory negative tests:

- forged ID;
- stale generation;
- traversal filename;
- expired confirmation;
- reused confirmation;
- marker removed;
- mismatched operation fingerprint.

## TASK-034 — channels and invalidation

Use:

### Commands
request/response operations.

### Low-rate events
only invalidation/state changes such as `devices-changed`.

### Channels
ordered/high-rate streams such as:

- live input;
- install progress.

Test React StrictMode double mount/unmount. Listener/worker counts must return to zero.

## TASK-035 — capability/CSP lockdown

After real commands exist:

- minimum Tauri capability;
- production CSP;
- no remote navigation;
- no generic filesystem permission;
- no generic shell permission;
- no generic HTTP permission;
- no frontend secure-store access;
- no frontend raw updater primitive.

Add a security harness that intentionally attempts forbidden calls.

---

# 13. Wave 5 — frontend foundation

TASK-036 and TASK-037 may run in parallel after shared frontend conventions are frozen.

## TASK-036 — app shell/design/accessibility primitives

Implement:

- semantic color tokens;
- typography;
- spacing;
- theme;
- reduced motion;
- high-contrast behavior;
- navigation;
- dialogs;
- toasts;
- live regions;
- focus restoration;
- keyboard primitives.

Do not hide feature-specific domain logic inside generic components.

## TASK-037 — localization

Migrate every current locale and pseudo-loc pipeline:

- English;
- Arabic;
- German;
- Spanish;
- French;
- Hindi;
- Italian;
- Japanese;
- Korean;
- Dutch;
- Polish;
- Portuguese;
- Simplified Chinese;
- pseudo locale.

Requirements:

- placeholder compatibility;
- RTL;
- correct `lang` and `dir`;
- pseudo-localization;
- overflow testing;
- stable Rust error codes localized frontend-side;
- no user data leaked through localization/logging.

---

# 14. First real clickable-editor milestone

## TASK-038 — editor parity UI

Every edit goes through typed Rust EditorOps.

Implement:

- modes;
- bindings;
- outputs;
- issues;
- raw projection;
- add/delete/move rows;
- add/rename/reorder modes;
- undo;
- revision conflicts;
- friendly/raw/controller labels;
- issue → focus navigation.

There is no TypeScript serializer and no canonical React copy of the document.

## TASK-040A — local CSV path

Before completing XLSX, prove the primary local loop:

```text
open local CSV
→ edit
→ undo
→ edit
→ save
→ close
→ reopen
→ same canonical state
```

### Clickable-editor acceptance

From the Tauri app:

1. open a real QCM CSV;
2. display its modes/bindings;
3. modify a binding;
4. undo;
5. modify again;
6. save through Rust;
7. reopen;
8. pass qcm-config validation;
9. prove no JS path or serializer performed the native file work.

This is the first useful Rust/Tauri alpha milestone. It is not feature completion.

---

# 15. Wave 6 — primary product UI

## TASK-039 — centered accessible QuadStick visualizer

Build the target product experience rather than copying the old window layout.

Requirements:

- QuadStick remains the visual center;
- mode/profile context is obvious;
- assignments are positioned clearly around/in relation to the device;
- selected input state;
- active/live input state;
- output/action context;
- mode/profile/LED context where applicable;
- readable supported zoom behavior;
- reduced-motion handling.

Accessibility requirement:

Every graphical hotspot has a synchronized semantic counterpart. A nonvisual user can discover the input, hear its assignment, select it, edit it, and understand activation without relying on geometry.

Test keyboard, semantic screen-reader path, zoom, RTL, pseudo-loc, reduced motion, and stale/disconnect clearing.

## TASK-040B — import/export/XLSX

Complete:

- CSV open;
- save as;
- export;
- XLSX import/review;
- skipped-tab/limitation model;
- preservation of extra K/L/M+ data;
- external-change/conflict behavior where parity requires it.

Security:

- file-size caps;
- decompression caps;
- formulas are never executed;
- malformed archives fail safely.

## TASK-041 — install/device library UI

Build against QcmClient so MockQcmClient can drive every state.

UI states:

- no device;
- one device;
- multiple devices;
- disconnected/stale;
- install preparation;
- exact protected confirmations;
- named transaction progress stages;
- final receipt;
- backup location;
- library;
- delete;
- rename;
- reorder;
- generation refresh.

Mock scenarios must include unplug, backup failure, readback failure, full storage, and stale generation.

Real hardware is final validation, not a prerequisite for constructing or automatically testing this UI.

## TASK-042 — device settings + visual band

Implement:

- preference groups/catalog;
- defaults;
- current-device preferences;
- joystick dead/full rings;
- QuadStick hotspots;
- live axis text;
- live buttons;
- accessible text equivalents;
- malformed numeric fallback;
- quiet live-region behavior;
- writes through the canonical safe transaction path.

---

# 16. Wave 7 — secondary feature parity

Once shared interfaces are frozen, these may be distributed among agents with disjoint ownership.

## TASK-043 — community profiles

- fetch only after explicit Community action;
- allowlisted endpoint;
- local cache;
- offline cached open;
- validate every downloaded profile;
- sanitize metadata;
- no startup/home fetch.

## TASK-044 — Google authentication

Implement architecture and mocks without waiting for production credentials.

Use:

- system browser;
- PKCE;
- random state;
- loopback callback;
- timeout;
- `drive.file` scope;
- secure refresh-token storage;
- Windows DPAPI;
- macOS Keychain;
- Linux unavailable unless a secure-store improvement is separately proven.

Tests:

- state mismatch;
- timeout;
- revoked token;
- `invalid_grant`;
- redaction;
- unconfigured client state.

Credentials are validation debt, not an implementation blocker.

## TASK-045 — Google backup/restore/share

Implement against HTTP mocks first.

Preserve:

- workbook/tab semantics;
- RAW values;
- stale-tail cleanup;
- formatting-preserving reshape behavior;
- linked Sheet ID;
- local save success even if backup fails;
- async retry;
- cloud/local conflict protection;
- rescue-local-before-online-replace;
- sharing confirmation.

Live Google validation happens later with real credentials.

## TASK-046 — privacy/crash rescue/diagnostics/feedback

Implement:

- consent;
- install ID according to policy;
- closed telemetry event schema;
- source/dev/CI analytics hard-off;
- local-first crash rescue;
- explicit crash-report prompt;
- diagnostics redaction;
- explicit feedback send;
- reset/deletion behavior.

Run a network spy against launch/home/community-unused/analytics-off/crash-rescue/feedback-unused states. Unexpected network traffic is a failure.

## TASK-047 — signed updater

Implement:

- signed manifest/artifact validation;
- nonfatal offline check;
- release-channel separation;
- download/install;
- dirty-profile restart blocking;
- unsafe-device-operation blocking;
- rollback path.

A deliberately invalid signature must be rejected in tests.

Signing credentials block production release validation, not implementation.

## TASK-048 — agent pipeline parity

Point the existing agent/eval workflow at Rust qsf and typed EditorOps.

Requirements:

- same corpus;
- old/new qsf contract comparison where applicable;
- accepted/rejected edit operation comparison;
- every final generated profile validated by qcm-config;
- model/network process receives no generic native privilege.

---

# 17. Feature-parity gate

Before release hardening, every required existing feature must have automated evidence and no required red parity row may remain.

Explicitly account for:

- local editor;
- visualizer;
- CSV;
- XLSX;
- device install;
- device library;
- device preferences;
- live input;
- community;
- Google;
- privacy;
- crash rescue;
- feedback;
- updater;
- localization;
- accessibility automation;
- agent pipeline.

Pending hardware/credential/human rows are allowed only when explicitly tracked and scheduled for hardening/RC.

---

# 18. Wave 8 — hardening

## TASK-049 — accessibility release hardening

Real assistive-technology validation is required.

Windows:

- NVDA;
- Narrator.

macOS:

- VoiceOver.

Also verify:

- keyboard-only critical paths;
- high contrast;
- zoom;
- RTL;
- pseudo-loc;
- reduced motion;
- editor;
- visualizer;
- device install;
- library;
- settings;
- dialogs/errors.

No P0/P1 accessibility defect may remain.

## TASK-050 — Windows release matrix

Build and test the real signed installer on clean Windows environments.

Validate:

- install/uninstall;
- WebView2 behavior;
- mounted storage;
- read-only/full storage;
- unplug;
- practical HID modes;
- reconnect;
- DPAPI;
- Google reconnect;
- NVDA/Narrator;
- updater;
- rollback.

Implementation/package automation may exist before hardware/credentials, but the task closes only when applicable release rows pass.

## TASK-051 — macOS release matrix

Validate:

- arm64;
- x64;
- signing;
- notarization;
- Gatekeeper;
- `/Volumes` discovery;
- storage unplug;
- HID;
- Keychain;
- Google reconnect;
- VoiceOver;
- updater.

## TASK-052 — Linux release matrix

Define an intentionally narrow supported baseline.

Validate:

- clean supported distro;
- WebKitGTK requirements;
- package install/uninstall;
- mount discovery;
- HID;
- udev/permissions;
- keyboard/accessibility smoke.

Document every required permission/setup step honestly. Do not advertise unsupported behavior.

## TASK-053 — reliability/performance soak

Run:

- baseline comparisons;
- 2-hour live input;
- repeated discovery;
- repeated stream start/stop;
- >=100 physical reconnects on the primary OS;
- repeated sacrificial installs with hashes;
- sleep/wake;
- memory growth checks;
- open-handle growth checks;
- worker/listener leak checks.

Persistent leaks are fixed before beta.

## TASK-054 — adversarial security/privacy review

Assume the WebView is compromised.

Attempt:

- forged IDs;
- stale IDs;
- traversal;
- oversized IPC;
- confirmation replay;
- expired confirmations;
- remote navigation;
- generic native calls;
- network abuse;
- secret leakage.

Audit every registered command, event, Channel, capability, plugin, network endpoint, and dependency. Remove unnecessary privileges/dependencies.

---

# 19. Wave 9 — side-by-side beta

## TASK-055

Do not replace stable yet.

Beta must have:

- separate app identity;
- separate update channel;
- deliberate settings migration;
- no stable-data overwrite;
- fallback instructions;
- ability for Avalonia stable and Tauri beta to coexist;
- safe behavior if the device is already busy.

Publish a prerelease and collect real feedback, but do not substitute beta feedback for deterministic evidence.

---

# 20. Wave 10 — frozen release candidate

## TASK-056 — full hardware RC

Freeze an exact RC commit and exact signed artifacts. No unrelated feature work after RC freeze.

Run one recorded matrix against those exact artifacts.

### Config

- open;
- edit;
- save;
- import;
- export.

### Storage

- install;
- exact readback;
- delete;
- order;
- preferences;
- protected default/prefs paths.

### Device conditions

- no device;
- one device;
- multiple devices;
- incorrect removable drive;
- stale device;
- unplug at relevant write stages.

### HID

- supported practical modes;
- connect;
- axes;
- buttons;
- disconnect;
- reconnect.

### Platforms

- Windows;
- macOS;
- supported Linux baseline.

### Services

- Google on supported OSes;
- updater;
- telemetry/privacy.

### Accessibility

- required assistive technologies.

Store sanitized RC SHA, artifact hash, OS, hardware/mode, result, and relevant logs.

---

# 21. TASK-057 — rollback rehearsal

Before stable cutover, prove rollback from the Tauri RC to the previous stable product.

On a machine upgraded to the RC:

1. create/use profiles;
2. create backups;
3. connect Google where supported;
4. install/update;
5. close Tauri;
6. return to previous stable Avalonia;
7. verify profiles remain usable;
8. verify backups remain usable;
9. verify known Google-token behavior;
10. verify the updater channel can be withdrawn.

There must be no irreversible data-format migration merely to use the Tauri app.

---

# 22. TASK-058 — stable cutover

Stable release is allowed only after Definition-of-Done sign-off.

Release order:

```text
freeze exact commit
↓
final CI
↓
build exact artifacts
↓
sign
↓
notarize macOS
↓
verify signatures
↓
verify artifact hashes
↓
install-smoke exact artifacts
↓
tag release
↓
publish stable release
↓
update stable updater manifest LAST
```

Never point the stable updater at artifacts before those exact artifacts have passed verification.

Keep the previous known-good stable release immediately available.

---

# 23. Final product acceptance

The Rust/Tauri QCM is finalized only when all applicable items below are satisfied.

## Core

- Rust matches required config behavior.
- No lossy raw-grid transformation.
- Typed Rust operations own edits.
- Revision/dirty/undo behavior is proven.

## Device/storage

- safe discovery;
- safe backup;
- temp write;
- flush/readback;
- verified replacement;
- truthful failure/restore classification;
- protected files;
- library/order/preferences;
- real hardware verification.

## Live/practice

- QuadStick-specific HID discovery;
- descriptor-driven parsing;
- axes/buttons;
- active visual state;
- reconnect;
- stale clearing;
- bounded resource use;
- documented XInput limitations.

## UI

- centered QuadStick visualizer;
- clear mode/profile context;
- understandable assignments;
- editor;
- library;
- settings;
- import/export;
- polished loading/empty/error states.

## Accessibility

The complete critical path works with:

- keyboard only;
- required screen readers;
- high contrast;
- zoom;
- RTL;
- reduced motion.

Every graphical visualizer control has a semantic equivalent.

## Services

- community profiles;
- Google backup/restore/share on supported OSes;
- secure token storage;
- privacy-consistent telemetry;
- crash rescue;
- feedback;
- signed updater.

## Platforms

- Windows release matrix passes;
- macOS release matrix passes;
- defined Linux baseline passes.

## Release

- signing complete;
- notarization complete where required;
- updater path proven;
- rollback proven;
- beta data isolation proven;
- previous stable retained.

---

# 24. TASK-059 — legacy retirement

Do not delete Avalonia immediately after Tauri launches.

Wait for at least one proven stable Tauri release cycle.

Then explicitly decide whether to retire legacy code.

If retired:

- use a dedicated PR;
- preserve useful C# oracle evidence;
- preserve fixtures;
- preserve migration history;
- preserve behavior evidence;
- do not alter the device/config file format as part of retirement.

Rollback must be repository-only.

---

# 25. TASK-060 — mobile feasibility

Mobile is not part of desktop rewrite completion.

After desktop is proven, test physical iPhone/iPad and Android devices with minimal native/Tauri-plugin spikes.

Determine actual support for:

- USB;
- HID;
- storage exposure;
- Bluetooth;
- required permissions;
- Tauri/plugin feasibility;
- PC bridge requirements.

Every capability receives one evidence state:

```text
PROVEN
UNSUPPORTED
BRIDGE_REQUIRED
UNKNOWN
```

Do not promise production mobile QuadStick transport before this evidence exists.

---

# 26. Canonical execution order

```text
FORMAT PARITY — DONE
TASK-001..019
        |
        v
CORE FOUNDATION
TASK-020 + TASK-021/022 + shell-only TASK-030
        |
        +---------------------------+
        |                           |
        v                           v
DEVICE CORE                     FRONTEND BOUNDARY
TASK-023 → TASK-024 → TASK-025  TASK-031 → TASK-032
TASK-029                        TASK-036 + TASK-037
        |                           |
        v                           |
HID TRACK                           |
TASK-026                            |
TASK-027A → TASK-027B               |
TASK-028                            |
        |                           |
        +-------------+-------------+
                      |
                      v
NATIVE INTEGRATION
TASK-033 → TASK-034 → TASK-035
                      |
                      v
CLICKABLE PRODUCT
TASK-038 + TASK-040A
                      |
                      v
PRIMARY UI PARITY
TASK-039 + TASK-040B + TASK-041 + TASK-042
                      |
                      v
SECONDARY FEATURES
TASK-043 + (TASK-044 → TASK-045) + TASK-046 + TASK-047 + TASK-048
                      |
                      v
FEATURE PARITY GATE
                      |
                      v
HARDENING
TASK-049 + TASK-050 + TASK-051 + TASK-052 + TASK-053 + TASK-054
                      |
                      v
SIDE-BY-SIDE BETA
TASK-055
                      |
                      v
FROZEN HARDWARE RC
TASK-056
                      |
                      v
ROLLBACK REHEARSAL
TASK-057
                      |
                      v
STABLE CUTOVER
TASK-058
                      |
                      v
PROVEN STABLE CYCLE
                      |
              +-------+-------+
              v               v
         TASK-059          TASK-060
         legacy            mobile
```

Parallelism is encouraged only when shared interfaces are frozen and file/module ownership is disjoint.

---

# 27. What must not happen

Do not:

- rewrite `main` during migration;
- merge the draft rewrite PR early;
- declare a task done because it compiles;
- stop implementation merely because one physical validation row is pending;
- pretend physical evidence exists because mocks passed;
- bypass qcm-core from React;
- expose raw paths to frontend;
- give WebView generic filesystem/network/shell privileges;
- create a second CSV serializer in TypeScript;
- add speculative serial functionality;
- allow cancellation during the unsafe install swap;
- report install success without exact verification;
- put secrets/tokens in IPC or logs;
- make live HID an unbounded event stream;
- require WebKitGTK merely for deterministic qcm-config parity;
- redesign architecture because a child agent prefers another library/pattern;
- delete Avalonia as part of the initial stable Tauri release.

---

# 28. Orchestrator decision rule

At every dispatch point ask, in order:

1. What is the next unmet dependency?
2. Can implementation proceed using deterministic fakes/fixtures?
3. What acceptance evidence can be produced now?
4. What validation debt must remain explicitly pending?
5. Can this task run without editing another active agent's files?
6. Will the consolidated full gate remain runnable afterward?
7. Is the last meaningful dependency wave remotely checkpointed and CI-verified?

If yes, dispatch.

If no, wait, narrow the task, or restructure ownership.

The objective is not maximum agent count.

The objective is **maximum safe parallel progress while preserving one continuously verifiable product**.

---

# 29. Final release rule

A green agent report is not release evidence.

A green local test is not release evidence.

A green GitHub build is not hardware evidence.

A hardware test is not accessibility evidence.

A polished UI is not behavioral parity.

The product ships only when all applicable automated, physical, credential, human, security, and release dimensions are independently satisfied on the exact release candidate.
