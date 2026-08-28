using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QuadStick.Format;
using QuadStick.Infrastructure.Google;

namespace QuadStick.App;

// Plain REST against Drive and Sheets, no Google SDK. No dependency on
// GoogleAuth: the access token comes through the provider, fetched per request.
public class DriveClient
{
    const string SheetsBase = "https://sheets.googleapis.com/v4/spreadsheets";
    const string DriveBase = "https://www.googleapis.com/drive/v3/files";

    // Last cell of the wide range the leftover clears sweep.
    const int LastColumn = 702; // ZZ
    const int LastRow = 10000;

    // Real QCM workbooks are tiny. Bound the transport before ZipArchive/XML
    // ever sees it so a remote response cannot be buffered without limit.
    public const int MaxWorkbookBytes = 16 * 1024 * 1024;

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

    /// <summary>Write the profile as one worksheet tab per mode. Writes are
    /// idempotent: if a later request fails the Application layer keeps the link
    /// dirty and replaying this method reconstructs the workbook.</summary>
    public async Task PushTabsAsync(string id, IReadOnlyList<ProfileTab> tabs, CancellationToken ct = default)
    {
        // Returning success here used to let Application mark a backup clean
        // even though Google was untouched.
        if (!tabs.Any(t => t.Rows.Any(r => r.Any(c => !string.IsNullOrWhiteSpace(c)))))
            throw new InvalidDataException("Refusing to replace a backup with an empty profile.");

        bool reshaped = await ShapeTabsAsync(id, tabs.Select(t => t.Title).ToList(), ct);

        // RAW so a pasted "=..." cell is stored as text, never evaluated.
        var data = new List<object>();
        var ranges = new List<string>();
        foreach (var tab in tabs)
        {
            // Every row padded to one width, so a binding that lost an input
            // has that cell blanked by the write instead of keeping its old value.
            int width = Math.Max(1, tab.Rows.Count == 0 ? 1 : tab.Rows.Max(r => r.Length));
            var grid = tab.Rows
                .Select(r => r.Length == width ? r : r.Concat(Enumerable.Repeat("", width - r.Length)).ToArray())
                .ToList();
            data.Add(new { range = $"{Quoted(tab.Title)}!A1", values = grid });

            if (grid.Count < LastRow) ranges.Add($"{Quoted(tab.Title)}!A{grid.Count + 1}:ZZ{LastRow}");
            if (width < LastColumn) ranges.Add($"{Quoted(tab.Title)}!{ColumnName(width + 1)}1:ZZ{LastRow}");
        }

        var body = JsonSerializer.Serialize(new { valueInputOption = "RAW", data });
        using var update = new HttpRequestMessage(HttpMethod.Post, $"{SheetsBase}/{id}/values:batchUpdate")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        (await SendAsync(update, ct)).Dispose();

        if (ranges.Count > 0)
        {
            var clearBody = JsonSerializer.Serialize(new { ranges });
            using var clear = new HttpRequestMessage(HttpMethod.Post, $"{SheetsBase}/{id}/values:batchClear")
            { Content = new StringContent(clearBody, Encoding.UTF8, "application/json") };
            (await SendAsync(clear, ct)).Dispose();
        }

        if (reshaped) await FormatTabsAsync(id, tabs, ct);
    }

    // Frozen headings and the app's own column colours. Best effort: values are
    // already written, and formatting failure must not turn a good backup bad.
    async Task FormatTabsAsync(string id, IReadOnlyList<ProfileTab> tabs, CancellationToken ct)
    {
        var byTitle = (await ListTabsAsync(id, ct))
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
        try { (await SendAsync(req, ct)).Dispose(); }
        catch (DriveApiException) { /* cosmetic only */ }
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

    // Make the spreadsheet's tabs match these titles, in this order.
    async Task<bool> ShapeTabsAsync(string id, List<string> titles, CancellationToken ct)
    {
        var existing = await ListTabsAsync(id, ct);
        if (existing.Select(e => e.Title).SequenceEqual(titles, StringComparer.Ordinal)) return false;

        // Never use a predictable placeholder such as _qsc_0: that is a legal
        // user mode title. A per-operation prefix is chosen so no existing or
        // desired title can collide with the temporary namespace.
        var occupied = existing.Select(e => e.Title).Concat(titles).ToList();
        string temporaryPrefix;
        do
        {
            temporaryPrefix = $"_qsc_tmp_{Guid.NewGuid():N}_";
        }
        while (occupied.Any(t => t.StartsWith(temporaryPrefix, StringComparison.Ordinal)));

        var requests = new List<object>();
        for (int i = 0; i < existing.Count; i++)
            requests.Add(Rename(existing[i].SheetId, temporaryPrefix + i));
        for (int i = 0; i < titles.Count; i++)
        {
            if (i < existing.Count) requests.Add(Rename(existing[i].SheetId, titles[i]));
            else requests.Add(new { addSheet = new { properties = new { title = titles[i] } } });
        }
        for (int i = titles.Count; i < existing.Count; i++)
            requests.Add(new { deleteSheet = new { sheetId = existing[i].SheetId } });

        var body = JsonSerializer.Serialize(new { requests });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{SheetsBase}/{id}:batchUpdate")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        (await SendAsync(req, ct)).Dispose();
        return true;
    }

    static object Rename(int sheetId, string title) =>
        new { updateSheetProperties = new { properties = new { sheetId, title }, fields = "title" } };

    public async Task<List<(int SheetId, string Title)>> ListTabsAsync(string id, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{SheetsBase}/{id}?fields={Uri.EscapeDataString("sheets.properties(sheetId,title)")}");
        using var resp = await SendAsync(req, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
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
        using var resp = await SendAsync(req, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("modifiedTime").GetString()!;
    }

    // Export the whole workbook through Drive and enforce a transport-level cap
    // before buffering it. CSV export would contain only the first tab.
    public async Task<byte[]> DownloadWorkbookAsync(string id, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{DriveBase}/{id}/export?mimeType={Uri.EscapeDataString(XlsxMimeType)}");
        using var resp = await SendAsync(req, ct, HttpCompletionOption.ResponseHeadersRead);

        if (resp.Content.Headers.ContentLength is long declared && declared > MaxWorkbookBytes)
            throw new InvalidDataException($"Drive workbook is larger than {MaxWorkbookBytes / (1024 * 1024)} MB.");

        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0) break;
            if (output.Length + read > MaxWorkbookBytes)
                throw new InvalidDataException($"Drive workbook is larger than {MaxWorkbookBytes / (1024 * 1024)} MB.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    const string XlsxMimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

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

    async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage req,
        CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _accessToken(ct));
        var resp = await _http.SendAsync(req, completion, ct);
        if (resp.IsSuccessStatusCode) return resp;
        var status = resp.StatusCode;
        var body = await resp.Content.ReadAsStringAsync(ct);
        resp.Dispose();
        throw new DriveApiException(status, body);
    }
}

public class DriveApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public DriveApiException(HttpStatusCode status, string body)
        : base($"Drive API returned {(int)status}: {body}") => StatusCode = status;
}
