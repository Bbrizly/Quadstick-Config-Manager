# Porting ledger

Statuses: UNASSESSED → ASSESSED → CONTRACTED → IMPLEMENTING → PORTED → PARITY-TESTED → HARDWARE-VERIFIED → RETIRED.

| Current path/symbol | Responsibility | Target | Action | Tests | Phase | Status |
|---|---|---|---|---|---:|---|
| `QuadStick.Format/Csv.cs` | raw CSV | qcm-config/csv.rs | REWRITE | golden/property | 2 | CONTRACTED |
| `Parser.cs` | firmware parser | qcm-config/parser.rs | REWRITE | oracle/fuzz | 2 | CONTRACTED |
| `Validator.cs` | validation | qcm-config/validator.rs | REWRITE | issue parity | 2 | CONTRACTED |
| `Model.cs` | parsed types | qcm-config/model.rs | REWRITE | canonical snapshots | 2 | CONTRACTED |
| `ProfileFile.cs` | raw grid/editor/undo/normalize | qcm-config/profile.rs + core session | SPLIT/REWRITE | mutation parity | 2/3 | CONTRACTED |
| `Vocab.cs` | vocab | qcm-config/vocab.rs | REWRITE/generated | set/count parity | 2 | CONTRACTED |
| `PreferenceCatalog.cs` | preference metadata | qcm-config/preferences.rs | REWRITE | catalog parity | 2 | CONTRACTED |
| `FunctionParameters.cs` | function metadata | qcm-config | REWRITE | parser/validator | 2 | CONTRACTED |
| `ModeLights.cs` | LED patterns | qcm-config | REWRITE | 1–32 exact | 2 | CONTRACTED |
| `Data/**`, Templates | embedded contract data | qcm-config assets | REUSE/GENERATE | hash/parity | 2 | CONTRACTED |
| `Device.cs` discovery | mount detection | native storage adapter | SPLIT/REWRITE | fake+hardware | 3 | CONTRACTED |
| `Device.cs` filename rules | safe target names | qcm-config/core | SPLIT | unit parity | 2/3 | CONTRACTED |
| `Device.cs` install | safe transaction | qcm-core/storage | REWRITE | fault+hardware | 3 | CONTRACTED |
| `Device.cs` library/delete | device files | qcm-core/storage | REWRITE | parity+hardware | 3 | CONTRACTED |
| `MainWindow.axaml` | shell/layout | React AppShell/Editor | REWRITE | component/E2E/AT | 5/6 | CONTRACTED |
| `MainWindow.axaml.cs` | orchestration/editor/UI | qcm-core + React features | SPLIT | behavior matrix | 3–7 | CONTRACTED |
| `RowControls.cs` | row editing controls | React editor | REWRITE | component | 6 | ASSESSED |
| `OutputCatalog.cs` | display output labels | qcm-config/UI catalog | SPLIT | label tests | 2/6 | ASSESSED |
| `CustomNames.cs` | custom naming | qcm-config/UI | SPLIT | parity | 2/6 | ASSESSED |
| `ModesWindow.cs` | modes UX | React ModesPanel | REWRITE | E2E/AT | 6 | CONTRACTED |
| `InstallFlow.cs` | install UX | core plan + React dialog | SPLIT | E2E/fault/AT | 3/6 | CONTRACTED |
| `DeviceFilesWindow.cs` | library UX | React DeviceLibrary | REWRITE | E2E/AT | 6 | CONTRACTED |
| `DeviceSettingsPage.cs` | prefs page | React device settings | REWRITE | E2E/AT | 6 | CONTRACTED |
| `PreferenceEditor.cs` | prefs controls | React + qcm-config | SPLIT | parity | 6 | CONTRACTED |
| `DeviceBand.cs` | visual/live explanation | React SVG/text component | REWRITE | component/AT | 6 | CONTRACTED |
| `LiveInput.cs` | HID | native HID + live manager | REWRITE | captures+hardware | 3 | CONTRACTED |
| `ImportReviewWindow.cs` | import review | React import + core | REWRITE | E2E/AT | 6 | CONTRACTED |
| `CommunityCatalog.cs` | community data | qcm-core/native HTTP | SPLIT | HTTP/privacy | 6 | CONTRACTED |
| `CommunityProfilesView.cs` | community UI | React CommunityPage | REWRITE | E2E/AT | 6 | CONTRACTED |
| `DriveBackup.cs` | backup policy | qcm-core/backup | REWRITE | fake cloud | 6 | CONTRACTED |
| `DriveClient.cs` | Google REST | native Google adapter | REWRITE | HTTP snapshots | 6 | CONTRACTED |
| `DrivePickerWindow.cs` | cloud picker | React cloud dialog | REWRITE | component/E2E | 6 | ASSESSED |
| `ShareSetupWindow.cs` | sharing UI | React ShareDialog | REWRITE | E2E/AT | 6 | ASSESSED |
| `GoogleAuth.cs` | OAuth PKCE | native auth adapter | REWRITE | auth mock/OS | 6 | CONTRACTED |
| `GoogleClient.cs` | client config/helper | native Google adapter | MERGE | HTTP | 6 | ASSESSED |
| `TokenStore.cs` | secure token | native secure store | REWRITE | OS integration | 6 | CONTRACTED |
| `SettingsView.cs` | settings UI | React SettingsPage | REWRITE | component/AT | 5/6 | CONTRACTED |
| `Theme.cs::{AppSettings,DriveLink,Settings,SettingsJsonContext}` | atomic persisted settings + migration schema | qcm-core/native settings | REWRITE/COMPAT-READ | migration/consent/link tests | 5/9 | CONTRACTED |
| `Localization.cs`, `Plural.cs` | locale runtime | frontend i18n | REWRITE | pseudo/RTL | 5 | ASSESSED |
| `Strings*.resx` | translations | generated frontend catalogs | CONVERT | key/placeholder | 5 | CONTRACTED |
| `Theme.cs`, `Style.cs`, `Palette.cs`, `Icons.axaml` | design system | CSS/components/assets | REWRITE | visual/contrast | 5 | ASSESSED |
| `TutorialTour.cs` | onboarding | React tutorial | REWRITE | E2E/AT | 6 | ASSESSED |
| `CrashGuard.cs`, `CrashReport.cs` | rescue/report | core/native + React | SPLIT/REWRITE | crash/privacy | 6 | CONTRACTED |
| `Telemetry.cs`, `TelemetryToken.cs` | analytics/feedback | diagnostics service | REWRITE | network/allowlist | 6 | CONTRACTED |
| `UpdateCheck.cs` | updater | native updater | REWRITE | signature/rollback | 6/8 | CONTRACTED |
| `AgentBridge.cs` | agent integration | core typed ops | REWRITE | corpus/eval | 6 | ASSESSED |
| `AgentFeature.cs` | feature gating | core/settings/UI | REWRITE | feature tests | 6 | ASSESSED |
| `AgentGuide.cs`, `AgentWindow.cs` | agent UX | React AgentPanel | REWRITE | E2E/AT | 6 | ASSESSED |
| `agent/**` | corpus/eval/model pipeline | retain/adapt | RETAIN-TEMPORARILY | old/new qsf eval | 0/6 | ASSESSED |
| `tools/qsf` | machine profile tool | Rust qsf | REWRITE | JSON parity | 2 | CONTRACTED |
| `GalleryWindow.cs` | component gallery | web gallery | REPLACE/RETIRE | visual tooling | 7 | ASSESSED |
| `tools/RenderPreview` | preview tooling | web snapshots | REPLACE/RETIRE | visual | 7 | ASSESSED |
| `Program.cs` | Avalonia startup/mac workaround | src-tauri startup | REWRITE/E parts | packaged smoke | 4/8 | ASSESSED |
| `App.axaml/.cs` | bootstrap/theme/window | Tauri + React bootstrap | REWRITE | startup | 4/5 | ASSESSED |
| `.github/workflows/build.yml` | CI/release | mixed then Tauri workflows | MODIFY INCREMENTALLY | CI/release | all | ASSESSED |
| Store workflow | Windows Store | Tauri Store TBD | D | OQ-008 | 8 | ASSESSED |
| Makefile | dev/release UX | mixed façade | MODIFY | command smoke | all | ASSESSED |
| `PRIVACY.md` | public data contract | preserve/update truthfully | RETAIN | network privacy tests | 6/8 | CONTRACTED |

**Gate 0:** no rows remain `UNASSESSED`. Files added to legacy `main` after the frozen SHA must be added here before parity can be claimed.
