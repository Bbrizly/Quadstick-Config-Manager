# Accessibility + Premium Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make QuadStick Config Manager genuinely accessible (keyboard + screen reader, low vision, limited motor) and look like a finished premium product, without an architecture rewrite.

**Architecture:** A single source-of-truth palette in C# (`Palette.cs`) feeds theme-scoped Avalonia resources registered in `App.axaml.cs`. Styles and code-behind consume `{DynamicResource}` tokens instead of hardcoded brushes, so OS/manual theme switches repaint live. A runnable contrast test gates the palette. UI stays code-behind; we extract small helpers, we do not restructure to MVVM. Install becomes a staged, announced dialog over the existing `Device.Install` logic.

**Tech Stack:** Avalonia 11.1.3, .NET 8, Fluent theme, Inter font, xUnit (`QuadStick.Format.Tests`).

**Reviewed by Codex** (assembly-verified against the Avalonia 11.1.3 NuGet package). Corrections folded in: no concrete-brush `Brush()` helper (freezes on theme switch) — everything themed via `BindBrush`/DynamicResource or a style class; binding indexer is `!` not `~`; `Application.Resources` uses a `ResourceDictionary` + `MergedDictionaries` with an `avares://QuadStickConfigManager/...` include; `BoxShadow` targets the `ContentPresenter`; `ComboBox`/`AutoCompleteBox` use `:focus-within`; the `SurfaceBorder/Surface` contrast pair is dropped (decorative, not a WCAG target); and the install phase is split into a `Device.Install` cleanup hardening (12a) plus a simpler confirm/progress/receipt dialog (12b) with honest failure wording.

## Global Constraints

- Target framework `net8.0`; Avalonia `11.1.3`. No new NuGet packages.
- No emoji in UI. Icons are `StreamGeometry` via `PathIcon`.
- No animation beyond default control transitions. No gradients/glass.
- Nothing below 14px text. Interactive targets ≥48px where practical.
- Every token pair meets WCAG AA in both themes (text ≥4.5:1, large/UI ≥3:1).
- Screen-reader live announcements: Assertive only for blocking states.
- `QuadStick.Format.Tests` must stay green; format library is not modified.
- Copy stays in the app's plain-language voice (see existing Help text).

---

## File Structure

**New files**
- `src/QuadStick.App/Palette.cs` — the single source-of-truth color maps (light/dark) + a `Contrast` helper (relative luminance + ratio). Referenced by both the app and the test.
- `src/QuadStick.App/Theme.cs` — builds theme-scoped `ResourceDictionary` from `Palette`, applies a saved theme choice, and merges theme into settings.
- `src/QuadStick.App/Icons.axaml` — `StreamGeometry` resource dictionary (merged in `App.axaml`).
- `src/QuadStick.App/InstallFlow.cs` — the staged install dialog (partial of `MainWindow` or a small owned window) over `Device.*`.
- `tests/QuadStick.Format.Tests/PaletteContrastTests.cs` — the contrast gate.
- `tests/QuadStick.Format.Tests/SettingsTests.cs` — settings merge round-trip.

Note: `Palette.cs` and `SettingsRecord` live in `QuadStick.App`, but the tests are in `QuadStick.Format.Tests`. Add a `ProjectReference` from the test project to `QuadStick.App` (Task 1, step 0) so tests can see them. `QuadStick.App` is `WinExe`; referencing it from a test project is fine for type access.

**Modified files**
- `src/QuadStick.App/App.axaml` — `RequestedThemeVariant="Default"`, merge `Icons.axaml`, rewrite styles to `{DynamicResource}` tokens, add spacing/type resources, focus-visible styles, 48px targets.
- `src/QuadStick.App/App.axaml.cs` — register theme resources, apply saved theme on startup.
- `src/QuadStick.App/MainWindow.axaml` — appearance toggle on Home, editor chrome from one clipping `StackPanel` to a stable two-row/Grid layout, status line becomes a chip host.
- `src/QuadStick.App/MainWindow.axaml.cs` — replace inline brushes with token lookups/classes; status-chip helper; live-region calibration; keyboard focus management; "Fix first problem"; device zones as `ToggleButton`s with richer semantics; target/text-size fixes; call the staged install flow.
- `tools/RenderPreview/Program.cs` — render both light and dark.

---

## Phase 1 — Foundations

### Task 1: Palette + contrast gate (TDD)

**Files:**
- Create: `src/QuadStick.App/Palette.cs`
- Create: `tests/QuadStick.Format.Tests/PaletteContrastTests.cs`
- Modify: `tests/QuadStick.Format.Tests/QuadStick.Format.Tests.csproj` (add ProjectReference)

**Interfaces:**
- Produces: `QuadStick.App.Palette.Light` and `.Dark` (`IReadOnlyDictionary<string,string>` token→hex); `QuadStick.App.Palette.Pairs` (`IReadOnlyList<(string fg, string bg, double minRatio)>`); `QuadStick.App.Contrast.Ratio(string hexA, string hexB) → double`.

- [ ] **Step 0: Let the test project see app types**

Add to `tests/QuadStick.Format.Tests/QuadStick.Format.Tests.csproj` inside the existing `<ItemGroup>` with the other `ProjectReference`:

```xml
<ProjectReference Include="../../src/QuadStick.App/QuadStick.App.csproj" />
```

- [ ] **Step 1: Write the failing contrast test**

```csharp
// tests/QuadStick.Format.Tests/PaletteContrastTests.cs
using QuadStick.App;
using Xunit;

public class PaletteContrastTests
{
    [Fact]
    public void KnownRatio_BlackOnWhite_Is21()
        => Assert.True(Math.Abs(Contrast.Ratio("#000000", "#FFFFFF") - 21.0) < 0.05);

    [Theory]
    [MemberData(nameof(Themes))]
    public void EveryTokenPair_MeetsAA(string theme, string fgKey, string bgKey, double min)
    {
        var map = theme == "light" ? Palette.Light : Palette.Dark;
        var ratio = Contrast.Ratio(map[fgKey], map[bgKey]);
        Assert.True(ratio >= min,
            $"{theme}: {fgKey} on {bgKey} = {ratio:F2}, need {min}");
    }

    public static IEnumerable<object[]> Themes()
    {
        foreach (var theme in new[] { "light", "dark" })
            foreach (var (fg, bg, min) in Palette.Pairs)
                yield return new object[] { theme, fg, bg, min };
    }
}
```

- [ ] **Step 2: Run it, verify it fails to compile (Palette/Contrast missing)**

Run: `dotnet test tests/QuadStick.Format.Tests --filter PaletteContrastTests`
Expected: build error, `Palette`/`Contrast` not found.

- [ ] **Step 3: Write `Palette.cs`**

