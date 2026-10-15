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

Status moves to PARITY-TESTED/HARDWARE-VERIFIED only with linked TEST_LEDGER evidence.
