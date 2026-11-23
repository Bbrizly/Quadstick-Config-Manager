# Test ledger

Evidence paths are target placeholders until implemented.

| ID | Contract | Automated target | Manual/hardware | Status |
|---|---|---|---|---|
| T-001 | CSV exact parse/write | `qcm-config` golden | — | PLANNED |
| T-002 | firmware parser/limits | differential + FirmwareOracle | device sample | PLANNED |
| T-003 | raw-grid preservation | property/mutation | real imported profile | PLANNED |
| T-004 | editor ops/undo/revision | differential | keyboard editor | PLANNED |
| T-005 | safe filenames/traversal | unit/IPC negative | — | PLANNED |
| T-006 | discovery stale/multiple | `qcm-core/tests/device_discovery.rs` + `src-tauri` temp-dir adapter tests | real multiple/remount | IMPLEMENTED |
| T-007 | install transaction faults | `qcm-core/tests/device_install.rs` every stage + real temp-dir install | unplug/full/read-only on a stick | IMPLEMENTED |
| T-008 | library/delete/order | `qcm-core/tests/device_library.rs` + `src-tauri` temp-dir delete tests | real device | IMPLEMENTED |
| T-009 | HID descriptor/normalize | captured fixtures | real modes 3 OS | PLANNED |
| T-010 | HID backoff/leak | `qcm-core/tests/live_input.rs` fake soak | 2h/reconnect on a stick | IMPLEMENTED |
| T-029 | live-input back pressure and release | `qcm-core/tests/live_input.rs` slow consumer, unplug, stale, stop | unplug with a button held on a stick | IMPLEMENTED |
| T-011 | IPC revision/security | `src-tauri/tests/profile_commands.rs` direct command negatives | — | IMPLEMENTED |
| T-012 | capability least privilege | static/forbidden-call | release audit | PLANNED |
| T-013 | frontend editor | `src/features/editor/EditorWorkspace.test.tsx` mock E2E + axe + keyboard | AT | IMPLEMENTED |
| T-014 | visualizer semantic equality | component/axe | NVDA/VoiceOver | PLANNED |
| T-015 | import/XLSX limits | malicious fixtures | review AT | PLANNED |
| T-016 | community privacy/offline | HTTP spy/cache | offline | PLANNED |
| T-017 | Google auth | mock state/PKCE | DPAPI/Keychain | PLANNED |
| T-018 | Drive backup/conflict | fake/HTTP mock | real account | PLANNED |
| T-019 | telemetry privacy | `src-tauri/src/telemetry.rs` (token/kill-switch/notice gates, allowlist, launch once, game/setup_seconds/strength, withdraw drops queue, feedback cap + flush, crash chain without message) + `qcm-core/tests/settings.rs` install id | policy review; one real PostHog event from a signed build | IMPLEMENTED |
| T-020 | crash rescue | diagnostics draft + pending offer/open + panic hook + CrashReportPrompt | packaged restart | Soft IMPLEMENTED |
| T-021 | i18n | generated key/placeholder/pseudo/RTL/error-code suite | locales | IMPLEMENTED |
| T-022 | updater signature/state | updates.rs Compare unit tests; Soft browser check | packaged rollback | SOFT: Compare tested; signed install PARKED |
| T-023 | agent/qsf | agent_feature off + EditorOp contract; corpus under agent/ | human sample | Soft: gated off like Avalonia |
| T-024 | performance | benchmarks | target vs baseline | PLANNED |
| T-025 | packaging | CI build/sign verify | clean machines | PLANNED |
| T-026 | privacy launch network | integration traffic spy | packet/support review | PLANNED |
| T-027 | error redaction | `qcm-core/tests/redaction.rs` every family | bug-report screenshot review | IMPLEMENTED |
| T-028 | session revision/atomic batch/close-dirty | `qcm-core/tests/profile_sessions.rs` on the fake library | leave prompt with a real save | IMPLEMENTED |
| T-031 | frontend client contract + import boundary | `src/platform/mockQcmClient.test.ts` scenarios + `src/platform/importBoundary.test.ts` | browser run of the mock with no Tauri | IMPLEMENTED |
| T-032 | console protocol (12 C# tests + half-line junk, boot, new-file, stopped sweep) | `crates/qcm-core/tests/device_console.rs` with `qcm_testkit::FakeConsole` | paired QuadStick over SPP and a 3.3V cable | IMPLEMENTED |
| T-033 | console shell: opaque ports, mac tty/cu, confirm gate, strength steps, redaction | `src-tauri/src/console_shell.rs` unit tests | real port open/close | IMPLEMENTED |
| T-034 | DeviceLink window (DeviceLinkWindowTests ported) | `src/features/device/DeviceLinkDialog.test.tsx` | sip/puff test with a person on a stick | IMPLEMENTED |
| T-036 | usage notice, install id, cancel reasons, panic chain | `src/features/diagnostics/UsageNotice.test.tsx` + `telemetry.rs` tests | first launch on each OS; a real panic report sent | IMPLEMENTED |
| T-037 | share setup steps | `src/features/cloud/ShareSetupDialog.test.tsx` | real Google connect | IMPLEMENTED |
| T-038 | controller bridge + Mac gamepad | `src-tauri/src/controller.rs` tests + `src/features/device/ControllerRow.test.tsx` | real bridge on Linux with a stick in mode 2; SDL game on a Mac | IMPLEMENTED |
| T-039 | Drive picker | `src/features/cloud/DrivePickerDialog.test.tsx` | real Drive list on each OS; Linux connect | IMPLEMENTED |
| T-040 | duplicate mark incl. combo rule | `src/features/editor/duplicates.test.tsx` | — | IMPLEMENTED |
| T-041 | pad names, function-number help | `src/features/editor/functionHelp.test.tsx` | — | IMPLEMENTED |
| T-035 | registry index/snapshot/link + running-app handoff | `src-tauri/src/registry.rs` tests + `src/app/RegistryLink.test.tsx` | click a website link on each OS | IMPLEMENTED |
| T-030 | settings revision/closed value sets | `qcm-core/tests/settings.rs` + `src-tauri/tests/local_library.rs` | settings screen with a real file | IMPLEMENTED |
| T-042 | follow a Sheets link (FollowLinkTests port) | `src-tauri/tests/follow_link.rs`, `qcm-config/src/profile_changes.rs`, `src-tauri/src/follow.rs`, `src/features/follow/FollowLinkActions.test.tsx` | a real shared and a published sheet | IMPLEMENTED |
| T-043 | console close mid-test/mid-send (6934bbf) | `src/features/device/DeviceLinkDialog.test.tsx` | close mid-test on a stick | IMPLEMENTED |
| T-044 | Drive Home button, restore offer, bulk restore | `HomeDriveButton.test.tsx`, `DrivePickerDialog.test.tsx`, `drive.rs` tests | real Drive restore on each OS | IMPLEMENTED |
| T-045 | Linux bundle has qs-bridge and the udev rule | `.github/workflows/rewrite.yml` `linux-bundle` | install the deb, start the bridge | IMPLEMENTED (CI) |
| T-046 | restore folder follows .NET 8 `MyDocuments` (Linux XDG lookup) | `src-tauri/src/drive.rs` `linux_documents_follow_the_dotnet_xdg_lookup`; macOS checked with a `net8.0` snippet | run the restore on a Linux desktop with a moved Documents folder | IMPLEMENTED |
| T-047 | focus follows a moved or copied mode (hidden controls never keep focus) | `src/features/editor/EditorWorkspace.test.tsx` | keyboard pass with VoiceOver | IMPLEMENTED |
| T-048 | idle Home, editor rows and device view: zero frames, no timers | headless Chrome trace on the production build (phase 5 in `implementation-status.md`) | trace in the Tauri WebView on each OS | MEASURED |

Critical behavior cannot move to PARITY-TESTED without at least one T row; storage/HID required parity also needs hardware evidence.

T-011 and T-030 are IMPLEMENTED, not PARITY-TESTED. They are unit and
temp-directory tests written from the command API document and the shipped
`Theme.cs` settings, not artifacts generated by the frozen C# oracle. Two
deliberate divergences from the shipped app are pinned by them rather than by
prose: a failed settings write is reported instead of swallowed, and a value
outside a closed set is refused instead of clamped.

T-029 is IMPLEMENTED, not PARITY-TESTED. `MockQcmClient` is a fake written to
the same contract as the native commands, not an oracle. It holds the revision
rule, the all-or-nothing batch and the error codes, and it deliberately models
no format behavior at all. What it cannot prove is that the two halves agree,
which is what a UI driven against both in TASK-038 is for.

T-006, T-007 and T-008 are IMPLEMENTED, not PARITY-TESTED. They are unit and
temp-directory tests written from the shipped `Device.cs` and its three test
files, not artifacts generated by the frozen C# oracle the way `qcm-config`
parity works. The rows move to PARITY-TESTED when the same sequences run against
a QuadStick.

T-010 and T-029 are IMPLEMENTED, not PARITY-TESTED. They run the live-input
manager against `FakeLiveInput`, which is a port double, not a QuadStick. The
soak is 200,000 reports on a `ManualClock` rather than two hours on a wall
clock, so it proves the invariant that gives boundedness (the slot never holds
more than one snapshot, and published equals delivered plus coalesced plus
pending) and does not prove anything about a real HID handle held open for two
hours. Both rows move when TASK-026 and TASK-027 put a real device behind the
port.

The `XInputOnly` state is proved against the fake only. That a QuadStick in
emulation mode 3 enumerates as interface class 0xFF comes from the firmware
(`Joystick/Descriptors.c`) and from the shipped reader's own comment, and no
test here has seen the device do it. Whichever adapter TASK-027 lands has to
report that candidate kind, and the hardware matrix is where it is checked.

Two guards in the storage adapter are held by review rather than by a test,
because the failure cannot be injected through `std::fs`:

- `restore_file` writing beside the target and renaming, rather than over it.
  Both shapes leave the same result unless the write is cut short, and a torn
  write cannot be forced from a test.
- the cleanup of a temp file whose create succeeded and whose write then failed.
  A read-only volume fails at create, so it exercises the other branch.

Both need the full-volume hardware case in TASK-056.

## Phase 6 (2026-10-15)

- `validation_parity.rs::compare_fixture_has_the_four_avalonia_warnings`: the RenderPreview fixture gives the C# oracle's four warnings (A4, C4, C5, C10).
- `library_shell.rs`: template names, card facts (modes not sheets), ids retired by the next listing.
- `profile_commands.rs`: the 85-command surface.
- Frontend: problems bar (four warnings, expand, focus), Rows grid edit/note/"normal", device sidebar parts and nothing selected, Unused (145), Modes window rename/move, settings card wording.

## Phase 7 (2026-10-15)

- `library_shell.rs::templates_rename_without_overwriting_and_delete_only_by_template_id`.
- `follow_link.rs::unused_custom_names_wait_for_a_file_then_last_with_it`.
- `profile_commands.rs` / `lib.rs`: the 90-command surface.
- Frontend: `DeviceSettingsRoute.test.tsx` (nav opens settings, mount path, files window; no stick bold line and drive picker), `CardActions.test.tsx` (forget a card's link with the editor shut), controller switch role.

## Polish pass (2026-11-23)

- `SettingsPage.test.tsx`: one tab stop, arrows and Home move and label the panel (B-082).
- `ModesDialog.test.tsx`: armed delete shows Really delete? on the button (B-083).
- `Dialog.test.tsx`: focus survives a button swap and a busy spell, and stays where the user put it (B-085).
- `TutorialTour.test.tsx`: opens on Next (B-086).
- `InstallProfileDialog.test.tsx`: the overwrite question focuses Cancel, so a second Enter does not overwrite default.csv.
- Every test above was run red with its fix removed. Screens checked on the mock in headless Chromium and Playwright WebKit 26 (Home, editor, modes help, Settings); a still editor runs zero animations in both. Not yet run in the real Tauri WebView (`pnpm e2e:mac`).
