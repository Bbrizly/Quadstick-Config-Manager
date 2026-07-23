// src/QuadStick.App/Theme.cs
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public bool DriveBackup = false;
    public Dictionary<string, DriveLink> DriveLinks = new(); // key: profile file path
}

// Per-profile Google Sheets backup state. Keyed by profile file path in
// AppSettings.DriveLinks.
public sealed class DriveLink
{
    public string SpreadsheetId = "";
    public string LastSeenModifiedTime = "";
    public bool BackupDirty = false;
    public bool LinkShared = false;
}

public static class Settings
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuadStickConfigManager", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(path ?? DefaultPath), SettingsJsonContext.Default.AppSettings) ?? new(); }
        catch { return new AppSettings(); }
    }

    // Best-effort save: a failed write is swallowed. Most callers want this,
    // because settings are a convenience and never worth crashing over.
    public static void Save(AppSettings s, string? path = null) => TrySave(s, path);

    // Same write, but reports whether it landed. Restore needs this: a profile
    // imported but not linked would fork a duplicate sheet on its next save, so
    // restore rolls back a file whose link state could not be persisted.
    public static bool TrySave(AppSettings s, string? path = null)
    {
        var p = path ?? DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(s, SettingsJsonContext.Default.AppSettings));
            return true;
        }
        catch { return false; }
    }
}

// Compile-time JSON metadata for AppSettings: lets Load/Save run with no
// reflection, so trimming and NativeAOT keep settings working. Case-insensitive
// so old files with lowercase "model"/"theme" still load.
[JsonSourceGenerationOptions(IncludeFields = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(DriveLink))]
[JsonSerializable(typeof(Dictionary<string, DriveLink>))]
internal partial class SettingsJsonContext : JsonSerializerContext { }

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
