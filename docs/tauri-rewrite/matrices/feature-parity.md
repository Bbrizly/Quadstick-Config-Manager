# Feature parity matrix

Status starts `NOT STARTED`; evidence must move it, not confidence.

| Feature | Legacy evidence | Required | Target owner | Automated proof | Hardware/AT proof | Status |
|---|---|---:|---|---|---|---|
| New profile/templates | ProfileFile/templates/App tests | yes | qcm-config/core + React | oracle/component | — | NOT STARTED |
| Open CSV | ProfileFile/MainWindow | yes | core/native picker | oracle/E2E | — | NOT STARTED |
| Save/Save As | ProfileFile/MainWindow | yes | core/local adapter | byte parity/E2E | filesystem smoke | NOT STARTED |
| Raw list/grid | MainWindow/App tests | yes | React projection | component | keyboard/AT | NOT STARTED |
| Binding edits | ProfileFile/qsf/tests | yes | qcm-config | mutation parity | — | NOT STARTED |
| Action names | ProfileFile/tests | yes | qcm-config + UI | oracle/component | keyboard | NOT STARTED |
| Undo/dirty | ProfileFile/tests | yes | qcm-config/core | state parity | — | NOT STARTED |
| Modes add/rename/reorder | ModesWindow/ProfileFile | yes | qcm-config + React | parity/E2E | keyboard/AT | NOT STARTED |
| Issues/validation | Parser/Validator/tests | yes | qcm-config + React | oracle | focus/AT | NOT STARTED |
| Friendly/raw/controller labels | MainWindow/OutputCatalog | yes | UI/catalog | component | AT | NOT STARTED |
| Centered visual editor | current editor + target redesign | parity+improvement | React | E2E | keyboard/AT/zoom | NOT STARTED |
| Device storage discovery | Device.cs | yes | native storage | fake/integration | real device 3 OS | NOT STARTED |
| Manual device-folder fallback | InstallFlow/Device | yes | native command | integration/E2E | real invalid/valid root | NOT STARTED |
| Install validation/confirm | InstallFlow/Device | yes | core + UI | negative/fault | real hardware | NOT STARTED |
| Backup/temp/readback/replace | Device.Install | yes critical | core/storage | fault matrix | unplug/write hardware | NOT STARTED |
| Device library/list/order | Device/DeviceFilesWindow | yes | core + UI | parity | hardware | NOT STARTED |
| Device delete/protected files | Device/tests | yes | core + UI | fault/negative | hardware | NOT STARTED |
| Device preferences/settings | PreferenceEditor/DeviceSettings | yes | qcm-config/core/UI | parity | hardware/AT | NOT STARTED |
| DeviceBand visual explanation | DeviceBand | yes | React | component | AT/zoom | NOT STARTED |
| HID live input | LiveInput | yes | HID adapter | captures/fakes | hardware modes | NOT STARTED |
| XInput limitation | LiveInput | yes limitation | capability UI | test | mode 3 hardware | NOT STARTED |
| Import review | ImportReviewWindow | yes | core/UI | E2E | AT | NOT STARTED |
| XLSX import/export | Xlsx/qsf/Drive | yes | qcm-config/cloud | fixtures | — | NOT STARTED |
| Community catalog | Community* | yes | core/native/UI | HTTP mock/privacy | offline smoke | NOT STARTED |
| Google auth | GoogleAuth/TokenStore | Win/mac yes | native | auth mock | OS secure-store | NOT STARTED |
| Drive backup/restore | DriveBackup/Client | Win/mac yes | core/cloud | HTTP/fake | account smoke | NOT STARTED |
| Share link | ShareSetup/DriveClient | yes where Google | core/UI | HTTP/UI | account smoke/AT | NOT STARTED |
| Theme | Theme/Style/Palette | yes | React tokens | component | contrast | NOT STARTED |
| UI scale preview rollback | SettingsView | yes | settings/UI | timers | zoom/AT | NOT STARTED |
| Reduced motion | Settings/animations | yes | UI | component | OS/manual | NOT STARTED |
| Localization all locales | Strings*.resx | yes | i18n | key/pseudo/RTL | manual language smoke | NOT STARTED |
| Tutorial | TutorialTour | yes | React | E2E | keyboard/AT | NOT STARTED |
| Crash rescue/report | CrashGuard/Report | yes | core/native/UI | fault/privacy | packaged crash smoke | NOT STARTED |
| Usage analytics consent | Telemetry/PRIVACY | yes | diagnostics | network/allowlist | privacy review | NOT STARTED |
| Feedback | Telemetry/UI | yes | diagnostics/UI | network | AT | NOT STARTED |
| Update check/install | UpdateCheck/release | yes | updater | mock/signature | packaged rollback | NOT STARTED |
| Agent-assisted editing | Agent* + agent/ + qsf | yes if current release feature | typed ops/tooling | corpus/eval | human usability | NOT STARTED |
| qsf CLI | tools/qsf | yes tooling contract | Rust qsf | golden JSON | — | NOT STARTED |
| Gallery/RenderPreview | Gallery/RenderPreview | tooling replacement | web gallery | visual tests | — | NOT STARTED |
| Windows Store | workflow | unresolved distribution | packaging | build | install | OPEN OQ-008 |
| Serial console | dependency/history | unresolved | optional port | spike | hardware | OPEN OQ-001 |
| Mobile direct hardware | none desktop parity | no desktop blocker | future plugin | spike | physical mobile | DEFERRED |