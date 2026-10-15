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
| `3ad22ce` combo input not a duplicate | **DONE**: `duplicates.ts`, the combo's own rows are subtracted |
| `610b93f` mapping panel room | **DONE**: 340 px track floor, stacks under 720 px |
| `8d35604` narrow to the socket picked | **DONE**: `parts.ts`, narrowed note, Show all |
| `ddbf8b5` cog becomes Back | **DONE**: Settings has no Done button |
| `94a77ec` drive label on Mac/Linux | **ALREADY**: `label_of` uses the mount folder name |
| `a7c492f` upper-case profile names | **ALREADY**: `DeviceFileName::is_profile` is case-insensitive, covered by `device_library.rs` |
| `a9f09a3` flush before the unplug wait | **DONE**: file + directory `sync_all` after rename, restore, delete (errors ignored, as on main) |
| `9f151ee` console adapter modes | **DONE**: help on `enable_DS3_emulation` |
| `1950d85`, `1cc0cd8`, `2b0552c`, `88eda16` wording | **DONE**: copy note after a community import; pad name and function-number sentences in the mapping inspector (`functionHelp.ts`) |

## Phase 2 (2026-10-15)

| Area | Rewrite state |
|---|---|
| Console protocol (`DeviceConsole.cs`) | **DONE** `qcm-core::console` over any `Read + Write`; fake + all 12 C# tests + half-line junk, boot, new-file, stopped sweep (`device_console.rs`) |
| Serial port | **DONE** `serialport 4.10.1` (no libudev) in `console_shell.rs`, 115200 baud, opaque per-list port ids, macOS `tty.*` twin hidden; opened by Connect, dropped on close, failed connect and the strength-test restart |
| DeviceLink window | **DONE** `DeviceLinkDialog.tsx` from the device page: ports, Connect, Send (backup, read-back, load, default/prefs questions), Measure (USB reading first, maxima 100, calibrate, 5..95 sweep, Stop, always delete + boot); `DeviceLinkWindowTests` ported |
| Telemetry (`Telemetry.cs`) | **DONE** `src-tauri/src/telemetry.rs`: PostHog `/batch/`, same events/allowlist/consent v2/kill switch/build-time token, durable install id, `game`, `setup_seconds`, `strength_measured`, feedback flush, crash send from a `chain` |
| Registry + `qcm://profile/<id>` | **DONE** `src-tauri/src/registry.rs` + App launch-link open |

## Phase 3 (2026-10-15)

| Area | Rewrite state |
|---|---|
| Usage notice (notice 2) | **DONE** `UsageNotice.tsx`: before the crash prompt and tour, No is default, skipped under `QSCM_TELEMETRY=0`, failed write is a no; fixed-URL privacy link |
| Install id, cancel reasons, PRIVACY.md | **DONE** id + copy in Settings > Advanced; `cancelled_device/folder/default/preferences` from the install dialog; PRIVACY.md describes the Tauri sender (`$lib`, fixed User-Agent, OS + arch) |
| Panic report | **DONE** `CrashPayload` shape with a chain of backtrace function names, no message/file/line; read-back re-filtered |
| Deep links | **DONE** `tauri-plugin-deep-link 2.4.10` + `tauri-plugin-single-instance 2.4.5` (last releases on tauri 2.11): bundle scheme, runtime registration on Windows/Linux, macOS Apple events and second launches into one `LinkInbox`, pushed to the window on a channel |
| Share setup | **DONE** `ShareSetupDialog.tsx` (connect, save, then the action) |
| Controller bridge / Mac gamepad | **DONE** `controller.rs` + `ControllerRow.tsx` on the device page; bridge child process, Mac SDL LaunchAgent switch or copy commands in the store build |
| Drive picker, Linux token | **DONE** `DrivePickerDialog.tsx` (one backup into the review); Linux keeps the Google token for the run (`InMemoryTokenStore` parity) |
| Duplicate mark | **DONE** `duplicates.ts`, incl. the 3ad22ce combo rule |
| Wording homes | **DONE** see the main-sync table |

