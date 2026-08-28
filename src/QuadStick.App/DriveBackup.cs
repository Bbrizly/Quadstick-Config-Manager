using System.Runtime.ExceptionServices;
using QuadStick.Infrastructure.Files;

namespace QuadStick.App;

/// <summary>Presentation compatibility facade for the remote-backup workflow.
/// It owns human prompts/status rendering; Application owns backup policy and
/// Infrastructure owns Google/filesystem mechanics.</summary>
public sealed class DriveBackup
{
    readonly DriveBackupWorkflow _workflow;
    readonly Func<string, string, Task<ConflictChoice>> _conflictPrompt;
    readonly Func<string, string, Task<bool>> _recreatePrompt;
    readonly Func<Task<bool>> _shareConfirm;
    readonly Action<string, bool> _status;

    const string ConflictTitle = "Sheet edited online";
    const string ConflictBody =
        "This profile's Google Sheet was edited online since your last backup. "
        + "Choose Yes to replace it with your copy (the online edits stay in "
        + "Drive revision history). Choose Cancel to keep the online version and "
        + "load it into the editor instead.";

    const string RecreateTitle = "Backup sheet not found";
    const string RecreateBody =
        "The Google Sheet for this profile could not be found. It may have been "
        + "deleted or moved to trash. Choose Yes to create a new sheet and back "
        + "up to it. Choose Cancel to turn backup off for this profile.";

    sealed class DelegateSettingsContext : IBackupSettingsContext
    {
        readonly Func<AppSettings> _get;
        readonly Func<bool> _save;
        public DelegateSettingsContext(Func<AppSettings> get, Func<bool> save)
        { _get = get; _save = save; }
        public AppSettings Current => _get();
        public bool TrySave() => _save();
    }

    public DriveBackup(
        DriveClient client,
        Func<AppSettings> getSettings,
        Func<bool> trySave,
        Func<string, string, Task<ConflictChoice>> conflictPrompt,
        Func<string, string, Task<bool>> recreatePrompt,
        Action<string, bool> status,
        Func<Task<bool>> shareConfirm)
    {
        _workflow = new DriveBackupWorkflow(
            new GoogleDriveBackupProvider(client),
            new DelegateSettingsContext(getSettings, trySave),
            new PhysicalProfileLibraryStore());
        _conflictPrompt = conflictPrompt;
        _recreatePrompt = recreatePrompt;
        _status = status;
        _shareConfirm = shareConfirm;
    }

    public async Task<PushResult> PushAsync(string profilePath, string csvText, CancellationToken ct = default)
    {
        ConflictChoice? conflict = null;
        bool? recreate = null;
        while (true)
        {
            var outcome = await _workflow.PushAsync(profilePath, csvText, conflict, recreate, ct);
            Emit(outcome.SettingsSaveFailed, outcome.Notice);
            switch (outcome.State)
            {
                case BackupPushState.RequiresConflictDecision:
                    conflict = await _conflictPrompt(ConflictTitle, ConflictBody);
                    continue;
                case BackupPushState.RequiresMissingRemoteDecision:
                    recreate = await _recreatePrompt(RecreateTitle, RecreateBody);
                    continue;
                default:
                    return ToPublic(outcome);
            }
        }
    }

    public async Task<PushResult?> RetryIfDirtyAsync(string profilePath, string csvText)
    {
        ConflictChoice? conflict = null;
        bool? recreate = null;
        while (true)
        {
            var outcome = await _workflow.RetryIfDirtyAsync(profilePath, csvText, conflict, recreate);
            if (outcome is null) return null;
            Emit(outcome.SettingsSaveFailed, outcome.Notice);
            switch (outcome.State)
            {
                case BackupPushState.RequiresConflictDecision:
                    conflict = await _conflictPrompt(ConflictTitle, ConflictBody);
                    continue;
                case BackupPushState.RequiresMissingRemoteDecision:
                    recreate = await _recreatePrompt(RecreateTitle, RecreateBody);
                    continue;
                default:
                    return ToPublic(outcome);
            }
        }
    }