```csharp
// src/QuadStick.App/Palette.cs
namespace QuadStick.App;

public static class Palette
{
    // Single source of truth. Keys match the DynamicResource names used in
    // App.axaml.cs (Theme.Build) and in styles.
    public static readonly IReadOnlyDictionary<string, string> Light = new Dictionary<string, string>
    {
        ["AppBackground"] = "#F6F5F2",
        ["Surface"]       = "#FFFFFF",
        ["SurfaceSubtle"] = "#FBFAF8",
        ["SurfaceBorder"] = "#D8D6D2",
        ["TextPrimary"]   = "#1F1F1F",
        ["TextSecondary"] = "#565656",
        ["Accent"]        = "#0F6CBD",
        ["AccentText"]    = "#0B5CA3",
        ["OnAccent"]      = "#FFFFFF",
        ["Error"]         = "#B3261E",
        ["Success"]       = "#146C2E",
        ["Warning"]       = "#8A5000",
        ["Focus"]         = "#1348A6",
        ["OutputTint"]    = "#FBF3D6",
        ["FunctionTint"]  = "#F9E1E8",
        ["InputTint"]     = "#DCEBFB",
    };

    public static readonly IReadOnlyDictionary<string, string> Dark = new Dictionary<string, string>
    {
        ["AppBackground"] = "#1B1B1A",
        ["Surface"]       = "#262625",
        ["SurfaceSubtle"] = "#2E2E2C",
        ["SurfaceBorder"] = "#43423F",
        ["TextPrimary"]   = "#F2F1EE",
        ["TextSecondary"] = "#BCBAB5",
        ["Accent"]        = "#4CA0EA",
        ["AccentText"]    = "#8FC3F5",
        ["OnAccent"]      = "#0B1E30",
        ["Error"]         = "#F2B8B5",
        ["Success"]       = "#7DD693",
        ["Warning"]       = "#E6C36B",
        ["Focus"]         = "#8FC3F5",
        ["OutputTint"]    = "#3A3320",
        ["FunctionTint"]  = "#3A2630",
        ["InputTint"]     = "#22303F",
    };

    // (foreground token, background token, minimum ratio).
    // Text = 4.5, large/UI affordance = 3.0.
    public static readonly IReadOnlyList<(string fg, string bg, double min)> Pairs = new (string, string, double)[]
    {
        ("TextPrimary",   "AppBackground", 4.5),
        ("TextPrimary",   "Surface",       4.5),
        ("TextPrimary",   "SurfaceSubtle", 4.5),
        ("TextSecondary", "AppBackground", 4.5),
        ("TextSecondary", "Surface",       4.5),
        ("AccentText",    "Surface",       4.5),
        ("OnAccent",      "Accent",        4.5),
        ("Error",         "Surface",       4.5),
        ("Success",       "Surface",       4.5),
        ("Warning",       "Surface",       4.5),
        ("Focus",         "Surface",       3.0),
        ("TextPrimary",   "OutputTint",    4.5),
        ("TextPrimary",   "FunctionTint",  4.5),
        ("TextPrimary",   "InputTint",     4.5),
        // NOTE (Codex review): SurfaceBorder is a decorative separator, not a
        // meaningful UI-component boundary or text, so WCAG does not require a
        // contrast floor for it. Do NOT add it to the gate — #D8D6D2 on white
        // is ~1.3:1 and would fail a bogus assertion.
    };
}

public static class Contrast
{
    public static double Ratio(string hexA, string hexB)
    {
        double la = Luminance(hexA), lb = Luminance(hexB);
        var (hi, lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    static double Luminance(string hex)
    {
        var (r, g, b) = Parse(hex);
        double R = Channel(r), G = Channel(g), B = Channel(b);
        return 0.2126 * R + 0.7152 * G + 0.0722 * B;
    }

    static double Channel(int c)
    {
        double s = c / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    static (int r, int g, int b) Parse(string hex)
    {
        hex = hex.TrimStart('#');
        return (Convert.ToInt32(hex[..2], 16),
                Convert.ToInt32(hex.Substring(2, 2), 16),
                Convert.ToInt32(hex.Substring(4, 2), 16));
    }
}
```

- [ ] **Step 4: Run the test; fix any failing pair by nudging the token, not the ratio**

Run: `dotnet test tests/QuadStick.Format.Tests --filter PaletteContrastTests`
Expected: PASS. If a pair fails, darken/lighten that token in `Palette` until it passes (the ratio thresholds are fixed; only hex values move).

- [ ] **Step 5: Commit**

```bash
git add src/QuadStick.App/Palette.cs tests/QuadStick.Format.Tests/PaletteContrastTests.cs tests/QuadStick.Format.Tests/QuadStick.Format.Tests.csproj
git commit -m "feat(a11y): palette single-source + WCAG AA contrast gate"
```

### Task 2: Theme resources + settings merge (TDD for settings)

**Files:**
- Create: `src/QuadStick.App/Theme.cs`
- Create: `tests/QuadStick.Format.Tests/SettingsTests.cs`
- Modify: `src/QuadStick.App/App.axaml` (`RequestedThemeVariant`), `src/QuadStick.App/App.axaml.cs` (register + apply)

**Interfaces:**
- Produces: `Theme.RegisterInto(Application app)`; `Theme.Apply(string choice)` where choice ∈ `System|Light|Dark`; `Settings.Load() → (QsModelName, ThemeChoice)`; `Settings.Save(model, theme)` (load-modify-save, never clobbers the other key).

- [ ] **Step 1: Write the failing settings round-trip test**

```csharp
// tests/QuadStick.Format.Tests/SettingsTests.cs
using QuadStick.App;
using Xunit;

public class SettingsTests
{
    [Fact]
    public void SaveTheme_PreservesModel()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "settings.json");
        Settings.WriteRaw(path, "{\"model\":\"Singleton\"}");

        Settings.Save(path, model: null, theme: "Dark");   // only theme changes
        var (model, theme) = Settings.Load(path);

        Assert.Equal("Singleton", model);
        Assert.Equal("Dark", theme);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var (model, theme) = Settings.Load(Path.Combine(Path.GetTempPath(), "nope-xyz.json"));
        Assert.Equal("FPS", model);
        Assert.Equal("System", theme);
    }
}
```

- [ ] **Step 2: Run, verify it fails (Settings missing)**

Run: `dotnet test tests/QuadStick.Format.Tests --filter SettingsTests`
Expected: build error, `Settings` not found.

- [ ] **Step 3: Write `Theme.cs` (contains `Settings` + `Theme`)**

