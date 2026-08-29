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
        ["TitleSize"] = 30,
        ["SectionSize"] = 22,
        ["SubheadSize"] = 17,
        ["BodySize"] = 15,
        ["SmallSize"] = 14,
        ["SpaceXs"] = 4,
        ["SpaceSm"] = 8,
        ["SpaceMd"] = 12,
        ["SpaceLg"] = 16,
        ["SpaceXl"] = 24,
        ["Space2Xl"] = 32,
        ["ControlRadius"] = 8,
        ["CellRadius"] = 6,
        ["TrackRadius"] = 10,
        ["IconRadius"] = 20,
        ["PanelRadius"] = 12,
        ["ControlHeight"] = 48,
        ["IconButton"] = 40,
        ["CardWidth"] = 280,
        ["CardHeight"] = 112,
        ["HairlineWidth"] = 1,
    };

    public static readonly IReadOnlyDictionary<string, Thickness> Paddings = new Dictionary<string, Thickness>
    {
        ["ControlPadding"] = new(16, 10),
        ["CardPadding"] = new(18, 16),
        ["TrackPadding"] = new(3),
        ["ZonePadding"] = new(6),
        ["HairlineThickness"] = new(1),
    };

    public const string FontFamily = "";
    public const string BrandFontFamily = "Cascadia Mono, JetBrains Mono, Menlo, DejaVu Sans Mono, monospace";

    public static void RegisterInto(Avalonia.Application app)
    {
        var d = new ResourceDictionary();
        foreach (var (key, value) in Numbers) d[key] = value;
        foreach (var (key, value) in Paddings) d[key] = value;
        foreach (var (key, value) in Numbers)
            if (key.EndsWith("Radius", StringComparison.Ordinal)) d[key + "Corner"] = new CornerRadius(value);
        d["AppFont"] = FontFamily.Length > 0
            ? new FontFamily(FontFamily) : Avalonia.Media.FontFamily.Default;
        d["BrandFont"] = new FontFamily(BrandFontFamily);
        app.Resources.MergedDictionaries.Add(d);
    }

    /// <summary>Live edit, for the gallery's sliders. Everything reading this
    /// token through a dynamic resource follows on the next layout pass.</summary>
    public static void Set(string key, double value)
    {
        var app = Avalonia.Application.Current!;
        app.Resources[key] = value;
        if (key.EndsWith("Radius", StringComparison.Ordinal))
            app.Resources[key + "Corner"] = new CornerRadius(value);
    }
}