    static PushResult ToPublic(BackupPushOutcome outcome) => outcome.State switch
    {
        BackupPushState.Pushed => new PushResult(PushResultKind.Pushed),
        BackupPushState.KeptOnline => new PushResult(PushResultKind.KeptOnline, outcome.DownloadedCsv),
        BackupPushState.RecreatedOff => new PushResult(PushResultKind.RecreatedOff),
        BackupPushState.Failed => new PushResult(PushResultKind.Failed),
        BackupPushState.Paused => new PushResult(PushResultKind.Paused),
        _ => throw new InvalidOperationException("A required backup decision was not resolved."),
    };

    public bool TryRecoverLink(string profilePath, string spreadsheetId)
    {
        var outcome = _workflow.TryRecoverLink(profilePath, spreadsheetId);
        if (outcome.SettingsSaveFailed) _status("Could not save backup settings.", true);
        if (outcome.Recovered) _status("Reconnected this profile to its Google Sheet.", false);
        return outcome.Recovered;
    }

    public string? LinkedSheetId(string profilePath) => _workflow.LinkedSheetId(profilePath);
    public bool Knows(string spreadsheetId) => _workflow.Knows(spreadsheetId);
    public string? LinkedSheetUrl(string profilePath) => _workflow.LinkedSheetUrl(profilePath);

    public async Task<string> ReadProfileAsync(string spreadsheetId, CancellationToken ct = default)
    {
        try { return await _workflow.ReadProfileAsync(spreadsheetId, ct); }
        catch (RemoteStorageException ex) { RethrowProvider(ex); throw; }
    }

    public async Task<ShareLinkResult> GetShareLinkAsync(
        string profilePath,
        string csvText,
        CancellationToken ct = default)
    {
        ConflictChoice? conflict = null;
        bool? recreate = null;
        bool? share = null;
        string? downloadedCsv = null;

        while (true)
        {
            var outcome = await _workflow.GetShareLinkAsync(
                profilePath, csvText, conflict, recreate, share, ct);
            downloadedCsv ??= outcome.DownloadedCsv;
            Emit(outcome.SettingsSaveFailed, outcome.Notice);

            switch (outcome.State)
            {
                case BackupShareState.RequiresConflictDecision:
                    conflict = await _conflictPrompt(ConflictTitle, ConflictBody);
                    continue;
                case BackupShareState.RequiresMissingRemoteDecision:
                    recreate = await _recreatePrompt(RecreateTitle, RecreateBody);
                    continue;
                case BackupShareState.RequiresShareConfirmation:
                    share = await _shareConfirm();
                    continue;
                case BackupShareState.Copied:
                    return new ShareLinkResult(ShareLinkKind.Copied, outcome.Url, outcome.Message,
                        outcome.DownloadedCsv ?? downloadedCsv);
                case BackupShareState.CopiedStale:
                    return new ShareLinkResult(ShareLinkKind.CopiedStale, outcome.Url, outcome.Message,
                        outcome.DownloadedCsv ?? downloadedCsv);
                case BackupShareState.Cancelled:
                    return new ShareLinkResult(ShareLinkKind.Cancelled, null, outcome.Message,
                        outcome.DownloadedCsv ?? downloadedCsv);
                case BackupShareState.Failed:
                    return new ShareLinkResult(ShareLinkKind.Failed, null, outcome.Message,
                        outcome.DownloadedCsv ?? downloadedCsv);
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    public async Task<List<DriveSheetInfo>> ListForPickerAsync(CancellationToken ct = default)
    {
        try { return await _workflow.ListForPickerAsync(ct); }
        catch (RemoteStorageException ex) { RethrowProvider(ex); throw; }
    }

    public Task<RestoreSummary> RestoreAsync(
        IReadOnlyList<(string Id, string Name)> picks,
        string libraryDir,
        CancellationToken ct = default) =>
        _workflow.RestoreAsync(picks, libraryDir, ct);

    void Emit(bool settingsSaveFailed, string? notice)
    {
        if (settingsSaveFailed) _status("Could not save backup settings.", true);
        if (!string.IsNullOrEmpty(notice)) _status(notice, true);
    }

    static void RethrowProvider(RemoteStorageException exception)
    {
        if (exception.InnerException is { } inner)
            ExceptionDispatchInfo.Capture(inner).Throw();
    }
}
