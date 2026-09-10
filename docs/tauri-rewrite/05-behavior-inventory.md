# Behavior inventory

This is the human-readable master. `ledgers/BEHAVIOR_LEDGER.md` is the status ledger.

Classification: **A** required compatibility; **B** intentional improvement; **C** existing bug to fix; **D** unresolved; **E** retire/tooling-only.

| ID | Behavior | Evidence | Class | Target owner |
|---|---|---|---|---|
| B-001 | Parse quoted CSV, escaped quotes, CRLF/LF input | `Csv.cs` | A | qcm-config |
| B-002 | Serialize CSV with CRLF | `Csv.cs` | A | qcm-config |
| B-003 | Preserve raw grid/comments beyond device columns | `ProfileFile.cs`, `FORMAT.md` | A | qcm-config |
| B-004 | A..J trimmed and embedded newlines flattened for device output | `ProfileFile.DeviceSafe` | A | qcm-config |
| B-005 | Add/fix Version 1.5 header and true blank sheet separators | `NormalizeForDeviceCsv` | A | qcm-config |
| B-006 | Firmware-aware 63-char keyword / 1023-byte line diagnostics | `Parser.cs` | A | qcm-config |
| B-007 | Parsed modes/bindings/preferences/IR + validation issues | Parser/Validator/Model | A | qcm-config |
| B-008 | Column K note, column L action name; action name constraints | `ProfileFile`, `FORMAT.md` | A | qcm-config |
| B-009 | Undo raw-grid mutations, Dirty and Revision | `ProfileFile` | A | qcm-core session |
| B-010 | qsf explicit inspect/vocab/validate/apply/diff contract | `tools/qsf` | A | Rust qsf CLI |
| B-011 | Detect QuadStick mount by ready drive + `default.csv`; macOS fixed `/Volumes` exception | `Device.cs` | A initially | native storage adapter |
| B-012 | 3s candidate display cache but live rescan before install | `Device.cs` | A | device manager |
| B-013 | Reject unsafe/long/reserved target names | `Device.cs`, SafeFileName | A | qcm-core/storage |
| B-014 | Confirm overwrite of `default.csv` | `InstallFlow.cs`, Device | A | frontend + core confirmation token |
| B-015 | Confirm install of `prefs.csv` | `InstallFlow.cs`, Device | A | frontend + core confirmation token |
| B-016 | Backup existing target before write | Device | A | storage transaction |
| B-017 | Temp-write, exact read-back verify, replace, restore logic | Device.Install | A | storage transaction |
| B-018 | Never keep backups on device; home `QuadStickBackups` | Device/FORMAT | A | storage adapter |
| B-019 | Protect default.csv/prefs.csv from normal delete | Device.DeleteProfile | A | core |
| B-020 | Device library excludes prefs from profile order; default first | Device | A | core |
| B-021 | LED file patterns only for audited 1–32; no extrapolation | Device/ModeLights | A | qcm-config/core |
| B-022 | Live HID only uses known QuadStick identities + gamepad descriptor usage | LiveInput | A | HID adapter |
| B-023 | Live X/Y normalized, buttons extracted, jitter/duplicate suppressed | LiveInput | A | HID adapter/core |
| B-024 | HID disconnect/error nonfatal and retries | LiveInput | A | device actor |
| B-025 | Xbox 360 native XInput mode not represented by current HID reader | LiveInput | A limitation | UI capability state |
| B-026 | Google OAuth uses system browser + PKCE + state + loopback timeout | GoogleAuth | A | cloud adapter |
| B-027 | `drive.file` scope and Google Sheets/Drive REST semantics | GoogleAuth/DriveClient | A | cloud adapter |
| B-028 | Keychain on macOS, DPAPI CurrentUser on Windows; Linux disabled | TokenStore/GoogleAuth | A until ADR change | secure store |
| B-029 | Backup push after save does not block save path | MainWindow/DriveBackup | A | background job manager |
| B-030 | Keep-online conflict first rescues local, validates remote, then atomically replaces | MainWindow/DriveBackup | A | cloud/profile service |
| B-031 | Usage/crash telemetry is opt-in/allowlisted/scrubbed and CI-off | Telemetry/build.yml | A | diagnostics service |
| B-032 | App theme/language/interface scale/reduced motion/settings persist | Settings/MainWindow | A | settings service + UI |
| B-033 | Interface scale preview auto-reverts unless confirmed | SettingsView | A | UI |
| B-034 | Localized UI languages including RTL Arabic + pseudo-loc asset | Strings*.resx | A | i18n |
| B-035 | Import review and XLSX paths preserve/report skipped tabs/limitations | ImportReview/qsf/Xlsx | A | import service/UI |
| B-036 | Community profile catalog/download/open flow | Community* | A | community service/UI |
| B-037 | Modes can be added/renamed/reordered/edited according to ProfileFile semantics | Modes/ProfileFile | A | qcm-config/UI |
| B-038 | Device preferences editor uses embedded preference catalog/defaults | PreferenceEditor/PreferenceCatalog | A | qcm-config/UI |
| B-039 | Agent edits go through constrained qsf/profile operations rather than arbitrary CSV | Agent*/qsf | A | qcm-core agent bridge |
| B-040 | Crash rescue/report behavior protects recoverable local work | CrashGuard/CrashReport | A | diagnostics/recovery |
| B-041 | Update discovery and release notes behavior | UpdateCheck/CI | A, implementation may improve | updater service |
| B-042 | `--gallery` render path | App/Gallery/RenderPreview | E unless visual regression tooling reuses it | test tooling |
| B-043 | Generic frontend arbitrary filesystem API | none | E/forbidden | — |
| B-044 | Generic frontend shell/process API | none | E/forbidden | — |
| B-045 | Production serial transport | dependency/README claims but source use not yet proven | D | spike only |

### Required inventory expansion during Phase 0

Before implementation passes Gate 0, enumerate every public/internal behavior in `MainWindow`, `DeviceFilesWindow`, `DeviceSettingsPage`, `ImportReviewWindow`, `ModesWindow`, `AgentWindow`, Drive backup and all tests. Add a row for any behavior that can affect file bytes, device state, user data, focus/AT behavior, persistent settings or network/telemetry.