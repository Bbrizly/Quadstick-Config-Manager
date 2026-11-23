# Behavior ledger

Source definitions live in `05-behavior-inventory.md`. This ledger tracks contract and proof.

| ID | Class | Contract | Target proof | Status |
|---|---|---|---|---|
| B-001 | A | CSV parse quoting/CRLF-LF | golden parity | CONTRACTED |
| B-002 | A | CSV writer CRLF | exact bytes | CONTRACTED |
| B-003 | A | raw grid extra columns preserved | property/mutation | CONTRACTED |
| B-004 | A | A..J trim/newline flatten | golden | CONTRACTED |
| B-005 | A | Version 1.5 + true blanks | oracle | CONTRACTED |
| B-006 | A | 63-char/1023-byte firmware diagnostics | boundary/oracle | CONTRACTED |
| B-007 | A | modes/bindings/prefs/IR parse+issues | differential | CONTRACTED |
| B-008 | A | K note/L action name | differential | CONTRACTED |
| B-009 | A | undo/dirty/revision | mutation state | CONTRACTED |
| B-010 | A | qsf explicit safe operations | JSON parity | CONTRACTED |
| B-011 | A | mount marker discovery | fake+hardware | CONTRACTED |
| B-012 | A | cached display/fresh destructive scan intent | discovery tests | CONTRACTED |
| B-013 | A | safe filename/path rules | negative tests | CONTRACTED |
| B-014 | A | default overwrite confirm | confirmation tests | CONTRACTED |
| B-015 | A | prefs install confirm | confirmation tests | CONTRACTED |
| B-016 | A | backup before target change | fault test | CONTRACTED |
| B-017 | A | temp/readback/replace/restore | fault+hardware | CONTRACTED |
| B-018 | A | backups off device | path tests | CONTRACTED |
| B-019 | A | protect default/prefs delete | negative | CONTRACTED |
| B-020 | A | library filtering/default first | parity | CONTRACTED |
| B-021 | A | LED file patterns audited 1–32 | exact table | CONTRACTED |
| B-022 | A | HID known IDs + gamepad collection | descriptor/hardware | CONTRACTED |
| B-023 | A | normalized axes/buttons/jitter | captures | CONTRACTED |
| B-024 | A | HID errors retry/nonfatal | state tests | CONTRACTED |
| B-025 | A limitation | XInput not current HID | capability test | CONTRACTED |
| B-026 | A | OAuth PKCE/state/loopback | auth tests | CONTRACTED |
| B-027 | A | drive.file + Sheets/Drive semantics | HTTP snapshots | CONTRACTED |
| B-028 | A | DPAPI/Keychain; Linux unavailable | OS tests | CONTRACTED |
| B-029 | A | cloud push does not block local save | fake cloud | CONTRACTED |
| B-030 | A | keep-online rescues local then validates remote | conflict tests | CONTRACTED |
| B-031 | A | telemetry opt-in/allowlist/scrub/CI-off | network tests | PORTED Soft (no PostHog) |
| B-032 | A | persistent theme/lang/scale/reduced motion | settings tests | PORTED |
| B-033 | A | scale preview auto-revert | timer/UI | PORTED |
| B-034 | A | all locales + RTL + pseudo | i18n CI | PARITY-TESTED |
| B-035 | A | import/XLSX review limitations | fixtures/E2E | CONTRACTED |
| B-036 | A | community profiles | HTTP/E2E | CONTRACTED |
| B-037 | A | mode operations | mutation/E2E | PORTED |
| B-038 | A | preference catalog/editor | parity/E2E | CONTRACTED |
| B-039 | A | agent constrained typed edits | corpus/eval | Soft: ENABLED=false; EditorOp ready |
| B-040 | A | crash rescue/report | forced crash/privacy | PORTED Soft (prompt + draft + panic hook; no PostHog) |
| B-041 | A | update behavior | signed updater | SOFT: GitHub check + browser open; signed install PARKED |
| B-042 | E | Avalonia gallery | web tooling replacement | ASSESSED |
| B-043 | E forbidden | generic JS filesystem | capability test absent | CONTRACTED |
| B-044 | E forbidden | generic JS shell/process | capability test absent | CONTRACTED |
| B-045 | A | serial/Bluetooth console (`DeviceConsole.cs`, main `f3525fe`): backspace prefix, `\r` line end, `\n>` prompt, 480-byte UTF-8-safe chunks, write_file 0/1/2 with two-digit year, read-back ignoring `\r`, silent preference = refused, load needs " OK", boot unanswered | `qcm-core/tests/device_console.rs` against `FakeConsole` | PARITY-TESTED (hardware pending). Supersedes the 2026-08-30 deferral: main shipped the feature |
| B-046 | A | console send = drive install gates + backup + read-back before load; strength test sends its profile through the same path | `device_console.rs`, `console_shell.rs` tests | PARITY-TESTED |
| B-047 | A | strength test: needs a USB reading first, maxima held at 100, calibrate, 5..95 sweep in <=7 tries each way, always delete test file + boot (Stop too), result "under 5%" below the sweep | `DeviceLinkDialog.test.tsx` | PARITY-TESTED (hardware pending) |
| B-048 | C | console port list: macOS `/dev/tty.*` twin of a `/dev/cu.*` port is hidden (HidSharp listed both; `tty` blocks on open); QuadStick-named ports first as before | `console_shell.rs::the_quadstick_is_offered_first_and_only_once_on_a_mac` | IMPLEMENTED |
| B-050 | A | telemetry: closed event set incl. `strength_measured`, per-event key allowlist, `game` on opened/saved/install_succeeded, `setup_seconds` since open, consent = !`QSCM_TELEMETRY=0` && notice >= 2 && toggle, no sender without a build token, launch tracked once per process, install id minted only for a yes and used only once saved, withdraw drops the queue | `telemetry.rs` tests | PARITY-TESTED |
| B-051 | C | telemetry wire: `$lib` is `quadstick-config-manager` and the User-Agent is the fixed `QuadStickConfigManager`, not `posthog-dotnet`/.NET; `os_version` is OS + arch; `$geoip_disable` kept. PRIVACY.md describes the Tauri sender | `telemetry.rs` tests | IMPLEMENTED |
| B-052 | A | first-run usage notice (notice 2) before the crash prompt and tour; No is default/Escape; skipped under `QSCM_TELEMETRY=0`; a failed write is a no with `Main_CouldNotSaveThatPreference`; privacy link is a fixed native URL; install id shown + copy in Settings > Advanced | `UsageNotice.test.tsx` | PARITY-TESTED |
| B-053 | C | install_failed reasons: native `no_profile`/`has_errors`/`not_a_quadstick`/`io_error`; React reports `cancelled_folder` (picker cancelled), `cancelled_device` (closed with 2+ drives before an attempt), `cancelled_default`/`cancelled_preferences` (closed at that confirmation). Device/folder also count `install_attempted`, as Avalonia tracks it first | `UsageNotice.test.tsx`, `telemetry.rs::only_the_four_cancel_reasons…` | IMPLEMENTED |
| B-054 | C | panic report = `CrashPayload` shape with a one-link chain (`type: panic`, frame function names from `Backtrace`, `::` as `.`, hash suffix dropped, identifier-filtered); no message/file/line is written. Read-back re-filters and caps (20/200/300) like `FromJson` | `telemetry.rs::a_panic_report…`, `a_hand_edited_report…` | IMPLEMENTED |
| B-055 | A | registry: index schema 1, size caps (8 MiB/512 KiB/50k), id/slug/platform/source/Google-Sheet URL/sha checks, duplicates refused, snapshot SHA-256 verified, verified caches cover an outage, a link only opens unsaved in the editor with `game` for telemetry | `registry.rs` tests | PARITY-TESTED |
| B-056 | A | deep link: canonical `qcm://profile/<id>` on the original spelling, no query/fragment/user/port/dot segments; first matching argv entry | `registry.rs` tests | PARITY-TESTED |
| B-057 | A | `qcm` scheme: bundle declares it (macOS `CFBundleURLTypes`, Windows installer), per-user runtime registration on Windows/Linux, macOS Apple events and second-launch argv go to one inbox; the running window opens the link straight away (no unsaved-changes prompt, as `OpenInEditor`), a link with no window yet waits, newest wins | `registry.rs` inbox tests + `RegistryLink.test.tsx` | IMPLEMENTED (OS click-through pending) |
| B-049 | C | backups taken over the console use the rewrite's backup store and its location display, not a raw home path | `console_shell.rs` tests | IMPLEMENTED |
| B-058 | A | share setup: Copy share link without Google opens the 2/3-step setup (connect, save if never saved, then the action); states in words, last step disabled with a spoken reason, the dialog never shares; Cancel shares nothing. "Connect Google Drive" (hard-coded English in Avalonia) is the localized `Rewrite_ConnectGoogleDrive` | `ShareSetupDialog.test.tsx` | PARITY-TESTED |
| B-059 | A | controller bridge: Linux only, offered only with `qs-bridge` beside the app; `--json` child, stdin close is the stop (2 s, then kill), stderr dropped, unknown status lines ignored, death says stopped unless the last word was unsupported; switch saved (no revision bump) and restarted at launch; stopped on exit. Shown on the device page header | `controller.rs` tests (fake bridge script), `ControllerRow.test.tsx` | PARITY-TESTED |
| B-060 | A | Mac gamepad: same SDL mappings/LaunchAgent command and undo, zsh with 10 s cap, a failed turn-on is undone, sandboxed build copies the commands instead | `controller.rs::the_mac_command…` (real command, scratch HOME + fake launchctl), `ControllerRow.test.tsx` | PARITY-TESTED |
| B-061 | C | Drive picker: dialog from Settings > Import from Drive, loads after opening, loading/none/could-not-load/nothing-selected/importing/failed lines, name + local short date (raw text if it will not parse). One backup at a time into the import review instead of a checked bulk copy into a library folder: the rewrite has no library folder yet, so `AlreadyLinked`/pre-check do not apply | `DrivePickerDialog.test.tsx` | IMPLEMENTED |
| B-062 | A | Linux Google token: kept for the run only (`InMemoryTokenStore`), so Google backup is offered on Linux and the next launch asks again; macOS/Windows keep the credential store | `google_auth.rs` (cfg) | IMPLEMENTED (Linux build pending CI) |
| B-063 | A | duplicate mark: per-mode counts of output tokens and each input cell (trimmed, ordinal); `×N` chip at 2+, spoken "Used N times in this mode"; on sentence cards and the mapping inspector; a combo's own rows are subtracted from its input count (3ad22ce) | `duplicates.test.tsx` | PARITY-TESTED |
| B-064 | C | wording homes: a community (anonymous Sheets) import says `Main_ImportIsACopy` once the editor opens (1950d85; Avalonia appends it to the import line; the rewrite has no pasted-link import); the mapping inspector shows the pad's name under the output (88eda16, `OtherNames`, not searched: no output picker yet) and the function-number sentences under the function (1cc0cd8, 2b0552c, `ParameterHint`) | `functionHelp.test.tsx` | IMPLEMENTED |
| B-065 | A | pasted Sheets link on Home downloads the workbook export, falls back to one CSV tab (published link, said in the review), same refusals as Import (web page, no profile tab with the A1/skipped wording, timeout, download error); the link is remembered against the file on the first save (incl. save on close), never by a profile opened in between | `follow.rs` tests, `tests/follow_link.rs`, `FollowLinkActions.test.tsx` | PARITY-TESTED |
| B-066 | A | Get the latest: only on press, one at a time (held through the question); unsaved work is saved first after asking, a no sends nothing; lists mode added/removed/changed (multiset) and other cells (notes/preferences), 12 lines then …; Keep mine is the default; a yes re-checks the session revision and the file bytes, copies the old file to the rescue folder under a new `-before-latest-` name that is never overwritten, writes atomically, reopens; Forget drops only the local link. Links live in a native-only `follow-links.json` keyed by the private path | `tests/follow_link.rs` (9), `profile_changes.rs` (6), vitest (10) | PARITY-TESTED |
| B-067 | C | Get the latest is in the editor only: the rewrite Home has no profile cards to carry it. The app's own Drive token is not tried first for a sheet it made (`FetchSheetAsync` `ours`): the public export only. Replace does not push to Drive (the rewrite backs up on press only) | — | IMPLEMENTED |
| B-068 | A | console window close during connect, send or strength test waits: stop is asked, the test file is deleted and the stick booted, then the window closes (6934bbf); a close during the profile picker stops the send | `DeviceLinkDialog.test.tsx` | PARITY-TESTED |
| B-069 | A | Drive restore: Home Drive button (connected opens the picker; signed out arms, second press within 4 s signs in, then asks to restore and opens the picker all ticked); the same question after a Settings sign-in; bulk restore into Documents/QuadStick Profiles (Avalonia `LibraryDir`), skip on a case-insensitive name already there, numbered names within a batch, metadata before the write, created-new never over a file, link saved or the file removed; Imported/Skipped/Failed line; linked backups dimmed. Supersedes B-061's single-review note when the native side has bulk restore | `DrivePickerDialog.test.tsx`, `HomeDriveButton.test.tsx`, `drive.rs` name tests | IMPLEMENTED (real Drive pending) |
| B-070 | B | restore file names use the Windows-safe character set on every OS (C# used the platform's own set) and cut by characters, not UTF-16 units | `drive.rs` tests | IMPLEMENTED |
| B-072 (was UNRESOLVED-FL1) | A | restore folder = .NET 8 `MyDocuments`/QuadStick Profiles. Checked in the runtime source (`release/8.0` `Environment.GetFolderPathCore.Unix.cs`) and with a `net8.0` snippet on macOS (prints `~/Documents`, ignores `HOME` and `XDG_*`). macOS: `NSDocumentDirectory` (`~/Documents`; .NET ignores a changed `HOME`, the rewrite reads `HOME`, same folder otherwise and under the sandbox). Linux: `ReadXdgDirectory`: absolute `XDG_DOCUMENTS_DIR`, else first `$HOME/` or absolute `XDG_DOCUMENTS_DIR` line of `user-dirs.dirs` (absolute `XDG_CONFIG_HOME` or `~/.config`), else `~/Documents`; home = non-empty `HOME`, else passwd. Windows: Documents known folder (`dirs`), was `%USERPROFILE%\Documents` | `drive.rs` `linux_documents_follow_the_dotnet_xdg_lookup` | IMPLEMENTED |
| B-071 | A | Linux bundle carries `qs-bridge` beside the app (`externalBin` in `tauri.linux.conf.json`, staged by `scripts/stage-qs-bridge.sh`) and `70-quadstick.rules` in the deb/rpm (ca813aa) | CI `linux-bundle` | IMPLEMENTED (needs CI) |
| B-073 | B | look: same tokens, Home column, tiles, quiet buttons, Fluent fields, Settings tabs, dialog title strip, one-line modes and the editor command bar as Avalonia (`RenderPreview --compare` pairs). Differences kept: no profile cards on Home, issues stay a panel, Rows view keeps the mapping panel, raw grid and DeviceLink/device settings not compared (mock has no prefs.csv or console) | phase 5 side-by-side set | IMPLEMENTED |
| B-074 | B | device view: the stage is drawn at 910 px and scaled to fit (Avalonia Viewbox), floor 0.6 then it scrolls; mode lights are still (the old rewrite glow ran 60 fps while idle); the FPS photo now shows (crop was 10000%) | trace + screenshots | IMPLEMENTED |
| B-075 | B | editor (phase 6): Avalonia command bar (name box with fixed .csv, Share flyout, Unused (N), Undo, Add row in Rows only, Select, template, Save, Install, Help), sidebar (Modes ? and pencil, Custom output names, Configuration picker, connection line, parts list in device view, View words / cards / one-line toggles), Rows as the numbered cell grid, nothing selected at start, problems in a bottom bar | IMPLEMENTED |
| B-076 | B | Home lists Documents/QuadStick Profiles as cards with a menu (share link, open sheet, get the latest, forget the link, delete); Use template opens saved templates; Save as template writes Templates/ | IMPLEMENTED |
| B-077 | B | Settings: General holds language, appearance, size, model, card wording, Drive backup, updates; Advanced holds motion, grouping, privacy, window, tutorial; Help holds the quick guide | IMPLEMENTED |
| B-078 | B | device nav opens device settings (drive found and read, none draws the catalog, several ask), files in a window from the page and Home; group picture and Games on a Mac block; drive path line | phase 7 shots + `DeviceSettingsRoute.test.tsx` | IMPLEMENTED |
| B-079 | B | Home card menu actions act on the file without opening it; unused custom names kept per profile; template rename/delete; Get the latest list as `ConfirmLatestAsync`; Undo never greyed; Inter | phase 7 | IMPLEMENTED |
| B-080 | B | Home "Edited" line is full secondary text, not Avalonia's 0.75 dim (4.0:1 on a light card failed 4.5:1) | contrast check + screenshot | IMPLEMENTED |
| B-081 | A | Home ? beside On your QuadStick opens the files-help flyout (`HomeFilesHelpButton`), not the whole guide | screenshot | IMPLEMENTED |
| B-082 | C | Settings tabs: one tab stop, arrows/Home/End move, panel labelled by its tab | `SettingsPage.test.tsx` | IMPLEMENTED |
| B-083 | C | mode delete: the armed first click shows Really delete? on the button, not only in its name | `ModesDialog.test.tsx` | IMPLEMENTED |
| B-084 | C | file delete on the QuadStick focuses Cancel, Delete is plain danger (Avalonia ConfirmAsync); Enter in rename commits | manual (mock has only protected files) | IMPLEMENTED |
| B-085 | C | a dialog whose buttons swap while open (Install to Close) keeps focus in the panel | `Dialog.test.tsx` | IMPLEMENTED |
| B-086 | C | tutorial opens on Next, Skip sits left in the button row, step count and text read together | `TutorialTour.test.tsx` | IMPLEMENTED |
| B-087 | B | Drive backup, Drive picker, drive choice and follow-link review put dismiss first and the action last, safe option focused | screenshots | IMPLEMENTED |
| B-088 | C | no animation loops while an input is held (visualizer and sentence pips fade in once); the device check fades instead of popping on every editor load; presses do not move under reduce motion | `grep infinite` empty + getAnimations idle probe | IMPLEMENTED |
| B-089 | B | device view gives the picture the width: inspector track `minmax(264px, 0.25fr)` (was 340 / 0.6fr), 4px canvas sides (Avalonia 12), and the stage fits height as well as width (a Uniform Viewbox) so the whole device is always in view; stage type is 16/17 design units (app 14/15), so callout text reads 11.0px at 1280x860 on FPS, Original and Singleton (was 8.9), 11.8px at 1440x900; callouts grow into the stage's top headroom, none clips or overflows (WebKit probe, busy profile, combos view); under the 0.6 floor (1024x700) it scrolls as before | `EditorLayout.test.tsx`, `StageType.test.ts` + WebKit per-model probe | IMPLEMENTED |
| B-090 | B | sidebar View block (labels, card toggles, Device/Parts/Rows) is pinned at the bottom; only modes, configuration and parts scroll above it. Below 650px tall the whole sidebar scrolls again, as Avalonia's does | `EditorLayout.test.tsx` + screenshots at 1280x860, 1024x700, 900x600 | IMPLEMENTED |
| B-091 | C | every dialog row puts dismiss left and the action right; overwrite questions (device settings save, install, link send) open on Cancel with a plain Yes; privacy prompts keep No thanks / Not now as the focused default | `DeviceSettingsRoute.test.tsx`, `InstallProfileDialog.test.tsx`, `DeviceLinkDialog.test.tsx`, `UsageNotice.test.tsx`, `CrashReportPrompt.test.tsx` | IMPLEMENTED |
| B-092 | A | Contact tab shows Report a bug, Website, LinkedIn and Email links after the intro, in `SettingsView.ContactTab` order, each a link button whose accessible name is its `*Help` string; gone when the network is off. The window names a link (`report_bug`, `website`, `linkedin`, `email`) and native opens the fixed address, so no URL crosses the boundary | `updates.rs` contact tests + `SettingsPage.test.tsx` | IMPLEMENTED |
Status moves to PARITY-TESTED/HARDWARE-VERIFIED only with linked TEST_LEDGER evidence.