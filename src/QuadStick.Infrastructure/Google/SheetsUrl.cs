using System.Text.RegularExpressions;

namespace QuadStick.Infrastructure.Google;

/// <summary>Pure parsing of Google Sheets provider URL shapes. Deterministic is
/// not the same as domain-generic: this belongs beside the Google adapter.</summary>
public static partial class SheetsUrl
{
    public static bool TryGetXlsxExportUrl(string pasted, out string exportUrl) =>
        TryGetExportUrl(pasted, "xlsx", wholeWorkbook: true, out exportUrl);

    public static bool TryGetCsvExportUrl(string pasted, out string exportUrl) =>
        TryGetExportUrl(pasted, "csv", wholeWorkbook: false, out exportUrl);

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

    static string? IdFromUrl(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Host, "docs.google.com", StringComparison.OrdinalIgnoreCase)) return null;
        return IdPattern().Match(source) is { Success: true } m ? m.Groups[1].Value : null;
    }

    static string? IdFromBareId(string source)
    {
        var m = IdPattern().Match("/spreadsheets/d/" + source);
        return m.Success && m.Groups[1].Value.Length == source.Length ? m.Groups[1].Value : null;
    }

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