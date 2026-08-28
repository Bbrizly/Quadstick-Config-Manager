using System.Text;
using QuadStick.Format;

namespace QuadStick.App;

public enum RemoteStorageFailureKind { NotFound, AuthRevoked, Transient }

public sealed class RemoteStorageException : Exception
{
    public RemoteStorageFailureKind Kind { get; }
    public RemoteStorageException(RemoteStorageFailureKind kind, string message, Exception? inner = null)
        : base(message, inner) => Kind = kind;
}

public sealed record RemoteDriveSheet(string Id, string Name, string ModifiedTime);

public interface IDriveBackupProvider
{
    Task<string> CreateSpreadsheetAsync(string title, CancellationToken cancellationToken = default);
    Task PushTabsAsync(string id, IReadOnlyList<ProfileTab> tabs, CancellationToken cancellationToken = default);
    Task<string> GetModifiedTimeAsync(string id, CancellationToken cancellationToken = default);
    Task<byte[]> DownloadWorkbookAsync(string id, CancellationToken cancellationToken = default);
    Task ShareAnyoneReaderAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RemoteDriveSheet>> ListSpreadsheetsAsync(CancellationToken cancellationToken = default);
}

public interface IBackupSettingsContext
{
    AppSettings Current { get; }
    bool TrySave();
}

public enum ConflictChoice { KeepOnline, ReplaceWithMine }
public enum PushResultKind { Pushed, KeptOnline, RecreatedOff, Failed, Paused }

public sealed class PushResult
{
    public PushResultKind Kind { get; }
    public string? DownloadedCsv { get; }
    public PushResult(PushResultKind kind, string? downloadedCsv = null)
    {
        Kind = kind;
        DownloadedCsv = downloadedCsv;
    }
}

public enum ShareLinkKind { Copied, CopiedStale, Cancelled, Failed }

public sealed class ShareLinkResult
{
    public ShareLinkKind Kind { get; }
    public string? Url { get; }
    public string Message { get; }
    public string? DownloadedCsv { get; }
    public ShareLinkResult(ShareLinkKind kind, string? url, string message, string? downloadedCsv = null)
    {
        Kind = kind;
        Url = url;
        Message = message;
        DownloadedCsv = downloadedCsv;
    }
}

public sealed class DriveSheetInfo
{
    public string Id { get; }
    public string Name { get; }
    public string ModifiedTime { get; }
    public bool AlreadyLinked { get; }
    public DriveSheetInfo(string id, string name, string modifiedTime, bool alreadyLinked)
    {
        Id = id;
        Name = name;
        ModifiedTime = modifiedTime;
        AlreadyLinked = alreadyLinked;
    }
}

public sealed class RestoreSummary
{
    public IReadOnlyList<string> Imported { get; }
    public IReadOnlyList<(string Name, string Reason)> Skipped { get; }
    public IReadOnlyList<(string Name, string Reason)> Failed { get; }
    public string Message { get; }

    public RestoreSummary(
        IReadOnlyList<string> imported,
        IReadOnlyList<(string Name, string Reason)> skipped,
        IReadOnlyList<(string Name, string Reason)> failed)
    {
        Imported = imported;
        Skipped = skipped;
        Failed = failed;
        var parts = new List<string> { $"{imported.Count} imported" };
        if (skipped.Count > 0)
            parts.Add($"{skipped.Count} skipped: " + string.Join(", ", skipped.Select(s => $"{s.Name} {s.Reason}")));
        if (failed.Count > 0)
            parts.Add($"{failed.Count} failed: " + string.Join(", ", failed.Select(f => $"{f.Name} {f.Reason}")));
        Message = string.Join(", ", parts);
    }
}

public enum BackupPushState
{
    Pushed, KeptOnline, RecreatedOff, Failed, Paused,
    RequiresConflictDecision, RequiresMissingRemoteDecision,
}

public sealed record BackupPushOutcome(
    BackupPushState State,
    string? DownloadedCsv = null,
    string? Notice = null,
    bool SettingsSaveFailed = false);

public enum BackupShareState
{
    Copied, CopiedStale, Cancelled, Failed,
    RequiresConflictDecision, RequiresMissingRemoteDecision, RequiresShareConfirmation,
}