Gates at `6df8f9b` (local, macOS): `pnpm check` (143 tests), `cargo test --workspace` (451), clippy `-D warnings`, fmt, catalog regeneration clean, `pnpm tauri build --debug --no-bundle`.

Still open (closed in phase 4: `qs-bridge` bundling config, Drive restore on Home and after first connect, bulk restore): Linux builds of the new cfg paths are unverified here (CI); real-OS link click-through, bridge with a stick in mode 2, SDL game on a Mac.

## Phase 4 (2026-10-15)

Local `follow-link` (10 commits on main) is merged; its C# side is Avalonia and kept as is.

| Area | Rewrite state |
|---|---|
| Pasted Sheets link (Home) | **DONE** `import_sheets_link`: workbook export, CSV fallback (said in the review), `SheetsUrl` port, same refusals as Import. Before this the Home link box opened the workbook file picker |
| Follow / Get the latest / Forget | **DONE** `follow.rs`, `follow_shell.rs`, `FollowLinkActions.tsx`; `profile_changes.rs` (mappings and other cells); rescue copy never overwritten; reopen as a new session |
| Console close (6934bbf) | **DONE** a close mid-run stops, puts the stick back, then closes |
| Drive restore | **DONE** Home Drive button (arm, sign in, offer restore), offer after a Settings sign-in, bulk `restore_drive_backups` into Documents/QuadStick Profiles with Imported/Skipped/Failed |
| qs-bridge on Linux | **CONFIG DONE, NEEDS CI** `tauri.linux.conf.json` externalBin + udev rule in deb/rpm, `scripts/stage-qs-bridge.sh`, CI job `linux-bundle` |
| Host build pieces (Drive-only gate, token store name) | N/A: the rewrite has no host executable |

Gates (local, macOS): `pnpm check` (159 tests), `cargo test --workspace` (471), clippy `-D warnings`, fmt, catalog regeneration clean, `pnpm tauri build --debug --no-bundle`.

Still open: the Linux bundle job has not run; Get the latest is editor-only (no Home cards); the app's own Drive token is not used for its own sheets; real Drive/Sheets runs on each OS. (UNRESOLVED-FL1 closed in phase 5.)

## Phase 5 (2026-10-15): look the same

Method: `RenderPreview --compare` draws the Avalonia screens headlessly at 1280x860 with the mock's two-mode Racing profile; the React app runs on the mock in headless Chrome at the same size. Pairs compared by eye and by sampled pixel colours.

| Screen | Fixed |
|---|---|
| Device view | FPS photo was never drawn (crop scaled 10000%); stage now laid out at 910 px and scaled like the Viewbox, floor 0.6 then sideways scroll; frameless stage on the surface; tables shorter |
| Home | column centred and as wide as its widest line; tiles keep 154x104 (an app-wide reset was zeroing them, also the cards); quiet buttons keep the hairline; Fluent field fill/edge; version footer |
| Settings | Fluent tabs (24 px plain, accent bar), panel full width inset 12 px, per-tab heading, 20 px checkboxes, Back control no longer tinted |
| Editor | undo, Add row, save and spreadsheet switch in the shell bar; one-line modes with controls on the picked one only; round Add mode; solid accent view key; sidebar and panel fills; no nav tab lit while editing |
| Dialogs | Avalonia title strip with accent mark; disabled buttons a flat grey plate (a disabled primary looked live) |

Still different: Home has no profile cards (no library list in the rewrite); validation stays a panel, not the status bar; Rows view keeps the mapping panel; the rewrite's raw grid has no Avalonia twin; device settings and DeviceLink are not compared (mock has no prefs.csv or console); Avalonia headless draws Arabic with missing glyphs, so RTL was checked on the rewrite only.

