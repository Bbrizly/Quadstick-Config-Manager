using System.Net;
using QuadStick.Format;

namespace QuadStick.App;

// The background backup engine. UI-free on purpose: it holds no Avalonia
// reference so tests run without a windowing stack. MainWindow owns the
// prompts, the status line, and the rescue-and-reload work; this class owns
// the Drive calls and the per-profile link state.
//
// One rule runs through every path here: a local save must never wait on the
// network and a backup failure must never reach the save. So every failure is
// swallowed into backupDirty plus a status message, and the flag retries on
// the next save, on open, and on Reconnect.
public sealed class DriveBackup
{
    readonly DriveClient _client;
    readonly Func<AppSettings> _getSettings;
    readonly Action _saveSettings;
    // title, body -> the user's pick. MainWindow maps these onto its dialog.
    readonly Func<string, string, Task<ConflictChoice>> _conflictPrompt;
    // title, body -> true recreate as a new sheet, false turn backup off here.
    readonly Func<string, string, Task<bool>> _recreatePrompt;
    // "Anyone with this link can view (read only). Turn on sharing and copy?"
    // -> the user's yes/no. Called at most once per sheet, the first time a
    // share link is copied. Marshalled to the UI thread by the caller.
    readonly Func<Task<bool>> _shareConfirm;
    // message, isWarning. Marshalled to the UI thread by the caller.
    readonly Action<string, bool> _status;

    const string ConflictTitle = "Sheet edited online";
    // The buttons are fixed Yes/Cancel, so the mapping lives in the words.
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

    const string PendingMessage = "Backup pending";
    const string PausedMessage = "Backup paused. Reconnect to Google in Settings.";

    public DriveBackup(
        DriveClient client,
        Func<AppSettings> getSettings,
        Action saveSettings,
        Func<string, string, Task<ConflictChoice>> conflictPrompt,
        Func<string, string, Task<bool>> recreatePrompt,
        Action<string, bool> status,
        Func<Task<bool>> shareConfirm)
    {
        _client = client;
        _getSettings = getSettings;
        _saveSettings = saveSettings;
        _conflictPrompt = conflictPrompt;
        _recreatePrompt = recreatePrompt;
        _status = status;
        _shareConfirm = shareConfirm;
    }

    // Push one profile's grid to its sheet, following the spec Flow exactly.
    // Returns a result the caller can act on; KeptOnline carries the sheet CSV
    // because the rescue copy, atomic overwrite, and editor reload need
    // MainWindow state this class does not hold.
    public async Task<PushResult> PushAsync(string profilePath, string csvText, CancellationToken ct = default)
    {
        var settings = _getSettings();
        settings.DriveLinks.TryGetValue(profilePath, out var link);
        try
        {
            if (link is null)
                return await CreateAndRecordAsync(profilePath, csvText, ct);

            // Conflict check: read the sheet's modifiedTime before touching it.
            var current = await _client.GetModifiedTimeAsync(link.SpreadsheetId, ct);
            if (current == link.LastSeenModifiedTime)
                return await PushAndRecordAsync(link, csvText, ct);

            // Edited online since our last write. Ask once.
            var choice = await _conflictPrompt(ConflictTitle, ConflictBody);
            if (choice == ConflictChoice.ReplaceWithMine)
                return await PushAndRecordAsync(link, csvText, ct);

            // Keep online: download the sheet and hand the bytes back. The
            // caller rescues the local file, overwrites it, and reloads. Record
            // the online time now so the next local save pushes without a
            // second, self-inflicted conflict prompt. Mapping stays put.
            var online = await _client.DownloadCsvAsync(link.SpreadsheetId, ct);
            link.LastSeenModifiedTime = current;
            link.BackupDirty = false;
            _saveSettings();
            return new PushResult(PushResultKind.KeptOnline, online);
        }
        catch (DriveApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return await Handle404Async(profilePath, csvText, link, ct);
        }
        catch (GoogleAuthRevokedException)
        {
            return Paused(link);
        }
        catch (Exception ex) when (ex is DriveApiException or HttpRequestException or TaskCanceledException)
        {
            return FailPending(link);
        }
    }

