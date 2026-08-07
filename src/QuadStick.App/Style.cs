// src/QuadStick.App/Style.cs
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace QuadStick.App;

// Every number that decides how the app looks, in one place, the way Palette
// holds every colour. Nothing here is a colour and nothing in Palette is a
// number, so between the two files the whole look is one edit away.
//
// These are real resources, so App.axaml and the C# views both read them with
// {DynamicResource Key} / Size(key), and setting one at runtime repaints
// everything bound to it. That is what the gallery's sliders do: turn a knob,
// watch the whole app move, then write the number you liked down here.
//
// The two heights are not taste. 48 and 40 are click-target floors for people
// aiming with a mouth stick or a head mouse, so they are the one pair to leave
// alone unless somebody has tested smaller ones with the people who use them.
public static class Style
{
    public static readonly IReadOnlyDictionary<string, double> Numbers = new Dictionary<string, double>
    {
        // Type. The scale the whole app is written in.
        ["TitleSize"]     = 28,
        ["SectionSize"]   = 19,
        ["SubheadSize"]   = 16,
        ["BodySize"]      = 15,
        ["SmallSize"]     = 14,

        // Space. Steps, not free numbers, so panels line up with each other.
        ["SpaceXs"]       = 4,
        ["SpaceSm"]       = 8,
        ["SpaceMd"]       = 12,
        ["SpaceLg"]       = 16,
        ["SpaceXl"]       = 24,
        ["Space2Xl"]      = 32,

        // Shape. Four radii, each with a job: a control, a cell inside a row,
        // a track that holds a bank of controls, and a round icon button.
        ["ControlRadius"] = 6,
        ["CellRadius"]    = 5,
        ["TrackRadius"]   = 8,
        ["IconRadius"]    = 20,

        // Size. The floors above, plus the card the home screen is built from.
        ["ControlHeight"] = 48,
        ["IconButton"]    = 40,
        ["CardWidth"]     = 280,
        ["CardHeight"]    = 96,

        // Weight of a hairline: the row separators, panel edges and rules.
        ["HairlineWidth"] = 1,
    };

    public static readonly IReadOnlyDictionary<string, Thickness> Paddings = new Dictionary<string, Thickness>
    {
        ["ControlPadding"] = new(16, 10),
        ["CardPadding"]    = new(18, 16),
        ["TrackPadding"]   = new(3),
        ["ZonePadding"]    = new(6),
        // A border is a Thickness, not a width, and a style setter takes the
        // resource as it finds it: a double here throws at styling time.
        ["HairlineThickness"] = new(1),
    };

    // "" means whatever the operating system hands us, which is what the app
    // has always used. A name here restyles every word in the program.
    public const string FontFamily = "";

    public static void RegisterInto(Application app)
    {
        var d = new ResourceDictionary();
        foreach (var (key, value) in Numbers) d[key] = value;
        foreach (var (key, value) in Paddings) d[key] = value;
        foreach (var (key, value) in Numbers)
            if (key.EndsWith("Radius")) d[key + "Corner"] = new CornerRadius(value);
        d["AppFont"] = FontFamily.Length > 0
            ? new FontFamily(FontFamily) : Avalonia.Media.FontFamily.Default;
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
