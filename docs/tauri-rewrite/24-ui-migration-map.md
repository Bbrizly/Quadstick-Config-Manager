# UI migration map

This is behavior mapping, not an instruction to clone Avalonia layout pixel-for-pixel.

| Current surface | Evidence | Target | Key parity |
|---|---|---|---|
| Main home/shell | `MainWindow.axaml/.cs` | `AppShell`, `HomePage` | keyboard nav, status, open/new/import/community/Drive entry points |
| Device-centered editor | MainWindow code-behind | `EditorPage` + `DeviceVisualizer` + `BindingEditor` | same profile mutation semantics; selected zone/mode; friendly/raw/Xbox label vocab |
| Raw List View | MainWindow | `RawGridView` | exposes canonical raw grid projection; edits through typed ops |
| Rail/list device view | MainWindow | `ModeRail` / alternate editor presentation | presentation only; same selected binding/state |
| Modes window | `ModesWindow.cs` | integrated `ModesPanel` | add/rename/reorder/delete behavior, numbering |
| Issues | Parser/Validator + MainWindow | `IssuesPanel` | cell identity, severity, fix guidance; focus target |
| Install dialog | `InstallFlow.cs` | `InstallDialog` | validate → choose device → special confirm → progress → receipt; focus restoration |
| Device files | `DeviceFilesWindow.cs` | `DeviceLibraryPage/Sheet` | list/protected files/order/delete/import/open; generation safety |
| Device settings | `DeviceSettingsPage.cs`, `PreferenceEditor.cs` | `DeviceSettingsPage` | embedded preference metadata/defaults and exact profile mutations |
| Device visual band | `DeviceBand.cs` | `DeviceSettingsVisualizer` | highlighted parts, dead/full-deflection rings, live joystick, textual equivalent |
| Import review | `ImportReviewWindow.cs` | `ImportReviewDialog/Page` | skipped tabs, repairs, issues, safe acceptance |
| Community profiles | `CommunityProfilesView.cs` | `CommunityPage` | catalog fetch/search/open; untrusted input validation |
| Google picker | `DrivePickerWindow.cs` | `CloudProfilesDialog` | connected gate, cherry-pick/precheck, conflict behavior |
| Share setup | `ShareSetupWindow.cs` | `ShareDialog` | link identity, permission warning/confirmation |
| Settings | `SettingsView.cs` | `SettingsPage` | General/Advanced/Help/Contact; live theme; scale preview rollback; consent |
| Tutorial | `TutorialTour.cs` | `TutorialOverlay/Flow` | dismissibility, focus, keyboard/AT semantics |
| Agent | `AgentWindow/Guide` | `AgentPanel` | constrained operations; questions do not block whole app; human approval/validation |
| Gallery | `GalleryWindow` | browser component gallery | tooling only, not production parity |

## Centered QuadStick target layout

The rewrite may adopt the intended minimal structure:

```text
Top bar: profile/title | share/status
Left rail: modes + configuration/library actions
Center: QuadStick SVG/photo visualizer
Around/inside mapped zones: current outputs/actions
Context panel/popover: edit selected binding
```

But implement it **after** editor parity primitives exist. The visualizer must be a view over `EditorSnapshot`; it cannot become a second editing model.

## View vocabulary

Preserve current label-style concept:
- raw token vocabulary;
- plain English/friendly labels;
- controller/Xbox-style labels where applicable.

Make label mode a presentation preference; serialized output tokens remain canonical.

## Focus migration

For every old modal/page, characterize:
- initial focus;
- Escape/cancel;
- default action;
- return focus;
- live-region announcements.

Current install explicitly returns focus to Install; scale countdown is assertive; live device text intentionally is not auto-announced. Preserve intent.