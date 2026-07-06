// src/QuadStick.App/Palette.cs
namespace QuadStick.App;

public static class Palette
{
    // Single source of truth. Keys match the DynamicResource names used in
    // App.axaml.cs (Theme.Build) and in styles.
    // "Instrument panel" palette: cool graphite surfaces + hairline borders,
    // not the warm-cream/soft-shadow default. Blue stays the accent hue
    // (existing users already read blue as "select/primary") but deepens to
    // a less generic instrument blue.
    public static readonly IReadOnlyDictionary<string, string> Light = new Dictionary<string, string>
    {
        ["AppBackground"] = "#E9EBED",
        ["Surface"]       = "#FFFFFF",
        ["SurfaceSubtle"] = "#F2F3F5",
        ["SurfaceBorder"] = "#C6CACF",
        ["TextPrimary"]   = "#16191C",
        ["TextSecondary"] = "#4E545B",
        ["Accent"]        = "#0B4F8A",
        ["AccentText"]    = "#084A80",
        ["OnAccent"]      = "#FFFFFF",
        ["Error"]         = "#A6291F",
        ["Success"]       = "#0F6B34",
        ["Warning"]       = "#7A4B00",
        // Distinct from Accent on purpose: a focus ring that matches an
        // accent-filled button's own fill/border is invisible on that button.
        ["Focus"]         = "#1E6FD1",
        ["OutputTint"]    = "#F3E2AE",
        ["FunctionTint"]  = "#F6DCE4",
        ["InputTint"]     = "#D3E6F5",
    };

    public static readonly IReadOnlyDictionary<string, string> Dark = new Dictionary<string, string>
    {
        ["AppBackground"] = "#17191B",
        ["Surface"]       = "#202224",
        ["SurfaceSubtle"] = "#26282B",
        ["SurfaceBorder"] = "#3C3F43",
        ["TextPrimary"]   = "#EEF0F2",
        ["TextSecondary"] = "#AEB3B8",
        ["Accent"]        = "#4FA8E0",
        ["AccentText"]    = "#7CC0EE",
        ["OnAccent"]      = "#062338",
        ["Error"]         = "#F2B4B0",
        ["Success"]       = "#7FDE9B",
        ["Warning"]       = "#E8C36A",
        ["Focus"]         = "#7CC0EE",
        ["OutputTint"]    = "#3C3420",
        ["FunctionTint"]  = "#3A2530",
        ["InputTint"]     = "#1E2F3F",
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
        // Real on-screen pairs the gate previously missed: the status bar
        // renders its colors on AppBackground, and the zone detail panel
        // puts secondary/accent text on SurfaceSubtle.
        ("Error",         "AppBackground", 4.5),
        ("Warning",       "AppBackground", 4.5),
        ("Success",       "AppBackground", 4.5),
        ("AccentText",    "AppBackground", 4.5),
        ("TextSecondary", "SurfaceSubtle", 4.5),
        ("AccentText",    "SurfaceSubtle", 4.5),
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
