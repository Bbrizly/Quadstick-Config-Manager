# Settings window + first-run tutorial — implementation plan

> **For agentic workers:** execute task-by-task with a review gate after each
> (superpowers:subagent-driven-development). Steps are checkboxes.

**Goal:** Add a proper Settings window (General / Advanced / Help / Contact)
and a replayable, first-run coach-mark tutorial that walks a new user through
making and installing a profile on a template.

**Architecture:** Avalonia 11.1.3, .NET 8, C# code-behind, **no MVVM** — match
the existing imperative style. The Settings window and tutorial follow the
app's existing dialog pattern (`new Window { Content = … }; ShowDialog(this)`).
The tutorial is a full-window overlay appended to the root `Panel`. Interface
scaling is a single global zoom via `LayoutTransformControl`.

**Tech stack:** Avalonia controls only. URLs open with
`this.Launcher.LaunchUriAsync(new Uri(url))` (cross-platform, no Process.Start).

## Global constraints (bind every task)

- **Accessibility is the product** (mouth-operated controller, motor + low
  vision). Every interactive control: 48px min target (inherited from the base
  `Button`/`TextBox`/`ComboBox` styles — do not shrink), `AutomationProperties.Name`,
  keyboard reachable. Announce dynamic changes with a live region (Assertive
  for errors, Polite otherwise). No text below 14px. Theme colours only via
  `{DynamicResource *Brush}` / the `Size(key)` helper — never frozen brushes.
  Non-colour cues alongside colour.
- **No dead controls.** Every setting must change real behaviour. If a control
  can't be wired to something real, it doesn't ship.
- **Safety is not optional.** `Device.Install` always backs up; overwriting
  `default.csv` always confirms. These are NOT settings and must not become
  toggleable — recovery from a bad `default.csv` needs a physical force-erase.
- **Commit style:** plain capitalized-sentence titles, ≤2 simple body points,
  no `feat():` prefixes, no `Co-Authored-By` trailer.
- Reuse existing helpers: `Size(key)`, `StatusChip`, `BindBrush`,
  `NewFromTemplate()`, `SelectZoneForPreview(id)`, `SetModelForPreview(i)`,
  `ShowEditor/ShowHome/SetDeviceView`, `ModelNames`.
- Settings file: `%APPDATA%/QuadStickConfigManager/settings.json`.

---

## Task 1: Settings as a record

**Files:** Modify `src/QuadStick.App/Theme.cs`; migrate callers in
`src/QuadStick.App/MainWindow.axaml.cs` (lines ~60, 62, 182–189) and
`src/QuadStick.App/App.axaml.cs`. Test: `tests/QuadStick.App.Tests/` (add
`SettingsTests` cases).

**Interfaces produced:**
```csharp
public sealed class AppSettings
{
    public string Model = "FPS";
    public string Theme = "System";           // System | Light | Dark
    public int InterfaceScalePercent = 100;    // 100 | 125 | 150 | 200
    public bool ReduceMotion = false;
    public bool RememberWindow = true;
    public bool TutorialSeen = false;
    public double? WinW, WinH, WinX, WinY;     // null = use window defaults
}
public static AppSettings Load(string? path = null);   // defaults on miss/corrupt/old file
public static void Save(AppSettings s, string? path = null);  // whole-object write
```

- Replace the `(string model, string theme)` tuple API with `AppSettings`.
  `Load` reads each key with `node["key"]?.GetValue<T>() ?? default`, so an old
  `{model,theme}`-only file loads with defaults for the new keys. Keep the
  `try/catch → defaults` shape and `WriteRaw`.
- Migrate `LoadModel`/`SaveModel` and the `AppearancePicker` block to read a
  single `AppSettings` loaded once in the constructor into a field
  `_settings`, mutate it, and `Settings.Save(_settings)`. App.axaml.cs reads
  `Settings.Load().Theme`.
- **Tests:** round-trip all fields; empty/corrupt file → all defaults;
  old 2-key file → new keys default; write-then-read equality.

## Task 2: Global interface scaling + window bounds

**Files:** Modify `src/QuadStick.App/MainWindow.axaml` (root restructure),
`src/QuadStick.App/MainWindow.axaml.cs` (apply + lifecycle).

**Interfaces produced:** `void ApplyInterfaceScale(int pct)`;
private field `bool _reduceMotion` (set from settings, read by the tutorial).

