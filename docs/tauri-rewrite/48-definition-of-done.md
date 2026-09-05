# Definition of done

The rewrite is **not done when the Tauri window looks good**.

## Format/data

- [ ] all required behavior ledger config rows resolved;
- [ ] Rust parser/validator/serializer differential parity passes;
- [ ] raw K/L/M+ content preservation proven;
- [ ] firmware oracle/device agreement parity proven;
- [ ] property/fuzz suite passes budgets;
- [ ] qsf machine contract has Rust-equivalent implementation or retained supported legacy binary.

## Local editing

- [ ] new/open/import/save/save-as/export/undo/modes/bindings/preferences work;
- [ ] dirty/revision/conflict semantics tested;
- [ ] no JS path can serialize device CSV independently;
- [ ] local crash rescue can recover unsaved canonical session.

## Device/storage

- [ ] storage discovery on Windows/macOS/Linux supported baseline;
- [ ] no/one/multiple/stale candidate flows;
- [ ] install protected default/prefs confirmations;
- [ ] backup/temp/readback/replace/restore fault matrix;
- [ ] delete/library/order parity;
- [ ] unplug at every transaction stage tested;
- [ ] real QuadStick write/readback verified on supported desktop OSes.

## Live input

- [ ] current supported VID/PID modes enumerate;
- [ ] descriptor-driven axis/buttons match C#;
- [ ] disconnect/backoff/staleness clears UI;
- [ ] no unbounded queue/resource leak;
- [ ] XInput limitation explicitly represented or intentionally improved/tested.

## Feature parity

- [ ] import/XLSX skipped-tab behavior;
- [ ] community profiles;
- [ ] device settings/device band semantics;
- [ ] Google backup/share/conflict/reconnect (on currently supported OSes);
- [ ] settings/theme/scale/reduced motion;
- [ ] all current production locales incl. pseudo-loc pipeline;
- [ ] tutorial;
- [ ] agent workflow/corpus/eval contract accounted for;
- [ ] crash report/feedback/privacy behavior;
- [ ] update checks.

## Security/privacy

- [ ] WebView has no generic filesystem/shell/http/secret capability;
- [ ] every Tauri command in API ledger;
- [ ] malicious/stale IPC negative tests;
- [ ] CSP/remote navigation audited;
- [ ] OAuth secrets/tokens never logs/IPC;
- [ ] source/dev/CI builds send zero analytics;
- [ ] current `PRIVACY.md` promises still true or policy explicitly revised before release;
- [ ] dependency/security audit has no unaccepted critical finding.

## Accessibility

- [ ] keyboard-only critical path;
- [ ] semantic nonvisual visualizer equivalent;
- [ ] axe green for critical surfaces;
- [ ] pseudo-loc/RTL/zoom/reduced-motion tested;
- [ ] NVDA + Narrator RC pass;
- [ ] VoiceOver RC pass;
- [ ] high-contrast/contrast-theme pass;
- [ ] no P0/P1 accessibility issue.

## Packaging/release

- [ ] Windows signed installer + smoke;
- [ ] macOS arm64/x64 signed/notarized + smoke;
- [ ] Linux supported package + smoke;
- [ ] updater signature/rollback rehearsal;
- [ ] release secrets isolated/no secret artifact leak;
- [ ] beta side-by-side data isolation verified;
- [ ] previous stable available.

## Ledgers

- [ ] PORTING_LEDGER has no required entry below PARITY-TESTED; hardware items HARDWARE-VERIFIED;
- [ ] BEHAVIOR_LEDGER all A rows pass;
- [ ] API_LEDGER matches registered commands/events/channels;
- [ ] TEST_LEDGER maps every critical behavior/risk to evidence;
- [ ] DECISION_LEDGER has no blocking undecided ADR.

## Retirement

Deleting Avalonia is a **separate Phase 11 decision** after a stable Tauri release. Rewrite DoD can be achieved while old source remains for rollback/oracle evidence.