    // 404 on push: never silently recreate (the id may be trashed, revoked, or
    // a stale link others share). Ask once.
    async Task<PushResult> Handle404Async(string profilePath, string csvText, DriveLink? link, CancellationToken ct)
    {
        var recreate = await _recreatePrompt(RecreateTitle, RecreateBody);
        if (!recreate)
        {
            // Turn backup off for this profile: drop the link, nothing dirty.
            _getSettings().DriveLinks.Remove(profilePath);
            _saveSettings();
            return new PushResult(PushResultKind.RecreatedOff);
        }
        try
        {
            return await CreateAndRecordAsync(profilePath, csvText, ct);
        }
        catch (GoogleAuthRevokedException)
        {
            return Paused(link);
        }
        catch (Exception ex) when (ex is DriveApiException or HttpRequestException or TaskCanceledException)
        {
            return FailPending(link);
        }
    }

    // First backup of a profile: create the sheet named after the file, push
    // the grid, then read back our own write's modifiedTime and record it.
    async Task<PushResult> CreateAndRecordAsync(string profilePath, string csvText, CancellationToken ct)
    {
        var title = Path.GetFileNameWithoutExtension(profilePath);
        var id = await _client.CreateSpreadsheetAsync(title, ct);
        await _client.PushGridAsync(id, Csv.Parse(csvText), ct);
        var mt = await _client.GetModifiedTimeAsync(id, ct);
        _getSettings().DriveLinks[profilePath] = new DriveLink
        {
            SpreadsheetId = id,
            LastSeenModifiedTime = mt,
            BackupDirty = false,
        };
        _saveSettings();
        return new PushResult(PushResultKind.Pushed);
    }

    // Silent push to an existing sheet, then re-read modifiedTime so the next
    // conflict check compares against our own write, not the stale value.
    async Task<PushResult> PushAndRecordAsync(DriveLink link, string csvText, CancellationToken ct)
    {
        await _client.PushGridAsync(link.SpreadsheetId, Csv.Parse(csvText), ct);
        link.LastSeenModifiedTime = await _client.GetModifiedTimeAsync(link.SpreadsheetId, ct);
        link.BackupDirty = false;
        _saveSettings();
        return new PushResult(PushResultKind.Pushed);
    }

    // 403/429/5xx/network: generic failure. Mark dirty, save, show pending.
    PushResult FailPending(DriveLink? link)
    {
        if (link != null) link.BackupDirty = true;
        _saveSettings();
        _status(PendingMessage, true);
        return new PushResult(PushResultKind.Failed);
    }

    // Revoked or expired token: pause backup, point the user at Reconnect.
    PushResult Paused(DriveLink? link)
    {
        if (link != null) link.BackupDirty = true;
        _saveSettings();
        _status(PausedMessage, true);
        return new PushResult(PushResultKind.Paused);
    }

    // Retry on profile open. No-op unless the link exists and is dirty; then a
    // normal push, which follows the same conflict rules. Returns null when
    // there was nothing to retry so the caller can skip the KeptOnline handling.
    public async Task<PushResult?> RetryIfDirtyAsync(string profilePath, string csvText)
    {
        var settings = _getSettings();
        if (settings.DriveLinks.TryGetValue(profilePath, out var link) && link.BackupDirty)
            return await PushAsync(profilePath, csvText);
        return null;
    }

    // Renames done inside the app move the link with the file. Path is the key.
    public void OnRenamed(string oldPath, string newPath)
    {
        var settings = _getSettings();
        if (settings.DriveLinks.Remove(oldPath, out var link))
        {
            settings.DriveLinks[newPath] = link;
            _saveSettings();
        }
    }

    // The share URL for a linked profile, or null when it has no sheet yet.
    // Used by "Open in Google Sheets".
    public string? LinkedSheetUrl(string profilePath) =>
        _getSettings().DriveLinks.TryGetValue(profilePath, out var link) ? Url(link.SpreadsheetId) : null;

    static string Url(string spreadsheetId) =>
        $"https://docs.google.com/spreadsheets/d/{spreadsheetId}/edit?usp=sharing";

