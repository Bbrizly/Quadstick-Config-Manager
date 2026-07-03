// src/QuadStick.App/Theme.cs
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public double? WinW, WinH, WinX, WinY;     // null = use window defaults
}

public static class Settings
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuadStickConfigManager", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path ?? DefaultPath))!.AsObject();
            return new AppSettings
            {
                Model = node["model"]?.GetValue<string>() ?? "FPS",
                Theme = node["theme"]?.GetValue<string>() ?? "System",
                InterfaceScalePercent = node["InterfaceScalePercent"]?.GetValue<int>() ?? 100,
                ReduceMotion = node["ReduceMotion"]?.GetValue<bool>() ?? false,
                RememberWindow = node["RememberWindow"]?.GetValue<bool>() ?? true,
                TutorialSeen = node["TutorialSeen"]?.GetValue<bool>() ?? false,
                WinW = node["WinW"]?.GetValue<double>(),
                WinH = node["WinH"]?.GetValue<double>(),
                WinX = node["WinX"]?.GetValue<double>(),
                WinY = node["WinY"]?.GetValue<double>(),
            };
        }
        catch { return new AppSettings(); }
    }

    public static void Save(AppSettings s, string? path = null)
    {
        var p = path ?? DefaultPath;
        try
        {
            var node = new JsonObject
            {
                ["model"] = s.Model,
                ["theme"] = s.Theme,
                ["InterfaceScalePercent"] = s.InterfaceScalePercent,
                ["ReduceMotion"] = s.ReduceMotion,
                ["RememberWindow"] = s.RememberWindow,
                ["TutorialSeen"] = s.TutorialSeen,
                ["WinW"] = s.WinW,
                ["WinH"] = s.WinH,
                ["WinX"] = s.WinX,
                ["WinY"] = s.WinY,
            };
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
