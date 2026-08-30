using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace QuadStick.App;

/// <summary>
/// Google Drive/Sheets client for documents the USER owns.
///
/// This type intentionally does not expose the destructive backup operations
/// in <see cref="DriveClient"/> (reshape tabs, clear stale ranges, format the
/// workbook). A file selected through Google Picker must flow through this
/// client so an existing years-old Sheet can never accidentally be treated as
/// a QCM-owned backup.
///
/// Every write here is idempotent: set literal cells or replace one CSV blob.
/// That makes bounded retries safe after a transient 429/5xx response.
/// </summary>
public sealed class LinkedDriveClient
{
    const string DriveRoot = "https://www.googleapis.com/drive/v3";
    const string DriveFiles = DriveRoot + "/files";
    const string SheetsRoot = "https://sheets.googleapis.com/v4/spreadsheets";
    const string CsvMimeType = "text/csv";

    readonly HttpClient _http;
    readonly Func<CancellationToken, Task<string>> _accessToken;

    public LinkedDriveClient(HttpMessageHandler handler, Func<CancellationToken, Task<string>> accessToken)
    {
        _http = new HttpClient(handler);
        _accessToken = accessToken;
    }

    /// <summary>A stable Google-account identity for link ownership.</summary>
    public async Task<LinkedGoogleAccount> GetAccountAsync(CancellationToken ct = default)
    {
        var fields = Uri.EscapeDataString("user(permissionId,emailAddress,displayName)");
        using var resp = await SendAsync(HttpMethod.Get, $"{DriveRoot}/about?fields={fields}", null, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var user = doc.RootElement.GetProperty("user");
        return new LinkedGoogleAccount(
            user.TryGetProperty("permissionId", out var p) ? p.GetString() ?? "" : "",
            user.TryGetProperty("emailAddress", out var e) ? e.GetString() ?? "" : "",
            user.TryGetProperty("displayName", out var d) ? d.GetString() ?? "" : "");
    }

    /// <summary>
    /// Validate a Picker result before adopting it. Never trust only the
    /// callback's filename/type: the Drive resource is the authority.
    /// </summary>
    public async Task<LinkedDriveFileMetadata> GetFileMetadataAsync(string fileId, CancellationToken ct = default)
    {
        var fields = Uri.EscapeDataString(
            "id,name,mimeType,version,modifiedTime,driveId,trashed,webViewLink," +
            "capabilities(canEdit,canModifyContent,canDownload)");
        using var resp = await SendAsync(HttpMethod.Get,
            $"{DriveFiles}/{Uri.EscapeDataString(fileId)}?supportsAllDrives=true&fields={fields}", null, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return ParseMetadata(doc.RootElement);
    }

    /// <summary>Get a durable cursor for future user/Shared-Drive changes.</summary>
    public async Task<string> GetStartPageTokenAsync(string? driveId = null, CancellationToken ct = default)
    {
        var url = $"{DriveRoot}/changes/startPageToken?supportsAllDrives=true";
        if (!string.IsNullOrWhiteSpace(driveId))
            url += "&driveId=" + Uri.EscapeDataString(driveId);
        using var resp = await SendAsync(HttpMethod.Get, url, null, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("startPageToken").GetString()!;
    }

    /// <summary>
    /// Read one page of Drive changes. The caller must drain nextPageToken to
    /// the end, durably queue relevant fileIds, THEN persist newStartPageToken.
    /// Advancing the cursor before the queue is durable can lose a change on a
    /// crash; LinkedDriveChangeTracker enforces that ordering.
    /// </summary>
    public async Task<LinkedDriveChangePage> ListChangesAsync(
        string pageToken, string? driveId = null, CancellationToken ct = default)
    {
        var fields = Uri.EscapeDataString(
            "nextPageToken,newStartPageToken,changes(fileId,removed,changeType,time,driveId," +
            "file(id,name,mimeType,version,modifiedTime,driveId,trashed,webViewLink," +
            "capabilities(canEdit,canModifyContent,canDownload)))");
        var url = $"{DriveRoot}/changes?pageToken={Uri.EscapeDataString(pageToken)}" +
                  "&pageSize=1000&includeRemoved=true&includeItemsFromAllDrives=true" +
                  $"&supportsAllDrives=true&fields={fields}";
        if (!string.IsNullOrWhiteSpace(driveId))
            url += "&driveId=" + Uri.EscapeDataString(driveId);

        using var resp = await SendAsync(HttpMethod.Get, url, null, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var changes = new List<LinkedDriveChange>();
        if (root.TryGetProperty("changes", out var arr))
        {
            foreach (var item in arr.EnumerateArray())
            {
                var fileId = item.TryGetProperty("fileId", out var f) ? f.GetString() ?? "" : "";
                var removed = item.TryGetProperty("removed", out var r) && r.GetBoolean();
                var changeType = item.TryGetProperty("changeType", out var t) ? t.GetString() ?? "" : "";
                var time = item.TryGetProperty("time", out var tm) ? tm.GetString() ?? "" : "";
                var itemDriveId = item.TryGetProperty("driveId", out var di) ? di.GetString() : null;
                LinkedDriveFileMetadata? metadata = null;
                if (item.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Object)
                    metadata = ParseMetadata(file);
                changes.Add(new LinkedDriveChange(fileId, removed, changeType, time, itemDriveId, metadata));
            }
        }
        return new LinkedDriveChangePage(
            changes,
            root.TryGetProperty("nextPageToken", out var n) ? n.GetString() : null,
            root.TryGetProperty("newStartPageToken", out var s) ? s.GetString() : null);
    }

    /// <summary>
    /// Sheet structure is read separately from values. Numeric sheetId is the
    /// durable tab identity; titles are presentation only. Non-warning
    /// protected ranges are surfaced so the sync engine can refuse to touch
    /// them instead of letting one protected cell fail an entire atomic batch.
    /// </summary>
    public async Task<IReadOnlyList<LinkedSheetTab>> GetSheetStructureAsync(
        string spreadsheetId, CancellationToken ct = default)
    {
        var fields = Uri.EscapeDataString(
            "sheets(properties(sheetId,title,index,gridProperties(rowCount,columnCount))," +
            "protectedRanges(protectedRangeId,warningOnly,range(sheetId,startRowIndex,endRowIndex,startColumnIndex,endColumnIndex)))");
        using var resp = await SendAsync(HttpMethod.Get,
            $"{SheetsRoot}/{Uri.EscapeDataString(spreadsheetId)}?fields={fields}", null, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var result = new List<LinkedSheetTab>();
        if (!doc.RootElement.TryGetProperty("sheets", out var sheets)) return result;

        foreach (var sheet in sheets.EnumerateArray())
        {
            var p = sheet.GetProperty("properties");
            var grid = p.TryGetProperty("gridProperties", out var gp) ? gp : default;
            var protectedRanges = new List<LinkedProtectedRange>();
            if (sheet.TryGetProperty("protectedRanges", out var prs))
            {
                foreach (var pr in prs.EnumerateArray())
                {
                    if (!pr.TryGetProperty("range", out var range)) continue;
                    protectedRanges.Add(new LinkedProtectedRange(
                        pr.TryGetProperty("protectedRangeId", out var pid) ? pid.GetInt32() : 0,
                        pr.TryGetProperty("warningOnly", out var wo) && wo.GetBoolean(),
                        range.TryGetProperty("startRowIndex", out var sr) ? sr.GetInt32() : 0,
                        range.TryGetProperty("endRowIndex", out var er) ? er.GetInt32() : int.MaxValue,
                        range.TryGetProperty("startColumnIndex", out var sc) ? sc.GetInt32() : 0,
                        range.TryGetProperty("endColumnIndex", out var ec) ? ec.GetInt32() : int.MaxValue));
                }
            }
            result.Add(new LinkedSheetTab(
                p.GetProperty("sheetId").GetInt32(),
                p.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                p.TryGetProperty("index", out var index) ? index.GetInt32() : 0,
                grid.ValueKind == JsonValueKind.Object && grid.TryGetProperty("rowCount", out var rows) ? rows.GetInt32() : 0,
                grid.ValueKind == JsonValueKind.Object && grid.TryGetProperty("columnCount", out var cols) ? cols.GetInt32() : 0,
                protectedRanges));
        }
        return result.OrderBy(t => t.Index).ToList();
    }

    /// <summary>
    /// Read actual user-entered values by numeric sheetId. getByDataFilter is
    /// used instead of A1/tab-name ranges so a concurrent browser rename does
    /// not make QCM read the wrong tab. Formula cells remain formulas and are
    /// marked explicitly; callers must not replace them with calculated text.
    /// </summary>
    public async Task<LinkedSheetGrid> ReadGridAsync(
        string spreadsheetId, int sheetId, int rowCount, int columnCount,
        CancellationToken ct = default)
    {
        rowCount = Math.Clamp(rowCount, 1, 10000);
        columnCount = Math.Clamp(columnCount, 1, 64);
        var body = JsonSerializer.Serialize(new
        {
            includeGridData = true,
            dataFilters = new[]
            {
                new
                {
                    gridRange = new
                    {
                        sheetId,
                        startRowIndex = 0,
                        endRowIndex = rowCount,
                        startColumnIndex = 0,
                        endColumnIndex = columnCount,
                    },
                },
            },
        });
        var fields = Uri.EscapeDataString(
            "sheets(properties(sheetId,title),data(startRow,startColumn,rowData.values(" +
            "userEnteredValue,effectiveValue,formattedValue)))");
        using var resp = await SendAsync(HttpMethod.Post,
            $"{SheetsRoot}/{Uri.EscapeDataString(spreadsheetId)}:getByDataFilter?fields={fields}",
            () => new StringContent(body, Encoding.UTF8, "application/json"), ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        var rows = new List<IReadOnlyList<LinkedSheetCell>>();
        string title = "";
        if (doc.RootElement.TryGetProperty("sheets", out var sheets))
        {
            foreach (var sheet in sheets.EnumerateArray())
            {
                if (sheet.TryGetProperty("properties", out var props))
                {
                    if (props.TryGetProperty("sheetId", out var sid) && sid.GetInt32() != sheetId) continue;
                    title = props.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                }
                if (!sheet.TryGetProperty("data", out var data)) continue;
                foreach (var block in data.EnumerateArray())
                {
                    var startRow = block.TryGetProperty("startRow", out var sr) ? sr.GetInt32() : 0;
                    if (!block.TryGetProperty("rowData", out var rowData)) continue;
                    while (rows.Count < startRow) rows.Add(Array.Empty<LinkedSheetCell>());
                    foreach (var row in rowData.EnumerateArray())
                    {
                        var cells = new List<LinkedSheetCell>();
                        if (row.TryGetProperty("values", out var values))
                            foreach (var cell in values.EnumerateArray()) cells.Add(ParseCell(cell));
                        rows.Add(cells);
                    }
                }
            }
        }
        return new LinkedSheetGrid(sheetId, title, rows);
    }

    /// <summary>
    /// Set only the exact literal cells QCM changed. There is deliberately no
    /// tab add/delete/rename or broad clear operation in this client.
    /// stringValue stores a leading '=' literally, never as a Sheet formula.
    /// </summary>
    public async Task UpdateCellsAsync(
        string spreadsheetId, IReadOnlyList<LinkedSheetCellUpdate> updates,
        CancellationToken ct = default)
    {
        if (updates.Count == 0) return;
        var requests = updates.Select(u => new
        {
            updateCells = new
            {
                range = new
                {
                    sheetId = u.SheetId,
                    startRowIndex = u.RowIndex,
                    endRowIndex = u.RowIndex + 1,
                    startColumnIndex = u.ColumnIndex,
                    endColumnIndex = u.ColumnIndex + 1,
                },
                rows = new[]
                {
                    new
                    {
                        values = new[]
                        {
                            new { userEnteredValue = new { stringValue = u.Value ?? "" } },
                        },
                    },
                },
                fields = "userEnteredValue",
            },
        }).ToList();
        var body = JsonSerializer.Serialize(new { requests });
        using var resp = await SendAsync(HttpMethod.Post,
            $"{SheetsRoot}/{Uri.EscapeDataString(spreadsheetId)}:batchUpdate",
            () => new StringContent(body, Encoding.UTF8, "application/json"), ct);
    }

    /// <summary>Download a Picker-authorized CSV blob from Drive.</summary>
    public async Task<byte[]> DownloadCsvAsync(string fileId, CancellationToken ct = default)
    {
        using var resp = await SendAsync(HttpMethod.Get,
            $"{DriveFiles}/{Uri.EscapeDataString(fileId)}?alt=media&supportsAllDrives=true", null, ct);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>
    /// Replace only the content of the same Drive CSV file. Metadata, parents,
    /// sharing and file id stay intact.
    /// </summary>
    public async Task UpdateCsvAsync(string fileId, byte[] contents, CancellationToken ct = default)
    {
        var url = $"https://www.googleapis.com/upload/drive/v3/files/{Uri.EscapeDataString(fileId)}" +
                  "?uploadType=media&supportsAllDrives=true";
        using var resp = await SendAsync(HttpMethod.Patch, url, () =>
        {
            var content = new ByteArrayContent(contents);
            content.Headers.ContentType = new MediaTypeHeaderValue(CsvMimeType);
            return content;
        }, ct);
    }

    static LinkedDriveFileMetadata ParseMetadata(JsonElement file)
    {
        var caps = file.TryGetProperty("capabilities", out var c) ? c : default;
        bool Cap(string name) => caps.ValueKind == JsonValueKind.Object &&
                                 caps.TryGetProperty(name, out var v) && v.GetBoolean();
        return new LinkedDriveFileMetadata(
            file.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            file.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            file.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "" : "",
            file.TryGetProperty("version", out var v) ? v.GetString() ?? v.GetRawText().Trim('"') : "",
            file.TryGetProperty("modifiedTime", out var mt) ? mt.GetString() ?? "" : "",
            file.TryGetProperty("driveId", out var di) ? di.GetString() : null,
            file.TryGetProperty("trashed", out var tr) && tr.GetBoolean(),
            file.TryGetProperty("webViewLink", out var w) ? w.GetString() : null,
            new LinkedDriveCapabilities(Cap("canEdit"), Cap("canModifyContent"), Cap("canDownload")));
    }

    static LinkedSheetCell ParseCell(JsonElement cell)
    {
        string? user = null;
        bool formula = false;
        if (cell.TryGetProperty("userEnteredValue", out var entered))
        {
            if (entered.TryGetProperty("formulaValue", out var fv))
            { user = fv.GetString(); formula = true; }
            else if (entered.TryGetProperty("stringValue", out var sv)) user = sv.GetString();
            else if (entered.TryGetProperty("numberValue", out var nv)) user = nv.GetDouble().ToString("R", CultureInfo.InvariantCulture);
            else if (entered.TryGetProperty("boolValue", out var bv)) user = bv.GetBoolean() ? "TRUE" : "FALSE";
        }

        string? effective = null;
        if (cell.TryGetProperty("effectiveValue", out var ev))
        {
            if (ev.TryGetProperty("stringValue", out var sv)) effective = sv.GetString();
            else if (ev.TryGetProperty("numberValue", out var nv)) effective = nv.GetDouble().ToString("R", CultureInfo.InvariantCulture);
            else if (ev.TryGetProperty("boolValue", out var bv)) effective = bv.GetBoolean() ? "TRUE" : "FALSE";
            else if (ev.TryGetProperty("errorValue", out _)) effective = null;
        }
        var formatted = cell.TryGetProperty("formattedValue", out var f) ? f.GetString() : null;
        return new LinkedSheetCell(user, effective, formatted, formula);
    }

    async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, Func<HttpContent?>? contentFactory, CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _accessToken(ct));
            req.Content = contentFactory?.Invoke();

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, ct);
            }
            catch (HttpRequestException) when (attempt + 1 < maxAttempts)
            {
                await Task.Delay(Backoff(attempt, null), ct);
                continue;
            }

            if (resp.IsSuccessStatusCode) return resp;

            var body = await resp.Content.ReadAsStringAsync(ct);
            var status = resp.StatusCode;
            var retryAfter = resp.Headers.RetryAfter?.Delta;
            bool retry = attempt + 1 < maxAttempts && IsTransient(status, body);
            resp.Dispose();
            if (!retry) throw new LinkedDriveApiException(status, body);
            await Task.Delay(Backoff(attempt, retryAfter), ct);
        }
    }

    static bool IsTransient(HttpStatusCode status, string body)
    {
        if (status == HttpStatusCode.RequestTimeout || (int)status == 429 || (int)status >= 500) return true;
        if (status != HttpStatusCode.Forbidden) return false;
        // Drive sometimes reports quota throttling as 403 rather than 429.
        return body.Contains("rateLimitExceeded", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("userRateLimitExceeded", StringComparison.OrdinalIgnoreCase);
    }

    static TimeSpan Backoff(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } requested && requested > TimeSpan.Zero)
            return requested > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : requested;
        var baseMs = Math.Min(8000, 500 * (1 << attempt));
        return TimeSpan.FromMilliseconds(baseMs + Random.Shared.Next(0, 300));
    }
}

public sealed record LinkedGoogleAccount(string PermissionId, string EmailAddress, string DisplayName);

public sealed record LinkedDriveCapabilities(bool CanEdit, bool CanModifyContent, bool CanDownload)
{
    public bool CanWrite => CanEdit && CanModifyContent;
}

public sealed record LinkedDriveFileMetadata(
    string Id,
    string Name,
    string MimeType,
    string Version,
    string ModifiedTime,
    string? DriveId,
    bool Trashed,
    string? WebViewLink,
    LinkedDriveCapabilities Capabilities);

public sealed record LinkedDriveChange(
    string FileId,
    bool Removed,
    string ChangeType,
    string Time,
    string? DriveId,
    LinkedDriveFileMetadata? File);

public sealed record LinkedDriveChangePage(
    IReadOnlyList<LinkedDriveChange> Changes,
    string? NextPageToken,
    string? NewStartPageToken);

public sealed record LinkedProtectedRange(
    int Id,
    bool WarningOnly,
    int StartRowIndex,
    int EndRowIndex,
    int StartColumnIndex,
    int EndColumnIndex)
{
    public bool Contains(int row, int column) =>
        row >= StartRowIndex && row < EndRowIndex &&
        column >= StartColumnIndex && column < EndColumnIndex;
}

public sealed record LinkedSheetTab(
    int SheetId,
    string Title,
    int Index,
    int RowCount,
    int ColumnCount,
    IReadOnlyList<LinkedProtectedRange> ProtectedRanges)
{
    public bool IsProtected(int row, int column) =>
        ProtectedRanges.Any(r => !r.WarningOnly && r.Contains(row, column));
}

public sealed record LinkedSheetCell(
    string? UserValue,
    string? EffectiveValue,
    string? FormattedValue,
    bool IsFormula)
{
    public string TextForProfile => UserValue ?? EffectiveValue ?? FormattedValue ?? "";
}

public sealed record LinkedSheetGrid(
    int SheetId,
    string Title,
    IReadOnlyList<IReadOnlyList<LinkedSheetCell>> Rows);

public sealed record LinkedSheetCellUpdate(
    int SheetId,
    int RowIndex,
    int ColumnIndex,
    string? Value);

public sealed class LinkedDriveApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string ResponseBody { get; }

    public LinkedDriveApiException(HttpStatusCode statusCode, string responseBody)
        : base($"Google API returned {(int)statusCode}: {responseBody}")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
