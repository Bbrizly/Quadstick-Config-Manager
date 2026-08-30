# Current system inventory

`CURRENT FACT` — baseline `f7783944`.

## Solution projects

| Project | Role | Migration disposition |
|---|---|---|
| `src/QuadStick.Format` | CSV parser/model/editor/validator/templates **plus mounted-device filesystem operations** | Split: pure semantics → `qcm-config`; device I/O → native adapter/core use case |
| `src/QuadStick.App` | Avalonia shell, UI, orchestration, HID live input, Google backup, settings, crash/update/telemetry | Replace incrementally with React + Rust/Tauri |
| `tests/QuadStick.Format.Tests` | format, device agreement, firmware oracle, backup/client/edit/install tests | Retain as C# oracle during migration; port/add Rust tests |
| `tests/QuadStick.App.Tests` | UI/behavior/accessibility-ish characterization | Use as parity evidence; replace by React/component/E2E tests |
| `tools/qsf` | JSON-in/out profile inspection/validation/edit/diff for agents | Reimplement as Rust CLI using `qcm-config`; keep contract |
| `tools/RenderPreview` | build/design preview tooling | Replace with web component visual test/gallery workflow or retire after evidence |

## App root hotspots

- `MainWindow.axaml.cs` (~317 KB): primary orchestration + editor state + many UI behaviors.
- `MainWindow.axaml`: main shell/view structure.
- `InstallFlow.cs`: candidate selection, destructive confirmations, background `Device.Install`, accessible progress/receipt.
- `DeviceFilesWindow.cs`: mounted-device library operations.
- `DeviceSettingsPage.cs`, `PreferenceEditor.cs`: preference/device settings UX.
- `ModesWindow.cs`: profile mode management.
- `ImportReviewWindow.cs`: import review/reconciliation.
- `LiveInput.cs`: HID descriptor-driven standard gamepad input reader.
- `DriveBackup.cs`, `DriveClient.cs`, `DrivePickerWindow.cs`, `GoogleAuth.cs`, `TokenStore.cs`, `ShareSetupWindow.cs`: Google backup/share stack.
- `CrashGuard.cs`, `CrashReport.cs`: local rescue/crash behavior.
- `Telemetry.cs`: consent-gated PostHog telemetry with strict allowlist/scrubbing.
- `CommunityCatalog.cs`, `CommunityProfilesView.cs`: community profile workflow.
- `AgentBridge.cs`, `AgentGuide.cs`, `AgentWindow.cs`, `AgentFeature.cs`: agent-assisted profile flow.
- `Localization.cs` + `Strings*.resx`: runtime localization.
- `Theme.cs`, `Style.cs`, `Palette.cs`, `App.axaml`: design system/theme behavior.
- `TutorialTour.cs`: onboarding/tutorial.
- `UpdateCheck.cs`: update discovery flow.

## Format/core hotspots

- `Csv.cs`: custom RFC-ish CSV read/write; writer emits CRLF.
- `Parser.cs`: firmware-aware section/binding parsing, warnings and hard limits.
- `ProfileFile.cs`: raw grid, mutations, undo/dirty/revision, serialization/normalization, action names.
- `Model.cs`: parsed document/sheet/binding/issues.
- `Device.cs`: mounted drive discovery/install/delete/list/backup + LED file patterns.
- `PreferenceCatalog.cs`, embedded validation CSVs: preference metadata.
- `ModeLights.cs`: mode/file LED semantics.
- templates + default prefs embedded as resources.

## Current external/native dependencies

Read out of `QuadStick.App.csproj` rather than remembered, 2026-08-30:

Avalonia 11.1.3 (`Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`); HidSharp 2.6.4; System.Security.Cryptography.ProtectedData 8.0.0; PostHog 2.12.0. `QuadStick.Format` has no package reference at all. Tests add xunit and `Avalonia.Headless.XUnit`; `tools/RenderPreview` adds `Avalonia.Headless`.

**Corrected 2026-08-30 (TASK-029).** This line previously listed CommunityToolkit.Mvvm, CsvHelper, System.IO.Ports, System.Management and Microsoft.Extensions, and gave HidSharp as 2.1.0. None of those five packages is a dependency, and `git log --all -p` over the project files shows none of them ever was. The note was wrong, not out of date. `System.IO.Ports` was the stated premise of OQ-001, so check a dependency claim against the project file before building on it.

**Important:** dependency presence does not prove runtime use, and neither does a document saying a dependency is present. Serial parity remains evidence-gated; see the OQ-001 resolution in `51-open-questions.md`.

## Current release surface

`.github/workflows/build.yml` runs `dotnet test QuadStick.sln`, suppresses telemetry, injects release-only Google/PostHog secrets, builds self-contained Windows x64, macOS arm64/x64 and Linux x64 artifacts on `v*` tags, then publishes a GitHub Release. Windows Store has a separate workflow.

## Documentation caveat

README references some architecture/device docs that are absent at this audited HEAD. `docs/FORMAT.md` is substantial and source-backed; README architecture prose is treated as secondary evidence.