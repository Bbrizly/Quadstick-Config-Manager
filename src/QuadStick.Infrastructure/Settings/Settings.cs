using System.Text.Json;
using System.Text.Json.Serialization;
using QuadStick.Infrastructure.Files;

namespace QuadStick.App;

// JSON persistence for AppSettings. Kept source-compatible while it moves out
// of the Avalonia assembly; the on-disk field names and location are unchanged.
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

    public static void Save(AppSettings s, string? path = null) => TrySave(s, path);

    /// <summary>Test seam for the fail-closed settings branches.</summary>
    public static bool FailSavesForTest { get; set; }

    public static bool TrySave(AppSettings s, string? path = null)
    {
        if (FailSavesForTest) return false;
        var p = path ?? DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            AtomicFileWriter.Write(p, JsonSerializer.Serialize(s, SettingsJsonContext.Default.AppSettings));
            return true;
        }
        catch { return false; }
    }
}

// Keep the production serialization contract exactly as it was: public fields,
// case-insensitive reads for older files, and compile-time metadata for trimming.
[JsonSourceGenerationOptions(IncludeFields = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(DriveLink))]
[JsonSerializable(typeof(Dictionary<string, DriveLink>))]
internal partial class SettingsJsonContext : JsonSerializerContext { }