public sealed record BackupShareOutcome(
    BackupShareState State,
    string? Url = null,
    string Message = "",
    string? DownloadedCsv = null,
    string? Notice = null,
    bool SettingsSaveFailed = false);

public sealed record LinkRecoveryOutcome(bool Recovered, bool SettingsSaveFailed);

/// <summary>Provider-neutral remote backup application workflow. It owns
/// conflict/link/restore policy but never prompts a human and never touches a
/// physical filesystem or provider API directly.</summary>
public sealed class DriveBackupWorkflow
{
    readonly IDriveBackupProvider _provider;
    readonly IBackupSettingsContext _settings;
    readonly IProfileLibraryStore _library;
    readonly SemaphoreSlim _gate = new(1, 1);

    const string PendingMessage = "Backup pending";
    const string PausedMessage = "Backup paused. Reconnect to Google in Settings.";
    const string InvalidProfileMessage = "The profile data could not be read safely, so neither copy was changed.";

    sealed class OperationContext { public bool SettingsSaveFailed; }

    public DriveBackupWorkflow(
        IDriveBackupProvider provider,
        IBackupSettingsContext settings,
        IProfileLibraryStore library)
    {
        _provider = provider;
        _settings = settings;
        _library = library;
    }

    async Task<T> Locked<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await operation().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    void SaveState(OperationContext context)
    {
        if (!_settings.TrySave()) context.SettingsSaveFailed = true;
    }

    public Task<BackupPushOutcome> PushAsync(
        string profilePath,
        string csvText,
        ConflictChoice? conflictDecision = null,
        bool? recreateMissing = null,
        CancellationToken cancellationToken = default) =>
        Locked(() => PushCoreAsync(profilePath, csvText, conflictDecision, recreateMissing,
            new OperationContext(), cancellationToken), cancellationToken);

    async Task<BackupPushOutcome> PushCoreAsync(
        string profilePath,
        string csvText,
        ConflictChoice? conflictDecision,
        bool? recreateMissing,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        settings.DriveLinks.TryGetValue(profilePath, out var link);
        try
        {
            if (link is null)
                return await CreateAndRecordAsync(profilePath, csvText, context, cancellationToken).ConfigureAwait(false);

            link.BackupDirty = true;
            SaveState(context);

            // Empty means the previous upload reached Google but fetching its
            // revision token did not. Comparing that upload against an older
            // token would manufacture a conflict with our own write. Replay the
            // local source of truth and establish a fresh token instead.
            if (string.IsNullOrEmpty(link.LastSeenModifiedTime))
                return await PushAndRecordAsync(link, csvText, context, cancellationToken).ConfigureAwait(false);

            var current = await _provider.GetModifiedTimeAsync(link.SpreadsheetId, cancellationToken).ConfigureAwait(false);
            if (current == link.LastSeenModifiedTime)
                return await PushAndRecordAsync(link, csvText, context, cancellationToken).ConfigureAwait(false);

            if (conflictDecision is null)
                return new BackupPushOutcome(BackupPushState.RequiresConflictDecision,
                    SettingsSaveFailed: context.SettingsSaveFailed);

            if (conflictDecision == ConflictChoice.ReplaceWithMine)
                return await PushAndRecordAsync(link, csvText, context, cancellationToken).ConfigureAwait(false);

            var online = await DownloadProfileAsync(link.SpreadsheetId, cancellationToken).ConfigureAwait(false);
            link.LastSeenModifiedTime = current;
            link.BackupDirty = false;
            SaveState(context);
            return new BackupPushOutcome(BackupPushState.KeptOnline, online,
                SettingsSaveFailed: context.SettingsSaveFailed);
        }
        catch (RemoteStorageException ex) when (ex.Kind == RemoteStorageFailureKind.NotFound)
        {
            if (recreateMissing is null)
                return new BackupPushOutcome(BackupPushState.RequiresMissingRemoteDecision,
                    SettingsSaveFailed: context.SettingsSaveFailed);

            if (!recreateMissing.Value)
            {
                _settings.Current.DriveLinks.Remove(profilePath);
                SaveState(context);
                return new BackupPushOutcome(BackupPushState.RecreatedOff,
                    SettingsSaveFailed: context.SettingsSaveFailed);
            }

            try
            {
                return await CreateAndRecordAsync(profilePath, csvText, context, cancellationToken).ConfigureAwait(false);
            }
            catch (RemoteStorageException create) when (create.Kind == RemoteStorageFailureKind.AuthRevoked)
            {
                return Paused(link, context);
            }
            catch (RemoteStorageException)
            {
                return FailPending(link, context);
            }
        }
        catch (RemoteStorageException ex) when (ex.Kind == RemoteStorageFailureKind.AuthRevoked)
        {
            return Paused(link, context);
        }
        catch (InvalidDataException)
        {
            if (link is not null) link.BackupDirty = true;
            SaveState(context);
            return new BackupPushOutcome(BackupPushState.Failed, Notice: InvalidProfileMessage,
                SettingsSaveFailed: context.SettingsSaveFailed);
        }
        catch (RemoteStorageException)
        {
            return FailPending(link, context);
        }
    }

