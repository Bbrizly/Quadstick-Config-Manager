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
| B-051 | C | telemetry wire: `$lib` is `quadstick-config-manager` and the User-Agent is the app's, not `posthog-dotnet`/.NET; `$geoip_disable` kept. PRIVACY.md still says ".NET version" and needs a wording pass before the rewrite ships | review | UNRESOLVED (docs) |
| B-052 | C | consent is asked only through the Settings toggle, which records notice version 2 (as `ApplyTelemetryAnswer` does); the first-run notice dialog is not ported yet, so a fresh rewrite install sends nothing until the toggle | — | IMPLEMENTED (dialog pending) |
| B-053 | C | install_failed reasons: `has_errors`, `not_a_quadstick`, `io_error` are native; the `cancelled_*` reasons happen in React dialogs and are not reported yet | — | PARTIAL |
| B-054 | C | crash Send needs a stored `chain`; the Rust panic hook writes none, so a rewrite panic report is kept, not sent (Avalonia also refuses an empty chain) | `telemetry.rs::a_crash_report_needs_a_chain…` | IMPLEMENTED |
| B-049 | C | backups taken over the console use the rewrite's backup store and its location display, not a raw home path | `console_shell.rs` tests | IMPLEMENTED |

Status moves to PARITY-TESTED/HARDWARE-VERIFIED only with linked TEST_LEDGER evidence.
