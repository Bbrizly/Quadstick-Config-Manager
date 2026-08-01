using System.Text.Json;
using QuadStick.Format;

namespace QuadStick.App;

/// <summary>One game row from the official QuadStick catalog.</summary>
public sealed record CommunityProfile(
    string Name,
    string SheetId,
    string CsvName,
    string Connection,
    string Notes,
    string Pointer);

/// <summary>Profiles plus how they were obtained, so the UI can be honest
/// about stale data and dropped rows.</summary>
public sealed record CommunityCatalogResult(
    IReadOnlyList<CommunityProfile> Profiles,
    bool FromCache,
    int SkippedRows);

/// <summary>The catalog could not be read from the network or from a cache.</summary>
public sealed class CommunityCatalogException : Exception
{
    public CommunityCatalogException(string message, Exception? inner = null) : base(message, inner) { }
}

// The official list of shared game profiles. The old program fetched this same
// endpoint and ran eval() on the reply, so the server could execute anything it
// liked on the user's machine. This reads it as plain JSON and keeps only rows
// that already look like a Google Sheet, and nothing here ever installs.
public sealed class CommunityCatalogClient
{
    internal const string CatalogUrl = "https://bvhbml89uymwxubx.quadstick.com";

    /// <summary>Last good copy, so the list still opens offline.</summary>
    public static string DefaultCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuadStickConfigManager", "community-catalog.json");

    // The live catalog is about 64 KB. Four megabytes is sixty times that, so
    // real growth is fine, while a broken or hostile endpoint cannot stream an
    // endless reply into memory. Going over is just another failed fetch, and
    // the last good cache is used instead.
    public const int MaxReplyBytes = 4 * 1024 * 1024;

    // One HttpClient for the whole app. The community window builds a catalog
    // client every time it opens, and an HttpClient of its own each time would
    // hold sockets and timers until the collector got round to them. Sharing
    // one costs nothing while the window is closed and needs no disposing.
    //
    // The endpoint is a redirector. The default handler follows redirects but
    // refuses an https to http downgrade, which is what we want.
    static readonly HttpClient Shared = NewHttpClient(new HttpClientHandler());

    readonly HttpClient _http;
    readonly string _cachePath;

    public CommunityCatalogClient() : this(Shared, DefaultCachePath) { }

    /// <summary>Test seam, same shape as DriveClient. The handler gets its own
    /// HttpClient, so a fake never reaches the shared one.</summary>
    public CommunityCatalogClient(HttpMessageHandler handler, string cachePath)
        : this(NewHttpClient(handler), cachePath) { }

    CommunityCatalogClient(HttpClient http, string cachePath)
    {
        _http = http;
        _cachePath = cachePath;
    }

    static HttpClient NewHttpClient(HttpMessageHandler handler) => new(handler)
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = MaxReplyBytes,
    };

    /// <summary>Reads the catalog. Without <paramref name="refresh"/> a usable
    /// cache is returned as is, so opening the list costs no request.</summary>
    // Every await here leaves the UI context, so the cache read and the parse
    // run on a pool thread. A large reply must not lock up the window.
    public async Task<CommunityCatalogResult> LoadAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && await TryReadCacheAsync().ConfigureAwait(false) is { } cached)
            return new CommunityCatalogResult(cached.Profiles, true, cached.Skipped);

        string body;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, CatalogUrl);
            // The reply is buffered here, so a body over MaxReplyBytes fails as
            // an HttpRequestException before it is ever held in full.
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        // The user asked to stop. Never quietly hand back the cache instead.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return await CacheOrThrowAsync(ex).ConfigureAwait(false);
        }

        List<CommunityProfile> profiles;
        int skipped;
        try { (profiles, skipped) = Parse(body); }
        catch (CommunityCatalogException ex) { return await CacheOrThrowAsync(ex).ConfigureAwait(false); }

        // Only a reply that parsed all the way through may replace the cache.
        SaveCache(body);
        return new CommunityCatalogResult(profiles, false, skipped);
    }

    async Task<CommunityCatalogResult> CacheOrThrowAsync(Exception cause) =>
        await TryReadCacheAsync().ConfigureAwait(false) is { } cached
            ? new CommunityCatalogResult(cached.Profiles, true, cached.Skipped)
            : throw new CommunityCatalogException("Could not read the community catalog.", cause);

    async Task<(List<CommunityProfile> Profiles, int Skipped)?> TryReadCacheAsync()
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            return Parse(await File.ReadAllTextAsync(_cachePath).ConfigureAwait(false));
        }
        // A corrupt or unreadable cache means "no cache", never a crash.
        catch (Exception ex) when (ex is CommunityCatalogException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Best effort. A full disk should not fail a fetch that already worked.
    void SaveCache(string body)
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            ProfileFile.WriteAtomic(_cachePath, body);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Reads the games array of a <c>[games, voices]</c> reply. Bad
    /// rows are counted and dropped; a bad top level throws.</summary>
    internal static (List<CommunityProfile> Profiles, int Skipped) Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new CommunityCatalogException("The community catalog was not valid JSON.", ex); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                throw new CommunityCatalogException("The community catalog was not a list.");

            // Element 1 is the voice list. It is never read, so nothing in it
            // can reach the UI or count as a skipped row.
            var games = root[0];
            if (games.ValueKind != JsonValueKind.Array)
                throw new CommunityCatalogException("The community catalog had no game list.");

            var profiles = new List<CommunityProfile>();
            var skipped = 0;
            foreach (var row in games.EnumerateArray())
            {
                var profile = TryReadRow(row);
                if (profile is null) skipped++;
                else profiles.Add(profile);
            }

            profiles.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return (profiles, skipped);
        }
    }

    // Index 3 is an edit URL we ignore: the link is built from the ID instead,
    // so a row cannot point the import anywhere else.
    static CommunityProfile? TryReadRow(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 3) return null;

        var name = Text(row, 0);
        var sheetId = Text(row, 1);
        var csvName = Text(row, 2);

        if (string.IsNullOrWhiteSpace(name)) return null;
        if (!IsSheetId(sheetId)) return null;
        if (!SafeFileName.ForCsv(csvName).Equals(csvName, StringComparison.OrdinalIgnoreCase)) return null;

        return new CommunityProfile(name, sheetId, csvName, Text(row, 4), Text(row, 5), Text(row, 6));
    }

    static string Text(JsonElement row, int index) =>
        index < row.GetArrayLength() && row[index].ValueKind == JsonValueKind.String
            ? row[index].GetString() ?? ""
            : "";

    // The ID is good only when its own edit link survives the importer's own
    // parser unchanged. One parser, one rule, no second pattern to keep in sync.
    static bool IsSheetId(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        return SheetsUrl.TryGetXlsxExportUrl($"https://docs.google.com/spreadsheets/d/{id}/edit", out var export)
               && export == $"https://docs.google.com/spreadsheets/d/{id}/export?format=xlsx";
    }
}
