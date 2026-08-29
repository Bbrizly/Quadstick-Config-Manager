using QuadStick.Application.Settings;

namespace QuadStick.Infrastructure.Settings;

/// <summary>Temporary compatibility facade over the real settings store. It
/// contains no persistence logic; callers are being migrated to the app-lifetime
/// AppSettingsSession composed at startup.</summary>
public static class Settings
{
    /// <summary>Test/render seam preserving the historical contract without
    /// putting persistence back in App. Null uses the per-user production path.</summary>
    public static string? PathOverride { get; set; }

    public static string DefaultPath => PathOverride ?? JsonAppSettingsStore.DefaultPath;

    public static string? LastLoadWarning { get; private set; }

    public static bool FailSavesForTest { get; set; }

    public static AppSettings Load(string? path = null)
    {
        var result = new JsonAppSettingsStore(path ?? PathOverride).Load();
        LastLoadWarning = result.Warning;
        return result.Settings;
    }

    public static void Save(AppSettings settings, string? path = null) =>
        _ = TrySave(settings, path);

    public static bool TrySave(AppSettings settings, string? path = null) =>
        !FailSavesForTest && new JsonAppSettingsStore(path ?? PathOverride).TrySave(settings);
}
