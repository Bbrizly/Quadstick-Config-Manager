using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace QuadStick.App;

// Presentation-only theme application. Persisted settings and JSON storage live
// in Application/Infrastructure respectively; this file knows only Avalonia and
// localized labels shown by the Settings page.
public static class Theme
{
    /// <summary>The three appearances in the language currently being read.
    /// The persisted value remains System/Light/Dark, never the translated word.</summary>
    public static string[] Choices =>
        new[] { Strings.Theme_System, Strings.Theme_Light, Strings.Theme_Dark };

    public static string ChoiceAt(int index) => index switch
    {
        1 => "Light",
        2 => "Dark",
        _ => "System",
    };

    public static void RegisterInto(Avalonia.Application app)
    {
        var rd = new ResourceDictionary();
        rd.ThemeDictionaries[ThemeVariant.Light] = BuildVariant(Palette.Light);
        rd.ThemeDictionaries[ThemeVariant.Dark] = BuildVariant(Palette.Dark);
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
        Avalonia.Application.Current!.RequestedThemeVariant = choice switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
}
