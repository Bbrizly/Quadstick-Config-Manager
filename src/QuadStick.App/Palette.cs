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