```csharp
// src/QuadStick.App/Theme.cs
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace QuadStick.App;

public static class Settings
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuadStickConfigManager", "settings.json");

    public static (string model, string theme) Load(string? path = null)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path ?? DefaultPath))!.AsObject();
            return (node["model"]?.GetValue<string>() ?? "FPS",
                    node["theme"]?.GetValue<string>() ?? "System");
        }
        catch { return ("FPS", "System"); }
    }

    // Load-modify-save. Pass null to leave a key untouched.
    public static void Save(string? path, string? model, string? theme)
    {
        var p = path ?? DefaultPath;
        JsonObject node;
        try { node = JsonNode.Parse(File.ReadAllText(p))!.AsObject(); }
        catch { node = new JsonObject(); }
        if (model is not null) node["model"] = model;
        if (theme is not null) node["theme"] = theme;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, node.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        }
        catch { /* settings are a convenience, never fatal */ }
    }

    public static void WriteRaw(string path, string json) => File.WriteAllText(path, json);
}

public static class Theme
{
    public static void RegisterInto(Application app)
    {
        var rd = new ResourceDictionary();
        rd.ThemeDictionaries[ThemeVariant.Light] = BuildVariant(Palette.Light);
        rd.ThemeDictionaries[ThemeVariant.Dark]  = BuildVariant(Palette.Dark);
        app.Resources.MergedDictionaries.Add(rd);
    }

    static ResourceDictionary BuildVariant(IReadOnlyDictionary<string, string> map)
    {
        var d = new ResourceDictionary();
        foreach (var (key, hex) in map)
            d[key + "Brush"] = new SolidColorBrush(Color.Parse(hex));
        return d;
    }

    public static void Apply(string choice) =>
        Application.Current!.RequestedThemeVariant = choice switch
        {
            "Light" => ThemeVariant.Light,
            "Dark"  => ThemeVariant.Dark,
            _       => ThemeVariant.Default,
        };
}
```

Note the resource keys are `"<Token>Brush"` (e.g. `AppBackgroundBrush`). Styles and code-behind use those names.

- [ ] **Step 4: Run settings test, verify PASS**

Run: `dotnet test tests/QuadStick.Format.Tests --filter SettingsTests`
Expected: PASS.

- [ ] **Step 5: Register + apply at startup**

In `src/QuadStick.App/App.axaml`, set on the root `<Application>`: `RequestedThemeVariant="Default"`.

In `src/QuadStick.App/App.axaml.cs`, extend `OnFrameworkInitializationCompleted`:

```csharp
public override void OnFrameworkInitializationCompleted()
{
    Theme.RegisterInto(this);
    Theme.Apply(Settings.Load().theme);
    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        desktop.MainWindow = new MainWindow();
    base.OnFrameworkInitializationCompleted();
}
```

- [ ] **Step 6: Build, commit**

Run: `dotnet build src/QuadStick.App`
Expected: builds.

```bash
git add src/QuadStick.App/Theme.cs src/QuadStick.App/App.axaml src/QuadStick.App/App.axaml.cs tests/QuadStick.Format.Tests/SettingsTests.cs
git commit -m "feat(a11y): theme-scoped resources + non-clobbering settings"
```

### Task 3: Icons resource dictionary

**Files:**
- Create: `src/QuadStick.App/Icons.axaml`
- Modify: `src/QuadStick.App/App.axaml` (merge it)

**Interfaces:**
- Produces: keyed `StreamGeometry` resources: `IconCheck, IconWarning, IconError, IconInstall, IconSave, IconUndo, IconHome, IconHelp, IconAdd, IconDelete, IconChevron, IconThemeSystem, IconThemeLight, IconThemeDark, IconDevice`.

- [ ] **Step 1: Create `Icons.axaml`**

```xml
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <!-- 24x24 path data. Simple, legible glyphs; text always accompanies. -->
  <StreamGeometry x:Key="IconCheck">M9 16.2 4.8 12l-1.4 1.4L9 19 21 7l-1.4-1.4z</StreamGeometry>
  <StreamGeometry x:Key="IconWarning">M1 21h22L12 2 1 21zm12-3h-2v-2h2v2zm0-4h-2v-4h2v4z</StreamGeometry>
  <StreamGeometry x:Key="IconError">M12 2a10 10 0 100 20 10 10 0 000-20zm1 15h-2v-2h2v2zm0-4h-2V7h2v6z</StreamGeometry>
  <StreamGeometry x:Key="IconInstall">M5 20h14v-2H5v2zM19 9h-4V3H9v6H5l7 7 7-7z</StreamGeometry>
  <StreamGeometry x:Key="IconSave">M17 3H5a2 2 0 00-2 2v14a2 2 0 002 2h14a2 2 0 002-2V7l-4-4zm-5 16a3 3 0 110-6 3 3 0 010 6zm3-10H5V5h10v4z</StreamGeometry>
  <StreamGeometry x:Key="IconUndo">M12 5V1L7 6l5 5V7a6 6 0 11-6 6H4a8 8 0 108-8z</StreamGeometry>
  <StreamGeometry x:Key="IconHome">M10 20v-6h4v6h5v-8h3L12 3 2 12h3v8z</StreamGeometry>
  <StreamGeometry x:Key="IconHelp">M12 2a10 10 0 100 20 10 10 0 000-20zm1 17h-2v-2h2v2zm2.1-7.8-.9.9A3.4 3.4 0 0013 15h-2v-.5c0-.9.4-1.7 1-2.3l1.2-1.3A2 2 0 1010 9.3H8a4 4 0 118 0c0 .9-.4 1.7-1 2.3z</StreamGeometry>
  <StreamGeometry x:Key="IconAdd">M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6z</StreamGeometry>
  <StreamGeometry x:Key="IconDelete">M6 19a2 2 0 002 2h8a2 2 0 002-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14z</StreamGeometry>
  <StreamGeometry x:Key="IconChevron">M8.6 16.6 13.2 12 8.6 7.4 10 6l6 6-6 6z</StreamGeometry>
  <StreamGeometry x:Key="IconThemeSystem">M21 2H3a2 2 0 00-2 2v12a2 2 0 002 2h7v2H8v2h8v-2h-2v-2h7a2 2 0 002-2V4a2 2 0 00-2-2zm0 14H3V4h18z</StreamGeometry>
  <StreamGeometry x:Key="IconThemeLight">M12 7a5 5 0 100 10 5 5 0 000-10zM2 13h3v-2H2v2zm17 0h3v-2h-3v2zM11 2v3h2V2h-2zm0 17v3h2v-3h-2zM5.6 4.2 4.2 5.6l1.8 1.8 1.4-1.4zM18 16.2l1.8 1.8 1.4-1.4-1.8-1.8zM16.2 6 18 4.2 19.4 5.6l-1.8 1.8zM4.2 18.4 5.6 19.8 7.4 18l-1.4-1.4z</StreamGeometry>
  <StreamGeometry x:Key="IconThemeDark">M12 3a9 9 0 109 9c0-.5 0-.9-.1-1.4A7 7 0 0112 3z</StreamGeometry>
  <StreamGeometry x:Key="IconDevice">M12 2a3 3 0 00-3 3v3H7a3 3 0 00-3 3v6a3 3 0 003 3h10a3 3 0 003-3v-6a3 3 0 00-3-3h-2V5a3 3 0 00-3-3zm-1 3a1 1 0 112 0v3h-2z</StreamGeometry>
</ResourceDictionary>
```

