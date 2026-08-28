using QuadStick.Application.Settings;

namespace QuadStick.Infrastructure.Settings;

/// <summary>Temporary compatibility facade over the real settings store. It
/// contains no persistence logic; callers are being migrated to the app-lifetime
/// AppSettingsSession composed at startup.</summary>
public static class Settings
{
    public static string DefaultPath => JsonAppSettingsStore.DefaultPath;

    public static string? LastLoadWarning { get; private set; }

    public static bool FailSavesForTest { get; set; }

    public static AppSettings Load(string? path = null)
    {
        var result = new JsonAppSettingsStore(path).Load();
        LastLoadWarning = result.Warning;
        return result.Settings;
    }

    public static void Save(AppSettings settings, string? path = null) =>
        _ = TrySave(settings, path);

    public static bool TrySave(AppSettings settings, string? path = null) =>
        !FailSavesForTest && new JsonAppSettingsStore(path).TrySave(settings);
}