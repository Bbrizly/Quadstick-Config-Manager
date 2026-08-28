using System.Text.Json;
using System.Text.Json.Serialization;
using QuadStick.Application.Settings;
using QuadStick.Infrastructure.Files;

namespace QuadStick.Infrastructure.Settings;

/// <summary>Durable settings persistence with one-writer cross-process locking,
/// valid-primary backup, corrupt-file quarantine, and atomic publication.</summary>
public sealed class JsonAppSettingsStore : IAppSettingsStore
{
    readonly string _path;
    readonly TimeSpan _lockTimeout;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuadStickConfigManager", "settings.json");

    public JsonAppSettingsStore(string? path = null, TimeSpan? lockTimeout = null)
    {
        _path = path ?? DefaultPath;
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(2);
    }

    public AppSettingsLoadResult Load()
    {
        using var processLock = AcquireLock();
        var backup = BackupPath(_path);

        if (TryRead(_path, out var current, out var primaryInvalid))
            return new AppSettingsLoadResult(current!);

        if (primaryInvalid) Quarantine(_path);

        if (TryRead(backup, out var recovered, out _))
            return new AppSettingsLoadResult(
                recovered!,
                "Settings were recovered from the previous good copy.");

        if (primaryInvalid || File.Exists(_path) || File.Exists(backup))
            return new AppSettingsLoadResult(
                new AppSettings(),
                "Settings could not be read; defaults are in use.");

        return new AppSettingsLoadResult(new AppSettings());
    }

    public bool TrySave(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            using var processLock = AcquireLock();
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Promote only a verified primary to the recovery copy, and publish
            // that backup atomically too. A corrupt primary never destroys the
            // last known-good settings.
            if (TryRead(_path, out _, out _))
            {
                var primaryJson = File.ReadAllText(_path);
                AtomicFileWriter.Write(BackupPath(_path), primaryJson);
            }

            var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
            AtomicFileWriter.Write(_path, json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or TimeoutException or JsonException or NotSupportedException)
        {
            return false;
        }
    }

    FileStream AcquireLock()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var lockPath = _path + ".lock";
        var deadline = DateTime.UtcNow + _lockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
            catch (IOException ex)
            {
                throw new TimeoutException("Another QuadStick Config Manager process is using settings.", ex);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort. TrySave never promotes malformed primary data to .bak.
        }
    }
}

[JsonSourceGenerationOptions(IncludeFields = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(DriveLink))]
[JsonSerializable(typeof(Dictionary<string, DriveLink>))]
internal partial class SettingsJsonContext : JsonSerializerContext { }