(Path data above is placeholder-quality but valid; the executor may refine any glyph, but must keep the `x:Key` names.)

- [ ] **Step 2: Merge into `App.axaml`**

`ResourceInclude` cannot sit directly under `Application.Resources` next to keyed resources — wrap in a `ResourceDictionary` with `MergedDictionaries`, and use an `avares://` URI with the overridden assembly name (`QuadStickConfigManager`), per Codex:

```xml
<Application.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <ResourceInclude Source="avares://QuadStickConfigManager/Icons.axaml"/>
    </ResourceDictionary.MergedDictionaries>
  </ResourceDictionary>
</Application.Resources>
```

(Task 4 Step 2 adds the `x:Double` tokens inside this same `ResourceDictionary`.)

- [ ] **Step 3: Build, commit**

Run: `dotnet build src/QuadStick.App`

```bash
git add src/QuadStick.App/Icons.axaml src/QuadStick.App/App.axaml
git commit -m "feat(ui): StreamGeometry icon set"
```

### Task 4: Restyle App.axaml onto tokens + focus + spacing/type

**Files:**
- Modify: `src/QuadStick.App/App.axaml`

**Interfaces:**
- Produces: styles that reference `{DynamicResource *Brush}`; spacing/type `x:Double` resources (`SpaceXs..SpaceXl`, `TitleSize`, `SectionSize`, `SubheadSize`, `BodySize`, `SmallSize`); a `.focusable` visual via per-control `:focus-visible`.

- [ ] **Step 1: Replace the styles block**

Rewrite `<Application.Styles>` so every hardcoded hex becomes a token. Full replacement:

```xml
<Application.Styles>
  <FluentTheme />

  <Style Selector="Window">
    <Setter Property="Background" Value="{DynamicResource AppBackgroundBrush}"/>
  </Style>

  <Style Selector="Button">
    <Setter Property="MinHeight" Value="48"/>
    <Setter Property="MinWidth" Value="48"/>
    <Setter Property="FontSize" Value="{DynamicResource BodySize}"/>
    <Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/>
    <Setter Property="Background" Value="{DynamicResource SurfaceBrush}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource SurfaceBorderBrush}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="CornerRadius" Value="8"/>
    <Setter Property="Padding" Value="16,10"/>
  </Style>
  <Style Selector="Button:pointerover /template/ ContentPresenter">
    <Setter Property="Background" Value="{DynamicResource SurfaceSubtleBrush}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource AccentBrush}"/>
  </Style>

  <!-- Focus ring: bold, offset, theme-aware, on every interactive control. -->
  <Style Selector="Button:focus-visible /template/ ContentPresenter,
                   ToggleButton:focus-visible /template/ ContentPresenter">
    <Setter Property="BorderBrush" Value="{DynamicResource FocusBrush}"/>
    <Setter Property="BorderThickness" Value="2"/>
  </Style>
  <!-- ComboBox/AutoCompleteBox focus their inner template parts, so use
       :focus-within (Codex: :focus-visible can miss the focused inner element). -->
  <Style Selector="TextBox:focus-within, ComboBox:focus-within, AutoCompleteBox:focus-within">
    <Setter Property="BorderBrush" Value="{DynamicResource FocusBrush}"/>
    <Setter Property="BorderThickness" Value="2"/>
  </Style>

  <Style Selector="Button.quiet">
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="Foreground" Value="{DynamicResource AccentTextBrush}"/>
    <Setter Property="FontWeight" Value="SemiBold"/>
  </Style>
  <Style Selector="Button.danger">
    <Setter Property="Foreground" Value="{DynamicResource ErrorBrush}"/>
  </Style>

  <Style Selector="Button.zone, ToggleButton.zone">
    <Setter Property="Background" Value="{DynamicResource SurfaceBrush}"/>
    <Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource SurfaceBorderBrush}"/>
    <Setter Property="BorderThickness" Value="2"/>
    <Setter Property="Padding" Value="6"/>
    <Setter Property="HorizontalContentAlignment" Value="Center"/>
    <Setter Property="VerticalContentAlignment" Value="Center"/>
  </Style>
  <Style Selector="Button.zoneSelected, ToggleButton.zone:checked">
    <Setter Property="BorderBrush" Value="{DynamicResource AccentBrush}"/>
    <Setter Property="Background" Value="{DynamicResource SurfaceSubtleBrush}"/>
  </Style>

  <Style Selector="TextBox">
    <Setter Property="MinHeight" Value="48"/>
    <Setter Property="FontSize" Value="{DynamicResource BodySize}"/>
  </Style>
  <Style Selector="ComboBox">
    <Setter Property="MinHeight" Value="48"/>
    <Setter Property="FontSize" Value="{DynamicResource BodySize}"/>
  </Style>
  <Style Selector="AutoCompleteBox">
    <Setter Property="MinHeight" Value="48"/>
    <Setter Property="FontSize" Value="{DynamicResource BodySize}"/>
  </Style>

  <Style Selector="Button.primary">
    <Setter Property="Background" Value="{DynamicResource AccentBrush}"/>
    <Setter Property="Foreground" Value="{DynamicResource OnAccentBrush}"/>
    <Setter Property="FontWeight" Value="SemiBold"/>
  </Style>
  <Style Selector="Button.primary:pointerover /template/ ContentPresenter">
    <Setter Property="Background" Value="{DynamicResource AccentBrush}"/>
    <Setter Property="Foreground" Value="{DynamicResource OnAccentBrush}"/>
  </Style>

  <Style Selector="Button.card">
    <Setter Property="Margin" Value="0,0,14,14"/>
    <Setter Property="Background" Value="{DynamicResource SurfaceBrush}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource SurfaceBorderBrush}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="CornerRadius" Value="10"/>
    <Setter Property="Padding" Value="18,16"/>
    <Setter Property="MinWidth" Value="280"/>
    <Setter Property="MinHeight" Value="96"/>
    <Setter Property="HorizontalContentAlignment" Value="Left"/>
    <Setter Property="VerticalContentAlignment" Value="Top"/>
  </Style>
  <!-- BoxShadow lives on the ContentPresenter, not the Button (Codex). -->
  <Style Selector="Button.card /template/ ContentPresenter">
    <Setter Property="BoxShadow" Value="0 1 3 0 #14000000"/>
  </Style>
  <Style Selector="Button.card:pointerover /template/ ContentPresenter">
    <Setter Property="Background" Value="{DynamicResource SurfaceSubtleBrush}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource AccentBrush}"/>
  </Style>

  <Style Selector="TextBlock.section">
    <Setter Property="FontSize" Value="{DynamicResource SectionSize}"/>
    <Setter Property="FontWeight" Value="Bold"/>
    <Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/>
  </Style>
  <Style Selector="TextBlock.cardsub">
    <Setter Property="FontSize" Value="{DynamicResource SmallSize}"/>
    <Setter Property="Foreground" Value="{DynamicResource TextSecondaryBrush}"/>
    <Setter Property="TextWrapping" Value="Wrap"/>
    <Setter Property="MaxWidth" Value="250"/>
  </Style>
  <Style Selector="TextBlock.secondary">
    <Setter Property="Foreground" Value="{DynamicResource TextSecondaryBrush}"/>
  </Style>
</Application.Styles>
```

