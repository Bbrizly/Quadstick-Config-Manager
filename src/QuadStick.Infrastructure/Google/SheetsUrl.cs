using System.Text.RegularExpressions;

namespace QuadStick.Format;

// Google Sheets share/edit links → export URL.
public static partial class SheetsUrl
{
    /// <summary>The whole workbook, every tab. Import wants this one: profiles
    /// are usually one mode per tab. The gid in the pasted link is dropped,
    /// since it names a single tab.</summary>
    public static bool TryGetXlsxExportUrl(string pasted, out string exportUrl) =>
        TryGetExportUrl(pasted, "xlsx", wholeWorkbook: true, out exportUrl);

    /// <summary>One tab as CSV: the linked one, or the first. The fallback for
    /// published links, where the workbook export is not available.</summary>
    public static bool TryGetCsvExportUrl(string pasted, out string exportUrl) =>
        TryGetExportUrl(pasted, "csv", wholeWorkbook: false, out exportUrl);

    /// <summary>The Sheet a device-written header points at, as a canonical edit
    /// link. Version 1.4 headers carry the full URL (the add-on's format);
    /// Version 1.5 headers carry the bare id (QMP's format). Never throws:
    /// a missing or malformed version/source just fails the parse.</summary>
    public static bool TryGetEditUrlFromHeader(string version, string source, out string url)
    {
        url = "";
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(source)) return false;

        string? id = version.Trim() switch
        {
            "Version 1.4" => IdFromUrl(source.Trim()),
            "Version 1.5" => IdFromBareId(source.Trim()),
            _ => null,
        };
        if (id is null) return false;

        url = $"https://docs.google.com/spreadsheets/d/{id}/edit";
        return true;
    }

    // Version 1.4: the source is a pasted Sheets URL. Reuse IdPattern to pull
    // the id out of it, same as the export-url path, but also require the
    // Google host so a lookalike link on another domain is rejected.
    static string? IdFromUrl(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Host, "docs.google.com", StringComparison.OrdinalIgnoreCase)) return null;
        return IdPattern().Match(source) is { Success: true } m ? m.Groups[1].Value : null;
    }

    // Version 1.5: the source IS the id, nothing to extract it from. Reuse the
    // same id character class by matching it in the shape IdPattern expects,
    // then require the match to consume the whole string so trailing garbage
    // after a valid-looking prefix is rejected rather than silently dropped.
    static string? IdFromBareId(string source)
    {
        var m = IdPattern().Match("/spreadsheets/d/" + source);
        return m.Success && m.Groups[1].Value.Length == source.Length ? m.Groups[1].Value : null;
    }

    /// <summary>The spreadsheet id in a pasted link, so a caller can ask
    /// whether it already knows that sheet. A published link carries a
    /// different kind of id and is not one.</summary>
    public static bool TryGetId(string pasted, out string id)
    {
        id = "";
        if (string.IsNullOrWhiteSpace(pasted)) return false;
        pasted = pasted.Trim();
        if (PublishedPattern().IsMatch(pasted)) return false;
        var m = IdPattern().Match(pasted) is { Success: true } x ? x : KeyPattern().Match(pasted);
        if (!m.Success) return false;
        id = m.Groups[1].Value;
        return true;
    }

    static bool TryGetExportUrl(string pasted, string format, bool wholeWorkbook, out string exportUrl)
    {
        exportUrl = "";
        if (string.IsNullOrWhiteSpace(pasted)) return false;
        pasted = pasted.Trim();

        var gid = wholeWorkbook ? null
                : GidPattern().Match(pasted) is { Success: true } g ? g.Groups[1].Value : null;

        var pub = PublishedPattern().Match(pasted);
        if (pub.Success)
        {
            exportUrl = $"https://docs.google.com/spreadsheets/d/e/{pub.Groups[1].Value}/pub?output={format}"
                        + (gid is null ? "" : $"&gid={gid}");
            return true;
        }

        var id = IdPattern().Match(pasted) is { Success: true } m ? m.Groups[1].Value
               : KeyPattern().Match(pasted) is { Success: true } k ? k.Groups[1].Value
               : null;
        if (id is null) return false;

        exportUrl = $"https://docs.google.com/spreadsheets/d/{id}/export?format={format}"
                    + (gid is null ? "" : $"&gid={gid}");
        return true;
    }

    [GeneratedRegex(@"/spreadsheets/d/e/([A-Za-z0-9_-]{20,})")] private static partial Regex PublishedPattern();
    [GeneratedRegex(@"/spreadsheets/d/([A-Za-z0-9_-]{20,})")] private static partial Regex IdPattern();
    [GeneratedRegex(@"[?&]key=([A-Za-z0-9_-]{20,})")] private static partial Regex KeyPattern();
    [GeneratedRegex(@"[#?&]gid=(\d+)")] private static partial Regex GidPattern();
}