- Restructure the root so the app content can be scaled and the tutorial
  overlay can sit un-scaled on top. Size the scaled box to `viewport/scale` in
  BOTH dimensions so, once scaled, it exactly equals the window — no outer
  ScrollViewer, and every existing fill/height DockPanel semantic is preserved
  (internal ScrollViewers still handle overflow):
```xml
<Panel x:Name="RootPanel">
  <LayoutTransformControl x:Name="ZoomHost"
                          HorizontalAlignment="Left" VerticalAlignment="Top">
    <Panel x:Name="ScaleContent">
      <!-- existing HomeView + EditorView move here unchanged -->
    </Panel>
  </LayoutTransformControl>
</Panel>
```
- `ApplyInterfaceScale(pct)`: `double s = pct/100.0;` store `_uiScale = s`;
  `ZoomHost.LayoutTransform = s == 1.0 ? null : new ScaleTransform(s, s);`
  call `UpdateScaleSize()`.
- `UpdateScaleSize()`: `if (RootPanel.Bounds is { Width: > 0, Height: > 0 } b)
  { ScaleContent.Width = b.Width / _uiScale; ScaleContent.Height = b.Height / _uiScale; }`
  — content lays out in a `window/scale` box, scaled up to fill the window.
  Subscribe to `RootPanel.GetObservable(Visual.BoundsProperty)` to re-run on resize.
- Apply `_settings.InterfaceScalePercent` and set `_reduceMotion =
  _settings.ReduceMotion` at startup (before/after `ShowHome()`).
- **Window bounds:** in the constructor, if `_settings.RememberWindow` and
  `WinW/WinH` present, set `Width/Height` (and `Position` from WinX/WinY). On
  `Closing`, if `RememberWindow`, store current `Width/Height/Position` into
  `_settings` and `Settings.Save(_settings)`. (The Advanced checkbox in Task 4
  flips `RememberWindow`.)
- **Verify:** `dotnet run --project tools/RenderPreview` mentally/at 100/150/200
  — text and layout grow together, nothing clipped. `dotnet test` stays green.

## Task 3: First-run tutorial tour

**Files:** Create `src/QuadStick.App/TutorialTour.cs` (`partial class
MainWindow`). Minor wiring in the constructor.

**Interfaces produced:** `public void StartTutorial();`

- Build the overlay once (lazy `EnsureTutorialOverlay()`) and append it as the
  **second child of `RootPanel`** (outside `ZoomHost`, so it stays at 100% and
  covers the viewport). Children, bottom→top:
  1. full-window transparent `Border` (input blocker — swallows clicks so the
     narrated tour drives, the user only presses Next/Skip),
  2. four dim `Border`s framing the current target's rect (leaves the target
     itself bright; a single full-window dim `Border` when the target is null),
  3. a 3px `AccentBrush` ring `Border` at the target rect,
  4. a callout card docked bottom-centre: step counter ("Step 2 of 7"), title
     (`Size("SubheadSize")`, bold), body (`Size("BodySize")`, wrapped),
     and **Back / Skip / Next** buttons (Next = `Classes="primary"`,
     `IsDefault`; Skip handles Esc via `IsCancel`). Callout is a live region
     (Polite); move focus to Next on each step.
- **Target geometry:** `target.TranslatePoint(new Point(0,0), RootPanel)` +
  `target.Bounds.Size` (TranslatePoint accounts for the zoom transform).
  Reposition after each step's Setup with
  `Dispatcher.UIThread.Post(PositionStep, DispatcherPriority.Loaded)` and on
  window `SizeChanged` while the tour is visible.
- **Steps** `(string Title, string Body, Action Setup, Func<Control?> Target)`:
  1. Welcome — Setup `ShowHome()`, Target `null`. "This shows you how to make
     and install a profile. You can skip anytime."
  2. Appearance — Target `AppearancePicker`. "Set light or dark to suit your eyes."
  3. New profile — Target `HomeNewButton`. "Every profile starts from a template."
  4. Your QuadStick — Setup `if (_file is null) NewFromTemplate(); SetDeviceView(true);`
     Target `DeviceViewButton`. "This is your QuadStick. Each part is a control you can map."
  5. Pick a part — Setup `SelectZoneForPreview("joystick");` Target
     `ZoneDetailPanel`. "Pick a part to see and change what it does."
  6. Save — Target `SaveButton`. "Save your work to a file." (explains only)
  7. Install — Target `InstallButton`. "When it's ready, send it to your QuadStick."
     (explains only)
  8. Done — Target `null`, Next reads "Finish". "You can replay this anytime
     from Settings ▸ Help."
