# Old → new traceability

This document answers: **where does every major current responsibility go?** The ledger has status; this file explains the split.

| Current source | Current responsibility | Target | Notes |
|---|---|---|---|
| `QuadStick.Format/Csv.cs` | CSV parse/write | `crates/qcm-config/src/csv.rs` | Port behavior first, not a third-party CSV parser swap |
| `Parser.cs` | firmware-aware document projection/issues | `qcm-config::parser` | Differential + firmware oracle tests mandatory |
| `Validator.cs` | semantic validation | `qcm-config::validator` | Preserve issue severity/cell/kind semantics where user-visible |
| `ProfileFile.cs` | raw grid, edit ops, undo, normalization, serialize | `qcm-config::profile`; session wrapper in `qcm-core` | Rust canonical editor state |
| `Model.cs` | document/sheet/binding/issues | `qcm-config::model` | Serde DTO projection separated from internal types if useful |
| `Vocab.cs` + embedded data | legal/legacy names | `qcm-config::vocab` + embedded assets | Generate/verify against existing sources |
| `PreferenceCatalog.cs` + data | preference metadata/defaults | `qcm-config::preferences` | UI receives typed catalog DTO |
| `ModeLights.cs` | audited LED patterns | `qcm-config::mode_lights` | Keep 1–32 audit guard |
| `Device.cs` format-independent filename rules | target filename protection | `qcm-core::library` / `qcm-config::filename` | Pure rules split from OS I/O |
| `Device.cs` drive enumeration | mounted device discovery | `src-tauri/adapters/storage/*` implementing `DeviceStoragePort` | Never expose raw generic filesystem command |
| `Device.cs` install/delete/list/backup | storage transactions/library | `qcm-core::device_storage` orchestrating native storage port | Preserve exact safety sequence |
| `MainWindow.axaml.cs` profile ownership | active editor session | `qcm-core::profiles::ProfileSessionManager` | UI receives session ID/snapshot |
| `MainWindow.axaml.cs` presentation | editor/home/device shell | React features/components | Do not translate code-behind method-for-method |
| `InstallFlow.cs` | install UX + validation/confirmation | React install flow + `qcm-core::install` | Core issues typed confirmation requirement |
| `DeviceFilesWindow.cs` | device library UX | React `features/device-library` + core library service | Core owns ordering/protected-file rules |
| `ModesWindow.cs` | mode management UX | React `features/editor/modes` | Mutations remain Rust ops |
| `PreferenceEditor.cs`, `DeviceSettingsPage.cs` | settings CSV UX | React `features/device-settings` | Preference catalog from Rust |
| `DeviceBand.cs` | visual/live settings explainer | React accessible SVG/component | Must retain textual equivalent; visual cannot be sole meaning |
| `LiveInput.cs` | HID discovery/report-descriptor parsing/read loop | native HID adapter + core live service | Dedicated blocking worker + Tauri Channel |
| `DriveBackup.cs` | backup policy/conflicts/links | `qcm-core::backup` | Pure policy tested with fake cloud/storage |
| `DriveClient.cs` | Google REST implementation | `src-tauri/adapters/cloud/google.rs` | Native HTTP only |
| `GoogleAuth.cs` | PKCE/system browser/loopback tokens | native Google auth adapter | No WebView OAuth |
| `TokenStore.cs` | Keychain/DPAPI secure store | native `SecureStorePort` implementation | Linux parity remains explicit decision |
| `CommunityCatalog.cs` | community manifest/download logic | core community service + native HTTP port | Treat downloaded profiles as untrusted input |
| `ImportReviewWindow.cs` | XLSX/import review and repair UX | `qcm-config::xlsx/import` + React review | Preserve skipped-tab/limitation reporting |
| `AgentBridge/Guide/Window/Feature` | constrained AI-assisted editing | React agent UI + core typed editor operations | Agent never gets arbitrary native power |
| `tools/qsf` | safe machine-readable profile operations | `crates/qsf` or bin target using `qcm-config` | Preserve JSON contract where practical |
| `SettingsView.cs` | app setting UI | React settings | persisted state via core settings API |
| `Localization.cs`, `Strings*.resx` | i18n | frontend message catalogs + Rust stable error codes | Do not make Rust user strings a translation bottleneck |
| `Theme/Style/Palette/App.axaml` | design system | CSS variables/tokens + component primitives | parity then redesign |
| `CrashGuard/CrashReport` | rescue/log/report | core recovery + native diagnostics | local rescue must survive frontend failure |
| `Telemetry.cs` | consent-gated PostHog | core diagnostics policy + native HTTP | retain allowlist/redaction/CI-off |
| `UpdateCheck.cs` | update discovery | updater service/adapter | signed Tauri update strategy after ADR |
| `TutorialTour.cs` | onboarding/tutorial | React tutorial feature | keyboard/AT equivalent |
| `GalleryWindow`, `RenderPreview` | design preview tooling | browser component gallery/visual snapshots | product parity E; tooling replacement recommended |

## Rule for split files

When a C# file mixes pure and impure work, do not put the whole file in `src-tauri`. Extract pure rules into `qcm-config`/`qcm-core`; adapters implement only OS/network/hardware effects.

## Rule for UI rewrite

A visual Avalonia control maps to a React feature only after its **behavioral contract** is written. Layout can change. Semantics, destructive confirmations, accessibility, source-of-truth and data-safety behavior cannot silently change.