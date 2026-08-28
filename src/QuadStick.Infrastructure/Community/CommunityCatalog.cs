using System.Text;
using System.Text.Json;
using QuadStick.Format;
using QuadStick.Infrastructure.Files;

namespace QuadStick.App;

// HTTP/cache implementation of the official shared-profile catalog provider.
public sealed class HttpCommunityCatalogSource : ICommunityCatalogSource
{
    public const string CatalogUrl = "https://bvhbml89uymwxubx.quadstick.com";

    public static string DefaultCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuadStickConfigManager", "community-catalog.json");

    public const int MaxReplyBytes = 4 * 1024 * 1024;
    public const int MaxRows = 5_000;
    public const int MaxFieldChars = 2_000;

    static readonly HttpClient Shared = NewHttpClient(new SocketsHttpHandler
    {
        MaxAutomaticRedirections = 5,
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    });

    readonly HttpClient _http;
    readonly string _cachePath;

    public HttpCommunityCatalogSource() : this(Shared, DefaultCachePath) { }

    public HttpCommunityCatalogSource(HttpMessageHandler handler, string cachePath)
        : this(NewHttpClient(handler), cachePath) { }

    HttpCommunityCatalogSource(HttpClient http, string cachePath)
    {
        _http = http;
        _cachePath = cachePath;
    }

    static HttpClient NewHttpClient(HttpMessageHandler handler) => new(handler)
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = MaxReplyBytes,
        DefaultRequestHeaders = { { "User-Agent", "QuadStickConfigManager" } },
    };

    public async Task<CommunityCatalogResult> LoadAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!refresh && await TryReadCacheAsync().ConfigureAwait(false) is { } cached)
            return new CommunityCatalogResult(cached.Profiles, true, cached.Skipped);

        string body;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, CatalogUrl);
            using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            body = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                         or IOException or InvalidOperationException)
        {
            return await CacheOrThrowAsync(ex).ConfigureAwait(false);
        }

        List<CommunityProfile> profiles;
        int skipped;
        try { (profiles, skipped) = Parse(body); }
        catch (CommunityCatalogException ex) { return await CacheOrThrowAsync(ex).ConfigureAwait(false); }

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
        catch (Exception ex) when (ex is CommunityCatalogException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
        catch (JsonException ex) { throw new CommunityCatalogException("The community catalog was not valid JSON.", ex); }

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
        return SheetsUrl.TryGetXlsxExportUrl($"https://docs.google.com/spreadsheets/d/{id}/edit", out var export)
               && export == $"https://docs.google.com/spreadsheets/d/{id}/export?format=xlsx";
    }
}
