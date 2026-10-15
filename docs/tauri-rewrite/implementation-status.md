# Rewrite implementation status

Integration branch: `rewrite/tauri-rust`

This file is the current execution checkpoint. `50-first-implementation-tasks.md` remains the requirement source. Git history and GitHub Actions remain the detailed evidence trail.

## Status vocabulary

Tasks are tracked on independent axes so hardware or credential debt is not confused with missing implementation:

- **Implementation** — code/spec work exists.
- **Automated verification** — exact-head tests/gates have passed.
- **Physical validation** — real QuadStick/OS validation where required.
- **Human/credential/release validation** — tracked separately when a later task requires it.

## Current task state

| Task | Implementation | Automated verification | Physical validation | Evidence |
|---|---|---|---|---|
| TASK-001–019 parity foundation | **DONE** | **PASS** | N/A | frozen C# oracle, qcm-config differential/property/fuzz suite, Rust qsf |
| TASK-020 profile session manager | **DONE** | **PASS** | N/A | `crates/qcm-core/src/profiles/**` |
| TASK-021 errors/ops/confirmations | **DONE** | **PASS** | N/A | `qcm-core/src/{error,operation,confirmation,clock}.rs` |
| TASK-022 storage port + fake | **DONE** | **PASS** | N/A | `qcm-core/src/ports/storage.rs`, `qcm-testkit` |
| TASK-023 discovery | **DONE** | **PASS** | **PENDING** real-device rows | discovery + storage adapters |
| TASK-024 safe install | **DONE** | **PASS** | **PENDING** sacrificial/unplug rows | stage-fault matrix in `qcm-core`/`qcm-testkit` |
| TASK-025 device library/delete/order/preferences | **DONE** | **PASS** | **PENDING** final real-device pass | device library operations/tests |
| TASK-026 Rust HID backend spike | **PENDING PHYSICAL SPIKE** | N/A | **PENDING** Windows/macOS/Linux + modes | task is intrinsically physical |
| TASK-027 descriptor-driven HID adapter | **DONE** | **PASS fixture/CI** | **PENDING** actual QuadStick/backend validation | `src-tauri/src/adapters/hid.rs` |
| TASK-028 LiveInputManager | **DONE** | **PASS** | **PENDING** final live-device pass | `qcm-core/src/live/**` |
| TASK-029 serial scope | **DONE** (deferral reversed: main `f3525fe` shipped the console) | **PASS** | **PENDING** SPP + 3.3V cable on a stick | `qcm-core/src/console.rs`, `src-tauri/src/console_shell.rs`, `DeviceLinkDialog.tsx` |
| TASK-030 Tauri + React scaffold | **DONE** | **PASS** | N/A | `src-tauri/`, React/Vite root |
| TASK-031 QcmClient + mock | **DONE** | **PASS** | N/A | `src/platform/**` |
| TASK-032 profile/settings commands | **DONE** | **PASS** | N/A | Tauri command/IPC shell |
| TASK-033 device commands + confirmation plans | **DONE** | **PASS** | covered later by device matrix | opaque device/plan/confirmation API |
| TASK-034 bounded channels + invalidation | **DONE** | **PASS** | live-device close/reconnect later | typed Channels + invalidation |
| TASK-035 capabilities + CSP | **DONE** | **PASS** | N/A | exact-head run `33338800870` |
| TASK-036 accessible app shell/design tokens | **DONE** | **PASS** | final manual AT later | exact-head run `33338801740` |
| TASK-037 localization migration | **DONE** | **PASS** — exact-head run `33347003405` | final manual locale/AT hardening later | deterministic shipping RESX → React catalogs, pseudo, RTL, error localization |
| TASK-038 editor parity UI | **DONE** | **PENDING final exact-head parity gate** | final AT pass later | finalizer `33354437900`; canonical read-before-undo head `5835843`; typed whole-mode ops; modes/bindings/issues/raw-grid UI; mock E2E |

The TASK-035/TASK-036 integration was also exercised by draft PR #5's synthetic merge run `33339138829`, which passed Gate 0, Rust parity, frontend checks and fuzz smoke against then-current `main`.

## HID state

The known QuadStick HID contract is encoded in Rust: shipped HID identities are filtered, application collections are descriptor-selected, X/Y and buttons are usage-driven, report IDs/missing axes are handled, reads have bounded cancellation latency, and native paths remain redacted. Native Xbox/XInput-only detection remains a platform-specific physical limitation rather than something faked through HID.

TASK-026 and the physical row of TASK-027 remain validation debt. A backend limitation found with real hardware may replace the native adapter without changing `LiveInputPort`, `LiveInputManager`, Channels, or the UI contract.

## WebView/device boundary

Device discovery/library/install/delete/open-preferences use opaque IDs/generations and process-local one-shot plans. Device writes are still safe core transactions. Live input uses a capacity-one latest-wins stream. The main capability has an empty permission set; generic Tauri filesystem/shell/http/process/store/updater capability is not exposed to React.

## Localization boundary

TASK-037 uses `src/QuadStick.App/Strings*.resx` as the shipping translation source of truth. The React catalogs are generated deterministically; absent satellite entries fall back to English, placeholders must remain compatible, pseudo-loc is generated from English, Arabic sets `dir=rtl`, and firmware/file tokens are not translated by config serialization. Stable Rust error codes are mapped to localized frontend messages instead of treating Rust fallback English as presentation authority.

The normal rewrite gate regenerates catalogs and fails if committed generated output is stale.

## Editor boundary