- [ ] **Step 2: Add the numeric tokens inside the same `ResourceDictionary`**

```xml
<Application.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <ResourceInclude Source="avares://QuadStickConfigManager/Icons.axaml"/>
    </ResourceDictionary.MergedDictionaries>
    <x:Double x:Key="SpaceXs">4</x:Double>
    <x:Double x:Key="SpaceSm">8</x:Double>
    <x:Double x:Key="SpaceMd">12</x:Double>
    <x:Double x:Key="SpaceLg">16</x:Double>
    <x:Double x:Key="SpaceXl">24</x:Double>
    <x:Double x:Key="Space2Xl">32</x:Double>
    <x:Double x:Key="TitleSize">28</x:Double>
    <x:Double x:Key="SectionSize">19</x:Double>
    <x:Double x:Key="SubheadSize">16</x:Double>
    <x:Double x:Key="BodySize">15</x:Double>
    <x:Double x:Key="SmallSize">14</x:Double>
  </ResourceDictionary>
</Application.Resources>
```

- [ ] **Step 3: Build and render a smoke check**

Run: `dotnet build src/QuadStick.App` then `dotnet run --project tools/RenderPreview -- /tmp/qscm-p1`
Expected: builds; PNGs render (still light-only for now). Visually confirm the app still looks right and text uses token colors.

- [ ] **Step 4: Commit**

```bash
git add src/QuadStick.App/App.axaml
git commit -m "feat(ui): styles on design tokens, 48px targets, focus rings, spacing/type scale"
```

### Task 5: Code-behind onto tokens (kill frozen brushes)

**Files:**
- Modify: `src/QuadStick.App/MainWindow.axaml.cs`

**Interfaces:**
- Produces: `BindBrush(control, property, tokenKey)` helper that binds a property to a theme brush (repaints on theme change); TextBlock color classes (`secondary`, `muted`, `success`, `warn`, `error`); removal of `static readonly OutputTint/FunctionTint/InputTint`; `SuggestBox` takes a `tintKey` string; all inline `Brushes.*`/`Color.Parse(...)` replaced.

> **Codex review correction:** there is NO `Brush(token) → IBrush` helper. Resolving a concrete brush with `FindResource` and assigning it **freezes on theme switch**, defeating the whole point. Every themed color is applied by a DynamicResource binding or a style class. The binding indexer is `!` (binding), not `~` (template-binding).

- [ ] **Step 1: Add the DynamicResource binder + color classes**

Add near the top of `MainWindow` (usings: `Avalonia`, `Avalonia.Media`, `Avalonia.Markup.Xaml.MarkupExtensions`):

```csharp
// Bind any brush property to a theme token so it repaints on theme change.
static void BindBrush(Control target, AvaloniaProperty property, string tokenKey) =>
    target[!property] = new DynamicResourceExtension(tokenKey + "Brush");
```

Add these TextBlock color classes to `App.axaml` styles (Task 4) so plain text can be themed by class instead of a per-instance binding:

```xml
<Style Selector="TextBlock.muted">   <Setter Property="Foreground" Value="{DynamicResource TextSecondaryBrush}"/></Style>
<Style Selector="TextBlock.success"> <Setter Property="Foreground" Value="{DynamicResource SuccessBrush}"/></Style>
<Style Selector="TextBlock.warn">    <Setter Property="Foreground" Value="{DynamicResource WarningBrush}"/></Style>
<Style Selector="TextBlock.error">   <Setter Property="Foreground" Value="{DynamicResource ErrorBrush}"/></Style>
```

Rule of thumb: **TextBlocks → add a class** (`tb.Classes.Add("muted")`). **Borders/AutoCompleteBox backgrounds and any non-TextBlock brush → `BindBrush(...)`.** Never assign a resolved brush.

- [ ] **Step 2: Replace the tint fields with keys**

Delete lines 76-78 (the `static readonly IBrush OutputTint/FunctionTint/InputTint`). Change `SuggestBox` signature from `IBrush tint` to `string tintKey`, and inside set the background via DynamicResource:

```csharp
Control SuggestBox(int row, int col, string value, double width, List<string> suggestions, string accessibleName, string tintKey)
{
    var box = new AutoCompleteBox { Text = value, Width = width, ItemsSource = suggestions,
        FilterMode = AutoCompleteFilterMode.Contains, MinimumPrefixLength = 1 };
    box[!TemplatedControl.BackgroundProperty] =
        new DynamicResourceExtension(tintKey + "Brush");   // '!' = binding, not '~'
    // ... unchanged commit/focus wiring ...
}
```

Update all `SuggestBox(...)` call sites to pass the key string: `"OutputTint"`, `"FunctionTint"`, `"InputTint"`. For `Swatch`/`HeaderRow`/`PrefsHeaderRow` `Border.Background`, use `BindBrush(border, Border.BackgroundProperty, "OutputTint")` etc.

> This whole task is done in **one commit** — deleting the tint fields, changing the `SuggestBox` signature, and updating every call site together. Splitting it breaks compile (Codex: the fields and `IBrush tint` signature flow through many builders).

- [ ] **Step 3: Replace every inline color**

Apply this exact mapping (search each, replace). **TextBlocks get a class; non-TextBlock brushes get `BindBrush`.** No resolved brushes.

| Location | Old | New |
|---|---|---|
| `BuildDeviceView` intro TextBlock | `Foreground = new SolidColorBrush(Color.Parse("#555555"))` | `Classes = { "muted" }` |
| `BuildDeviceView` model desc TextBlock | `Foreground = Brushes.Gray` | `Classes = { "muted" }` |
| Mouthpiece bar Border | `Background = new SolidColorBrush(Color.Parse("#F0EEEA"))` | `BindBrush(border, Border.BackgroundProperty, "SurfaceSubtle")` |
| `ZoneButton` summary TextBlock | `Brushes.Gray` / `Color.Parse("#2B2B2B")` | `Classes = { "muted" }` (unmapped) / no class = inherits `TextPrimary` (mapped) |
| `ZoneButton` foreign border | `BorderBrush = Color.Parse("#C77700")` | `BindBrush(btn, TemplatedControl.BorderBrushProperty, "Warning")` |
| `BuildZoneDetail` blurb TextBlock | `Color.Parse("#555555")` | `Classes = { "muted" }` |
| detail "nothing selected/mapped" TextBlock | `Brushes.Gray` | `Classes = { "muted" }` |
| mapping card Border bg/border | `Brushes.White` / `Color.Parse("#E3E1DD")` | `BindBrush(border, Border.BackgroundProperty, "Surface")` + `BindBrush(border, Border.BorderBrushProperty, "SurfaceBorder")` |
| `RefreshEditor` device status | `Brushes.Green` / `Brushes.Gray` | see Task 7 (device status becomes a chip) |
| `RebuildRows` empty text TextBlock | `Brushes.Gray` | `Classes = { "muted" }` |
| `RefreshIssues` "No problems" TextBlock | `Brushes.Green` | `Classes = { "success" }` |
| `RefreshIssues` issue text TextBlock | `Brushes.Crimson`/`Brushes.DarkOrange` | `Classes = { "error" }` / `Classes = { "warn" }` |
| `RefreshIssues` cell border | `Brushes.Crimson`/`Brushes.DarkOrange` | `BindBrush(border, Border.BorderBrushProperty, "Error"/"Warning")`; clear = `BindBrush(..., "SurfaceBorder")` or set `BorderBrush = Brushes.Transparent` |
| `Status` line | brush arg | replaced by `StatusChip` in Task 7 |
| `PickDeviceRootAsync` subtitle TextBlock | `Brushes.Gray` | `Classes = { "muted" }` |

