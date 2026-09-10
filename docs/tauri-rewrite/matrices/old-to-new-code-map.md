# Old → new code map

| Legacy group | Target group | Migration unit |
|---|---|---|
| `QuadStick.Format/{Csv,Parser,Validator,Model,ProfileFile}` | `crates/qcm-config/src/*` | direct behavior port + oracle |
| `QuadStick.Format/{Vocab,PreferenceCatalog,FunctionParameters,ModeLights,Data,Templates}` | qcm-config modules/assets | generated/copied with hash tests |
| `QuadStick.Format/Device.cs` | qcm-config filename rules + qcm-core transaction + native storage adapter | split, never copy whole file |
| `MainWindow.axaml/.cs` | React app/features + qcm-core sessions/use cases | decompose by behavior IDs |
| `RowControls`, `OutputCatalog`, `CustomNames` | React editor/catalog + qcm-config typed rules | separate display from persistence |
| `ModesWindow` | React modes panel + EditorOps | parity slice |
| `InstallFlow` | core prepare/commit + React dialog | parity slice |
| `DeviceFilesWindow` | core library + React library | parity slice |
| `DeviceSettingsPage`, `PreferenceEditor`, `DeviceBand` | device-settings feature + core/qcm-config | parity slice |
| `LiveInput` | native HID + LiveInputManager + Channel hook | hardware slice |
| `ImportReviewWindow` | import service + React review | parity slice |
| `CommunityCatalog`, `CommunityProfilesView` | community service/native HTTP + React | network slice |
| `DriveBackup`, `DriveClient`, `DrivePickerWindow`, `ShareSetupWindow` | backup policy + Google adapter + React | cloud slice |
| `GoogleAuth`, `TokenStore` | native auth/secure-store | security slice |
| `Localization`, `Strings*.resx`, `Plural` | frontend i18n catalogs/runtime | generated conversion |
| `Theme`, `Style`, `Palette`, `Icons.axaml` | CSS tokens/components/assets | visual foundation |
| `CrashGuard`, `CrashReport` | Rust recovery + React report UI | diagnostics slice |
| `Telemetry`, `TelemetryToken`, `PRIVACY.md` | diagnostics privacy policy + native HTTP | privacy slice |
| `UpdateCheck` | native updater service | release slice |
| `AgentBridge`, `AgentFeature`, `AgentGuide`, `AgentWindow` | typed agent feature + Rust qsf/core | agent slice |
| `agent/**` Python corpus/eval/finalize | retain then adapt to Rust qsf; replace only with proof | tooling/eval slice |
| `tools/qsf` | Rust qsf | parity CLI |
| `GalleryWindow`, `tools/RenderPreview` | browser component gallery/snapshot tools | E/tooling |
| `.github/workflows`, Makefile, scripts | mixed legacy+new CI then Tauri release | incremental release slice |