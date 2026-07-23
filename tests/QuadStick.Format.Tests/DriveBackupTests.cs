using System.Net;
using System.Text;
using QuadStick.App;
using Xunit;

namespace QuadStick.Format.Tests;

// The backup engine drives a real DriveClient over a fake transport, so these
// exercise the whole Flow (create, silent push, conflict, 404, revoke, retry)
// with no Avalonia and no network. Settings live in memory; saveSettings is a
// no-op because the engine mutates the AppSettings object in place.
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
    static bool IsDownload(HttpRequestMessage r) => r.RequestUri!.AbsoluteUri.Contains("export?format=csv");

    static (DriveBackup backup, AppSettings settings, List<(string Msg, bool Warn)> statuses) Make(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        ConflictChoice conflict = ConflictChoice.ReplaceWithMine,
        bool recreate = true,
        Func<CancellationToken, Task<string>>? token = null)
    {
        var settings = new AppSettings();
        var client = new DriveClient(new FakeHandler(responder), token ?? (_ => Task.FromResult("tok")));
        var statuses = new List<(string, bool)>();
        var backup = new DriveBackup(client, () => settings, () => { },
            conflictPrompt: (_, _) => Task.FromResult(conflict),
            recreatePrompt: (_, _) => Task.FromResult(recreate),
            status: (m, w) => statuses.Add((m, w)));
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

    [Fact]
    public void OnRenamed_MovesLink()
    {
        var (backup, settings, _) = Make(_ => Json("{}"));
        settings.DriveLinks["/old.csv"] = new DriveLink { SpreadsheetId = "s", LastSeenModifiedTime = "t0" };

        backup.OnRenamed("/old.csv", "/new.csv");

        Assert.False(settings.DriveLinks.ContainsKey("/old.csv"));
        Assert.Equal("s", settings.DriveLinks["/new.csv"].SpreadsheetId);
    }
}
