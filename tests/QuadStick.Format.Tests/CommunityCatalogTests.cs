using System.Net;
using System.Reflection;
using System.Text;
using QuadStick.App;
using Xunit;

namespace QuadStick.Format.Tests;

public sealed class CommunityCatalogTests : IDisposable
{
    const string IdA = "1AbCdEfGhIjKlMnOpQrStUvWxYz012345678";
    const string IdB = "2ZyXwVuTsRqPoNmLkJiHgFeDcBa987654321";
    const string IdC = "3QqWwEeRrTtYyUuIiOoPpAaSsDdFfGgHhJj";

    // A full eight-field row plus a minimum three-field row.
    const string GoodBody = """
        [[["Cyberpunk 2077","1AbCdEfGhIjKlMnOpQrStUvWxYz012345678","Cyberpunk.csv","https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345678/edit","PS5 USB","Sip to toggle","mouse","future"],
        ["Doom","2ZyXwVuTsRqPoNmLkJiHgFeDcBa987654321","Doom.csv"]],
        [["Voice pack","voices.vch","9ZzZzZzZzZzZzZzZzZzZzZzZzZzZzZzZ"]]]
        """;

    readonly string _dir = Path.Combine(Path.GetTempPath(), $"qscm-catalog-{Guid.NewGuid():N}");

    string CachePath => Path.Combine(_dir, "community-catalog.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    void SeedCache(string body)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(CachePath, body);
    }