    async Task<BackupPushOutcome> CreateAndRecordAsync(
        string profilePath,
        string csvText,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        // Parse before creating a remote sheet. An invalid/empty local profile
        // must not leave an orphaned blank spreadsheet behind.
        var tabs = Tabs(csvText);
        var title = Path.GetFileNameWithoutExtension(profilePath);
        var id = await _provider.CreateSpreadsheetAsync(title, cancellationToken).ConfigureAwait(false);
        var link = new DriveLink { SpreadsheetId = id, BackupDirty = true };
        _settings.Current.DriveLinks[profilePath] = link;
        SaveState(context);

        await _provider.PushTabsAsync(id, tabs, cancellationToken).ConfigureAwait(false);
        MarkRevisionUnknown(link, context);
        link.LastSeenModifiedTime = await _provider.GetModifiedTimeAsync(id, cancellationToken).ConfigureAwait(false);
        link.BackupDirty = false;
        SaveState(context);
        return new BackupPushOutcome(BackupPushState.Pushed,
            SettingsSaveFailed: context.SettingsSaveFailed);
    }

    async Task<BackupPushOutcome> PushAndRecordAsync(
        DriveLink link,
        string csvText,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        await _provider.PushTabsAsync(link.SpreadsheetId, Tabs(csvText), cancellationToken).ConfigureAwait(false);
        MarkRevisionUnknown(link, context);
        link.LastSeenModifiedTime = await _provider.GetModifiedTimeAsync(link.SpreadsheetId, cancellationToken).ConfigureAwait(false);
        link.BackupDirty = false;
        SaveState(context);
        return new BackupPushOutcome(BackupPushState.Pushed,
            SettingsSaveFailed: context.SettingsSaveFailed);
    }

    void MarkRevisionUnknown(DriveLink link, OperationContext context)
    {
        // Persist this *after* the value write and before asking Google for its
        // revision. If the metadata read fails, the next retry knows that the
        // apparent remote change may be our own successful write and replays
        // local state instead of showing a false conflict.
        link.LastSeenModifiedTime = "";
        link.BackupDirty = true;
        SaveState(context);
    }

    static List<ProfileTab> Tabs(string csvText)
    {
        if (string.IsNullOrWhiteSpace(csvText))
            throw new InvalidDataException("Refusing to back up an empty profile.");
        var file = ProfileFile.Load(csvText);
        if (file.Document.Sheets.Count == 0)
            throw new InvalidDataException("Refusing to back up a file with no readable profile sheets.");
        var tabs = SheetTabs.Split(file).ToList();
        if (!tabs.Any(t => t.Rows.Any(r => r.Any(c => !string.IsNullOrWhiteSpace(c)))))
            throw new InvalidDataException("Refusing to back up an empty profile.");
        return tabs;
    }

    async Task<string> DownloadProfileAsync(string id, CancellationToken cancellationToken)
    {
        var bytes = await _provider.DownloadWorkbookAsync(id, cancellationToken).ConfigureAwait(false);
        if (!Xlsx.LooksLikeXlsx(bytes)) return Encoding.UTF8.GetString(bytes);
        using var stream = new MemoryStream(bytes);
        return Xlsx.ToCsv(stream);
    }

