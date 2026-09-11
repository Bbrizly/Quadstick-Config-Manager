using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuadStick.Format;

namespace QuadStick.App;

internal sealed record RegistryInstallProfile(
    string Id,
    string GameId,
    string Platform,
    string DeviceId,
    string Title,
    string SourceType,
    string? SourceUrl,
    string SnapshotSha256);

internal sealed record RegistryCatalogResult(
    IReadOnlyList<RegistryInstallProfile> Profiles,
    bool FromCache);

internal sealed class ProfileRegistryException(string message) : Exception(message);

/// <summary>
/// Reads the public Adaptive Profiles registry from GitHub. Network data is
/// always treated as untrusted: size, schema, identifiers, duplicates, source
/// URLs and snapshot hashes are validated before QCM uses anything. The last
/// valid catalog and each verified CSV snapshot are cached for outage fallback.
/// </summary>
internal sealed class ProfileRegistryClient
{
    internal const string CatalogUrl =
        "https://raw.githubusercontent.com/Bbrizly/Adaptive-Profiles/main/generated/index.json";

    const string RawRoot =
        "https://raw.githubusercontent.com/Bbrizly/Adaptive-Profiles/main";
    const int MaxCatalogBytes = 8 * 1024 * 1024;
    const int MaxCsvBytes = 512 * 1024;
    const int MaxProfiles = 50_000;

    static readonly Regex ProfileId = new("^[a-z0-9]+(?:-+[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    static readonly Regex Slug = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    static readonly Regex Sha256 = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);

    readonly HttpClient _http;
    readonly string _cachePath;
    readonly string _snapshotCacheDir;

    public ProfileRegistryClient(HttpMessageHandler? handler = null, string? cachePath = null, string? snapshotCacheDir = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("QuadStick-Config-Manager/1.0");
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuadStickConfigManager");
        _cachePath = cachePath ?? Path.Combine(appData, "adaptive-profiles-index.json");
        _snapshotCacheDir = snapshotCacheDir ?? Path.Combine(appData, "adaptive-profiles");
    }

    public async Task<RegistryCatalogResult> LoadAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && TryReadCache(out var cached)) return new(cached, true);
        try
        {
            using var response = await _http.GetAsync(CatalogUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var bytes = await ReadLimitedAsync(response, MaxCatalogBytes, ct);
            var json = DecodeUtf8(bytes, "Registry JSON is not valid UTF-8.");
            var profiles = Parse(json);
            WriteCache(_cachePath, json);
            return new(profiles, false);
        }
        catch when (TryReadCache(out var fallback))
        {
            return new(fallback, true);
        }
    }

