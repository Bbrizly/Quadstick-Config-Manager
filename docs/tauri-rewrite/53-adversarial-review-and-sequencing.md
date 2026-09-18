# Adversarial review findings and execution sequencing

This file records issues found by reviewing the specification **as if implementation were about to begin**. When this file clarifies ambiguous sequencing, it is authoritative over shorthand task ordering.

## Finding AR-001 — Real native adapters were scheduled before the Tauri/native host existed

`TASK-023` and `TASK-027` describe real OS storage/HID adapters, while `TASK-030` scaffolds the Tauri host. The architecture intentionally keeps pure core work before the UI shell, but a coding agent still needs a concrete place to compile native adapters.

### Resolution

Execute Phase 3 in two layers:

```text
TASK-020/021/022
  qcm-core + ports + qcm-testkit fakes
        ↓
TASK-024/025
  transaction/library policy against fakes
        ↓
TASK-026
  standalone HID hardware spike (throwaway/diagnostic Rust binary is allowed)
        ↓
TASK-030
  scaffold minimal src-tauri host + React shell
        ↓
TASK-023B
  real storage adapter inside src-tauri/adapters/storage
        ↓
TASK-027B/028
  real HID adapter + LiveInputManager integration
        ↓
Gate 3 physical hardware verification
```

The **design/rules** portion of TASK-023 may be completed before TASK-030 using `DeviceStoragePort` + fake probes. The real Windows/macOS/Linux implementation is `TASK-023B` immediately after TASK-030.

Similarly, TASK-026 may use a standalone Rust diagnostic executable before Tauri. The production `src-tauri/adapters/hid` implementation is completed after TASK-030.

Do not create an uncompiled `src-tauri` directory full of placeholder Rust solely to satisfy task numbering.

## Finding AR-002 — Dependency task number typo

The dependency plan formerly said “scaffold TASK-010.” Correct meaning:
- **TASK-007** pins pure Rust workspace/toolchain/dependencies;
- **TASK-030** pins Tauri/React/Vite/Node/pnpm dependencies.

`39-dependency-plan.md` has been corrected.

## Finding AR-003 — Current docs are not uniformly authoritative

README references architecture/device documents that are absent at the audited HEAD. `docs/FORMAT.md`, source and tests are stronger evidence. Keep evidence hierarchy from `06-behavioral-compatibility-contract.md`.

## Finding AR-004 — `agent/` is a first-class migration surface

The repo contains a substantial Python agent corpus/cache/charts/eval/finalize/predict pipeline, not only the Avalonia `AgentWindow` and qsf tool. Gate 0 must inventory every tracked agent file and classify:
- runtime product dependency;
- developer/eval tooling;
- generated cache/output;
- licensed corpus/input;
- safe to retain/retire.

Do not rewrite the agent pipeline just because the desktop shell changes. First point it at Rust qsf after parity and compare eval outputs.

## Finding AR-005 — Privacy is executable behavior

`PRIVACY.md` states concrete network behavior: no accounts/ads; Community fetch only on explicit Community action/refresh; profiles/paths never sent; analytics off until opt-in; crash report local first and sends only when user presses Send; source builds send nothing. These are target integration tests, not prose aspirations.

## Finding AR-006 — Physical device identity aggregation remains unsafe to assume

Storage and HID should remain separately identified until OQ-002 proves a stable correlation. Two-device test is required before a combined physical-unit UI can claim “this live input belongs to this mounted drive.”

## Final execution rule

When task number order conflicts with a prerequisite proven by architecture, **prerequisites win**. Update `49-implementation-checklist.md` and `50-first-implementation-tasks.md` in the same implementation PR when the task is first executed so future agents see the corrected sequence directly.