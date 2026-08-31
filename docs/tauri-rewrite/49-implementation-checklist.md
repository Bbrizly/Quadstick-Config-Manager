# Master implementation checklist

Work top-to-bottom. A checked box means its implementation acceptance criteria and linked automated evidence are complete; it does **not** imply that separately tracked physical, credential, human, or release validation has passed.

**Execution ordering:** use `52-final-product-execution-spec.md`. Pending physical, credential, human, or release validation does not by itself make implementation blocked. Track those dimensions separately as defined there.

## Phase 0 — freeze current reality

- [x] TASK-001 Complete source/test/tool inventory at the implementation-base SHA.
- [x] TASK-002 Build fixture manifest and copy safe existing fixtures.
- [x] TASK-003 Build deterministic C# oracle exporter.
- [x] TASK-004 Define canonical parity JSON schema and hash policy.
- [x] TASK-005 Record current performance/resource baseline.
- [x] TASK-006 Finish behavior/porting/test ledgers, including agent pipeline and serial evidence.

**Gate 0:** no runtime-critical source is UNASSESSED; fixture/oracle output is reproducible.

## Phase 1/2 — pure Rust config parity

- [x] TASK-007 Create Rust workspace and `qcm-config` crate only.
- [x] TASK-008 Port CSV raw-grid parse/write.
- [x] TASK-009 Port model and issue types.
- [x] TASK-010 Port vocab/embedded validation/template data.
- [x] TASK-011 Port header/sheet parser.
- [x] TASK-012 Port binding/preferences/IR parsing.
- [x] TASK-013 Port validator and firmware limits.
- [x] TASK-014 Port device-safe serializer/normalization.
- [x] TASK-015 Port profile editor operations.
- [x] TASK-016 Port undo/dirty/revision/action-name semantics.
- [x] TASK-017 Add C#↔Rust differential suite.
- [x] TASK-018 Add property/fuzz/size-limit suite.
- [x] TASK-019 Reimplement qsf against Rust core and compare JSON contract.

**Gate 2:** config parser/serializer/mutations pass oracle; no lossy raw-grid behavior.

## Phase 3 — core and hardware adapters

- [x] TASK-020 Create `qcm-core` profile session manager.
- [x] TASK-021 Implement stable error/confirmation/operation model.
- [x] TASK-022 Define device-storage ports and `qcm-testkit` fake storage.
- [x] TASK-023 Implement mounted QuadStick discovery + opaque IDs.
- [x] TASK-024 Port safe install transaction with fault injection.
- [x] TASK-025 Port device library/delete/order/preferences operations.
- [ ] TASK-026 Run HID library/report-descriptor spike on real hardware.
- [x] TASK-027 Implement descriptor-driven HID adapter/worker. Physical backend validation remains pending under TASK-026/RC hardware rows.
- [x] TASK-028 Implement live-input manager/bounded stream.
- [x] TASK-029 Resolve serial production-use question; implement nothing if E/not required.

**Gate 3:** core automated implementation is available without UI; physical QuadStick install/readback + live input remain separately tracked release evidence.

## Phase 4/5 — shell and frontend foundation

- [x] TASK-030 Scaffold Tauri 2 + React + Vite using pinned toolchains.
- [x] TASK-031 Define `QcmClient`, DTOs and `MockQcmClient`.
- [x] TASK-032 Implement profile/settings Tauri commands.
- [x] TASK-033 Implement device/storage Tauri commands and confirmation plans.
- [x] TASK-034 Implement low-rate invalidation + live/progress Channels.
- [x] TASK-035 Lock capabilities/CSP and add negative permission tests.
- [x] TASK-036 Build accessible app shell/design tokens/settings foundation.
- [ ] TASK-037 Migrate all localization catalogs + pseudo-loc/RTL pipeline.

**Gate 5:** browser/mock and native shell use the typed QcmClient boundary with no generic native capability. TASK-037 closes the remaining frontend-foundation localization row.

## Phase 6 — product parity slices

- [ ] TASK-038 Build editor modes/bindings/issues/raw-grid UI.
- [ ] TASK-039 Build centered accessible QuadStick visualizer + semantic mirror.
- [ ] TASK-040 Complete local open/save/import/export/XLSX review.
- [ ] TASK-041 Build install + device library UI.
- [ ] TASK-042 Build device settings/preferences/device-band parity.
- [ ] TASK-043 Port community catalog/profile workflow.
- [ ] TASK-044 Implement Google auth + secure token adapters.
- [ ] TASK-045 Port Google backup/restore/share/conflict policy.
- [ ] TASK-046 Port privacy/telemetry/crash rescue/feedback diagnostics.
- [ ] TASK-047 Implement signed update check/install flow.
- [ ] TASK-048 Port agent workflow/corpus/eval integration around typed EditorOps.

**Gate 6:** every required feature has automated evidence; no required red feature-parity row.

## Phase 7/8 — hardening

- [ ] TASK-049 Accessibility/manual AT + localization hardening.
- [ ] TASK-050 Windows packaging/signing/WebView/HID/storage release matrix.
- [ ] TASK-051 macOS packaging/notarization/Keychain/HID/storage release matrix.
- [ ] TASK-052 Linux packaging/WebKitGTK/HID/storage support matrix.
- [ ] TASK-053 Reliability/performance soak and leak investigation.
- [ ] TASK-054 Security/privacy/capability adversarial review.

## Phase 9/10 — beta and cutover

- [ ] TASK-055 Create side-by-side beta identity/update channel/data migration rehearsal.
- [ ] TASK-056 Run full real-hardware release-candidate matrix.
- [ ] TASK-057 Run rollback rehearsal from Tauri beta/stable to previous known-good release.
- [ ] TASK-058 Stable cutover only after DoD sign-off.

## Phase 11/12 — after proven stable

- [ ] TASK-059 Decide/execute legacy .NET retirement while retaining oracle history/fixtures.
- [ ] TASK-060 Run mobile physical-hardware feasibility before any iOS/Android production transport.

## Mandatory PR footer during migration

Every migration PR description includes:

```text
TASKS: TASK-xxx
BEHAVIORS: B-xxx
PORTING: paths/symbols moved or replaced
TESTS: automated + hardware/manual
ACCESSIBILITY: impact/checks
SECURITY/PRIVACY: impact/checks
ROLLBACK: exact revert/fallback
OPEN QUESTIONS: IDs or none
GATE IMPACT: none / advances Gate N
```
