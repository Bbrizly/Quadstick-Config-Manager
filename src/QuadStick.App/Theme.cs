// src/QuadStick.App/Theme.cs
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace QuadStick.App;

public sealed class AppSettings
{
    public string Model = "FPS";
    public string Theme = "System";           // System | Light | Dark
    public int InterfaceScalePercent = 100;    // 100 | 125 | 150 | 200
    public bool ReduceMotion = false;
    public bool RememberWindow = true;
    public bool TutorialSeen = false;
    public bool DeviceCards = true;            // device view mappings as sentence cards
    public double? WinW, WinH, WinX, WinY;     // null = use window defaults
}

public static class Settings
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuadStickConfigManager", "settings.json");

    // Case-insensitive so old files with lowercase "model"/"theme" still load.
    static readonly JsonSerializerOptions Opts = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    public static AppSettings Load(string? path = null)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path ?? DefaultPath), Opts) ?? new(); }
        catch { return new AppSettings(); }
    }

    public static void Save(AppSettings s, string? path = null)
    {
        var p = path ?? DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(s, Opts));
        }
        catch { /* settings are a convenience, never fatal */ }
    }
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