    BackupPushOutcome FailPending(DriveLink? link, OperationContext context)
    {
        if (link is not null) link.BackupDirty = true;
        SaveState(context);
        return new BackupPushOutcome(BackupPushState.Failed, Notice: PendingMessage,
            SettingsSaveFailed: context.SettingsSaveFailed);
    }

    BackupPushOutcome Paused(DriveLink? link, OperationContext context)
    {
        if (link is not null) link.BackupDirty = true;
        SaveState(context);
        return new BackupPushOutcome(BackupPushState.Paused, Notice: PausedMessage,
            SettingsSaveFailed: context.SettingsSaveFailed);
    }

    public Task<BackupPushOutcome?> RetryIfDirtyAsync(
        string profilePath,
        string csvText,
        ConflictChoice? conflictDecision = null,
        bool? recreateMissing = null) =>
        Locked<BackupPushOutcome?>(async () =>
        {
            var settings = _settings.Current;
            if (!settings.DriveLinks.TryGetValue(profilePath, out var link) || !link.BackupDirty)
                return null;
            return await PushCoreAsync(profilePath, csvText, conflictDecision, recreateMissing,
                new OperationContext(), CancellationToken.None).ConfigureAwait(false);
        }, CancellationToken.None);

    public LinkRecoveryOutcome TryRecoverLink(string profilePath, string spreadsheetId)
    {
        if (string.IsNullOrWhiteSpace(spreadsheetId)) return new LinkRecoveryOutcome(false, false);
        var settings = _settings.Current;
        if (settings.DriveLinks.ContainsKey(profilePath)) return new LinkRecoveryOutcome(false, false);

        string? oldPath = null;
        foreach (var (path, link) in settings.DriveLinks)
            if (string.Equals(link.SpreadsheetId, spreadsheetId, StringComparison.Ordinal))
            {
                if (_library.Exists(path)) return new LinkRecoveryOutcome(false, false);
                oldPath = path;
            }
        if (oldPath is null) return new LinkRecoveryOutcome(false, false);

        settings.DriveLinks[profilePath] = settings.DriveLinks[oldPath];
        settings.DriveLinks.Remove(oldPath);
        var context = new OperationContext();
        SaveState(context);
        return new LinkRecoveryOutcome(true, context.SettingsSaveFailed);
    }

    public string? LinkedSheetId(string profilePath) =>
        _settings.Current.DriveLinks.TryGetValue(profilePath, out var link) ? link.SpreadsheetId : null;

    public bool Knows(string spreadsheetId) =>
        _settings.Current.DriveLinks.Values.Any(l =>
            string.Equals(l.SpreadsheetId, spreadsheetId, StringComparison.Ordinal));

    public Task<string> ReadProfileAsync(string spreadsheetId, CancellationToken cancellationToken = default) =>
        Locked(() => DownloadProfileAsync(spreadsheetId, cancellationToken), cancellationToken);

    public string? LinkedSheetUrl(string profilePath) =>
        _settings.Current.DriveLinks.TryGetValue(profilePath, out var link) ? Url(link.SpreadsheetId) : null;

    static string Url(string spreadsheetId) =>
        $"https://docs.google.com/spreadsheets/d/{spreadsheetId}/edit?usp=sharing";

    public Task<BackupShareOutcome> GetShareLinkAsync(
        string profilePath,
        string csvText,
        ConflictChoice? conflictDecision = null,
        bool? recreateMissing = null,
        bool? allowLinkSharing = null,
        CancellationToken cancellationToken = default) =>
        Locked(() => GetShareLinkCoreAsync(profilePath, csvText, conflictDecision, recreateMissing,
            allowLinkSharing, new OperationContext(), cancellationToken), cancellationToken);