Also bump the two 12px `FontSize = 12` device summary TextBlocks (ZoneButton summary, PickDeviceRoot subtitle) and the 13px model-desc to `14`.

- [ ] **Step 4: Build + render smoke check**

Run: `dotnet build src/QuadStick.App`
Expected: builds with no reference to the removed tint fields.

- [ ] **Step 5: Commit**

```bash
git add src/QuadStick.App/MainWindow.axaml.cs
git commit -m "feat(a11y): code-behind colors onto theme tokens (no frozen brushes)"
```

### Task 6: Appearance toggle on Home

**Files:**
- Modify: `src/QuadStick.App/MainWindow.axaml` (add control), `src/QuadStick.App/MainWindow.axaml.cs` (wire it)

**Interfaces:**
- Consumes: `Settings.Save`, `Theme.Apply`.
- Produces: a labeled `ComboBox x:Name="AppearancePicker"` with items `System/Light/Dark`.

- [ ] **Step 1: Add the control to the Home header**

In `MainWindow.axaml`, change the Home top `StackPanel` (lines ~11-16) into a `DockPanel` row so the appearance control sits top-right:

```xml
<DockPanel DockPanel.Dock="Top">
  <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" Spacing="8"
              VerticalAlignment="Top">
    <TextBlock Text="Appearance" VerticalAlignment="Center" Classes="secondary"/>
    <ComboBox x:Name="AppearancePicker" MinWidth="130"
              AutomationProperties.Name="Appearance: choose System, Light, or Dark theme"/>
  </StackPanel>
  <StackPanel Spacing="4">
    <TextBlock Text="QuadStick Config Manager" FontSize="{DynamicResource TitleSize}" FontWeight="Bold"/>
    <TextBlock Text="Edit and install game profiles for your QuadStick. Free, unofficial, works offline."
               FontSize="{DynamicResource SubheadSize}" Classes="secondary"/>
    <TextBlock x:Name="HomeVersionText" FontSize="{DynamicResource SmallSize}" Classes="secondary"/>
  </StackPanel>
</DockPanel>
```

- [ ] **Step 2: Wire it in the constructor** (after `_model = LoadModel()` block)

```csharp
var (_, savedTheme) = Settings.Load();
AppearancePicker.ItemsSource = new[] { "System", "Light", "Dark" };
AppearancePicker.SelectedIndex = savedTheme switch { "Light" => 1, "Dark" => 2, _ => 0 };
AppearancePicker.SelectionChanged += (_, _) =>
{
    var choice = (string)AppearancePicker.SelectedItem!;
    Theme.Apply(choice);
    Settings.Save(null, model: null, theme: choice);
};
```

- [ ] **Step 3: Migrate `LoadModel`/`SaveModel` to `Settings`**

Replace `LoadModel()` body with `Enum.TryParse<QsModel>(Settings.Load().model, out var m) ? m : QsModel.FPS;` and `SaveModel()` with `Settings.Save(null, model: _model.ToString(), theme: null);`. Delete the now-unused `SettingsFile` member.

- [ ] **Step 4: Build; manual toggle test**

Run: `dotnet run --project src/QuadStick.App` (if a display is available) and flip System/Light/Dark — confirm **every** surface, text, tint, status color repaints with nothing frozen. If no display, defer to the both-theme render in Task 14.

- [ ] **Step 5: Commit**

```bash
git add src/QuadStick.App/MainWindow.axaml src/QuadStick.App/MainWindow.axaml.cs
git commit -m "feat(a11y): appearance toggle (System/Light/Dark), persisted"
```

---

## Phase 2 — Accessibility hardening

### Task 7: Status chip helper + non-color cues

**Files:** Modify `src/QuadStick.App/MainWindow.axaml.cs`, `src/QuadStick.App/MainWindow.axaml`.

**Interfaces:** Produces `enum StatusKind { Ready, Info, Warning, Error }` and `Control StatusChip(StatusKind kind, string text)` (icon + text + color + border), replacing freeform `Status(text,color)`.

- [ ] **Step 1: Add the chip builder**

```csharp
enum StatusKind { Ready, Info, Warning, Error }

Control StatusChip(StatusKind kind, string text)
{
    var (iconKey, tokenKey) = kind switch
    {
        StatusKind.Ready   => ("IconCheck",   "Success"),
        StatusKind.Warning => ("IconWarning", "Warning"),
        StatusKind.Error   => ("IconError",   "Error"),
        _                  => ("IconChevron", "TextSecondary"),
    };
    // Data is a one-time resource read (geometry doesn't change with theme);
    // Foreground MUST be a dynamic binding so the color follows the theme.
    var icon = new PathIcon { Width = 16, Height = 16,
        Data = (Geometry)Application.Current!.FindResource(iconKey)! };
    BindBrush(icon, IconElement.ForegroundProperty, tokenKey);
    var label = new TextBlock { Text = text, FontSize = 15,
        VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    BindBrush(label, TextBlock.ForegroundProperty, tokenKey);
    return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
        Children = { icon, label } };
}
```

- [ ] **Step 2: Route status through it**

Replace the `StatusText` `TextBlock` in `MainWindow.axaml` with a host: `<ContentControl x:Name="StatusHost" DockPanel.Dock="Top" Margin="0,12"/>`. Rewrite `Status(...)` to set `StatusHost.Content = StatusChip(kind, text)` and take a `StatusKind` instead of a brush. Update `RefreshIssues` to choose `Error`/`Warning`/`Ready`. The clipboard-copy path uses `StatusKind.Info`.

- [ ] **Step 3: Non-color invalid-cell marker**

In `RefreshIssues`, when marking `_cellBorders`, also show a severity glyph. Simplest: set the wrapper border thickness to 2 and add a `PathIcon` overlay is heavy; instead thicken to 3 and set `AutomationProperties.SetName` on the wrapper to prefix "Error:"/"Warning:". Keep color too. (Border already carries the token color from Task 5.)