    // Records every request and replies via a responder, same idiom as DriveClientTests.
    class RecordingHandler : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = new();
        readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_responder(request));
        }
    }

    static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    static RecordingHandler Serving(string body) => new(_ => Ok(body));

    static RecordingHandler Failing() => new(_ => throw new HttpRequestException("no network"));

    CommunityCatalogClient Client(RecordingHandler handler) => new(handler, CachePath);

    // CAT-06: opening the app must not touch the network. Building the client
    // is the only thing the Start card does before the user asks for the list.
    [Fact]
    public void Constructor_SendsNoRequest()
    {
        var handler = Serving(GoodBody);
        _ = Client(handler);
        Assert.Empty(handler.Requests);
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public async Task Load_SendsOneGetToTheCatalogUrl()
    {
        var handler = Serving(GoodBody);
        await Client(handler).LoadAsync();

        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("https://bvhbml89uymwxubx.quadstick.com/", handler.Requests[0].RequestUri!.ToString());
    }

    // Apps Script answers 403 to a request with no User-Agent, and HttpClient
    // sends none unless it is asked to. Dropping this header breaks the catalog
    // for everyone while every test with a fake handler still passes.
    [Fact]
    public async Task Load_SendsAUserAgent()
    {
        var handler = Serving(GoodBody);
        await Client(handler).LoadAsync();

        Assert.NotEmpty(handler.Requests[0].Headers.UserAgent);
    }

    [Fact]
    public async Task Load_ParsesFullAndMinimumRows()
    {
        var result = await Client(Serving(GoodBody)).LoadAsync();

        Assert.False(result.FromCache);
        Assert.Equal(0, result.SkippedRows);
        Assert.Equal(2, result.Profiles.Count);

        var cyberpunk = result.Profiles[0];
        Assert.Equal("Cyberpunk 2077", cyberpunk.Name);
        Assert.Equal(IdA, cyberpunk.SheetId);
        Assert.Equal("Cyberpunk.csv", cyberpunk.CsvName);
        Assert.Equal("PS5 USB", cyberpunk.Connection);
        Assert.Equal("Sip to toggle", cyberpunk.Notes);
        Assert.Equal("mouse", cyberpunk.Pointer);

        var doom = result.Profiles[1];
        Assert.Equal("Doom", doom.Name);
        Assert.Equal(IdB, doom.SheetId);
        Assert.Equal("Doom.csv", doom.CsvName);
        Assert.Equal("", doom.Connection);
        Assert.Equal("", doom.Notes);
        Assert.Equal("", doom.Pointer);
    }

    // The second array is the voice list. It must not appear and must not even
    // count as a skipped row.
    [Fact]
    public async Task Load_IgnoresTheVoiceArray()
    {
        var result = await Client(Serving(GoodBody)).LoadAsync();

        Assert.Equal(0, result.SkippedRows);
        Assert.DoesNotContain(result.Profiles, p => p.Name == "Voice pack");
        Assert.DoesNotContain(result.Profiles, p => p.CsvName == "voices.vch");
    }

    [Fact]
    public async Task Load_SkipsBadRowsAndReportsTheCount()
    {
        var body = $$"""
            [[["Good","{{IdA}}","Good.csv"],
            "not a row",
            ["Two fields only","{{IdB}}"],
            ["Short id","abc","Short.csv"],
            ["Path in name","{{IdB}}","../evil.csv"],
            ["","{{IdC}}","Nameless.csv"],
            [1,2,3]]]
            """;

        var result = await Client(Serving(body)).LoadAsync();

        Assert.Single(result.Profiles);
        Assert.Equal("Good", result.Profiles[0].Name);
        Assert.Equal(6, result.SkippedRows);
    }

    // A sheet ID that is really a published link or a path would send the
    // import somewhere the row did not name.
    [Fact]
    public async Task Load_SkipsRowsWhoseIdIsNotAPlainSheetId()
    {
        var body = $$"""
            [[["Published","e/2PACX-1vTestTestTestTestTest","Published.csv"],
            ["Traversal","{{IdA}}/edit#gid=0","Traversal.csv"],
            ["Fine","{{IdC}}","Fine.csv"]]]
            """;

        var result = await Client(Serving(body)).LoadAsync();

        Assert.Single(result.Profiles);
        Assert.Equal("Fine", result.Profiles[0].Name);
        Assert.Equal(2, result.SkippedRows);
    }

    [Fact]
    public async Task Load_SortsByNameIgnoringCase()
    {
        var body = $$"""
            [[["zebra","{{IdA}}","z.csv"],["Apple","{{IdB}}","a.csv"],["mango","{{IdC}}","m.csv"]]]
            """;

        var result = await Client(Serving(body)).LoadAsync();

        Assert.Equal(new[] { "Apple", "mango", "zebra" }, result.Profiles.Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task Load_WritesTheCacheAndCreatesItsFolder()
    {
        Assert.False(Directory.Exists(_dir));

        await Client(Serving(GoodBody)).LoadAsync();

        Assert.Equal(GoodBody, File.ReadAllText(CachePath));
        Assert.False(File.Exists(CachePath + ".qscm-tmp"));
    }

    // Rows dropped is normal, the reply still parsed. It replaces the cache.
    [Fact]
    public async Task Load_ReplacesCacheWhenSomeRowsWereSkipped()
    {
        SeedCache(GoodBody);
        var body = $$"""[[["Only","{{IdA}}","Only.csv"],"junk"]]""";

        var result = await Client(Serving(body)).LoadAsync(refresh: true);

        Assert.Equal(1, result.SkippedRows);
        Assert.Equal(body, File.ReadAllText(CachePath));
    }

    // Opening the list should be free. Only Refresh spends a request.
    [Fact]
    public async Task Load_UsesTheCacheWithoutAnyRequest()
    {
        SeedCache(GoodBody);
        var handler = Serving(GoodBody);

        var result = await Client(handler).LoadAsync();

        Assert.True(result.FromCache);
        Assert.Equal(2, result.Profiles.Count);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Load_RefreshFetchesEvenWhenTheCacheIsGood()
    {
        SeedCache(GoodBody);
        var body = $$"""[[["Fresh","{{IdA}}","Fresh.csv"]]]""";
        var handler = Serving(body);

        var result = await Client(handler).LoadAsync(refresh: true);

        Assert.False(result.FromCache);
        Assert.Equal("Fresh", Assert.Single(result.Profiles).Name);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Load_FallsBackToTheCacheWhenTheFetchFails()
    {
        SeedCache(GoodBody);

        var result = await Client(Failing()).LoadAsync(refresh: true);

        Assert.True(result.FromCache);
        Assert.Equal(2, result.Profiles.Count);
    }

    [Fact]
    public async Task Load_FallsBackToTheCacheOnAnErrorStatus()
    {
        SeedCache(GoodBody);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        { Content = new StringContent("nope") });

        var result = await Client(handler).LoadAsync(refresh: true);

        Assert.True(result.FromCache);
        Assert.Equal(2, result.Profiles.Count);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"games\":[]}")]
    [InlineData("[]")]
    [InlineData("[\"games\",\"voices\"]")]
    [InlineData("[null,[]]")]
    public async Task Load_MalformedTopLevelLeavesTheCacheByteForByte(string body)
    {
        SeedCache(GoodBody);
        var before = File.ReadAllBytes(CachePath);

        var result = await Client(Serving(body)).LoadAsync(refresh: true);

        Assert.True(result.FromCache);
        Assert.Equal(2, result.Profiles.Count);
        Assert.Equal(before, File.ReadAllBytes(CachePath));
    }

    [Fact]
    public async Task Load_ThrowsScopedExceptionWhenTheFetchFailsAndThereIsNoCache()
    {
        var ex = await Assert.ThrowsAsync<CommunityCatalogException>(
            () => Client(Failing()).LoadAsync());

        Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public async Task Load_ThrowsScopedExceptionWhenTheReplyIsMalformedAndThereIsNoCache()
    {
        await Assert.ThrowsAsync<CommunityCatalogException>(
            () => Client(Serving("{\"games\":[]}")).LoadAsync());

        Assert.False(File.Exists(CachePath));
    }

    // An empty games array is still a valid reply, just an empty catalog.
    [Fact]
    public async Task Load_AcceptsAnEmptyGamesArray()
    {
        var result = await Client(Serving("[[],[]]")).LoadAsync();

        Assert.Empty(result.Profiles);
        Assert.Equal(0, result.SkippedRows);
        Assert.False(result.FromCache);
    }

    // A corrupt cache is treated as no cache, never a crash.
    [Fact]
    public async Task Load_IgnoresACorruptCacheAndFetches()
    {
        SeedCache("half a fi");
        var handler = Serving(GoodBody);

        var result = await Client(handler).LoadAsync();

        Assert.False(result.FromCache);
        Assert.Single(handler.Requests);
        Assert.Equal(GoodBody, File.ReadAllText(CachePath));
    }

    // Cancelling must stop, not silently hand back stale data.
    [Fact]
    public async Task Load_HonoursCancellationEvenWithACache()
    {
        SeedCache(GoodBody);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client(Serving(GoodBody)).LoadAsync(refresh: true, cts.Token));
    }

    // A broken or hostile endpoint must not be able to pour an endless reply
    // into memory. Over the cap is just another failed fetch.
    [Fact]
    public async Task Load_FallsBackToTheCacheWhenTheReplyIsOverTheCap()
    {
        SeedCache(GoodBody);
        var before = File.ReadAllBytes(CachePath);
        var huge = new string('a', CommunityCatalogClient.MaxReplyBytes + 1);

        var result = await Client(Serving(huge)).LoadAsync(refresh: true);

        Assert.True(result.FromCache);
        Assert.Equal(2, result.Profiles.Count);
        Assert.Equal(before, File.ReadAllBytes(CachePath));
    }

    [Fact]
    public async Task Load_ThrowsWhenTheReplyIsOverTheCapAndThereIsNoCache()
    {
        var huge = new string('a', CommunityCatalogClient.MaxReplyBytes + 1);

        await Assert.ThrowsAsync<CommunityCatalogException>(
            () => Client(Serving(huge)).LoadAsync());

        Assert.False(File.Exists(CachePath));
    }

    // The cap has room to spare, so the catalog can grow a long way past the
    // 64 KB it is today without the list going stale.
    [Fact]
    public async Task Load_AcceptsAReplyWellUnderTheCap()
    {
        var padded = GoodBody + new string(' ', 256 * 1024);

        var result = await Client(Serving(padded)).LoadAsync();

        Assert.False(result.FromCache);
        Assert.Equal(2, result.Profiles.Count);
    }

    // The community window builds a catalog client every time it opens, so a
    // private HttpClient each time would leave one behind for the collector on
    // every open. The test seam still gets its own.
    [Fact]
    public void Clients_ShareOneHttpClientButTheTestSeamDoesNot()
    {
        var http = typeof(CommunityCatalogClient)
            .GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.Same(http.GetValue(new CommunityCatalogClient()),
                    http.GetValue(new CommunityCatalogClient()));
        Assert.NotSame(http.GetValue(new CommunityCatalogClient()),
                       http.GetValue(Client(Serving(GoodBody))));
    }

    [Fact]
    public void DefaultCachePath_SitsUnderApplicationData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QuadStickConfigManager", "community-catalog.json");

        Assert.Equal(expected, CommunityCatalogClient.DefaultCachePath);
    }
}