    async Task<BackupShareOutcome> GetShareLinkCoreAsync(
        string profilePath,
        string csvText,
        ConflictChoice? conflictDecision,
        bool? recreateMissing,
        bool? allowLinkSharing,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        settings.DriveLinks.TryGetValue(profilePath, out var link);
        string? keptOnlineCsv = null;
        bool stale = false;
        string? staleNotice = null;

        if (link is null)
        {
            var first = await PushCoreAsync(profilePath, csvText, conflictDecision, recreateMissing,
                context, cancellationToken).ConfigureAwait(false);
            if (first.State == BackupPushState.RequiresConflictDecision)
                return RequiredShare(BackupShareState.RequiresConflictDecision, first, keptOnlineCsv);
            if (first.State == BackupPushState.RequiresMissingRemoteDecision)
                return RequiredShare(BackupShareState.RequiresMissingRemoteDecision, first, keptOnlineCsv);
            if (first.State != BackupPushState.Pushed)
                return new BackupShareOutcome(BackupShareState.Failed, Message:
                    "Could not create the Google Sheet, so there is nothing to share yet.",
                    Notice: first.Notice, SettingsSaveFailed: context.SettingsSaveFailed);
            link = settings.DriveLinks[profilePath];
        }
        else if (link.BackupDirty)
        {
            var push = await PushCoreAsync(profilePath, csvText, conflictDecision, recreateMissing,
                context, cancellationToken).ConfigureAwait(false);
            if (push.State == BackupPushState.RequiresConflictDecision)
                return RequiredShare(BackupShareState.RequiresConflictDecision, push, keptOnlineCsv);
            if (push.State == BackupPushState.RequiresMissingRemoteDecision)
                return RequiredShare(BackupShareState.RequiresMissingRemoteDecision, push, keptOnlineCsv);
            if (push.State == BackupPushState.KeptOnline) keptOnlineCsv = push.DownloadedCsv;
            else if (push.State == BackupPushState.RecreatedOff)
                return new BackupShareOutcome(BackupShareState.Failed, Message:
                    "Backup was turned off for this profile, so nothing was copied.",
                    SettingsSaveFailed: context.SettingsSaveFailed);
            else if (push.State is BackupPushState.Failed or BackupPushState.Paused)
            {
                stale = true;
                staleNotice = push.Notice;
                // A stale URL is useful only when this exact sheet was already
                // shared. Otherwise returning it before the permission flow
                // hands the user a private link that looks successfully shared.
                if (link.LinkShared)
                    return new BackupShareOutcome(BackupShareState.CopiedStale, Url(link.SpreadsheetId),
                        "Link copied. Your latest changes are not uploaded yet (backup pending).",
                        Notice: push.Notice, SettingsSaveFailed: context.SettingsSaveFailed);
            }
            link = settings.DriveLinks[profilePath];
        }

        if (!link.LinkShared)
        {
            if (allowLinkSharing is null)
                return new BackupShareOutcome(BackupShareState.RequiresShareConfirmation,
                    DownloadedCsv: keptOnlineCsv, Notice: staleNotice,
                    SettingsSaveFailed: context.SettingsSaveFailed);
            if (!allowLinkSharing.Value)
                return new BackupShareOutcome(BackupShareState.Cancelled,
                    DownloadedCsv: keptOnlineCsv, Notice: staleNotice,
                    SettingsSaveFailed: context.SettingsSaveFailed);
            try
            {
                await _provider.ShareAnyoneReaderAsync(link.SpreadsheetId, cancellationToken).ConfigureAwait(false);
                link.LinkShared = true;
                SaveState(context); // permission succeeded; persist that fact immediately

                try
                {
                    link.LastSeenModifiedTime = await _provider.GetModifiedTimeAsync(
                        link.SpreadsheetId, cancellationToken).ConfigureAwait(false);
                    SaveState(context);
                }
                catch (RemoteStorageException)
                {
                    // Permission is live even if its revision token could not be
                    // refreshed. Force the next backup to replay local truth
                    // rather than manufacturing a conflict with this metadata change.
                    link.LastSeenModifiedTime = "";
                    link.BackupDirty = true;
                    SaveState(context);
                }
            }
            catch (RemoteStorageException)
            {
                return new BackupShareOutcome(BackupShareState.Failed, Message:
                    "Could not turn on link sharing, so nothing was copied.", DownloadedCsv: keptOnlineCsv,
                    SettingsSaveFailed: context.SettingsSaveFailed);
            }
        }

        return new BackupShareOutcome(stale ? BackupShareState.CopiedStale : BackupShareState.Copied,
            Url(link.SpreadsheetId),
            stale ? "Link copied. Your latest changes are not uploaded yet (backup pending)." : "Link copied.",
            keptOnlineCsv, staleNotice, context.SettingsSaveFailed);
    }