    // "Copy share link", the spec's five step sequence. Returns the URL to put
    // on the clipboard plus a message, or a Cancelled/Failed result. Step 1,
    // the local save, is the caller's job: saving is UI and the state map is
    // keyed by path, so the caller saves (which names the file) before this
    // runs. No path, no sheet.
    public async Task<ShareLinkResult> GetShareLinkAsync(string profilePath, string csvText, CancellationToken ct = default)
    {
        var settings = _getSettings();
        settings.DriveLinks.TryGetValue(profilePath, out var link);

        // Step 2: not linked yet. Run the first backup (create + push) through
        // the existing path. A sheet that never held the profile would share as
        // a blank, so a failed first push copies nothing.
        if (link is null)
        {
            var first = await PushAsync(profilePath, csvText, ct);
            if (first.Kind != PushResultKind.Pushed)
                return new ShareLinkResult(ShareLinkKind.Failed, null,
                    "Could not create the Google Sheet, so there is nothing to share yet.");
            link = settings.DriveLinks[profilePath];
        }
        // Step 3: linked but the last backup did not land. Push. If it fails,
        // still copy the link but say the latest changes are not up yet; a
        // known good earlier backup beats no link offline.
        else if (link.BackupDirty)
        {
            var push = await PushAsync(profilePath, csvText, ct);
            if (push.Kind is PushResultKind.Failed or PushResultKind.Paused)
                return new ShareLinkResult(ShareLinkKind.CopiedStale, Url(link.SpreadsheetId),
                    "Link copied. Your latest changes are not uploaded yet (backup pending).");
        }

        // Step 4: share the sheet once. Anyone with the link can view (read
        // only). The linked-and-clean path reaches here with no network call
        // yet, so an already shared sheet is a pure clipboard write below.
        if (!link.LinkShared)
        {
            if (!await _shareConfirm())
                return new ShareLinkResult(ShareLinkKind.Cancelled, null, "");
            try
            {
                await _client.ShareAnyoneReaderAsync(link.SpreadsheetId, ct);
                link.LinkShared = true;
                // The grant can bump modifiedTime; re-read it now so the next
                // save does not see a phantom online edit and prompt a
                // self-inflicted conflict.
                link.LastSeenModifiedTime = await _client.GetModifiedTimeAsync(link.SpreadsheetId, ct);
                _saveSettings();
            }
            catch (Exception ex) when (ex is DriveApiException or HttpRequestException or TaskCanceledException or GoogleAuthRevokedException)
            {
                // An unshared link is useless, so copy nothing.
                return new ShareLinkResult(ShareLinkKind.Failed, null,
                    "Could not turn on link sharing, so nothing was copied.");
            }
        }

        // Step 5: hand back the URL for the clipboard.
        return new ShareLinkResult(ShareLinkKind.Copied, Url(link.SpreadsheetId), "Link copied.");
    }
}

// The user's pick on the conflict prompt.
public enum ConflictChoice { KeepOnline, ReplaceWithMine }

// What a push did, enough for the caller and the tests to branch on.
public enum PushResultKind { Pushed, KeptOnline, RecreatedOff, Failed, Paused }

public sealed class PushResult
{
    public PushResultKind Kind { get; }
    // Only set for KeptOnline: the downloaded sheet CSV the caller writes over
    // the local file after rescuing it.
    public string? DownloadedCsv { get; }

    public PushResult(PushResultKind kind, string? downloadedCsv = null)
    {
        Kind = kind;
        DownloadedCsv = downloadedCsv;
    }
}

// What "Copy share link" ended up doing. Copied and CopiedStale both carry a
// URL for the clipboard; Cancelled and Failed do not.
public enum ShareLinkKind { Copied, CopiedStale, Cancelled, Failed }

public sealed class ShareLinkResult
{
    public ShareLinkKind Kind { get; }
    // The share URL to put on the clipboard. Null on Cancelled and Failed.
    public string? Url { get; }
    // A ready-to-show line for the status bar.
    public string Message { get; }

    public ShareLinkResult(ShareLinkKind kind, string? url, string message)
    {
        Kind = kind;
        Url = url;
        Message = message;
    }
}
