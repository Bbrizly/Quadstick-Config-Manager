using System.Net;
using System.Text;
using System.Text.Json;
using QuadStick.App;
using Xunit;

namespace QuadStick.Format.Tests;

// The backup engine drives a real DriveClient over a fake transport, so these
// exercise the whole Flow (create, silent push, conflict, 404, revoke, retry)
// with no Avalonia and no network. Settings live in memory; trySave defaults to
// a no-op that reports success, because the engine mutates the AppSettings
// object in place. Restore tests override it to force the save-failed path.
public class DriveBackupTests
{
    class FakeHandler : HttpMessageHandler
    {
        readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(_responder(req));
    }

    static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    static HttpResponseMessage Csv(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    static bool IsModified(HttpRequestMessage r) => r.RequestUri!.AbsoluteUri.Contains("fields=modifiedTime");
    static bool IsCreate(HttpRequestMessage r) =>
        r.Method == HttpMethod.Post && r.RequestUri!.AbsoluteUri == "https://sheets.googleapis.com/v4/spreadsheets";
    static bool IsDownload(HttpRequestMessage r) => r.RequestUri!.AbsoluteUri.Contains("/export?mimeType=");
    static bool IsValuesUpdate(HttpRequestMessage r) => r.RequestUri!.AbsoluteUri.Contains("values:batchUpdate");
    static bool IsPermissions(HttpRequestMessage r) =>
        r.Method == HttpMethod.Post && r.RequestUri!.AbsoluteUri.Contains("/permissions");

    static (DriveBackup backup, AppSettings settings, List<(string Msg, bool Warn)> statuses) Make(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        ConflictChoice conflict = ConflictChoice.ReplaceWithMine,
        bool recreate = true,
        Func<CancellationToken, Task<string>>? token = null,
        bool shareConfirm = true,
        Func<bool>? trySave = null)
    {
        var settings = new AppSettings();
        var client = new DriveClient(new FakeHandler(responder), token ?? (_ => Task.FromResult("tok")));
        var statuses = new List<(string, bool)>();
        var backup = new DriveBackup(client, () => settings, trySave ?? (() => true),
            conflictPrompt: (_, _) => Task.FromResult(conflict),
            recreatePrompt: (_, _) => Task.FromResult(recreate),
            status: (m, w) => statuses.Add((m, w)),
            shareConfirm: () => Task.FromResult(shareConfirm));
        return (backup, settings, statuses);
    }

    const string Grid = "x,circle\r\ny,cross\r\n";

    [Fact]
    public async Task FirstPush_Creates_RecordsIdAndTime()
    {
        var (backup, settings, _) = Make(r =>
        {
            if (IsCreate(r)) return Json("{\"spreadsheetId\":\"sheetNew\"}");
            if (IsModified(r)) return Json("{\"modifiedTime\":\"t-created\"}");
            return Json("{}"); // clear + update
        });

        var result = await backup.PushAsync("/lib/mygame.csv", Grid);

        Assert.Equal(PushResultKind.Pushed, result.Kind);
        var link = settings.DriveLinks["/lib/mygame.csv"];
        Assert.Equal("sheetNew", link.SpreadsheetId);
        Assert.Equal("t-created", link.LastSeenModifiedTime);
        Assert.False(link.BackupDirty);
    }

    [Fact]
    public async Task FirstPush_UsesTheProfileTitleForTheSpreadsheetName()
    {
        string? createBody = null;
        var (backup, _, _) = Make(r =>
        {
            if (IsCreate(r))
            {
                createBody = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json("{\"spreadsheetId\":\"sheetNew\"}");
            }
            if (IsModified(r)) return Json("{\"modifiedTime\":\"t-created\"}");
            return Json("{}");
        });

        await backup.PushAsync("/lib/re9.csv",
            "QuadStick Configuration,Version 1.5,,Resident Evil 9\n"
            + "Profile Name,,Solo\nre9.csv\nOutputs,Function,usb\nx,normal,lip\n");

        using var doc = JsonDocument.Parse(createBody!);
        Assert.Equal("Resident Evil 9", doc.RootElement.GetProperty("properties").GetProperty("title").GetString());
    }

    [Fact]
    public async Task EditingAfterTheFirstBackupUpdatesTheSameSpreadsheet()
    {
        int creates = 0;
        int modifiedReads = 0;
        int valueUpdates = 0;
        var (backup, settings, _) = Make(r =>
        {
            if (IsCreate(r)) { creates++; return Json("{\"spreadsheetId\":\"same-sheet\"}"); }
            if (IsValuesUpdate(r)) { valueUpdates++; return Json("{}"); }
            if (IsModified(r))
                return Json($"{{\"modifiedTime\":\"{(modifiedReads++ < 2 ? "t-created" : "t-updated")}\"}}");
            return Json("{}");
        });

        await backup.PushAsync("/lib/re9.csv", Grid);
        var result = await backup.PushAsync("/lib/re9.csv", "x,circle\r\nchanged,cross\r\n");

        Assert.Equal(PushResultKind.Pushed, result.Kind);
        Assert.Equal(1, creates);
        Assert.Equal(2, valueUpdates);
        Assert.Equal("same-sheet", settings.DriveLinks["/lib/re9.csv"].SpreadsheetId);
    }

    [Fact]
    public async Task LinkedUnchanged_PushesSilently_RecordsNewTime()
    {
        int mt = 0;
        var (backup, settings, _) = Make(r =>
        {
            if (IsModified(r)) return Json(mt++ == 0 ? "{\"modifiedTime\":\"t0\"}" : "{\"modifiedTime\":\"t1\"}");
            return Json("{}");
        });
        settings.DriveLinks["/p.csv"] = new DriveLink { SpreadsheetId = "s", LastSeenModifiedTime = "t0" };

        var result = await backup.PushAsync("/p.csv", Grid);

        Assert.Equal(PushResultKind.Pushed, result.Kind);
        var link = settings.DriveLinks["/p.csv"];
        Assert.Equal("s", link.SpreadsheetId);
        Assert.Equal("t1", link.LastSeenModifiedTime);
        Assert.False(link.BackupDirty);
    }

    [Fact]
    public async Task Conflict_ReplaceWithMine_PushesAndRecords()
    {
        int mt = 0;
        var (backup, settings, _) = Make(r =>
        {
            if (IsModified(r)) return Json(mt++ == 0 ? "{\"modifiedTime\":\"tX\"}" : "{\"modifiedTime\":\"tY\"}");
            return Json("{}");
        }, conflict: ConflictChoice.ReplaceWithMine);
        settings.DriveLinks["/p.csv"] = new DriveLink { SpreadsheetId = "s", LastSeenModifiedTime = "t0" };

        var result = await backup.PushAsync("/p.csv", Grid);

        Assert.Equal(PushResultKind.Pushed, result.Kind);
        Assert.Equal("tY", settings.DriveLinks["/p.csv"].LastSeenModifiedTime);
        Assert.False(settings.DriveLinks["/p.csv"].BackupDirty);
    }

    [Fact]
    public async Task Conflict_KeepOnline_ReturnsCsv_MappingUnchanged()
    {
        var (backup, settings, _) = Make(r =>
        {
            if (IsModified(r)) return Json("{\"modifiedTime\":\"tX\"}");
            if (IsDownload(r)) return Csv("a,b\r\nc,d\r\n");
            return Json("{}");
        }, conflict: ConflictChoice.KeepOnline);
        settings.DriveLinks["/p.csv"] = new DriveLink { SpreadsheetId = "s", LastSeenModifiedTime = "t0" };

        var result = await backup.PushAsync("/p.csv", Grid);

        Assert.Equal(PushResultKind.KeptOnline, result.Kind);
        Assert.Equal("a,b\r\nc,d\r\n", result.DownloadedCsv);
        // Mapping stays put; the online time is recorded so the next save is silent.
        Assert.Equal("s", settings.DriveLinks["/p.csv"].SpreadsheetId);
        Assert.Equal("tX", settings.DriveLinks["/p.csv"].LastSeenModifiedTime);
    }

    [Fact]
    public async Task NotFound_Recreate_RecordsNewId()
    {
        int mt = 0;
        var (backup, settings, _) = Make(r =>
        {
            if (IsModified(r)) return mt++ == 0 ? Json("{}", HttpStatusCode.NotFound) : Json("{\"modifiedTime\":\"tNew\"}");
            if (IsCreate(r)) return Json("{\"spreadsheetId\":\"freshId\"}");
            return Json("{}");
        }, recreate: true);
        settings.DriveLinks["/p.csv"] = new DriveLink { SpreadsheetId = "gone", LastSeenModifiedTime = "t0" };

        var result = await backup.PushAsync("/p.csv", Grid);

        Assert.Equal(PushResultKind.Pushed, result.Kind);
        Assert.Equal("freshId", settings.DriveLinks["/p.csv"].SpreadsheetId);
        Assert.Equal("tNew", settings.DriveLinks["/p.csv"].LastSeenModifiedTime);
    }

    [Fact]
    public async Task NotFound_Decline_RemovesLink()
    {
        var (backup, settings, _) = Make(r =>
        {
            if (IsModified(r)) return Json("{}", HttpStatusCode.NotFound);
            return Json("{}");
        }, recreate: false);
        settings.DriveLinks["/p.csv"] = new DriveLink { SpreadsheetId = "gone", LastSeenModifiedTime = "t0" };

        var result = await backup.PushAsync("/p.csv", Grid);

        Assert.Equal(PushResultKind.RecreatedOff, result.Kind);
        Assert.False(settings.DriveLinks.ContainsKey("/p.csv"));
    }

    [Fact]
    public async Task GenericFailure_SetsDirty_ThenRetryPushes()
    {
        bool fault = true;
        int mt = 0;
        var (backup, settings, statuses) = Make(r =>
        {
            if (IsModified(r))
            {
                if (fault) return Json("{}", HttpStatusCode.InternalServerError);
                return Json(mt++ == 0 ? "{\"modifiedTime\":\"t0\"}" : "{\"modifiedTime\":\"t1\"}");
            }
            return Json("{}");
        });
        settings.DriveLinks["/p.csv"] = new DriveLink { SpreadsheetId = "s", LastSeenModifiedTime = "t0" };

        var first = await backup.PushAsync("/p.csv", Grid);
        Assert.Equal(PushResultKind.Failed, first.Kind);
        Assert.True(settings.DriveLinks["/p.csv"].BackupDirty);
        Assert.Contains(statuses, s => s.Msg == "Backup pending" && s.Warn);

        // The fault clears; retry-on-open pushes and clears the flag.
        fault = false;
        var retry = await backup.RetryIfDirtyAsync("/p.csv", Grid);
        Assert.NotNull(retry);
        Assert.Equal(PushResultKind.Pushed, retry!.Kind);
        Assert.False(settings.DriveLinks["/p.csv"].BackupDirty);
        Assert.Equal("t1", settings.DriveLinks["/p.csv"].LastSeenModifiedTime);
    }

    [Fact]
    public async Task RetryIfDirty_NoopWhenClean()
    {
        var (backup, settings, _) = Make(_ => Json("{}"));
        settings.DriveLinks["/p.csv"] = new DriveLink { SpreadsheetId = "s", LastSeenModifiedTime = "t0", BackupDirty = false };

        var result = await backup.RetryIfDirtyAsync("/p.csv", Grid);

        Assert.Null(result);
    }

    [Fact]
    public async Task Revoked_Pauses_SetsDirty()
    {
        var (backup, settings, statuses) = Make(
            _ => Json("{}"),
            token: _ => Task.FromException<string>(new GoogleAuthRevokedException()));
        settings.DriveLinks["/p.csv"] = new DriveLink { SpreadsheetId = "s", LastSeenModifiedTime = "t0" };

        var result = await backup.PushAsync("/p.csv", Grid);

        Assert.Equal(PushResultKind.Paused, result.Kind);
        Assert.True(settings.DriveLinks["/p.csv"].BackupDirty);
        Assert.Contains(statuses, s => s.Msg.Contains("Reconnect"));
    }

    // ---- Share link, the spec's five step sequence ----

    // First share of a never-linked profile: create + push, confirm, grant
    // reader, then re-read modifiedTime so the grant does not look like an
    // online edit next save.
    [Fact]
    public async Task Share_FirstTime_GrantsReader_RecordsSharedAndTime()
    {
        int mt = 0;
        var (backup, settings, _) = Make(r =>
        {
            if (IsCreate(r)) return Json("{\"spreadsheetId\":\"sheetX\"}");
            if (IsModified(r)) return Json(mt++ == 0 ? "{\"modifiedTime\":\"t-created\"}" : "{\"modifiedTime\":\"t-shared\"}");
            return Json("{}"); // clear + update + permissions
        });

        var result = await backup.GetShareLinkAsync("/lib/mygame.csv", Grid);

        Assert.Equal(ShareLinkKind.Copied, result.Kind);
        Assert.Equal("https://docs.google.com/spreadsheets/d/sheetX/edit?usp=sharing", result.Url);
        var link = settings.DriveLinks["/lib/mygame.csv"];
        Assert.True(link.LinkShared);
        Assert.Equal("t-shared", link.LastSeenModifiedTime); // re-read after the grant
    }

    // The first backup failing means the sheet never held the profile, so
    // sharing it would hand a friend a blank: copy nothing.
    [Fact]
    public async Task Share_FirstBackupFails_ReturnsFailed_NoUrl()
    {
        var (backup, settings, _) = Make(r =>
            IsCreate(r) ? Json("{}", HttpStatusCode.InternalServerError) : Json("{}"));

        var result = await backup.GetShareLinkAsync("/p.csv", Grid);

        Assert.Equal(ShareLinkKind.Failed, result.Kind);
        Assert.Null(result.Url);
        Assert.False(settings.DriveLinks.ContainsKey("/p.csv"));
    }

    // Linked but dirty and the push fails: still copy the last good link, just
    // flag it stale.
    [Fact]
    public async Task Share_DirtyPushFails_ReturnsStale_WithUrl()
    {
        var (backup, settings, _) = Make(r =>
            IsModified(r) ? Json("{}", HttpStatusCode.InternalServerError) : Json("{}"));
        settings.DriveLinks["/p.csv"] = new DriveLink
        { SpreadsheetId = "s", LastSeenModifiedTime = "t0", BackupDirty = true };

        var result = await backup.GetShareLinkAsync("/p.csv", Grid);

        Assert.Equal(ShareLinkKind.CopiedStale, result.Kind);
        Assert.Equal("https://docs.google.com/spreadsheets/d/s/edit?usp=sharing", result.Url);
    }

    // Declining the share confirmation copies nothing and leaves the sheet
    // unshared.
    [Fact]
    public async Task Share_ConfirmDeclined_ReturnsCancelled()
    {
        var (backup, settings, _) = Make(_ => Json("{}"), shareConfirm: false);
        settings.DriveLinks["/p.csv"] = new DriveLink { SpreadsheetId = "s", LastSeenModifiedTime = "t0" };

        var result = await backup.GetShareLinkAsync("/p.csv", Grid);

        Assert.Equal(ShareLinkKind.Cancelled, result.Kind);
        Assert.Null(result.Url);
        Assert.False(settings.DriveLinks["/p.csv"].LinkShared);
    }

    // The grant call failing leaves an unshared link, which is useless: copy
    // nothing.
    [Fact]
    public async Task Share_GrantFails_ReturnsFailed_NoUrl()
    {
        var (backup, settings, _) = Make(r =>
            IsPermissions(r) ? Json("{}", HttpStatusCode.InternalServerError) : Json("{}"));
        settings.DriveLinks["/p.csv"] = new DriveLink { SpreadsheetId = "s", LastSeenModifiedTime = "t0" };

        var result = await backup.GetShareLinkAsync("/p.csv", Grid);

        Assert.Equal(ShareLinkKind.Failed, result.Kind);
        Assert.Null(result.Url);
        Assert.False(settings.DriveLinks["/p.csv"].LinkShared);
    }

    // After the one-time grant, a linked and clean sheet is a pure clipboard
    // write: not a single HTTP request.
    [Fact]
    public async Task Share_AfterGrant_MakesNoHttpRequests()
    {
        int calls = 0;
        var (backup, settings, _) = Make(r => { calls++; return Json("{}"); });
        settings.DriveLinks["/p.csv"] = new DriveLink
        { SpreadsheetId = "s", LastSeenModifiedTime = "t0", BackupDirty = false, LinkShared = true };

        var result = await backup.GetShareLinkAsync("/p.csv", Grid);

        Assert.Equal(ShareLinkKind.Copied, result.Kind);
        Assert.Equal("https://docs.google.com/spreadsheets/d/s/edit?usp=sharing", result.Url);
        Assert.Equal(0, calls);
    }

    // A dirty share whose push finds the sheet gone and rebuilds it must copy
    // the NEW sheet, and must share it. Reusing the pre-push link hands a
    // friend a link to a deleted sheet, or to a sheet nobody granted them.
    [Fact]
    public async Task Share_DirtyPush404Recreates_CopiesAndSharesNewSheet()
    {
        int mt = 0;
        var (backup, settings, _) = Make(r =>
        {
            if (IsCreate(r)) return Json("{\"spreadsheetId\":\"fresh\"}");
            if (IsModified(r))
                // The dead sheet 404s; every read after the rebuild works.
                return r.RequestUri!.AbsoluteUri.Contains("/dead")
                    ? Json("{}", HttpStatusCode.NotFound)
                    : Json(mt++ == 0 ? "{\"modifiedTime\":\"t-new\"}" : "{\"modifiedTime\":\"t-shared\"}");
            return Json("{}");
        }, recreate: true);
        // Shared once already, so a stale link would skip the grant entirely.
        settings.DriveLinks["/p.csv"] = new DriveLink
        { SpreadsheetId = "dead", LastSeenModifiedTime = "t0", BackupDirty = true, LinkShared = true };

        var result = await backup.GetShareLinkAsync("/p.csv", Grid);

        Assert.Equal(ShareLinkKind.Copied, result.Kind);
        Assert.Equal("https://docs.google.com/spreadsheets/d/fresh/edit?usp=sharing", result.Url);
        var link = settings.DriveLinks["/p.csv"];
        Assert.Equal("fresh", link.SpreadsheetId);
        Assert.True(link.LinkShared); // the new sheet got its own grant
    }

    // ---- Restore (bulk import from Drive) ----

    static bool IsList(HttpRequestMessage r) => r.RequestUri!.AbsoluteUri.Contains("fields=nextPageToken");

    // A guaranteed-valid profile CSV: the factory default the app ships. Restore
    // validates by parsing, so a random grid would look like a broken sheet.
    static string ValidProfile() => ProfileFile.NewFromTemplate("game.csv").ToCsvText();

    static string TempLib()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qcm-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Restore_ImportsAndLinks()
    {
        var lib = TempLib();
        try
        {
            var (backup, settings, _) = Make(r =>
            {
                if (IsDownload(r)) return Csv(ValidProfile());
                if (IsModified(r)) return Json("{\"modifiedTime\":\"t-restored\"}");
                return Json("{}");
            });

            var summary = await backup.RestoreAsync(new[] { ("id1", "mygame") }, lib);

            Assert.Equal(new[] { "mygame" }, summary.Imported);
            var dest = Path.Combine(lib, "mygame.csv");
            Assert.True(File.Exists(dest));
            var link = settings.DriveLinks[dest];
            Assert.Equal("id1", link.SpreadsheetId);
            Assert.Equal("t-restored", link.LastSeenModifiedTime);
            Assert.False(link.BackupDirty);
        }
        finally { Directory.Delete(lib, recursive: true); }
    }

    [Fact]
    public async Task Restore_NameCollision_SkipsAndReports()
    {
        var lib = TempLib();
        try
        {
            // The local file is the source of truth; restore never overwrites it.
            var dest = Path.Combine(lib, "mygame.csv");
            File.WriteAllText(dest, "local truth\r\n");

            var (backup, settings, _) = Make(r =>
            {
                if (IsDownload(r)) return Csv(ValidProfile());
                if (IsModified(r)) return Json("{\"modifiedTime\":\"t\"}");
                return Json("{}");
            });

            var summary = await backup.RestoreAsync(new[] { ("id1", "mygame") }, lib);

            Assert.Empty(summary.Imported);
            Assert.Single(summary.Skipped);
            Assert.Contains("mygame already exists", summary.Message);
            Assert.Equal("local truth\r\n", File.ReadAllText(dest)); // untouched
            Assert.False(settings.DriveLinks.ContainsKey(dest));
        }
        finally { Directory.Delete(lib, recursive: true); }
    }

    [Fact]
    public async Task Restore_TwoPicksSameName_GetNumbered()
    {
        var lib = TempLib();
        try
        {
            var (backup, _, _) = Make(r =>
            {
                if (IsDownload(r)) return Csv(ValidProfile());
                if (IsModified(r)) return Json("{\"modifiedTime\":\"t\"}");
                return Json("{}");
            });

            var summary = await backup.RestoreAsync(
                new[] { ("id1", "mygame"), ("id2", "mygame") }, lib);

            Assert.Equal(2, summary.Imported.Count);
            Assert.True(File.Exists(Path.Combine(lib, "mygame.csv")));
            Assert.True(File.Exists(Path.Combine(lib, "mygame (2).csv")));
        }
        finally { Directory.Delete(lib, recursive: true); }
    }

    [Fact]
    public async Task Restore_OneDownloadFails_BatchContinues()
    {
        var lib = TempLib();
        try
        {
            var (backup, settings, _) = Make(r =>
            {
                // The first sheet's download faults; the second succeeds.
                if (IsDownload(r))
                    return r.RequestUri!.AbsoluteUri.Contains("bad")
                        ? Json("{}", HttpStatusCode.InternalServerError)
                        : Csv(ValidProfile());
                if (IsModified(r)) return Json("{\"modifiedTime\":\"t\"}");
                return Json("{}");
            });

            var summary = await backup.RestoreAsync(
                new[] { ("bad", "broken"), ("good", "works") }, lib);

            Assert.Single(summary.Failed);
            Assert.Equal(new[] { "works" }, summary.Imported);
            Assert.True(File.Exists(Path.Combine(lib, "works.csv")));
        }
        finally { Directory.Delete(lib, recursive: true); }
    }

    // Imported must mean linked: when the link state cannot be persisted, the
    // just-written CSV is deleted and the file is reported as failed.
    [Fact]
    public async Task Restore_TrySaveFalse_DeletesCsv_ReportsFailed()
    {
        var lib = TempLib();
        try
        {
            var (backup, settings, _) = Make(r =>
            {
                if (IsDownload(r)) return Csv(ValidProfile());
                if (IsModified(r)) return Json("{\"modifiedTime\":\"t\"}");
                return Json("{}");
            }, trySave: () => false);

            var summary = await backup.RestoreAsync(new[] { ("id1", "mygame") }, lib);

            Assert.Empty(summary.Imported);
            Assert.Single(summary.Failed);
            Assert.False(File.Exists(Path.Combine(lib, "mygame.csv")));
            Assert.Empty(settings.DriveLinks);
        }
        finally { Directory.Delete(lib, recursive: true); }
    }

    // A link whose local file is missing must not grey out its sheet. The entry
    // itself stays: missing can mean an unmounted drive or a folder mid-sync,
    // and dropping the link would fork a duplicate sheet the moment that file
    // came back and got saved.
    [Fact]
    public async Task ListForPicker_MissingFile_NotAlreadyLinked_LinkKept()
    {
        var (backup, settings, _) = Make(r =>
            IsList(r)
                ? Json("{\"files\":[{\"id\":\"s1\",\"name\":\"mygame\",\"modifiedTime\":\"t\"}]}")
                : Json("{}"));
        // The mapped file does not exist on disk.
        var gonePath = Path.Combine(Path.GetTempPath(), "qcm-gone-" + Guid.NewGuid().ToString("N") + ".csv");
        settings.DriveLinks[gonePath] = new DriveLink { SpreadsheetId = "s1", LastSeenModifiedTime = "t0" };

        var sheets = await backup.ListForPickerAsync();

        var s1 = Assert.Single(sheets);
        Assert.Equal("s1", s1.Id);
        Assert.False(s1.AlreadyLinked);           // the missing file must not grey it out
        Assert.True(settings.DriveLinks.ContainsKey(gonePath)); // but the link survives
    }

    // Create succeeds but the first push fails: the sheet id must already be
    // recorded (dirty), so the retry pushes to the SAME sheet instead of
    // creating a second one and orphaning the first.
    [Fact]
    public async Task FirstCreate_PushFails_RecordsLink_RetryReusesSheet()
    {
        int creates = 0;
        bool fault = true;
        var (backup, settings, _) = Make(r =>
        {
            if (IsCreate(r)) { creates++; return Json("{\"spreadsheetId\":\"keep\"}"); }
            if (fault && IsValuesUpdate(r))
                return Json("{}", HttpStatusCode.InternalServerError);
            if (IsModified(r)) return Json("{\"modifiedTime\":\"t\"}");
            return Json("{}");
        });

        var first = await backup.PushAsync("/lib/p.csv", Grid);
        Assert.Equal(PushResultKind.Failed, first.Kind);
        var link = settings.DriveLinks["/lib/p.csv"];
        Assert.Equal("keep", link.SpreadsheetId);
        Assert.True(link.BackupDirty);

        fault = false;
        var retry = await backup.RetryIfDirtyAsync("/lib/p.csv", Grid);
        Assert.Equal(PushResultKind.Pushed, retry!.Kind);
        Assert.Equal(1, creates);
        Assert.False(settings.DriveLinks["/lib/p.csv"].BackupDirty);
    }

    // A dirty share that hits the conflict prompt and keeps the online version
    // must hand the sheet CSV back so the caller still replaces the local file,
    // and the copy still happens: the link points at the version the user kept.
    [Fact]
    public async Task Share_DirtyConflict_KeepOnline_CarriesCsv_AndCopies()
    {
        var (backup, settings, _) = Make(r =>
        {
            if (IsModified(r)) return Json("{\"modifiedTime\":\"tOnline\"}");
            if (IsDownload(r)) return Csv("online,grid\r\n");
            return Json("{}");
        }, conflict: ConflictChoice.KeepOnline);
        settings.DriveLinks["/p.csv"] = new DriveLink
        { SpreadsheetId = "s", LastSeenModifiedTime = "tOld", BackupDirty = true, LinkShared = true };

        var result = await backup.GetShareLinkAsync("/p.csv", Grid);

        Assert.Equal(ShareLinkKind.Copied, result.Kind);
        Assert.Equal("online,grid\r\n", result.DownloadedCsv);
        Assert.Equal("tOnline", settings.DriveLinks["/p.csv"].LastSeenModifiedTime);
    }

    // Slow transport so two engine calls genuinely overlap in time.
    class SlowHandler : HttpMessageHandler
    {
        readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public SlowHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            await Task.Delay(30, ct);
            return _responder(req);
        }
    }

