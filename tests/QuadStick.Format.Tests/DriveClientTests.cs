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

    [Fact]
    public async Task PushGrid_ClearsThenUpdatesRaw()
    {
        var handler = new RecordingHandler(_ => Json("{}"));
        await Client(handler).PushGridAsync("id", new List<string[]> { new[] { "a", "b" } });

        Assert.Equal(2, handler.Requests.Count);
        // Clear first.
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Contains(":clear", handler.Requests[0].RequestUri!.ToString());
        // Then update, RAW.
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Contains("valueInputOption=RAW", handler.Requests[1].RequestUri!.ToString());
        Assert.Contains("\"values\"", handler.Bodies[1]);
    }

    [Fact]
    public async Task GetModifiedTime_Parses()
    {
        var handler = new RecordingHandler(_ => Json("{\"modifiedTime\":\"2026-07-22T10:00:00.000Z\"}"));
        var mt = await Client(handler).GetModifiedTimeAsync("id");
        Assert.Equal("2026-07-22T10:00:00.000Z", mt);
    }

    [Fact]
    public async Task DownloadCsv_SendsBearer()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("a,b\r\n") });
        var csv = await Client(handler).DownloadCsvAsync("id");

        Assert.Equal("a,b\r\n", csv);
        var auth = handler.Requests[0].Headers.Authorization!;
        Assert.Equal("Bearer", auth.Scheme);
        Assert.Equal("tok", auth.Parameter);
        // Must be the Drive API export endpoint, not the docs.google.com web
        // export, which returns an HTML sign-in page on an unaccepted token.
        var url = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains("/drive/v3/files/id/export", url);
        Assert.DoesNotContain("docs.google.com", url);
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