Motion (ADR-016): hover eases in 100 ms, press lands at once and not on disabled buttons, entry animations 140 ms, all off under reduced motion.

Idle (production build, headless Chrome, focus emulated, 5 s settle then a 5 s trace): Home, editor Rows and device view each 0 DrawFrame, 0 animation-frame callbacks, 0 timers, 0 layout/style passes. Control with one CSS loop: 300 DrawFrame per 5 s. Before this phase the device view drew 300 frames per 5 s (mode-light glow).

Bundle (`vite build`): JS 487.12 to 489.20 kB (gzip 146.47 to 147.12), CSS 39.41 to 41.28 kB (gzip 7.72 to 8.23).

UNRESOLVED-FL1 closed (B-072): .NET 8 `MyDocuments` read from the runtime source and a `net8.0` run on macOS. macOS `~/Documents` (NSDocumentDirectory; ignores a changed `HOME`, the rewrite reads `HOME`), Linux the .NET XDG lookup, Windows the Documents known folder.

Gates (local, macOS): `pnpm check` (159 tests), `cargo test --workspace` (472), clippy `-D warnings`, fmt, `pnpm tauri build --debug --no-bundle`.

## Phase 6 (2026-10-15): the reviewer's diff list

| Item | State |
|---|---|
| Profile name box with fixed .csv | **DONE** (writes the file-name cell) |
| Toolbar set and order | **DONE** Share, Unused (N), Undo, Add row (Rows only), Select, template, Save, Install, Help; Close and the raw-grid switch gone, Home leaves the editor |
| Modes sidebar | **DONE** ? and pencil (Modes window), Custom output names table, plain rows |
| Sidebar config / status / view | **DONE** model picker, connection line (drives + live reader, no polling), words / cards / one-line toggles |
| Device view | **DONE** parts list in the sidebar incl. Hole combos, nothing selected at start, callout rows no longer cover the photo top |
| Rows view | **DONE** numbered cell grid; "nothing yet" was a display bug (normal hidden), now the file's word; Avalonia display names |
| Validation | the Rust validator already matches C# (4 warnings, new test); the mock never validated. Now a bottom bar |
| Home | **DONE** library cards with menus, Import from Sheets button, Import enabled, tighter line height |
| Settings | **DONE** card wording, Drive backup, update check on General; Fluent selects |
| Device settings / DeviceLink | **DONE** layout like Avalonia via `?device` mock (prefs.csv, console) |

Still different: the device nav opens the file list (Avalonia opens settings, files in a window); device settings has no group picture or Mac games block; card menu actions open the profile first; custom-name drafts last the session only; template rename/delete not ported; Avalonia's path line names the drive instead.

Idle (production build, headless Chrome, focus emulated, 5 s settle, 5 s trace): Home, editor Rows, device view each 0 DrawFrame / animation frames / timers / layout; control 300 DrawFrame.

Bundle: main JS 489 to 542 kB (gzip 147 to 160): new views plus validation.json for Unused.

Gates (local, macOS): `pnpm check` (162 tests), `cargo test --workspace` (475), clippy `-D warnings`, fmt, catalogs regenerated clean, `pnpm tauri build --debug --no-bundle`.

## Runtime cost while a game runs

- Console: nothing runs while the dialog is closed or idle. Ports are listed on open and on Refresh only. Live input is subscribed, and the 30 ms breath wait runs, only during a measurement.
- Telemetry: off is a flag read. On and idle is one thread parked on a channel (no timer); an event opens a 5 s batch window.
- Follow / Sheets: nothing until a press; asking whether a profile follows reads a local file. Home's Drive button reads the local token store once on mount; the only timer is the 4 s disarm after a first press.
- Controller bridge: off, nothing runs. On, one thread is parked on the child's stdout; the bridge process itself checks stdin four times a second, as on Avalonia. Links and bridge state arrive on channels only when something changes.

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
