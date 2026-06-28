using System.Text.RegularExpressions;

namespace QuadStick.Format;

// Google Sheets share/edit links → CSV export URL.
public static partial class SheetsUrl
{
    public static bool TryGetCsvExportUrl(string pasted, out string exportUrl)
    {
        exportUrl = "";
        if (string.IsNullOrWhiteSpace(pasted)) return false;
        pasted = pasted.Trim();

        var gid = GidPattern().Match(pasted) is { Success: true } g ? g.Groups[1].Value : null;

        var pub = PublishedPattern().Match(pasted);
        if (pub.Success)
        {
            exportUrl = $"https://docs.google.com/spreadsheets/d/e/{pub.Groups[1].Value}/pub?output=csv"
                        + (gid is null ? "" : $"&gid={gid}");
            return true;
        }

        var id = IdPattern().Match(pasted) is { Success: true } m ? m.Groups[1].Value
               : KeyPattern().Match(pasted) is { Success: true } k ? k.Groups[1].Value
               : null;
        if (id is null) return false;

        exportUrl = $"https://docs.google.com/spreadsheets/d/{id}/export?format=csv"
                    + (gid is null ? "" : $"&gid={gid}");
        return true;
    }

    [GeneratedRegex(@"/spreadsheets/d/e/([A-Za-z0-9_-]{20,})")] private static partial Regex PublishedPattern();
    [GeneratedRegex(@"/spreadsheets/d/([A-Za-z0-9_-]{20,})")] private static partial Regex IdPattern();
    [GeneratedRegex(@"[?&]key=([A-Za-z0-9_-]{20,})")] private static partial Regex KeyPattern();
    [GeneratedRegex(@"[#?&]gid=(\d+)")] private static partial Regex GidPattern();
}