TASK-038 does not make React a profile engine. The UI renders `EditorSnapshot`, sends typed `EditorOp` values with the revision it saw, and replaces its projection only with the returned Rust snapshot. Whole-mode duplicate/delete/reorder call the already-ported whole-sheet Rust semantics. A revision conflict uses the narrow read-only `get_profile_snapshot` domain command to re-fetch canonical state instead of retrying or reconstructing state in JavaScript. Undo also re-reads the canonical Rust session revision immediately before applying the inverse so a keyboard shortcut cannot race a pending React render. Raw-grid editing is an intentional advanced projection of the same session and still travels through `set_cell`.

## Main sync (2026-10-15)

`main` is merged up to `f3525fe`. Avalonia code comes from `main`; rewrite code from this branch. User-facing changes from `main` ported to React/Rust:

| Main commit | Rewrite state |
|---|---|
| `f7e15b8`, `033c274` combo in one box, one-step undo | **DONE**: `comboGroups`, combo card, Rust `CopyInputs`/`DeleteRows`/`AddComboOutput`/`Group` ops under `ProfileFile::one_step` |
| `3ad22ce` combo input not a duplicate | **N/A**: the rewrite has no duplicate-input mark yet |
| `610b93f` mapping panel room | **DONE**: 340 px track floor, stacks under 720 px |
| `8d35604` narrow to the socket picked | **DONE**: `parts.ts`, narrowed note, Show all |
| `ddbf8b5` cog becomes Back | **DONE**: Settings has no Done button |
| `94a77ec` drive label on Mac/Linux | **ALREADY**: `label_of` uses the mount folder name |
| `a7c492f` upper-case profile names | **ALREADY**: `DeviceFileName::is_profile` is case-insensitive, covered by `device_library.rs` |
| `a9f09a3` flush before the unplug wait | **DONE**: file + directory `sync_all` after rename, restore, delete (errors ignored, as on main) |
| `9f151ee` console adapter modes | **DONE**: help on `enable_DS3_emulation` |
| `1950d85`, `1cc0cd8`, `2b0552c`, `88eda16` wording | catalogs regenerated (the generator now reads `QuadStick.Format` RESX too); the rewrite has no Sheets-link import, function-number hints or output picker to show them yet |

## Phase 2 (2026-10-15)

| Area | Rewrite state |
|---|---|
| Console protocol (`DeviceConsole.cs`) | **DONE** `qcm-core::console` over any `Read + Write`; fake + all 12 C# tests + half-line junk, boot, new-file, stopped sweep (`device_console.rs`) |
| Serial port | **DONE** `serialport 4.10.1` (no libudev) in `console_shell.rs`, 115200 baud, opaque per-list port ids, macOS `tty.*` twin hidden; opened by Connect, dropped on close, failed connect and the strength-test restart |
| DeviceLink window | **DONE** `DeviceLinkDialog.tsx` from the device page: ports, Connect, Send (backup, read-back, load, default/prefs questions), Measure (USB reading first, maxima 100, calibrate, 5..95 sweep, Stop, always delete + boot); `DeviceLinkWindowTests` ported |
| Telemetry (`Telemetry.cs`) | **DONE** `src-tauri/src/telemetry.rs`: PostHog `/batch/`, same events/allowlist/consent v2/kill switch/build-time token, durable install id, `game`, `setup_seconds`, `strength_measured`, feedback flush, crash send from a `chain` |
| Registry + `qcm://profile/<id>` | **DONE** `src-tauri/src/registry.rs` + App launch-link open; **BLOCKED** scheme registration/macOS delivery/second-launch forwarding: `tauri-plugin-deep-link` is not a dependency |

Still open from this phase: the first-run usage notice dialog (consent today comes only from the Settings toggle, which records notice 2); `install_failed` `cancelled_*` reasons; showing the install id in Settings; PRIVACY.md wording for the Rust `$lib`/User-Agent; a chain in the Rust panic report so a crash can be sent.

## Runtime cost while a game runs

- Console: nothing runs while the dialog is closed or idle. Ports are listed on open and on Refresh only. Live input is subscribed, and the 30 ms breath wait runs, only during a measurement.
- Telemetry: off is a flag read. On and idle is one thread parked on a channel (no timer); an event opens a 5 s batch window.

- Live input subscribes only while the window is visible and focused (DOM `visibilitychange`/`focus`/`blur`; Tauri window events would need an event permission the capability does not grant). Frames are coalesced to one React update per animation frame (`src/features/live/useLiveInput.ts`).
- The native worker (`src-tauri/src/streaming.rs`) has no 4 ms or 50 ms polling. Without a device it sleeps on a condvar until the 1.5 s rescan (floor 250 ms); the last unsubscribe wakes and stops it, so nothing enumerates HID without a subscriber. A quiet open stick wakes twice a second (500 ms read timeout). A new subscriber gets the current state at once.
- The mode-light glow animates `scale` instead of `filter`, so it no longer repaints the photo (not profiled), and stops with the pips when the window is inactive.
- Only the active catalog loads (English stays bundled). Measured with `pnpm build:web`: main JS chunk 2311 kB to 455 kB (gzip 683 to 137). No production source maps. Device photos recompressed losslessly (5.07 to 4.71 MB). `dist-ui` 9.2 MB to 7.6 MB.

Not measured: real wakeups/CPU with a QuadStick plugged in and a game running. That needs the physical pass.

## Remaining validation debt

- real QuadStick discovery/install/unplug/HID/reconnect rows;
- TASK-026 physical HID backend matrix;
- final VoiceOver/NVDA/Narrator/zoom/high-contrast/RTL manual matrix;
- code-signing/notarization/update credentials and platform release artifacts;
- exact release-candidate physical and rollback matrices.

These are not blanket blockers for implementing later product slices.

## Next implementation boundary

Run the full exact-head parity gate for the finalized TASK-038 branch and promote only if it passes. TASK-040A local CSV open/edit/undo/save/reopen follows from that verified integration head.
