using System.Text;
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

    // The byte cap bounds the transfer and nothing else. Every row becomes
    // controls in the list, rebuilt on each keystroke in the search box, so a
    // reply that is small enough to arrive can still be far too big to draw:
    // tens of thousands of minimal rows fit inside the cap, as does a single
    // name four megabytes long. The live catalog holds about 311 games with
    // short names, so these are already far past anything real.
    public const int MaxRows = 5_000;
    public const int MaxFieldChars = 2_000;

    // One HttpClient for the whole app. The community window builds a catalog
    // client every time it opens, and an HttpClient of its own each time would
    // hold sockets and timers until the collector got round to them. Sharing
    // one costs nothing while the window is closed and needs no disposing.
    //
    // The endpoint is a redirector, so following redirects is load bearing and
    // cannot be turned off. The handler refuses an https to http downgrade,
    // which is what we want. The hop limit is the default 50 cut to something a
    // real redirector never needs, and the connection lifetime is there because
    // a process wide client with no lifetime never re-resolves DNS: a desktop
    // app left open across a network change kept failing against a stale
    // address until it was restarted.
    static readonly HttpClient Shared = NewHttpClient(new SocketsHttpHandler
    {
        MaxAutomaticRedirections = 5,
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    });

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

    // The redirector lands on Google Apps Script, which answers 403 with an HTML
    // page when the request carries no User-Agent, and HttpClient sends none by
    // default. Any value is accepted; ours names the app in Fred's logs. Without
    // this the catalog never loads, while the Sheets export used by import is
    // unaffected, so import kept working and only this list looked broken.
    static HttpClient NewHttpClient(HttpMessageHandler handler) => new(handler)
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = MaxReplyBytes,
        DefaultRequestHeaders = { { "User-Agent", "QuadStickConfigManager" } },
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
            // Read as bytes and decode here rather than letting HttpClient pick
            // the encoding from the Content-Type header. A charset .NET cannot
            // resolve, "utf-99" or any other typo at the endpoint, made
            // ReadAsStringAsync throw InvalidOperationException, which is not an
            // HttpRequestException and so escaped the whole method: the window
            // sat on "Loading the community list..." for ever with a perfectly
            // good cache on disk, and Refresh reported a crash for a server
            // header. JSON is UTF-8 by definition, and bytes that are not decode
            // to replacement characters and fail as JSON, which is handled.
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            // The byte order mark has to go by hand. ReadAsStringAsync used to
            // detect and drop it, and GetString keeps it, and JsonDocument
            // refuses a document that starts with one. Apps Script can emit it,
            // so leaving it in would have swapped one way of never loading the
            // list for another.
            body = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        }
        // The user asked to stop. Never quietly hand back the cache instead.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                         or IOException or InvalidOperationException)
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
                // Everything past the cap is counted, not silently dropped, so
                // the window can still say how much it did not show.
                if (profiles.Count >= MaxRows) { skipped++; continue; }
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

        var profile = new CommunityProfile(name, sheetId, csvName, Text(row, 4), Text(row, 5), Text(row, 6));

        // The ID and the file name are already held to a shape. The rest is free
        // text that goes straight into wrapping labels, so one row with a four
        // megabyte name could fit inside the reply cap and still hang the list.
        // A game called that is not a game, so the row is dropped and counted.
        if (profile.Name.Length > MaxFieldChars || profile.Connection.Length > MaxFieldChars
            || profile.Notes.Length > MaxFieldChars || profile.Pointer.Length > MaxFieldChars)
            return null;

        return profile;
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
