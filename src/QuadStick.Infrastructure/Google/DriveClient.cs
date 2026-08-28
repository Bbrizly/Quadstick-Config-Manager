using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QuadStick.Format;

namespace QuadStick.Infrastructure.Google;

// Plain REST against Drive and Sheets, no Google SDK. Authentication is supplied
// per request so this type owns only provider transport behavior.
public class DriveClient : IDisposable
{
    const string SheetsBase = "https://sheets.googleapis.com/v4/spreadsheets";
    const string DriveBase = "https://www.googleapis.com/drive/v3/files";
    const int LastColumn = 702;
    const int LastRow = 10000;

    public const int MaxWorkbookBytes = 16 * 1024 * 1024;

    readonly HttpClient _http;
    readonly bool _ownsHttp;
    readonly Func<CancellationToken, Task<string>> _accessToken;
    bool _disposed;

    public DriveClient(HttpMessageHandler handler, Func<CancellationToken, Task<string>> accessToken)
        : this(new HttpClient(handler), accessToken, ownsHttp: true) { }

    public DriveClient(HttpClient http, Func<CancellationToken, Task<string>> accessToken)
        : this(http, accessToken, ownsHttp: false) { }

    DriveClient(HttpClient http, Func<CancellationToken, Task<string>> accessToken, bool ownsHttp)
    {
        _http = http;
        _accessToken = accessToken;
        _ownsHttp = ownsHttp;
    }

    public async Task<string> CreateSpreadsheetAsync(string title, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { properties = new { title } });
        using var req = new HttpRequestMessage(HttpMethod.Post, SheetsBase)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var resp = await SendAsync(req, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.GetProperty("spreadsheetId").GetString()!;
    }

    public async Task DeleteSpreadsheetAsync(string id, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{DriveBase}/{id}");
        using var resp = await SendAsync(req, ct).ConfigureAwait(false);
    }

    /// <summary>Idempotently reconstruct the profile workbook. If a request
    /// fails after a partial remote change, replaying this method converges the
    /// workbook to the complete desired profile.</summary>
    public async Task PushTabsAsync(string id, IReadOnlyList<ProfileTab> tabs, CancellationToken ct = default)
    {
        if (!tabs.Any(t => t.Rows.Any(r => r.Any(c => !string.IsNullOrWhiteSpace(c)))))
            throw new InvalidDataException("Refusing to replace a backup with an empty profile.");

        var reshaped = await ShapeTabsAsync(id, tabs.Select(t => t.Title).ToList(), ct).ConfigureAwait(false);

        var data = new List<object>();
        var ranges = new List<string>();
        foreach (var tab in tabs)
        {
            var width = Math.Max(1, tab.Rows.Count == 0 ? 1 : tab.Rows.Max(r => r.Length));
            var grid = tab.Rows
                .Select(r => r.Length == width
                    ? r
                    : r.Concat(Enumerable.Repeat("", width - r.Length)).ToArray())
                .ToList();
            data.Add(new { range = $"{Quoted(tab.Title)}!A1", values = grid });

            if (grid.Count < LastRow) ranges.Add($"{Quoted(tab.Title)}!A{grid.Count + 1}:ZZ{LastRow}");
            if (width < LastColumn) ranges.Add($"{Quoted(tab.Title)}!{ColumnName(width + 1)}1:ZZ{LastRow}");
        }

        var body = JsonSerializer.Serialize(new { valueInputOption = "RAW", data });
        using (var update = new HttpRequestMessage(HttpMethod.Post, $"{SheetsBase}/{id}/values:batchUpdate")
               { Content = new StringContent(body, Encoding.UTF8, "application/json") })
        using (await SendAsync(update, ct).ConfigureAwait(false)) { }

        if (ranges.Count > 0)
        {
            var clearBody = JsonSerializer.Serialize(new { ranges });
            using var clear = new HttpRequestMessage(HttpMethod.Post, $"{SheetsBase}/{id}/values:batchClear")
            { Content = new StringContent(clearBody, Encoding.UTF8, "application/json") };
            using (await SendAsync(clear, ct).ConfigureAwait(false)) { }
        }

        if (reshaped) await FormatTabsAsync(id, tabs, ct).ConfigureAwait(false);
    }

    async Task FormatTabsAsync(string id, IReadOnlyList<ProfileTab> tabs, CancellationToken ct)
    {
        var byTitle = (await ListTabsAsync(id, ct).ConfigureAwait(false))
            .GroupBy(t => t.Title, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().SheetId, StringComparer.Ordinal);

        var requests = new List<object>();
        foreach (var tab in tabs)
        {
            if (!byTitle.TryGetValue(tab.Title, out var sheetId)) continue;
            requests.Add(new
            {
                updateSheetProperties = new
                {
                    properties = new { sheetId, gridProperties = new { frozenRowCount = tab.HeaderRow + 1 } },
                    fields = "gridProperties.frozenRowCount",
                },
            });
            if (tab.HeaderRow == 0) continue;
            requests.Add(Tint(sheetId, tab.HeaderRow, 0, 1, "OutputTint"));
            requests.Add(Tint(sheetId, tab.HeaderRow, 1, 2, "FunctionTint"));
            requests.Add(Tint(sheetId, tab.HeaderRow, 2, 10, "InputTint"));
        }
        if (requests.Count == 0) return;

        var body = JsonSerializer.Serialize(new { requests });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{SheetsBase}/{id}:batchUpdate")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        try { using (await SendAsync(req, ct).ConfigureAwait(false)) { } }
        catch (DriveApiException) { /* formatting is cosmetic */ }
    }

