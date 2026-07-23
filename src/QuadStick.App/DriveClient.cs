using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace QuadStick.App;

// Plain REST against Drive and Sheets, no Google SDK. It does not touch
// GoogleAuth: the access token arrives through the provider, so the two stay
// separable. A fresh token is fetched per request via the provider.
public class DriveClient
{
    const string SheetsBase = "https://sheets.googleapis.com/v4/spreadsheets";
    const string DriveBase = "https://www.googleapis.com/drive/v3/files";

    // Wide bounded range with no sheet prefix. Profiles are a few hundred cells,
    // so this covers any grid while still targeting the first visible sheet.
    const string ClearRange = "A1:ZZ10000";

    readonly HttpClient _http;
    readonly Func<CancellationToken, Task<string>> _accessToken;

    public DriveClient(HttpMessageHandler handler, Func<CancellationToken, Task<string>> accessToken)
    {
        _http = new HttpClient(handler);
        _accessToken = accessToken;
    }

    public async Task<string> CreateSpreadsheetAsync(string title, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { properties = new { title } });
        using var req = new HttpRequestMessage(HttpMethod.Post, SheetsBase)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var resp = await SendAsync(req, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("spreadsheetId").GetString()!;
    }

    // A range in A1 notation with no sheet prefix targets the spreadsheet's
    // first visible sheet, which is exactly the spec's "first worksheet only"
    // rule. Clear happens before update so a shrunken profile leaves no stale cells.
    public async Task PushGridAsync(string id, List<string[]> rows, CancellationToken ct = default)
    {
        using (var clear = new HttpRequestMessage(HttpMethod.Post, $"{SheetsBase}/{id}/values/{ClearRange}:clear")
        { Content = new StringContent("{}", Encoding.UTF8, "application/json") })
            (await SendAsync(clear, ct)).Dispose();

        // RAW so a pasted "=..." cell is stored as text, never evaluated.
        var body = JsonSerializer.Serialize(new { values = rows });
        using var update = new HttpRequestMessage(HttpMethod.Put, $"{SheetsBase}/{id}/values/A1?valueInputOption=RAW")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        (await SendAsync(update, ct)).Dispose();
    }

    public async Task<string> GetModifiedTimeAsync(string id, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{DriveBase}/{id}?fields=modifiedTime");
        using var resp = await SendAsync(req, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("modifiedTime").GetString()!;
    }

    // Authenticated CSV export of the first worksheet. Everything after the
    // bytes arrive is the existing unauthenticated import code unchanged.
    public async Task<string> DownloadCsvAsync(string id, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://docs.google.com/spreadsheets/d/{id}/export?format=csv");
        using var resp = await SendAsync(req, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    // Anyone-with-link reader. allowFileDiscovery=false keeps it link-only.
    public async Task ShareAnyoneReaderAsync(string id, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { role = "reader", type = "anyone", allowFileDiscovery = false });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{DriveBase}/{id}/permissions")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        (await SendAsync(req, ct)).Dispose();
    }

    // Under drive.file this lists exactly the sheets this app created.
    public async Task<List<(string Id, string Name, string ModifiedTime)>> ListSpreadsheetsAsync(CancellationToken ct = default)
    {
        var results = new List<(string, string, string)>();
        var q = Uri.EscapeDataString("mimeType='application/vnd.google-apps.spreadsheet' and trashed=false");
        string? pageToken = null;
        do
        {
            var url = $"{DriveBase}?q={q}&fields=nextPageToken,files(id,name,modifiedTime)";
            if (pageToken != null) url += "&pageToken=" + Uri.EscapeDataString(pageToken);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await SendAsync(req, ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("files", out var files))
                foreach (var f in files.EnumerateArray())
                    results.Add((
                        f.GetProperty("id").GetString()!,
                        f.GetProperty("name").GetString()!,
                        f.TryGetProperty("modifiedTime", out var m) ? m.GetString()! : ""));
            pageToken = root.TryGetProperty("nextPageToken", out var pt) ? pt.GetString() : null;
        } while (!string.IsNullOrEmpty(pageToken));
        return results;
    }

    async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _accessToken(ct));
        var resp = await _http.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode) return resp;
        var status = resp.StatusCode;
        var body = await resp.Content.ReadAsStringAsync(ct);
        resp.Dispose();
        // Carry the status so callers can branch on 404 vs everything else.
        throw new DriveApiException(status, body);
    }
}

public class DriveApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public DriveApiException(HttpStatusCode status, string body)
        : base($"Drive API returned {(int)status}: {body}") => StatusCode = status;
}
