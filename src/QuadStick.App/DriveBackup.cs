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
    // Persist settings, reporting success. Push paths treat it as best-effort
    // and ignore the result; restore checks it, because an imported-but-unlinked
    // profile would fork a duplicate sheet on its next save.
    readonly Func<bool> _trySave;
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

    // One Drive operation at a time. A save's background push and a share-link
    // copy can race on the same unlinked profile and each create its own sheet;
    // the gate serializes them so the second one sees the first one's link.
    // ponytail: one global gate, per-profile gates if backups ever feel slow.
    readonly SemaphoreSlim _gate = new(1, 1);

    async Task<T> Locked<T>(Func<Task<T>> op, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await op(); }
        finally { _gate.Release(); }
    }

    // A failed settings write surfaces in the status line instead of being
    // swallowed (spec rule). Restore checks _trySave itself because it must
    // also undo the file write.
    void SaveState()
    {
        if (!_trySave()) _status("Could not save backup settings.", true);
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
        _client = client;
        _getSettings = getSettings;
        _trySave = trySave;
        _conflictPrompt = conflictPrompt;
        _recreatePrompt = recreatePrompt;
        _status = status;
        _shareConfirm = shareConfirm;
    }

    // Push one profile's grid to its sheet, following the spec Flow exactly.
    // Returns a result the caller can act on; KeptOnline carries the sheet CSV
    // because the rescue copy, atomic overwrite, and editor reload need
    // MainWindow state this class does not hold.
    public Task<PushResult> PushAsync(string profilePath, string csvText, CancellationToken ct = default) =>
        Locked(() => PushCoreAsync(profilePath, csvText, ct), ct);

    async Task<PushResult> PushCoreAsync(string profilePath, string csvText, CancellationToken ct)
    {
        var settings = _getSettings();
        settings.DriveLinks.TryGetValue(profilePath, out var link);
        try
        {
            if (link is null)
                return await CreateAndRecordAsync(profilePath, csvText, ct);

            // Persist the dirty flag before any network work. A crash or quit
            // mid-push then still retries on the next launch; success below
            // clears it again.
            link.BackupDirty = true;
            SaveState();

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
            SaveState();
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
            SaveState();
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
        // Record the sheet the moment it exists, dirty until the push lands.
        // If the push or the time read fails past this point, the retry pushes
        // to THIS sheet instead of creating a second one.
        var link = new DriveLink { SpreadsheetId = id, BackupDirty = true };
        _getSettings().DriveLinks[profilePath] = link;
        SaveState();
        await _client.PushGridAsync(id, Csv.Parse(csvText), ct);
        link.LastSeenModifiedTime = await _client.GetModifiedTimeAsync(id, ct);
        link.BackupDirty = false;
        SaveState();
        return new PushResult(PushResultKind.Pushed);
    }

    // Silent push to an existing sheet, then re-read modifiedTime so the next
    // conflict check compares against our own write, not the stale value.
    async Task<PushResult> PushAndRecordAsync(DriveLink link, string csvText, CancellationToken ct)
    {
        await _client.PushGridAsync(link.SpreadsheetId, Csv.Parse(csvText), ct);
        link.LastSeenModifiedTime = await _client.GetModifiedTimeAsync(link.SpreadsheetId, ct);
        link.BackupDirty = false;
        SaveState();
        return new PushResult(PushResultKind.Pushed);
    }

    // 403/429/5xx/network: generic failure. Mark dirty, save, show pending.
    PushResult FailPending(DriveLink? link)
    {
        if (link != null) link.BackupDirty = true;
        SaveState();
        _status(PendingMessage, true);
        return new PushResult(PushResultKind.Failed);
    }

    // Revoked or expired token: pause backup, point the user at Reconnect.
    PushResult Paused(DriveLink? link)
    {
        if (link != null) link.BackupDirty = true;
        SaveState();
        _status(PausedMessage, true);
        return new PushResult(PushResultKind.Paused);
    }

    // Retry on profile open. No-op unless the link exists and is dirty; then a
    // normal push, which follows the same conflict rules. Returns null when
    // there was nothing to retry so the caller can skip the KeptOnline handling.
    public Task<PushResult?> RetryIfDirtyAsync(string profilePath, string csvText) =>
        Locked<PushResult?>(async () =>
        {
            var settings = _getSettings();
            if (settings.DriveLinks.TryGetValue(profilePath, out var link) && link.BackupDirty)
                return await PushCoreAsync(profilePath, csvText, CancellationToken.None);
            return null;
        }, CancellationToken.None);

    // Renames done inside the app move the link with the file. Path is the key.
    public void OnRenamed(string oldPath, string newPath)
    {
        var settings = _getSettings();
        if (settings.DriveLinks.Remove(oldPath, out var link))
        {
            settings.DriveLinks[newPath] = link;
            SaveState();
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
    public Task<ShareLinkResult> GetShareLinkAsync(string profilePath, string csvText, CancellationToken ct = default) =>
        Locked(() => GetShareLinkCoreAsync(profilePath, csvText, ct), ct);

    async Task<ShareLinkResult> GetShareLinkCoreAsync(string profilePath, string csvText, CancellationToken ct)
    {
        var settings = _getSettings();
        settings.DriveLinks.TryGetValue(profilePath, out var link);

        // The dirty push below can hit the conflict prompt; Keep online hands
        // back the sheet CSV, which must reach the caller so the local file
        // still gets rescued, overwritten, and reloaded even mid-share.
        string? keptOnlineCsv = null;

        // Step 2: not linked yet. Run the first backup (create + push) through
        // the existing path. A sheet that never held the profile would share as
        // a blank, so a failed first push copies nothing.
        if (link is null)
        {
            var first = await PushCoreAsync(profilePath, csvText, ct);
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
            var push = await PushCoreAsync(profilePath, csvText, ct);
            if (push.Kind == PushResultKind.KeptOnline)
                keptOnlineCsv = push.DownloadedCsv;
            else if (push.Kind == PushResultKind.RecreatedOff)
                return new ShareLinkResult(ShareLinkKind.Failed, null,
                    "Backup was turned off for this profile, so nothing was copied.");
            else if (push.Kind is PushResultKind.Failed or PushResultKind.Paused)
                return new ShareLinkResult(ShareLinkKind.CopiedStale, Url(link.SpreadsheetId),
                    "Link copied. Your latest changes are not uploaded yet (backup pending).");
        }

        // Step 4: share the sheet once. Anyone with the link can view (read
        // only). The linked-and-clean path reaches here with no network call
        // yet, so an already shared sheet is a pure clipboard write below.
        if (!link.LinkShared)
        {
            if (!await _shareConfirm())
                return new ShareLinkResult(ShareLinkKind.Cancelled, null, "", keptOnlineCsv);
            try
            {
                await _client.ShareAnyoneReaderAsync(link.SpreadsheetId, ct);
                link.LinkShared = true;
                // The grant can bump modifiedTime; re-read it now so the next
                // save does not see a phantom online edit and prompt a
                // self-inflicted conflict.
                link.LastSeenModifiedTime = await _client.GetModifiedTimeAsync(link.SpreadsheetId, ct);
                SaveState();
            }
            catch (Exception ex) when (ex is DriveApiException or HttpRequestException or TaskCanceledException or GoogleAuthRevokedException)
            {
                // An unshared link is useless, so copy nothing.
                return new ShareLinkResult(ShareLinkKind.Failed, null,
                    "Could not turn on link sharing, so nothing was copied.", keptOnlineCsv);
            }
        }

        // Step 5: hand back the URL for the clipboard.
        return new ShareLinkResult(ShareLinkKind.Copied, Url(link.SpreadsheetId), "Link copied.", keptOnlineCsv);
    }

    // ---- Restore (bulk import from Drive) ----

    // List every backup sheet this app created, tagged with whether it is
    // already linked to a local profile. A sheet counts as linked only when its
    // mapped local file still exists on disk; a stale entry for a deleted CSV is
    // ignored AND pruned, so a deleted local file can never grey out the very
    // sheet restore exists to bring back. Match is by spreadsheetId, so a rename
    // does not fool it.
    public Task<List<DriveSheetInfo>> ListForPickerAsync(CancellationToken ct = default) =>
        Locked(() => ListForPickerCoreAsync(ct), ct);

    async Task<List<DriveSheetInfo>> ListForPickerCoreAsync(CancellationToken ct)
    {
        var sheets = await _client.ListSpreadsheetsAsync(ct);

        var settings = _getSettings();
        var linkedIds = new HashSet<string>(StringComparer.Ordinal);
        var stale = new List<string>();
        foreach (var (path, link) in settings.DriveLinks)
        {
            if (File.Exists(path)) linkedIds.Add(link.SpreadsheetId);
            else stale.Add(path);
        }
        if (stale.Count > 0)
        {
            foreach (var p in stale) settings.DriveLinks.Remove(p);
            SaveState();
        }

        return sheets
            .Select(s => new DriveSheetInfo(s.Id, s.Name, s.ModifiedTime, linkedIds.Contains(s.Id)))
            .ToList();
    }

    // Import the picked sheets into libraryDir and link each on the spot. Every
    // per-file failure is recorded and never aborts the batch.
    public Task<RestoreSummary> RestoreAsync(
        IReadOnlyList<(string Id, string Name)> picks, string libraryDir, CancellationToken ct = default) =>
        Locked(() => RestoreCoreAsync(picks, libraryDir, ct), ct);

    async Task<RestoreSummary> RestoreCoreAsync(
        IReadOnlyList<(string Id, string Name)> picks, string libraryDir, CancellationToken ct)
    {
        var settings = _getSettings();
        var imported = new List<string>();
        var skipped = new List<(string Name, string Reason)>();
        var failed = new List<(string Name, string Reason)>();

        // A fresh machine has no library folder yet; restore is exactly the
        // moment that must not matter.
        Directory.CreateDirectory(libraryDir);

        // Existing library file names, case-insensitive: a collision is a skip,
        // never an overwrite. The local file is the source of truth.
        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(libraryDir, "*.csv"))
            onDisk.Add(Path.GetFileName(f));
        // Names already claimed within this batch, so two picks that sanitize to
        // the same name get a numbered suffix instead of fighting over one file.
        var batchNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pick in picks)
        {
            var reportName = Path.GetFileNameWithoutExtension(SafeFileName.ForCsv(pick.Name));
            try
            {
                var csv = await _client.DownloadCsvAsync(pick.Id, ct);

                // Validate by parsing. A sheet with no readable mode sheet is a
                // per-file failure, not a written-then-broken profile.
                ProfileFile parsed;
                try { parsed = ProfileFile.Load(csv); }
                catch { failed.Add((reportName, "could not read the sheet")); continue; }
                if (parsed.Document.Sheets.Count == 0)
                { failed.Add((reportName, "not a valid profile")); continue; }

                var fileName = DedupeName(SafeFileName.ForCsv(pick.Name), batchNames);
                batchNames.Add(fileName);
                reportName = Path.GetFileNameWithoutExtension(fileName);

                if (onDisk.Contains(fileName))
                { skipped.Add((reportName, "already exists")); continue; }

                var dest = Path.Combine(libraryDir, fileName);
                ProfileFile.WriteAtomic(dest, csv);

                // Link on the spot: future saves push to this sheet instead of
                // forking a duplicate. modifiedTime is read fresh so the next
                // save's conflict check compares against a real value.
                var mt = await _client.GetModifiedTimeAsync(pick.Id, ct);
                settings.DriveLinks[dest] = new DriveLink
                {
                    SpreadsheetId = pick.Id,
                    LastSeenModifiedTime = mt,
                    BackupDirty = false,
                    LinkShared = false,
                };

                // Imported must mean linked. If the link state cannot be saved,
                // undo the write so the file cannot silently fork a new sheet.
                if (!_trySave())
                {
                    settings.DriveLinks.Remove(dest);
                    try { File.Delete(dest); } catch { /* the write may already be gone */ }
                    failed.Add((reportName, "could not save the link"));
                    continue;
                }

                onDisk.Add(fileName);
                imported.Add(reportName);
            }
            catch (Exception ex) when (ex is DriveApiException or HttpRequestException or TaskCanceledException or GoogleAuthRevokedException)
            {
                failed.Add((reportName, "download failed"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A disk failure on one file must not abort the batch either.
                failed.Add((reportName, "could not write the file"));
            }
        }

        return new RestoreSummary(imported, skipped, failed);
    }

    // A file name unique within this batch: append " (2)", " (3)", ... before
    // the extension until it stops colliding with a name already claimed.
    static string DedupeName(string fileName, HashSet<string> claimed)
    {
        if (!claimed.Contains(fileName)) return fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!claimed.Contains(candidate)) return candidate;
        }
    }
}

// One backup sheet as the picker shows it. AlreadyLinked greys it out; the
// picker never imports one already linked to a local profile on disk.
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

// What a restore run did. The three lists feed the picker's summary line, and
// Message is the ready-to-show one-liner ("3 imported, 1 skipped: mygame
// already exists").
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
    // Set when the dirty push hit a conflict and the user kept the online
    // version: the caller must still rescue and overwrite the local file.
    public string? DownloadedCsv { get; }

    public ShareLinkResult(ShareLinkKind kind, string? url, string message, string? downloadedCsv = null)
    {
        Kind = kind;
        Url = url;
        Message = message;
        DownloadedCsv = downloadedCsv;
    }
}
