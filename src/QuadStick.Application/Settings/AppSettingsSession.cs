namespace QuadStick.Application.Settings;

public sealed record AppSettingsLoadResult(
    AppSettings Settings,
    string? Warning = null);

public interface IAppSettingsStore
{
    AppSettingsLoadResult Load();
    bool TrySave(AppSettings settings);
}

/// <summary>
/// App-lifetime settings state. Mutations are applied to an isolated clone and
/// become visible only after persistence succeeds, preventing memory from
/// silently getting ahead of disk after a failed save.
/// </summary>
public sealed class AppSettingsSession
{
    readonly object _gate = new();
    readonly IAppSettingsStore _store;
    AppSettings _current;

    public string? LoadWarning { get; }

    public AppSettingsSession(IAppSettingsStore store)
    {
        _store = store;
        var loaded = store.Load();
        _current = loaded.Settings.DeepClone();
        LoadWarning = loaded.Warning;
    }

    public AppSettings Snapshot()
    {
        lock (_gate) return _current.DeepClone();
    }

    public bool TryUpdate(Action<AppSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            var candidate = _current.DeepClone();
            update(candidate);
            if (!_store.TrySave(candidate)) return false;
            _current = candidate;
            return true;
        }
    }

    public bool TryReplace(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            var candidate = settings.DeepClone();
            if (!_store.TrySave(candidate)) return false;
            _current = candidate;
            return true;
        }
    }
}

/// <summary>Purpose-specific remote-link store backed by the transactional
/// settings session. Callers never receive a mutable global AppSettings object.</summary>
public interface IDriveLinkStore
{
    DriveLink? Get(string profilePath);
    IReadOnlyDictionary<string, DriveLink> Snapshot();
    bool TrySet(string profilePath, DriveLink link);
    bool TryRemove(string profilePath);
    bool TryMove(string oldPath, string newPath);
}

public sealed class DriveLinkStore : IDriveLinkStore
{
    readonly AppSettingsSession _session;

    public DriveLinkStore(AppSettingsSession session) => _session = session;

    public DriveLink? Get(string profilePath)
    {
        var settings = _session.Snapshot();
        return settings.DriveLinks.TryGetValue(profilePath, out var link) ? link.DeepClone() : null;
    }

    public IReadOnlyDictionary<string, DriveLink> Snapshot() =>
        _session.Snapshot().DriveLinks.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.DeepClone(),
            StringComparer.Ordinal);

    public bool TrySet(string profilePath, DriveLink link) =>
        _session.TryUpdate(settings => settings.DriveLinks[profilePath] = link.DeepClone());

    public bool TryRemove(string profilePath) =>
        _session.TryUpdate(settings => settings.DriveLinks.Remove(profilePath));

    public bool TryMove(string oldPath, string newPath)
    {
        var changed = false;
        var persisted = _session.TryUpdate(settings =>
        {
            if (!settings.DriveLinks.TryGetValue(oldPath, out var link)
                || settings.DriveLinks.ContainsKey(newPath)) return;
            settings.DriveLinks[newPath] = link.DeepClone();
            settings.DriveLinks.Remove(oldPath);
            changed = true;
        });
        return changed && persisted;
    }
}