    // The race the gate exists for: a save's background push and a share-link
    // copy hit the same unlinked profile at once. Without serialization each
    // sees "no link" and creates its own sheet, forking a duplicate. Exactly
    // one create may reach the wire.
    [Fact]
    public async Task ConcurrentPushAndShare_CreateExactlyOneSheet()
    {
        int creates = 0;
        var settings = new AppSettings();
        var handler = new SlowHandler(r =>
        {
            if (IsCreate(r)) { Interlocked.Increment(ref creates); return Json("{\"spreadsheetId\":\"only\"}"); }
            if (IsModified(r)) return Json("{\"modifiedTime\":\"t\"}");
            return Json("{}"); // clear, update, permissions
        });
        var client = new DriveClient(handler, _ => Task.FromResult("tok"));
        var backup = new DriveBackup(client, () => settings, () => true,
            conflictPrompt: (_, _) => Task.FromResult(ConflictChoice.ReplaceWithMine),
            recreatePrompt: (_, _) => Task.FromResult(true),
            status: (_, _) => { },
            shareConfirm: () => Task.FromResult(true));

        var pushTask = backup.PushAsync("/lib/race.csv", Grid);
        var shareTask = backup.GetShareLinkAsync("/lib/race.csv", Grid);
        var push = await pushTask;
        var share = await shareTask;

        Assert.Equal(1, creates);
        Assert.Equal(PushResultKind.Pushed, push.Kind);
        Assert.Equal(ShareLinkKind.Copied, share.Kind);
        Assert.Equal("only", settings.DriveLinks["/lib/race.csv"].SpreadsheetId);
    }

