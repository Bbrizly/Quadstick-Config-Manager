# Testing strategy

## Test pyramid with an oracle at the bottom

### Layer 0 — Existing C# characterization/oracle

Keep `QuadStick.Format.Tests` and `QuadStick.App.Tests` green. Do not “fix” old tests to make Rust look compatible.

### Layer 1 — `qcm-config` unit/property/fuzz

Pure and exhaustive: CSV, parser, validator, normalization, edit ops, filename rules, preferences, LED tables.

### Layer 2 — cross-language differential parity

Same fixtures/ops through C# oracle and Rust; compare canonical JSON/bytes.

### Layer 3 — `qcm-core` with fakes

Fake storage/HID/cloud/secure store/clock/update. Fault injection at every transaction stage.

### Layer 4 — native adapter integration

Temp filesystems/mount simulations, real OS keychain test identities, HID fixture devices where possible, HTTP mock server.

### Layer 5 — React component/feature

`MockQcmClient` drives every state; React Testing Library queries by role/name, not implementation classes.

### Layer 6 — browser-mode app E2E

Run full React app against deterministic mock client for navigation/editor/error/a11y workflows. Playwright is acceptable here because native Tauri is mocked.

### Layer 7 — packaged Tauri smoke

OS-specific launch/open/save/device picker/basic IPC/update-signature tests. Use current Tauri-supported automation options; do not force browser Playwright onto native WebViews if brittle.

### Layer 8 — physical hardware

Real QuadStick matrix is mandatory before beta for install/delete/library/live HID/device settings.

## Critical fault injection

Fake storage transaction must fail independently at:
- marker validation;
- backup mkdir/copy/flush;
- temp create/write/flush;
- temp reopen/read/compare;
- target replace pre-change;
- target replace post-displacement;
- restore copy/write/rename;
- cleanup.

Assert exact reported data-integrity state.

## Test naming

Use behavior IDs in names/comments where useful: `B017_install_verifies_temp_before_replace`.

## Flake policy

No arbitrary sleeps in unit/component tests. Inject clock/backoff. Hardware tests may wait on physical enumeration but use bounded condition polling and capture diagnostics on timeout.

## Accessibility

axe/component tests are automated gate; manual AT matrix has versioned checklist. Both required.

## Security

Negative IPC tests call commands with forged/stale IDs, traversal names, oversized arrays, invalid confirmation IDs and disallowed state. Test command layer as if WebView were compromised.