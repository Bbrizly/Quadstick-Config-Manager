using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace QuadStick.App;

// Every number the look depends on, beside Palette.cs which holds every colour.
// Registered as resources, so setting one at runtime repaints what reads it.
// ControlHeight 48 and IconButton 40 are click-target floors for mouth stick
// and head mouse users. Do not shrink them without testing on those users.
public static class Style
{
    public static readonly IReadOnlyDictionary<string, double> Numbers = new Dictionary<string, double>
    {
        // Type. The scale the whole app is written in. Every step is a real
        // jump. The old scale ran 28/19/16/15/14, so a section heading was four
        // pixels off body text and no screen had anything that led the eye.
        // Nothing below body moves: 15 and 14 are the reading sizes, and this
        // app is read by people who cannot lean in.
        ["TitleSize"]     = 30,
        ["SectionSize"]   = 22,
        ["SubheadSize"]   = 17,
        ["BodySize"]      = 15,
        ["SmallSize"]     = 14,
        ["SidebarToggleTextSize"] = 12,

        // Space. Steps, not free numbers, so panels line up with each other.
        ["SpaceXs"]       = 4,
        ["SpaceSm"]       = 8,
        ["SpaceMd"]       = 12,
        ["SpaceLg"]       = 16,
        ["SpaceXl"]       = 24,
        ["Space2Xl"]      = 32,

        // Shape. A control is 8, anything that holds controls is 12, a round
        // icon button is 20. Cell and Track sit one step inside their parent so
        // a nested corner never bulges past the one around it.
        ["ControlRadius"] = 8,
        ["CellRadius"]    = 6,
        ["TrackRadius"]   = 10,
        ["IconRadius"]    = 20,
        ["PanelRadius"]   = 12,

        // Size. The floors above, plus the card the home screen is built from.
        ["ControlHeight"] = 48,
        ["IconButton"]    = 40,
        ["CardWidth"]     = 280,
        // The three persistent destinations are intentionally icon-only. Keep
        // their hit areas generous while the glyphs stay visually simple.
        ["ShellNavButton"] = 64,
        ["ShellNavIcon"]   = 32,
        ["ShellSettingsButton"] = 64,
        ["ShellSettingsIcon"]   = 40,
        // The panel down the left of the editor. Give its labels, mode list and
        // three view keys a little air; the device canvas still owns the larger
        // share of the workspace, while the mapping detail stays compact.
        ["SidebarWidth"]  = 304,
        ["CardHeight"]    = 112,

        // The editor's own commands. A command button is a square plate, not a
        // round chip: Save, Undo and Save as template are the three things you
        // press all day, and at 40 in a circle they read as decoration beside
        // Install. Install is the destination, so its glyph is the largest
        // thing in the bar.
        ["CommandButton"] = 56,
        ["CommandIcon"]   = 26,
        ["InstallIcon"]   = 36,

        // Weight of a hairline: the row separators, panel edges and rules.
        ["HairlineWidth"] = 1,
    };

    public static readonly IReadOnlyDictionary<string, Thickness> Paddings = new Dictionary<string, Thickness>
    {
        ["ControlPadding"] = new(16, 10),
        ["CardPadding"]    = new(18, 16),
        ["TrackPadding"]   = new(3),
        // A dropped menu keeps its items off its own rounded corners.
        ["MenuPadding"]    = new(4),
        ["ZonePadding"]    = new(6),
        // A border is a Thickness, not a width, and a style setter takes the
        // resource as it finds it: a double here throws at styling time.
        ["HairlineThickness"] = new(1),
    };

    // "" means whatever the operating system hands us, which is what the app
    // has always used. A name here restyles every word in the program.
    public const string FontFamily = "";
    // The compact QCM mark uses a technical display face without forcing a
    // monospace font on long instructions and form labels. Every platform has
    // at least one of these; the last entry is Avalonia's portable fallback.
    public const string BrandFontFamily = "Cascadia Mono, JetBrains Mono, Menlo, DejaVu Sans Mono, monospace";

    public static void RegisterInto(Application app)
    {
        var d = new ResourceDictionary();
        foreach (var (key, value) in Numbers) d[key] = value;
        foreach (var (key, value) in Paddings) d[key] = value;
        foreach (var (key, value) in Numbers)
            if (key.EndsWith("Radius")) d[key + "Corner"] = new CornerRadius(value);
        d["AppFont"] = FontFamily.Length > 0
            ? new FontFamily(FontFamily) : Avalonia.Media.FontFamily.Default;
        d["BrandFont"] = new FontFamily(BrandFontFamily);
        app.Resources.MergedDictionaries.Add(d);
    }

    /// <summary>Live edit, for the gallery's sliders. Everything reading this
    /// token through a dynamic resource follows on the next layout pass.</summary>
    public static void Set(string key, double value)
    {
        var app = Application.Current!;
        app.Resources[key] = value;
        if (key.EndsWith("Radius")) app.Resources[key + "Corner"] = new CornerRadius(value);
    }
}
