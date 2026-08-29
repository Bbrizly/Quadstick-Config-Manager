# Executive summary

## Decision

Use **Tauri 2.11.x (pin exact patch at implementation start), React 19.2, TypeScript, Vite and stable Rust**. Keep a small workspace: frontend at `/src`, native shell/adapters at `/src-tauri`, pure crates at `/crates/qcm-config`, `/crates/qcm-core`, and `/crates/qcm-testkit`.

## Why rewrite

The current app has strong domain logic and tests but substantial presentation/orchestration coupling. The rewrite should not replace correct domain knowledge with new guesses; it should **extract and prove it**, then replace the shell incrementally.

## What we keep

- Firmware-compatible CSV behavior and tolerance.
- Raw-grid preservation, notes/action-name extension columns, modes/preferences/IR semantics.
- Validation vocab/data/templates and firmware-oracle evidence.
- Mounted-device recognition and destructive-operation confirmations.
- Backup/temp/read-back/replace/restore safety model.
- Device file library ordering/protection rules.
- Live HID practice input behavior.
- Import/review, community profiles, device settings, modes, custom names.
- Google Drive backup/share semantics and secure refresh-token storage policy.
- Crash rescue, opt-in usage/crash telemetry policy, update behavior, localization and accessibility requirements.
- `qsf`'s agent-safe, explicit operation model (reimplemented atop Rust core, not abandoned).

## What changes architecturally

- `MainWindow` ceases to be the application service container.
- Rust holds canonical profile sessions and config mutations.
- React gets immutable editor snapshots plus explicit operation results.
- Native adapters own filesystem/HID/secure storage/network/platform work.
- The frontend only sees a domain-level `QcmClient`.
- Tauri Channels carry live-input frames; commands carry request/response; events only invalidate low-frequency state.

## Rewrite principle

**Parity before improvement.** Every behavior is classified A required, B intentional improvement, C bug fix, D unresolved, or E retire. B/C/E changes require separate evidence and tests.

## Biggest risks

1. Lossy CSV model or changed normalization corrupts meaning.
2. Device unplug/write races regress current recovery guarantees.
3. HID parsing is reduced to fixed byte offsets and fails across emulation modes.
4. React duplicates Rust domain state and creates two truths.
5. Tauri permissions expose generic filesystem/shell capabilities.
6. Localization/accessibility regress during visual redesign.
7. Rewrite scope swallows product work before parity is usable.

## Recommended cutover

Keep Avalonia shipping while Rust core is built and oracle-tested. Build Tauri beta side-by-side with a distinct bundle/channel. Retire legacy only after parity matrix, hardware tests, AT tests, packaged smoke tests and rollback rehearsal pass.