    public async Task<string> DownloadCsvAsync(RegistryInstallProfile profile, CancellationToken ct = default)
    {
        var cachePath = Path.Combine(_snapshotCacheDir, profile.Id + ".csv");
        try
        {
            using var response = await _http.GetAsync(CsvUrl(profile), HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var bytes = await ReadLimitedAsync(response, MaxCsvBytes, ct);
            VerifySnapshot(bytes, profile.SnapshotSha256);
            var csv = DecodeUtf8(bytes, "Profile CSV is not valid UTF-8.");
            WriteCache(cachePath, csv);
            return csv;
        }
        catch when (TryReadSnapshotCache(cachePath, profile.SnapshotSha256, out var cached))
        {
            return cached;
        }
    }

    internal static IReadOnlyList<RegistryInstallProfile> Parse(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new ProfileRegistryException($"Registry JSON is invalid: {ex.Message}"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var version) || version.GetInt32() != 1 ||
                !root.TryGetProperty("profiles", out var rows) || rows.ValueKind != JsonValueKind.Array)
                throw new ProfileRegistryException("Registry has an unsupported shape.");
            if (rows.GetArrayLength() > MaxProfiles) throw new ProfileRegistryException("Registry has too many profiles.");

            var result = new List<RegistryInstallProfile>(rows.GetArrayLength());
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) throw new ProfileRegistryException("Registry profile is not an object.");
                string Text(string name, int max = 220)
                {
                    if (!row.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                        throw new ProfileRegistryException($"Profile is missing {name}.");
                    var text = value.GetString() ?? "";
                    if (text.Length is 0 || text.Length > max) throw new ProfileRegistryException($"Profile {name} is invalid.");
                    return text;
                }

                var id = Text("id");
                var gameId = Text("gameId");
                var platform = Text("platform", 20);
                var deviceId = Text("deviceId");
                var title = Text("title", 100);
                if (!ProfileId.IsMatch(id) || !Slug.IsMatch(gameId) || !Slug.IsMatch(deviceId))
                    throw new ProfileRegistryException("Registry profile contains an invalid id.");
                if (!new[] { "pc", "xbox", "playstation", "switch" }.Contains(platform, StringComparer.Ordinal))
                    throw new ProfileRegistryException($"Profile {id} has an invalid platform.");
                if (!seen.Add(id)) throw new ProfileRegistryException($"Duplicate registry profile id: {id}");

                if (!row.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object ||
                    !source.TryGetProperty("type", out var sourceTypeValue) || sourceTypeValue.ValueKind != JsonValueKind.String)
                    throw new ProfileRegistryException($"Profile {id} has no supported source.");
                var sourceType = sourceTypeValue.GetString() ?? "";
                if (sourceType is not ("google-sheet" or "csv"))
                    throw new ProfileRegistryException($"Profile {id} has no supported source.");
                string? sourceUrl = null;
                if (sourceType == "google-sheet")
                {
                    if (!source.TryGetProperty("url", out var urlValue) || urlValue.ValueKind != JsonValueKind.String)
                        throw new ProfileRegistryException($"Profile {id} has no Google Sheet URL.");
                    sourceUrl = urlValue.GetString();
                    if (!IsAllowedGoogleSheet(sourceUrl)) throw new ProfileRegistryException($"Profile {id} has an invalid Google Sheet URL.");
                }

                if (!row.TryGetProperty("snapshot", out var snapshot) || snapshot.ValueKind != JsonValueKind.Object ||
                    !snapshot.TryGetProperty("file", out var file) || file.GetString() != "profile.csv" ||
                    !snapshot.TryGetProperty("sha256", out var sha) || sha.ValueKind != JsonValueKind.String)
                    throw new ProfileRegistryException($"Profile {id} has an invalid snapshot.");
                var snapshotSha = sha.GetString() ?? "";
                if (!Sha256.IsMatch(snapshotSha)) throw new ProfileRegistryException($"Profile {id} has an invalid snapshot hash.");

                result.Add(new(id, gameId, platform, deviceId, title, sourceType, sourceUrl, snapshotSha));
            }
            return result;
        }
    }

    internal static string CsvUrl(RegistryInstallProfile profile) =>
        $"{RawRoot}/data/profiles/{profile.GameId}/{profile.DeviceId}/{profile.Id}/profile.csv";

    static bool IsAllowedGoogleSheet(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && parts[0] == "spreadsheets" && parts[1] == "d" &&
               parts[2].Length is >= 20 and <= 200 && parts[2].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    }

    bool TryReadCache(out IReadOnlyList<RegistryInstallProfile> profiles)
    {
        profiles = Array.Empty<RegistryInstallProfile>();
        try
        {
            if (!File.Exists(_cachePath)) return false;
            var info = new FileInfo(_cachePath);
            if (info.Length <= 0 || info.Length > MaxCatalogBytes) return false;
            profiles = Parse(File.ReadAllText(_cachePath, Encoding.UTF8));
            return true;
        }
        catch { return false; }
    }

    static bool TryReadSnapshotCache(string path, string expectedSha, out string csv)
    {
        csv = "";
        try
        {
            if (!File.Exists(path)) return false;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length is <= 0 or > MaxCsvBytes) return false;
            VerifySnapshot(bytes, expectedSha);
            csv = DecodeUtf8(bytes, "Cached profile CSV is not valid UTF-8.");
            return true;
        }
        catch { return false; }
    }

    static void VerifySnapshot(byte[] bytes, string expectedSha)
    {
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expectedSha)))
            throw new ProfileRegistryException("Profile CSV does not match the reviewed registry snapshot.");
    }

    static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is long length && length > maxBytes)
            throw new ProfileRegistryException("Registry response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0) break;
            if (memory.Length + read > maxBytes) throw new ProfileRegistryException("Registry response is too large.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    static string DecodeUtf8(byte[] bytes, string message)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new ProfileRegistryException(message); }
    }

    static void WriteCache(string path, string text)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            ProfileFile.WriteAtomic(path, text);
        }
        catch { /* A cache failure must never make a registry import fail. */ }
    }
}
