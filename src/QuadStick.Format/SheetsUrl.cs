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