- **Reduce motion:** when `!_reduceMotion`, give the callout a 150ms Opacity
  `DoubleTransition` (fade in per step); when `_reduceMotion`, no transitions
  (instant). This is the toggle's observable effect.
- **Trigger / teardown:** in the constructor, after `ShowHome()`,
  `if (!_settings.TutorialSeen) Opened += StartOnce;` (unsubscribe after firing).
  Finishing **or** skipping: `_settings.TutorialSeen = true;
  Settings.Save(_settings);` hide+teardown overlay; `_file = null; ShowHome();`
  (discard the scratch template so no unsaved file lingers). `StartTutorial()`
  is also the replay entry (Task 4).
- **Keyboard:** Enter = Next, Esc = Skip, Tab cycles Back/Skip/Next; focus
  stays inside the overlay while visible.

## Task 4: Settings window + entry point

**Files:** Create `src/QuadStick.App/SettingsWindow.cs`; modify
`src/QuadStick.App/MainWindow.axaml` (add a `SettingsButton` to the Home
header) and `MainWindow.axaml.cs` (wire it; expose helpers; extract
`HelpSections()`).

- **Extract** the `sections` array in `ShowHelp()` into
  `static (string Title, string Body)[] HelpSections()` and have both
  `ShowHelp()` and the Help tab use it (DRY).
- **Expose on MainWindow** for the window to call:
  `public AppSettings CurrentSettings => _settings;`
  `public void ApplyTheme(string choice)` (Theme.Apply + sync `AppearancePicker`
  + persist), `ApplyInterfaceScale(int)` (Task 2, also persist),
  `public void SetReduceMotion(bool)` (`_reduceMotion = v` + persist),
  `public void SetDefaultModel(string)`, `public void StartTutorial()` (Task 3).
- **`SettingsButton`** ("⚙ Settings") in the Home header row next to Appearance,
  `AutomationProperties.Name="Open Settings"`; click →
  `new SettingsWindow(this).ShowDialog(this)`.
- **`SettingsWindow(MainWindow owner)`** — a `Window` (Title "Settings",
  ~640×640, CenterOwner) whose content is a `TabControl` with four `TabItem`s.
  Every control has `AutomationProperties.Name`, applies live, and persists.
  - **General:** Appearance ComboBox (System/Light/Dark → `owner.ApplyTheme`);
    Interface size ComboBox (100/125/150/200% → `owner.ApplyInterfaceScale`);
    Default model ComboBox (`ModelNames` → `owner.SetDefaultModel`).
  - **Advanced:** Reduce motion CheckBox (`owner.SetReduceMotion`); Remember
    window size & position CheckBox (flips `RememberWindow` + persist); "Show
    the tutorial next time I open the app" CheckBox (checked ⇒
    `TutorialSeen=false`); "Open settings folder" button
    (`Launcher.LaunchUriAsync(new Uri(dir))`); "Reset all settings to defaults"
    button (confirm → write `new AppSettings()` → apply theme+scale+motion).
  - **Help:** a "Replay tutorial" primary button at top (closes the window, then
    `owner.StartTutorial()`), followed by `HelpSections()` in a ScrollViewer.
  - **Contact:** buttons opening via `Launcher` — "Report a bug on GitHub"
    (`https://github.com/Bbrizly/Quadstick-Config-Manager/issues`),
    "Website" (`https://bbrizly.github.io`),
    "LinkedIn" (`https://www.linkedin.com/in/bassam-k/`),
    "Email" (`mailto:bassamkamal.py@gmail.com`).
- **Verify:** `dotnet test` green; open Settings, change each control, confirm
  live effect + persistence across restart; keyboard-only reachable.

---

## Notes / deliberate scope decisions

- Global zoom (not per-token font/spacing scaling): the `Space*` tokens are
  unreferenced and some code-behind font sizes are literals, so token scaling
  would grow text without its containers. One zoom scales everything together.
- Backup / default.csv confirm are not settings (safety, see constraints).
- Appearance lives both on the Home header (quick) and in Settings ▸ General;
  both call the same `ApplyTheme`, so there is one source of truth.