    static BackupShareOutcome RequiredShare(
        BackupShareState state,
        BackupPushOutcome push,
        string? downloadedCsv) =>
        new(state, DownloadedCsv: downloadedCsv ?? push.DownloadedCsv,
            Notice: push.Notice, SettingsSaveFailed: push.SettingsSaveFailed);

    public Task<List<DriveSheetInfo>> ListForPickerAsync(CancellationToken cancellationToken = default) =>
        Locked(() => ListForPickerCoreAsync(cancellationToken), cancellationToken);

    async Task<List<DriveSheetInfo>> ListForPickerCoreAsync(CancellationToken cancellationToken)
    {
        var sheets = await _provider.ListSpreadsheetsAsync(cancellationToken).ConfigureAwait(false);
        var linkedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, link) in _settings.Current.DriveLinks)
            if (_library.Exists(path)) linkedIds.Add(link.SpreadsheetId);

        return sheets.Select(s => new DriveSheetInfo(
            s.Id, s.Name, s.ModifiedTime, linkedIds.Contains(s.Id))).ToList();
    }

    public Task<RestoreSummary> RestoreAsync(
        IReadOnlyList<(string Id, string Name)> picks,
        string libraryDirectory,
        CancellationToken cancellationToken = default) =>
        Locked(() => RestoreCoreAsync(picks, libraryDirectory, cancellationToken), cancellationToken);

    async Task<RestoreSummary> RestoreCoreAsync(
        IReadOnlyList<(string Id, string Name)> picks,
        string libraryDirectory,
        CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        var imported = new List<string>();
        var skipped = new List<(string Name, string Reason)>();
        var failed = new List<(string Name, string Reason)>();

        _library.EnsureDirectory(libraryDirectory);
        var onDisk = new HashSet<string>(
            _library.ListCsvFiles(libraryDirectory).Select(path => Path.GetFileName(path)!),
            StringComparer.OrdinalIgnoreCase);
        var batchNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pick in picks)
        {
            var reportName = Path.GetFileNameWithoutExtension(SafeFileName.ForCsv(pick.Name));
            try
            {
                var csv = await DownloadProfileAsync(pick.Id, cancellationToken).ConfigureAwait(false);
                ProfileFile parsed;
                try { parsed = ProfileFile.Load(csv); }
                catch { failed.Add((reportName, "could not read the sheet")); continue; }
                if (parsed.Document.Sheets.Count == 0)
                { failed.Add((reportName, "not a valid profile")); continue; }

                var fileName = SafeFileName.ForCsv(pick.Name, batchNames);
                batchNames.Add(fileName);
                reportName = Path.GetFileNameWithoutExtension(fileName);
                if (onDisk.Contains(fileName))
                { skipped.Add((reportName, "already exists")); continue; }

                var modifiedTime = await _provider.GetModifiedTimeAsync(pick.Id, cancellationToken).ConfigureAwait(false);
                var destination = Path.Combine(libraryDirectory, fileName);

                // The snapshot above is only for fast/user-friendly reporting.
                // This create-only publication is the actual no-overwrite rule:
                // a file appearing during the download is never replaced.
                if (!_library.TryCreate(destination, csv))
                {
                    skipped.Add((reportName, "already exists"));
                    onDisk.Add(fileName);
                    continue;
                }

                settings.DriveLinks[destination] = new DriveLink
                {
                    SpreadsheetId = pick.Id,
                    LastSeenModifiedTime = modifiedTime,
                    BackupDirty = false,
                    LinkShared = false,
                };

                if (!_settings.TrySave())
                {
                    // Never roll back by deleting the destination: another
                    // process can legitimately replace it after TryCreate, and
                    // deleting then would destroy data we did not write. The
                    // imported profile is still safe on disk; only its Drive
                    // association failed to persist.
                    settings.DriveLinks.Remove(destination);
                    onDisk.Add(fileName);
                    failed.Add((reportName, "profile saved, but could not save its Drive link"));
                    continue;
                }

                onDisk.Add(fileName);
                imported.Add(reportName);
            }
            catch (RemoteStorageException)
            {
                failed.Add((reportName, "download failed"));
            }
            catch (InvalidDataException)
            {
                failed.Add((reportName, "could not read the sheet"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add((reportName, "could not write the file"));
            }
        }

        return new RestoreSummary(imported, skipped, failed);
    }
}