    // Copy share link, paste that link into Import: the whole chain, from the
    // bytes the backup puts on the wire to the profile the importer makes of
    // them. The user's own profile used to come back as "no profile tab".
    [Fact]
    public async Task WhatTheBackupPushes_ImportsBackAsTheSameProfile()
    {
        string? pushed = null;
        var (backup, _, _) = Make(r =>
        {
            if (IsCreate(r)) return Json("{\"spreadsheetId\":\"s\"}");
            if (IsModified(r)) return Json("{\"modifiedTime\":\"t\"}");
            if (IsValuesUpdate(r)) pushed = LastBody(r);
            return Json("{}");
        });
        var saved = ProfileFile.NewFromTemplate("mygame.csv");
        saved.NormalizeForDeviceCsv();

        await backup.PushAsync("/lib/mygame.csv", saved.ToCsvText());

        Assert.NotNull(pushed);
        using var workbook = WorkbookFrom(pushed!);
        var imported = ProfileFile.Load(Xlsx.ToCsv(workbook));

        Assert.Equal(saved.Document.Sheets.Count, imported.Document.Sheets.Count);
        Assert.Equal("mygame.csv", imported.Document.CsvFileName);
        Assert.Equal(
            saved.Document.Sheets[0].Bindings.Select(b => b.Output),
            imported.Document.Sheets[0].Bindings.Select(b => b.Output));
    }