    static object Tint(int sheetId, int row, int firstColumn, int lastColumn, string paletteKey)
    {
        var (r, g, b) = Rgb(GoogleSheetPalette.Light[paletteKey]);
        return new
        {
            repeatCell = new
            {
                range = new
                {
                    sheetId,
                    startRowIndex = row,
                    endRowIndex = row + 1,
                    startColumnIndex = firstColumn,
                    endColumnIndex = lastColumn,
                },
                cell = new
                {
                    userEnteredFormat = new
                    {
                        backgroundColor = new { red = r, green = g, blue = b },
                        textFormat = new { bold = true },
                    },
                },
                fields = "userEnteredFormat(backgroundColor,textFormat.bold)",
            },
        };
    }

    static (double R, double G, double B) Rgb(string hex)
    {
        var v = Convert.ToInt32(hex.TrimStart('#'), 16);
        return (((v >> 16) & 0xFF) / 255.0, ((v >> 8) & 0xFF) / 255.0, (v & 0xFF) / 255.0);
    }

    async Task<bool> ShapeTabsAsync(string id, List<string> titles, CancellationToken ct)
    {
        var existing = await ListTabsAsync(id, ct).ConfigureAwait(false);
        if (existing.Select(e => e.Title).SequenceEqual(titles, StringComparer.Ordinal)) return false;

        var occupied = existing.Select(e => e.Title).Concat(titles).ToList();
        string temporaryPrefix;
        do
        {
            temporaryPrefix = $"_qsc_tmp_{Guid.NewGuid():N}_";
        }
        while (occupied.Any(t => t.StartsWith(temporaryPrefix, StringComparison.Ordinal)));

        var requests = new List<object>();
        for (var i = 0; i < existing.Count; i++)
            requests.Add(Rename(existing[i].SheetId, temporaryPrefix + i));
        for (var i = 0; i < titles.Count; i++)
        {
            if (i < existing.Count) requests.Add(Rename(existing[i].SheetId, titles[i]));
            else requests.Add(new { addSheet = new { properties = new { title = titles[i] } } });
        }
        for (var i = titles.Count; i < existing.Count; i++)
            requests.Add(new { deleteSheet = new { sheetId = existing[i].SheetId } });

        var body = JsonSerializer.Serialize(new { requests });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{SheetsBase}/{id}:batchUpdate")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using (await SendAsync(req, ct).ConfigureAwait(false)) { }
        return true;
    }

    static object Rename(int sheetId, string title) =>
        new { updateSheetProperties = new { properties = new { sheetId, title }, fields = "title" } };

    public async Task<List<(int SheetId, string Title)>> ListTabsAsync(string id, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{SheetsBase}/{id}?fields={Uri.EscapeDataString("sheets.properties(sheetId,title)")}");
        using var resp = await SendAsync(req, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var tabs = new List<(int, string)>();
        if (doc.RootElement.TryGetProperty("sheets", out var sheets))
            foreach (var s in sheets.EnumerateArray())
            {
                var p = s.GetProperty("properties");
                tabs.Add((
                    p.TryGetProperty("sheetId", out var sid) ? sid.GetInt32() : 0,
                    p.TryGetProperty("title", out var t) ? t.GetString() ?? "" : ""));
            }
        return tabs;
    }

    static string Quoted(string title) => "'" + title.Replace("'", "''") + "'";

    static string ColumnName(int index)
    {
        var name = "";
        while (index > 0)
        {
            index -= 1;
            name = (char)('A' + index % 26) + name;
            index /= 26;
        }
        return name;
    }

    public async Task<string> GetModifiedTimeAsync(string id, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{DriveBase}/{id}?fields=modifiedTime");
        using var resp = await SendAsync(req, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.GetProperty("modifiedTime").GetString()!;
    }

    public async Task<byte[]> DownloadWorkbookAsync(string id, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{DriveBase}/{id}/export?mimeType={Uri.EscapeDataString(XlsxMimeType)}");
        using var resp = await SendAsync(req, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

        if (resp.Content.Headers.ContentLength is long declared && declared > MaxWorkbookBytes)
            throw new InvalidDataException($"Drive workbook is larger than {MaxWorkbookBytes / (1024 * 1024)} MB.");

        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaxWorkbookBytes)
                throw new InvalidDataException($"Drive workbook is larger than {MaxWorkbookBytes / (1024 * 1024)} MB.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    const string XlsxMimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public async Task ShareAnyoneReaderAsync(string id, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { role = "reader", type = "anyone", allowFileDiscovery = false });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{DriveBase}/{id}/permissions")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using (await SendAsync(req, ct).ConfigureAwait(false)) { }
    }

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
            using var resp = await SendAsync(req, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
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

    async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage req,
        CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _accessToken(ct).ConfigureAwait(false));
        var resp = await _http.SendAsync(req, completion, ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode) return resp;
        var status = resp.StatusCode;
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        resp.Dispose();
        throw new DriveApiException(status, body);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttp) _http.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class DriveApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public DriveApiException(HttpStatusCode status, string body)
        : base($"Drive API returned {(int)status}: {body}") => StatusCode = status;
}