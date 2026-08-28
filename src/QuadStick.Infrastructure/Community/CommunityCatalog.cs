using System.Text;
using System.Text.Json;
using QuadStick.Application.Community;
using QuadStick.Format;
using QuadStick.Infrastructure.Files;
using QuadStick.Infrastructure.Google;

namespace QuadStick.Infrastructure.Community;

/// <summary>HTTP/cache implementation of the official shared-profile catalog.
/// The HttpClient lifetime belongs to composition; external response/cache size
/// is enforced before an unbounded string allocation.</summary>
public sealed class HttpCommunityCatalogSource : ICommunityCatalogSource
{
    public const string CatalogUrl = "https://bvhbml89uymwxubx.quadstick.com";

    public static string DefaultCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuadStickConfigManager", "community-catalog.json");

    public const int MaxReplyBytes = 4 * 1024 * 1024;
    public const int MaxRows = 5_000;
    public const int MaxFieldChars = 2_000;

    readonly HttpClient _http;
    readonly string _cachePath;

    public HttpCommunityCatalogSource(HttpClient http, string? cachePath = null)
    {
        _http = http;
        _cachePath = cachePath ?? DefaultCachePath;
    }

    // Test seam. Production composition uses the HttpClient constructor.
    public HttpCommunityCatalogSource(HttpMessageHandler handler, string cachePath)
        : this(CreateClient(handler), cachePath) { }

    public static HttpClient CreateProductionClient()
    {
        var handler = new SocketsHttpHandler
        {
            MaxAutomaticRedirections = 5,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        };
        return CreateClient(handler);
    }

    static HttpClient CreateClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "QuadStickConfigManager");
        return client;
    }

    public async Task<CommunityCatalogResult> LoadAsync(
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!refresh && await TryReadCacheAsync(cancellationToken).ConfigureAwait(false) is { } cached)
            return new CommunityCatalogResult(cached.Profiles, true, cached.Skipped);

        string body;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, CatalogUrl);
            using var resp = await _http.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            body = await ReadBoundedUtf8Async(resp.Content, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                         or IOException or InvalidOperationException or InvalidDataException)
        {
            return await CacheOrThrowAsync(ex, cancellationToken).ConfigureAwait(false);
        }

        List<CommunityProfile> profiles;
        int skipped;
        try { (profiles, skipped) = Parse(body); }
        catch (CommunityCatalogException ex)
        {
            return await CacheOrThrowAsync(ex, cancellationToken).ConfigureAwait(false);
        }

        SaveCache(body);
        return new CommunityCatalogResult(profiles, false, skipped);
    }

    async Task<CommunityCatalogResult> CacheOrThrowAsync(
        Exception cause,
        CancellationToken cancellationToken) =>
        await TryReadCacheAsync(cancellationToken).ConfigureAwait(false) is { } cached
            ? new CommunityCatalogResult(cached.Profiles, true, cached.Skipped)
            : throw new CommunityCatalogException("Could not read the community catalog.", cause);

    async Task<(List<CommunityProfile> Profiles, int Skipped)?> TryReadCacheAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            var info = new FileInfo(_cachePath);
            if (info.Length > MaxReplyBytes) return null;
            var bytes = await File.ReadAllBytesAsync(_cachePath, cancellationToken).ConfigureAwait(false);
            if (bytes.Length > MaxReplyBytes) return null;
            return Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is CommunityCatalogException or IOException
                                         or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    static async Task<string> ReadBoundedUtf8Async(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long declared && declared > MaxReplyBytes)
            throw new InvalidDataException("The community catalog response is too large.");

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaxReplyBytes)
                throw new InvalidDataException("The community catalog response is too large.");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray()).TrimStart('\uFEFF');
    }

    void SaveCache(string body)
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicFileWriter.Write(_cachePath, body);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static (List<CommunityProfile> Profiles, int Skipped) Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new CommunityCatalogException("The community catalog was not valid JSON.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                throw new CommunityCatalogException("The community catalog was not a list.");

            var games = root[0];
            if (games.ValueKind != JsonValueKind.Array)
                throw new CommunityCatalogException("The community catalog had no game list.");

            var profiles = new List<CommunityProfile>();
            var skipped = 0;
            foreach (var row in games.EnumerateArray())
            {
                if (profiles.Count >= MaxRows) { skipped++; continue; }
                var profile = TryReadRow(row);
                if (profile is null) skipped++;
                else profiles.Add(profile);
            }

            profiles.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return (profiles, skipped);
        }
    }

    static CommunityProfile? TryReadRow(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 3) return null;

        var name = Text(row, 0);
        var sheetId = Text(row, 1);
        var csvName = Text(row, 2).Trim();

        if (string.IsNullOrWhiteSpace(name)) return null;
        if (!IsSheetId(sheetId)) return null;
        if (!SafeFileName.ForCsv(csvName).Equals(csvName, StringComparison.OrdinalIgnoreCase)) return null;

        var profile = new CommunityProfile(name, sheetId, csvName, Text(row, 4), Text(row, 5), Text(row, 6));
        if (profile.Name.Length > MaxFieldChars || profile.Connection.Length > MaxFieldChars
            || profile.Notes.Length > MaxFieldChars || profile.Pointer.Length > MaxFieldChars)
            return null;

        return profile;
    }

    static string Text(JsonElement row, int index) =>
        index < row.GetArrayLength() && row[index].ValueKind == JsonValueKind.String
            ? row[index].GetString() ?? ""
            : "";

    static bool IsSheetId(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        return SheetsUrl.TryGetXlsxExportUrl(
                   $"https://docs.google.com/spreadsheets/d/{id}/edit", out var export)
               && export == $"https://docs.google.com/spreadsheets/d/{id}/export?format=xlsx";
    }
}