    // The handler hands back a response, not the body, so read it here. The
    // content is already buffered by the time SendAsync returns.
    static string LastBody(HttpRequestMessage r) =>
        r.Content is null ? "" : r.Content.ReadAsStringAsync().GetAwaiter().GetResult();

    // Turn a values:batchUpdate body back into the workbook Google would export
    // from it: one tab per range, named by the range's own tab name.
    static MemoryStream WorkbookFrom(string valuesBatchUpdate)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(valuesBatchUpdate);
        var tabs = doc.RootElement.GetProperty("data").EnumerateArray().Select(d =>
        {
            var range = d.GetProperty("range").GetString()!;
            var title = range[1..range.LastIndexOf("'!", StringComparison.Ordinal)].Replace("''", "'");
            var rows = d.GetProperty("values").EnumerateArray()
                .Select(row => row.EnumerateArray().Select(c => c.GetString() ?? "").ToArray())
                .ToArray();
            return (title, rows);
        }).ToArray();
        return TestWorkbook.Build(tabs);
    }

    // ---- Recovering a link from the file's own C1 ----

    // A profile that moved carries its sheet id in the file. The settings entry
    // is under the old path, and the old file is gone, so this is a move and the
    // link follows it.
    [Fact]
    public void RecoverLink_MovesTheEntry_WhenTheOldFileIsGone()
    {
        var (backup, settings, _) = Make(_ => Json("{}"));
        var gone = Path.Combine(Path.GetTempPath(), "qsc-gone-" + Guid.NewGuid() + ".csv");
        settings.DriveLinks[gone] = new DriveLink { SpreadsheetId = "sheet-1" };

        Assert.True(backup.TryRecoverLink("/new/place.csv", "sheet-1"));
        Assert.Equal("sheet-1", backup.LinkedSheetId("/new/place.csv"));
        Assert.False(settings.DriveLinks.ContainsKey(gone));
    }

    // The original still exists, so this is a copy. A copy inheriting the link
    // would start overwriting the original's sheet on its next save.
    [Fact]
    public void RecoverLink_Refuses_WhenTheLinkedFileStillExists()
    {
        var (backup, settings, _) = Make(_ => Json("{}"));
        var original = Path.Combine(Path.GetTempPath(), "qsc-orig-" + Guid.NewGuid() + ".csv");
        File.WriteAllText(original, "x");
        try
        {
            settings.DriveLinks[original] = new DriveLink { SpreadsheetId = "sheet-1" };
            Assert.False(backup.TryRecoverLink("/copy/place.csv", "sheet-1"));
            Assert.Null(backup.LinkedSheetId("/copy/place.csv"));
            Assert.Equal("sheet-1", backup.LinkedSheetId(original));
        }
        finally { File.Delete(original); }
    }

    // An id this app never recorded belongs to somebody else's sheet, most
    // likely one that arrived with a shared profile. Adopting it would push
    // this user's profile over a stranger's.
    [Fact]
    public void RecoverLink_Refuses_AnIdItNeverRecorded()
    {
        var (backup, _, _) = Make(_ => Json("{}"));
        Assert.False(backup.TryRecoverLink("/new/place.csv", "someone-elses-sheet"));
        Assert.Null(backup.LinkedSheetId("/new/place.csv"));
    }

    [Fact]
    public void RecoverLink_LeavesAnExistingLinkAlone()
    {
        var (backup, settings, _) = Make(_ => Json("{}"));
        settings.DriveLinks["/here.csv"] = new DriveLink { SpreadsheetId = "mine" };
        Assert.False(backup.TryRecoverLink("/here.csv", "other"));
        Assert.Equal("mine", backup.LinkedSheetId("/here.csv"));
    }

    [Fact]
    public void RecoverLink_IgnoresABlankId()
    {
        var (backup, _, _) = Make(_ => Json("{}"));
        Assert.False(backup.TryRecoverLink("/new/place.csv", "   "));
    }

    // The push path stamps what it recovered, so the bytes that reach the sheet
    // and the bytes on disk both name the sheet they belong to.
    [Fact]
    public void LinkedSheetId_IsNullUntilThereIsALink()
    {
        var (backup, _, _) = Make(_ => Json("{}"));
        Assert.Null(backup.LinkedSheetId("/never/seen.csv"));
    }
}
