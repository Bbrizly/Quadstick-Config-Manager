using System.Text.Json;
using System.Text.RegularExpressions;
using QuadStick.Format;

namespace QuadStick.App;

internal sealed record RegistryInstallProfile(
    string Id,
    string GameId,
    string Platform,
    string DeviceId,
    string Variant,
    string Contributor,
    string SheetId,
    string? CsvName);

internal sealed record RegistryCatalogResult(
    IReadOnlyList<RegistryInstallProfile> Profiles,
    bool FromCache);

internal sealed class ProfileRegistryException(string message) : Exception(message);

/// <summary>
/// The public adaptive-profile registry is plain JSON in GitHub. QCM treats it
/// as untrusted network input anyway: size, ids, counts, duplicates and install
/// sources are checked again before a profile can reach the existing Sheets
/// importer. A saved copy keeps deep links useful during a GitHub/network outage.
/// </summary>
internal sealed class ProfileRegistryClient
{
    internal const string CatalogUrl =
        "https://raw.githubusercontent.com/Bbrizly/Quadstick-Config-Manager/main/registry/index.json";

    const int MaxBytes = 8 * 1024 * 1024;
    const int MaxProfiles = 50_000;
    static readonly Regex ProfileId = new("^[a-z0-9]+(?:-+[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    static readonly Regex Slug = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    static readonly Regex SheetId = new("^[A-Za-z0-9_-]{20,200}$", RegexOptions.CultureInvariant);

    readonly HttpClient _http;
    readonly string _cachePath;

    public ProfileRegistryClient(HttpMessageHandler? handler = null, string? cachePath = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("QuadStick-Config-Manager/1.0");
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QuadStickConfigManager", "adaptive-profile-registry.json");
    }

    public async Task<RegistryCatalogResult> LoadAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && TryReadCache(out var cached)) return new(cached, true);
        try
        {
            using var response = await _http.GetAsync(CatalogUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long n && n > MaxBytes)
                throw new ProfileRegistryException("Registry reply is too large.");
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > MaxBytes) throw new ProfileRegistryException("Registry reply is too large.");
            var json = System.Text.Encoding.UTF8.GetString(bytes);
            var profiles = Parse(json);
            WriteCache(json);
            return new(profiles, false);
        }
        catch when (TryReadCache(out var fallback))
        {
            return new(fallback, true);
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
                var variant = Text("variant", 40);
                var contributor = Text("contributor", 39);
                if (!ProfileId.IsMatch(id) || !Slug.IsMatch(gameId) || !Slug.IsMatch(deviceId))
                    throw new ProfileRegistryException("Registry profile contains an invalid id.");
                if (!seen.Add(id)) throw new ProfileRegistryException($"Duplicate registry profile id: {id}");
                if (!row.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object ||
                    !source.TryGetProperty("kind", out var kind) || kind.GetString() != "google-sheet" ||
                    !source.TryGetProperty("sheetId", out var sheet) || sheet.ValueKind != JsonValueKind.String)
                    throw new ProfileRegistryException($"Profile {id} has no supported install source.");
                var sid = sheet.GetString() ?? "";
                if (!SheetId.IsMatch(sid)) throw new ProfileRegistryException($"Profile {id} has an invalid Sheet id.");
                string? csv = null;
                if (source.TryGetProperty("csvName", out var csvValue) && csvValue.ValueKind == JsonValueKind.String)
                {
                    csv = csvValue.GetString();
                    if (csv is { Length: > 100 } || (csv is not null && !csv.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
                        throw new ProfileRegistryException($"Profile {id} has an invalid CSV name.");
                }
                result.Add(new(id, gameId, platform, deviceId, variant, contributor, sid, csv));
            }
            return result;
        }
    }

    internal static string SheetEditUrl(RegistryInstallProfile profile) =>
        $"https://docs.google.com/spreadsheets/d/{profile.SheetId}/edit";

    bool TryReadCache(out IReadOnlyList<RegistryInstallProfile> profiles)
    {
        profiles = Array.Empty<RegistryInstallProfile>();
        try
        {
            if (!File.Exists(_cachePath)) return false;
            var info = new FileInfo(_cachePath);
            if (info.Length <= 0 || info.Length > MaxBytes) return false;
            profiles = Parse(File.ReadAllText(_cachePath));
            return true;
        }
        catch { return false; }
    }

    void WriteCache(string json)
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            ProfileFile.WriteAtomic(_cachePath, json);
        }
        catch { /* Cache failure can never make a community import fail. */ }
    }
}