- [ ] **Step 4: Build, commit**

Run: `dotnet build src/QuadStick.App`

```bash
git add src/QuadStick.App/MainWindow.axaml src/QuadStick.App/MainWindow.axaml.cs
git commit -m "feat(a11y): status-chip system with non-color cues"
```

### Task 8: Live regions, calibrated

**Files:** Modify `src/QuadStick.App/MainWindow.axaml.cs` (+ `.axaml`).

- [ ] **Step 1:** On `StatusHost` set `AutomationProperties.LiveSetting` dynamically: Assertive for `StatusKind.Error`, Polite otherwise. Set it in `Status(...)` via `AutomationProperties.SetLiveSetting(StatusHost, kind == StatusKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite)`.
- [ ] **Step 2:** The Home `HomeStatusText` keeps `Assertive` (import errors are blocking).
- [ ] **Step 3:** In `BuildZoneDetail`, set `AutomationProperties.LiveSetting=Polite` on the zone title TextBlock so selecting a zone announces the change.
- [ ] **Step 4:** Build; commit `feat(a11y): calibrated live regions`.

### Task 9: Keyboard focus management + Fix first problem

**Files:** Modify `src/QuadStick.App/MainWindow.axaml.cs`, `.axaml`.

- [ ] **Step 1: Focus after view/screen switches.** In `ShowEditor()` focus the first toolbar control; after `DeviceViewButton`/`ListViewButton` click, focus the view's first interactive element; in `ShowHome()` focus the New card.
- [ ] **Step 2: Focus new mapping in Device View.** In `BuildZoneDetail`'s add-mapping handler, after rebuild, focus the new input box (mirror List View's `AddRow` focus logic — find the new row's first cell in `_cellBorders` or the last card's first `AutoCompleteBox` and `.Focus()`).
- [ ] **Step 3: Delete preserves focus.** In both delete handlers (`BuildZoneDetail` `del`, `BindingRow`/`PrefsRow` `del`), after rebuild move focus to the next sibling control instead of losing it.
- [ ] **Step 4: Problem selects the cell.** Change `IssuesList.SelectionChanged`: still copy, but also, if the issue's `Cell` is in `_cellBorders`, `BringIntoView()` + focus that box. Add a `Button x:Name="FixFirstButton"` next to the Problems header; on click, focus the first error's cell (first `_file.Issues` with `Severity.Error`).
- [ ] **Step 5: Invalid filename focuses field.** In `CommitFileName`/`RefreshIssues`, if a filename-cell error exists, `FileNameBox.Focus()` is available via the Fix-first path; ensure the filename rule text is in the issue message (it already surfaces as an error).
- [ ] **Step 6: Shortcuts.** Extend the `KeyDown` switch: `Key.I` → `InstallAsync()`, `Key.D` → toggle device/list, `Key.H`/`F1` → `ShowHelp()`. Add these to the Help "Keyboard" section text.
- [ ] **Step 7:** Build; manual keyboard pass (new → edit → error → Fix first → correct → save). Commit `feat(a11y): keyboard focus management + fix-first-problem`.

### Task 10: Device schematic as a non-visual model

**Files:** Modify `src/QuadStick.App/MainWindow.axaml.cs`.

- [ ] **Step 1:** Change `ZoneButton` to build a `ToggleButton` (Classes `zone`), `IsChecked = _selectedZone == z.Id`. Remove the manual `zoneSelected` class add (the `:checked` style from Task 4 handles it). Keep the click handler (set `_selectedZone`, rebuild).
- [ ] **Step 2:** Enrich the `AutomationProperties.Name` to include selected state and mapping count explicitly: `"{z.Title}. {(selected ? "selected. " : "")}{count} mapping(s). {spoken}. {(foreign ? warning : "")} Press Enter to edit."`.
- [ ] **Step 3:** Verify tab order: zones are added to `DeviceCanvas` in a sensible order already (joystick, mouthpiece holes, side/lip, extras). Confirm every visible zone is a focusable ToggleButton and reachable by Tab.
- [ ] **Step 4:** Build; screen-reader smoke check if available. Commit `feat(a11y): device zones as toggle buttons with rich semantics`.

### Task 11: Window sizing + reflow

**Files:** Modify `src/QuadStick.App/MainWindow.axaml`, `.axaml.cs`.

- [ ] **Step 1:** Lower `MinWidth`/`MinHeight` on the Window (e.g. `MinWidth="760" MinHeight="560"`) so magnified/half-screen use works.
- [ ] **Step 2:** Convert the editor top toolbar and controls `StackPanel`s (MainWindow.axaml lines ~79-93 and ~98-111) into a layout that never clips: put actions in a `WrapPanel` OR a two-row `DockPanel` where the filename `TextBox` grows and buttons stay put. Prefer the two-row DockPanel (Codex: WrapPanel toolbars feel unstable). Ensure the Device View `Grid ColumnDefinitions="*,420"` right panel min-widths degrade (make it `*,Auto` with a `MaxWidth` on the detail panel, or reduce to 360 and allow the canvas to scroll).
- [ ] **Step 3:** Build; resize small, confirm no clipping/horizontal page scroll. Commit `feat(a11y): magnification-friendly sizing and non-clipping chrome`.

---

## Phase 3 — Install trust flow

> **Codex correction:** the original "custom staged modal" was over-built for this pass, and its "your QuadStick was not changed" promise is not always true — `Device.Install` can leave a `.qscm-tmp` on some failure paths. So: (12a) harden `Device.Install` cleanup + get precise result/failure semantics with a test, then (12b) a simpler confirm → progress → receipt dialog with honest wording.

### Task 12a: Harden `Device.Install` cleanup (TDD)

**Files:** Modify `src/QuadStick.Format/Device.cs`; add `tests/QuadStick.Format.Tests/InstallCleanupTests.cs`. (This is the one place we touch the format library, and it is a safety fix, not UI.)

- [ ] **Step 1: Failing test** — simulate a readback mismatch is hard to force, so test the observable guarantee we can control: after a **successful** install no `.qscm-tmp` remains, and after a mid-swap failure the original file is intact and no `.qscm-tmp` remains. Write a test that installs into a temp "device" dir (a folder containing `default.csv`), then asserts `Directory.GetFiles(dir, "*.qscm-tmp")` is empty and the target content matches.

```csharp
// tests/QuadStick.Format.Tests/InstallCleanupTests.cs
using QuadStick.Format;
using Xunit;

public class InstallCleanupTests
{
    [Fact]
    public void Install_LeavesNoTempFile_AndWritesTarget()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "default.csv"), "QuadStick Configuration File,\n,mygame.csv\n");
        var file = ProfileFile.NewFromTemplate("mygame.csv");
        var backups = Directory.CreateTempSubdirectory().FullName;

        var result = Device.Install(file, dir, backups);

        Assert.Empty(Directory.GetFiles(dir, "*.qscm-tmp"));
        Assert.True(File.Exists(result.InstalledPath));
    }
}
```

