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

    // Last cell of the wide range the leftover clears sweep. No sheet prefix,
    // so it covers any profile grid on the first sheet.
    // ponytail: a profile past 10000 rows keeps its stale tail; nothing near
    // that exists, and the data written is still correct.
    const int LastColumn = 702; // ZZ
    const int LastRow = 10000;

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

    /// <summary>Write the profile as one worksheet tab per mode: the shape the
    /// community writes its sheets in, and the only shape this app's own
    /// importer reads back. Tabs are reused in place, so a tab keeps its
    /// formatting from push to push.
    ///
    /// Write first, clear stale cells second. Never leave the sheet blank
    /// mid-push.</summary>
    public async Task PushTabsAsync(string id, IReadOnlyList<ProfileTab> tabs, CancellationToken ct = default)
    {
        // A blank grid writes nothing, so the clear below would sweep the whole
        // sheet. An empty or truncated local file must never empty the backup.
        if (!tabs.Any(t => t.Rows.Any(r => r.Any(c => !string.IsNullOrWhiteSpace(c))))) return;

        bool reshaped = await ShapeTabsAsync(id, tabs.Select(t => t.Title).ToList(), ct);

        // RAW so a pasted "=..." cell is stored as text, never evaluated.
        var data = new List<object>();
        var ranges = new List<string>();
        foreach (var tab in tabs)
        {
            // Every row padded to one width, so a binding that lost an input
            // has that cell blanked by the write instead of keeping its old
            // value.
            int width = Math.Max(1, tab.Rows.Count == 0 ? 1 : tab.Rows.Max(r => r.Length));
            var grid = tab.Rows
                .Select(r => r.Length == width ? r : r.Concat(Enumerable.Repeat("", width - r.Length)).ToArray())
                .ToList();
            data.Add(new { range = $"{Quoted(tab.Title)}!A1", values = grid });

            // Whatever a bigger earlier profile left outside the block just
            // written: the rows under it and the columns right of it.
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

        // Only when a tab was made or renamed. A save is a push, and paying two
        // more requests on every save to set colours that are already there
        // would make the backup slower for nothing.
        if (reshaped) await FormatTabsAsync(id, tabs, ct);
    }

    // Frozen headings and the app's own column colours, so the sheet reads like
    // the editor and like the community workbooks people already share. Best
    // effort: the values are already written, and a profile that is in the
    // sheet but grey is not a failure worth failing the backup over.
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
            // A tab with no column-naming row has nothing to colour: its one
            // heading is the keyword, and tinting that as an output column
            // would say something untrue about it.
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
        catch (DriveApiException) { /* the profile is up; colours are not worth a failed backup */ }
    }

    static object Tint(int sheetId, int row, int firstColumn, int lastColumn, string paletteKey)
    {
        // Spreadsheet formatting is provider output, not an Avalonia concern.
        // Keep the exact existing light-theme tints here so moving Drive code
        // cannot silently couple Infrastructure back to the UI assembly.
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

    // "#F3E2AE" as the 0..1 channels Sheets wants. The light palette either
    // way: a spreadsheet is white paper, whatever theme the app is in.
    static (double R, double G, double B) Rgb(string hex)
    {
        var v = Convert.ToInt32(hex.TrimStart('#'), 16);
        return (((v >> 16) & 0xFF) / 255.0, ((v >> 8) & 0xFF) / 255.0, (v & 0xFF) / 255.0);
    }

    // Make the spreadsheet's tabs match these titles, in this order: rename what
    // is there, add what is missing, delete what a shorter profile left behind.
    //
    // Every existing tab is renamed out of the way first, in the same batch. A
    // spreadsheet refuses two tabs with one title, and pushing a profile whose
    // modes were reordered or renamed would otherwise collide with its own old
    // names. One batchUpdate applies whole or not at all, so a failure cannot
    // leave the placeholder names behind.
    // True when it changed anything, so the caller knows whether the formatting
    // pass has any work to do.
    async Task<bool> ShapeTabsAsync(string id, List<string> titles, CancellationToken ct)
    {
        var existing = await ListTabsAsync(id, ct);
        // Already the right tabs, in the right order.
        if (existing.Select(e => e.Title).SequenceEqual(titles, StringComparer.Ordinal)) return false;

        var requests = new List<object>();

        for (int i = 0; i < existing.Count; i++)
            requests.Add(Rename(existing[i].SheetId, $"_qsc_{i}"));
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

    // A1 notation quotes a tab name, and an apostrophe in the name is doubled.
    static string Quoted(string title) => "'" + title.Replace("'", "''") + "'";

    // A1 column name: 1 -> A, 27 -> AA.
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

    // Export via the Drive API export endpoint, not docs.google.com. The web
    // endpoint returns 200 with an HTML sign-in page on a bad token; this returns
    // a real 401/403. Works under drive.file for sheets this app created.
    //
    // The workbook, not CSV: a CSV export is the FIRST TAB and nothing else.
    // Now that a push writes one tab per mode, downloading CSV would hand back
    // a profile short every mode but the first, and the two callers write what
    // they download over the user's local file.
    public async Task<byte[]> DownloadWorkbookAsync(string id, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{DriveBase}/{id}/export?mimeType={Uri.EscapeDataString(XlsxMimeType)}");
        using var resp = await SendAsync(req, ct);
        return await resp.Content.ReadAsByteArrayAsync(ct);
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
