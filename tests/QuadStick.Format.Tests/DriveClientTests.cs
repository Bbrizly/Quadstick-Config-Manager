using System.Net;
using System.Text.Json;
using QuadStick.App;
using Xunit;

namespace QuadStick.Format.Tests;

public class DriveClientTests
{
    // Records every request and its body, replies via a responder.
    class RecordingHandler : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = new();
        public readonly List<string> Bodies = new();
        readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct));
            Requests.Add(request);
            return _responder(request);
        }
    }

    static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    static DriveClient Client(RecordingHandler handler) =>
        new(handler, _ => Task.FromResult("tok"));

    [Fact]
    public async Task Create_ReturnsId()
    {
        var handler = new RecordingHandler(_ => Json("{\"spreadsheetId\":\"sheet123\"}"));
        var id = await Client(handler).CreateSpreadsheetAsync("My Profile");
        Assert.Equal("sheet123", id);
    }

    static ProfileTab Tab(string title, params string[][] rows) => new(title, rows.ToList());

    // One tab already named right: nothing to reshape, straight to the values.
    static HttpResponseMessage OneTab(string title) =>
        Json($"{{\"sheets\":[{{\"properties\":{{\"sheetId\":0,\"title\":\"{title}\"}}}}]}}");

    [Fact]
    public async Task PushTabs_UpdatesRawThenClearsLeftovers()
    {
        var handler = new RecordingHandler(r => r.Method == HttpMethod.Get ? OneTab("Menu") : Json("{}"));
        await Client(handler).PushTabsAsync("id", new[] { Tab("Menu", new[] { "a", "b" }) });

        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("values:batchUpdate", handler.Requests[1].RequestUri!.ToString());
        using var update = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal("RAW", update.RootElement.GetProperty("valueInputOption").GetString());
        Assert.Equal("'Menu'!A1", update.RootElement.GetProperty("data")[0].GetProperty("range").GetString());
        Assert.Contains(":batchClear", handler.Requests[2].RequestUri!.ToString());
        using var doc = JsonDocument.Parse(handler.Bodies[2]);
        var ranges = doc.RootElement.GetProperty("ranges").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Contains("'Menu'!A2:ZZ10000", ranges);
        Assert.Contains("'Menu'!C1:ZZ10000", ranges);
    }

    [Fact]
    public async Task PushTabs_RenamesOutOfTheWayThenAddsAndDeletes()
    {
        var handler = new RecordingHandler(r => r.Method == HttpMethod.Get
            ? Json("{\"sheets\":[{\"properties\":{\"sheetId\":7,\"title\":\"Gameplay\"}},"
                 + "{\"properties\":{\"sheetId\":8,\"title\":\"Menu\"}},"
                 + "{\"properties\":{\"sheetId\":9,\"title\":\"Old\"}}]}")
            : Json("{}"));

        await Client(handler).PushTabsAsync("id", new[]
        {
            Tab("Menu", new[] { "a" }),
            Tab("Gameplay", new[] { "b" }),
        });

        using var doc = JsonDocument.Parse(handler.Bodies[1]);
        var requests = doc.RootElement.GetProperty("requests").EnumerateArray().ToList();
        Assert.Equal(3, requests.Count(r => r.TryGetProperty("updateSheetProperties", out var u)
            && u.GetProperty("properties").GetProperty("title").GetString()!.StartsWith("_qsc_tmp_", StringComparison.Ordinal)));
        Assert.Equal("Menu", requests[3].GetProperty("updateSheetProperties")
            .GetProperty("properties").GetProperty("title").GetString());
        Assert.Equal(9, requests[^1].GetProperty("deleteSheet").GetProperty("sheetId").GetInt32());
    }

    [Fact]
    public async Task PushTabs_DoesNotReshapeWhenTheTabsAlreadyMatch()
    {
        var handler = new RecordingHandler(r => r.Method == HttpMethod.Get ? OneTab("Menu") : Json("{}"));
        await Client(handler).PushTabsAsync("id", new[] { Tab("Menu", new[] { "a" }) });

        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.ToString().EndsWith("id:batchUpdate"));
    }

    [Fact]
    public async Task PushTabs_PadsShortRowsSoDroppedCellsAreBlanked()
    {
        var handler = new RecordingHandler(r => r.Method == HttpMethod.Get ? OneTab("Menu") : Json("{}"));
        await Client(handler).PushTabsAsync("id", new[]
        {
            Tab("Menu", new[] { "a", "b", "c" }, new[] { "d" }),
        });

        using var doc = JsonDocument.Parse(handler.Bodies[1]);
        var rows = doc.RootElement.GetProperty("data")[0].GetProperty("values").EnumerateArray().ToList();
        Assert.Equal(3, rows[1].GetArrayLength());
        Assert.Equal("", rows[1][1].GetString());
        Assert.Equal("", rows[1][2].GetString());
    }

    [Fact]
    public async Task PushTabs_RejectsWhenThereIsNoData()
    {
        var handler = new RecordingHandler(_ => Json("{}"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Client(handler).PushTabsAsync("id", Array.Empty<ProfileTab>()));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PushTabs_RejectsWhenEveryCellIsBlank()
    {
        var handler = new RecordingHandler(_ => Json("{}"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Client(handler).PushTabsAsync("id", new[] { Tab("Menu", new[] { "" }, new[] { " ", "" }) }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PushTabs_FormatsATabItJustMade()
    {
        int lists = 0;
        var handler = new RecordingHandler(r => r.Method == HttpMethod.Get
            ? OneTab(lists++ == 0 ? "Sheet1" : "Menu")
            : Json("{}"));
        await Client(handler).PushTabsAsync("id", new[]
        {
            new ProfileTab("Menu", new List<string[]>
            {
                new[] { "Profile Name", "", "Menu" },
                new[] { "mygame.csv" },
                new[] { "XBox Outputs", "Function", "usb" },
                new[] { "dpad_N", "normal", "lip" },
            }, HeaderRow: 2),
        });

        var format = handler.Bodies[^1];
        using var doc = JsonDocument.Parse(format);
        var requests = doc.RootElement.GetProperty("requests").EnumerateArray().ToList();
        Assert.Equal(3, requests[0].GetProperty("updateSheetProperties").GetProperty("properties")
            .GetProperty("gridProperties").GetProperty("frozenRowCount").GetInt32());
        var colour = requests[1].GetProperty("repeatCell").GetProperty("cell")
            .GetProperty("userEnteredFormat").GetProperty("backgroundColor");
        Assert.Equal(0xF3 / 255.0, colour.GetProperty("red").GetDouble(), 3);
        Assert.Equal(0xE2 / 255.0, colour.GetProperty("green").GetDouble(), 3);
        Assert.Equal(0xAE / 255.0, colour.GetProperty("blue").GetDouble(), 3);
    }

    [Fact]
    public async Task PushTabs_DoesNotReformatATabThatWasAlreadyRight()
    {
        var handler = new RecordingHandler(r => r.Method == HttpMethod.Get ? OneTab("Menu") : Json("{}"));
        await Client(handler).PushTabsAsync("id", new[] { Tab("Menu", new[] { "a" }) });

        Assert.DoesNotContain(handler.Bodies, b => b.Contains("frozenRowCount"));
    }

    [Fact]
    public async Task PushTabs_QuotesAnApostropheInATabName()
    {
        var handler = new RecordingHandler(r => r.Method == HttpMethod.Get ? OneTab("Bob's mode") : Json("{}"));
        await Client(handler).PushTabsAsync("id", new[] { Tab("Bob's mode", new[] { "a" }) });

        using var doc = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal("'Bob''s mode'!A1", doc.RootElement.GetProperty("data")[0].GetProperty("range").GetString());
    }

    [Fact]
    public async Task GetModifiedTime_Parses()
    {
        var handler = new RecordingHandler(_ => Json("{\"modifiedTime\":\"2026-07-22T10:00:00.000Z\"}"));
        var mt = await Client(handler).GetModifiedTimeAsync("id");
        Assert.Equal("2026-07-22T10:00:00.000Z", mt);
    }

    [Fact]
    public async Task DownloadWorkbook_SendsBearerAndAsksForXlsx()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[] { (byte)'P', (byte)'K', 3, 4 }) });
        var bytes = await Client(handler).DownloadWorkbookAsync("id");

        Assert.Equal(new byte[] { (byte)'P', (byte)'K', 3, 4 }, bytes);
        Assert.Contains("spreadsheetml.sheet", Uri.UnescapeDataString(handler.Requests[0].RequestUri!.ToString()));
        var auth = handler.Requests[0].Headers.Authorization!;
        Assert.Equal("Bearer", auth.Scheme);
        Assert.Equal("tok", auth.Parameter);
        var url = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains("/drive/v3/files/id/export", url);
        Assert.DoesNotContain("docs.google.com", url);
    }

    [Fact]
    public async Task DownloadWorkbook_RejectsDeclaredOversizeBeforeReadingBody()
    {
        var handler = new RecordingHandler(_ =>
        {
            var content = new ByteArrayContent(new byte[] { 1 });
            content.Headers.ContentLength = DriveClient.MaxWorkbookBytes + 1L;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).DownloadWorkbookAsync("id"));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ShareAnyoneReader_SendsExpectedBody()
    {
        var handler = new RecordingHandler(_ => Json("{}"));
        await Client(handler).ShareAnyoneReaderAsync("id");

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        var root = doc.RootElement;
        Assert.Equal("reader", root.GetProperty("role").GetString());
        Assert.Equal("anyone", root.GetProperty("type").GetString());
        Assert.False(root.GetProperty("allowFileDiscovery").GetBoolean());
    }

    [Fact]
    public async Task ListSpreadsheets_FollowsNextPageToken()
    {
        var handler = new RecordingHandler(r =>
            r.RequestUri!.Query.Contains("pageToken")
                ? Json("{\"files\":[{\"id\":\"b\",\"name\":\"B\",\"modifiedTime\":\"t2\"}]}")
                : Json("{\"files\":[{\"id\":\"a\",\"name\":\"A\",\"modifiedTime\":\"t1\"}],\"nextPageToken\":\"PAGE2\"}"));

        var list = await Client(handler).ListSpreadsheetsAsync();

        Assert.Equal(2, list.Count);
        Assert.Equal("a", list[0].Id);
        Assert.Equal("b", list[1].Id);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task NotFound_ThrowsDriveApiExceptionWithStatus()
    {
        var handler = new RecordingHandler(_ => Json("{}", HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<DriveApiException>(() => Client(handler).GetModifiedTimeAsync("x"));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }
}
