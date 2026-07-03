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