- [ ] **Step 2: Run, verify** (may already pass for the happy path). Run: `dotnet test tests/QuadStick.Format.Tests --filter InstallCleanupTests`.
- [ ] **Step 3: Harden** — wrap the write/readback/move in a `try/finally` that deletes a stray `tmp` if it still exists, so no `.qscm-tmp` is ever orphaned regardless of which step throws. Keep the existing restore-from-backup path.
- [ ] **Step 4:** Run tests green. Commit `fix(install): never orphan the temp file`.

### Task 12b: Confirm → progress → receipt dialog

**Files:** Create `src/QuadStick.App/InstallFlow.cs` (partial class `MainWindow`), modify `MainWindow.axaml.cs`.

**Interfaces:** Consumes `Device.FindCandidates/IsInstallTarget/Install/DefaultBackupDir`, `ConfirmAsync`, `PickDeviceRootAsync`, `StatusChip`. Produces `Task RunInstallFlowAsync()`.

- [ ] **Step 1:** `RunInstallFlowAsync` keeps today's control flow (validate → find/pick device → confirm default → `Device.Install` on a background thread) but drives a single modal `Window` that shows, in order: the target drive, a "Backing up and installing…" progress line (announced Polite), then flips to a result panel. Reuse the existing `PickDeviceRootAsync`/`ConfirmAsync`. Do **not** build a custom multi-row stepper.
- [ ] **Step 2: Success = receipt.** Result panel shows a `StatusChip(Ready, "Installed")`, then: installed filename, target drive (`root`), and backup path (`result.BackupPath ?? "no previous file to back up"`). Close button focused. Editor status becomes `StatusChip(Ready, ...)`.
- [ ] **Step 3: Failure = honest safety.** On the caught exception set, show `StatusChip(Error, ...)` with the message. Use the message verbatim from `Device.Install` (which already distinguishes "device was not modified" for readback failure vs "restored from backup" for mid-swap) rather than asserting a blanket "unchanged." Return focus to Install.
- [ ] **Step 4:** Point `InstallButton.Click` and `Ctrl+I` at `RunInstallFlowAsync`. Build; commit `feat(install): confirm/progress/receipt install dialog`.

---

## Phase 4 — Premium polish

### Task 13: Elevation, empty-as-action states, spacing pass

**Files:** Modify `src/QuadStick.App/MainWindow.axaml`, `.axaml.cs`.

- [ ] **Step 1: Elevation.** Card `BoxShadow` is set in Task 4. Add a single subtle `BoxShadow` to the editor `GridContainer`/`DeviceContainer` and the Problems border (`0 1 3 0 #14000000`). Nothing else gets shadow.
- [ ] **Step 2: Empty-as-action.** Replace `LibraryEmptyText` with a small panel offering New / Open / Import buttons (reuse existing handlers). Replace `DeviceEmptyText` with a compact "Plug in your QuadStick… (in PS4 boot mode or controller emulation? the drive won't appear)" hint.
- [ ] **Step 3: Spacing/type sweep.** Replace remaining ad hoc `FontSize=`/`Margin=` literals in `MainWindow.axaml` and code-behind with the `{DynamicResource}` size tokens / `Space*` values. Keep values already on-scale.
- [ ] **Step 4: Device View hierarchy.** It is already default; add a one-line contextual header row above the canvas showing the device-connected chip + current mode, so the "signature surface" reads as the main editor. List View button stays as the fallback.
- [ ] **Step 5:** Build; render smoke check; commit `feat(ui): elevation, action-state empty states, spacing/type pass`.

### Task 14: Both-theme verification + docs

**Files:** Modify `tools/RenderPreview/Program.cs`; add screenshots under `docs/`.

- [ ] **Step 1:** Parameterize `RenderPreview` to render each capture twice, once per theme. Before each `Capture`, call `Application.Current!.RequestedThemeVariant = variant;` and prefix filenames with `light-`/`dark-`. (Theme resources are registered by `App` during `SetupWithoutStarting`.)

```csharp
foreach (var (suffix, variant) in new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) })
{
    Application.Current!.RequestedThemeVariant = variant;
    Capture($"{suffix}-1-home", _ => { });
    // ... the rest, prefixed with {suffix}- ...
}
```

- [ ] **Step 2:** Run `dotnet run --project tools/RenderPreview -- /tmp/qscm-final`. Inspect light and dark renders: contrast, no clipping, chips visible, focus (won't show without focus), device view legible.
- [ ] **Step 3:** Run the full test suite: `dotnet test`. Expected: green (contrast gate, settings, existing format tests).
- [ ] **Step 4: Manual a11y pass (record results in the PR).** Keyboard-only run (new → edit → error → Fix first → correct → save → install mocked, no mouse). Screen reader reads control names, announces validation + selected-zone changes, does not spam. Window small + magnified: no clipping.
- [ ] **Step 5:** Update the four `docs/screenshot-*.png` with fresh light-theme renders and add dark equivalents. Commit `docs+test: both-theme renders and a11y verification`.

---

## Self-Review

**Spec coverage:** Phase 1 (tokens/theme/toggle/icons/helpers) → Tasks 1-6. Phase 2 (contrast gate is Task 1; non-color cues Task 7; focus Task 4; live regions Task 8; keyboard/Fix-first Task 9; device SR model Task 10; sizing Task 11) → covered. Phase 3 (install trust flow) → Task 12. Phase 4 (chips Task 7, elevation/empty/spacing/device-hierarchy Task 13, both-theme verify Task 14) → covered. Verification (contrast gate, both-theme renders, manual a11y) → Tasks 1 + 14.

**Placeholder scan:** Icon path data is explicitly flagged as refinable but valid; no TBD/TODO steps; the code-behind color sweep is a concrete mapping table, not "handle colors."

**Type consistency:** Resource keys are `"<Token>Brush"` everywhere (Palette keys + `"Brush"` suffix in `Theme.BuildVariant`, `Brush()` helper, and `{DynamicResource *Brush}` in styles). `Settings.Load/Save` signatures match between Task 2 definition and Task 6 usage. `StatusKind` defined in Task 7 and used in Tasks 8-12.

## Risks

- **Frozen brush escapes:** the Task 5 mapping table is the guard; the both-theme render (Task 14) plus a manual toggle will surface any missed inline brush as a patch of wrong color in dark mode.
- **`focus-visible` coverage:** if a control type doesn't honor the pseudo-class, fall back to `:focus`. Verify each control shows a ring by keyboard.
- **AutoCompleteBox theming:** its popup uses Fluent defaults; confirm the dropdown is legible in dark mode, restyle its `ListBoxItem` if not.
- **RenderPreview theme switch:** if setting `RequestedThemeVariant` mid-run doesn't re-resolve DynamicResources in headless mode, render two separate process runs (one per theme via an arg).
