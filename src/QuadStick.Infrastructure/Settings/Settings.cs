using System.Text.Json;
using System.Text.Json.Serialization;
using QuadStick.Infrastructure.Files;

namespace QuadStick.App;

// JSON persistence for AppSettings. Kept source-compatible while it moves out
// of the Avalonia assembly; the on-disk field names and location are unchanged.
public static class Settings
{
    static readonly object Gate = new();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuadStickConfigManager", "settings.json");

    /// <summary>A recoverable persistence problem from the most recent load.
    /// Presentation may surface this without making persistence depend on UI.</summary>
    public static string? LastLoadWarning { get; private set; }

    public static AppSettings Load(string? path = null)
    {
        lock (Gate)
        {
            var p = path ?? DefaultPath;
            var backup = BackupPath(p);
            LastLoadWarning = null;

            if (TryRead(p, out var current, out var primaryInvalid))
                return current!;

            // Bad JSON is not "no settings". Preserve it for diagnosis instead
            // of letting the next save overwrite the only evidence, then try the
            // last known-good copy.
            if (primaryInvalid)
                Quarantine(p);

            if (TryRead(backup, out var recovered, out _))
            {
                LastLoadWarning = "Settings were recovered from the previous good copy.";
                return recovered!;
            }

            if (primaryInvalid || File.Exists(p) || File.Exists(backup))
                LastLoadWarning = "Settings could not be read; defaults are in use.";

            return new AppSettings();
        }
    }

    public static void Save(AppSettings s, string? path = null) => TrySave(s, path);

    /// <summary>Test seam for the fail-closed settings branches.</summary>
    public static bool FailSavesForTest { get; set; }

    public static bool TrySave(AppSettings s, string? path = null)
    {
        if (FailSavesForTest) return false;
        var p = path ?? DefaultPath;

        lock (Gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                // Only promote a file we can actually deserialize to the backup.
                // A malformed primary must never replace the last good copy.
                if (TryRead(p, out _, out _))
                    File.Copy(p, BackupPath(p), overwrite: true);

                var json = JsonSerializer.Serialize(s, SettingsJsonContext.Default.AppSettings);
                AtomicFileWriter.Write(p, json);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    static string BackupPath(string path) => path + ".bak";

    static bool TryRead(string path, out AppSettings? settings, out bool invalid)
    {
        settings = null;
        invalid = false;
        if (!File.Exists(path)) return false;

        try
        {
            settings = JsonSerializer.Deserialize(
                File.ReadAllText(path), SettingsJsonContext.Default.AppSettings);
            if (settings is not null) return true;
            invalid = true;
            return false;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            invalid = true;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static void Quarantine(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var directory = Path.GetDirectoryName(path) ?? "";
            var name = Path.GetFileName(path);
            var quarantine = Path.Combine(directory,
                $"{name}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
            File.Move(path, quarantine, overwrite: false);
        }
        catch
        {
            // Recovery is best effort. The unreadable primary is never copied
            // over the good .bak by TrySave, so failure to quarantine is safe.
        }
    }
}

// Keep the production serialization contract exactly as it was: public fields,
// case-insensitive reads for older files, and compile-time metadata for trimming.
[JsonSourceGenerationOptions(IncludeFields = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(DriveLink))]
[JsonSerializable(typeof(Dictionary<string, DriveLink>))]
internal partial class SettingsJsonContext : JsonSerializerContext { }
