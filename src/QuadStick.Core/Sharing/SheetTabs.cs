namespace QuadStick.Format;

/// <summary>One worksheet tab: the title it is given and the rows on it.
/// <paramref name="HeaderRow"/> is the 0-based row that names the columns
/// (Outputs, Function, and the input columns), or 0 on a tab that has no such
/// row. Everything down to and including it is heading, which is what a writer
/// needs to know to freeze and colour it.</summary>
public sealed record ProfileTab(string Title, List<string[]> Rows, int HeaderRow = 0);

/// <summary>A profile grid split the way the community writes a workbook: one
/// tab per sheet, titled with the mode name.
///
/// The device never sees a tab name, and the CSV has nowhere to put one, so
/// this is presentation and nothing else. It exists because the backup used to
/// push the whole file flat onto one worksheet, which is a shape neither the
/// community sheets nor this app's own importer expect.</summary>
public static class SheetTabs
{
    // Google's own cap on a worksheet title.
    const int MaxTitle = 100;

    public static List<ProfileTab> Split(ProfileFile file)
    {
        var grid = file.Grid;
        var sheets = file.Document.Sheets;

        // Nothing the parser recognised. Push it whole rather than decide it is
        // not worth keeping: the sheet is the only copy that is not on this
        // machine.
        if (sheets.Count == 0)
            return new List<ProfileTab> { new("Profile", Rows(grid, 0, grid.Count)) };

        var tabs = new List<ProfileTab>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < sheets.Count; i++)
        {
            // The first tab starts at row 1, not at its keyword row, so the
            // version header above it (the sheet id and the profile's name)
            // travels with the profile instead of being dropped.
            int start = i == 0 ? 0 : sheets[i].StartRow - 1;
            int end = i + 1 < sheets.Count ? sheets[i + 1].StartRow - 1 : grid.Count;
            var rows = Rows(grid, start, end);
            tabs.Add(new ProfileTab(Unique(Title(sheets[i], i), taken), rows, HeaderRow(rows)));
        }
        return tabs;
    }

    // The row that names the columns. A preferences or infrared tab has none,
    // and its keyword row is the only heading it has.
    static int HeaderRow(List<string[]> rows)
    {
        for (int r = 0; r < rows.Count; r++)
            if (rows[r].Length > 1 && rows[r][1].Trim().Equals("Function", StringComparison.OrdinalIgnoreCase))
                return r;
        return 0;
    }

    // The blank row between two sheets belongs to neither of them: the device
    // needs it to end a mode, and the importer puts one back between tabs.
    static List<string[]> Rows(List<string[]> grid, int start, int end)
    {
        var rows = grid.GetRange(start, end - start);
        while (rows.Count > 0 && rows[^1].All(c => c.Trim().Length == 0)) rows.RemoveAt(rows.Count - 1);
        return rows;
    }

    static string Title(ModeSheet sheet, int index) => sheet.Type switch
    {
        SheetType.Preferences => "Preferences",
        SheetType.Infrared => "Infrared",
        _ => sheet.ModeName.Trim().Length > 0 ? Safe(sheet.ModeName.Trim()) : $"Mode {index + 1}",
    };

    // Sheets rejects these in a title and cuts one past 100 characters.
    static string Safe(string name)
    {
        var cleaned = new string(name.Where(c => !char.IsControl(c) && !"[]:*?/\\".Contains(c)).ToArray()).Trim();
        if (cleaned.Length == 0) return "Mode";
        return cleaned.Length <= MaxTitle ? cleaned : cleaned[..MaxTitle].TrimEnd();
    }

    // Two modes may share a name (the device tells them apart by position), and
    // two tabs in one spreadsheet may not.
    static string Unique(string title, HashSet<string> taken)
    {
        if (taken.Add(title)) return title;
        for (int n = 2; ; n++)
        {
            var candidate = $"{title} ({n})";
            if (candidate.Length > MaxTitle) candidate = $"{title[..(MaxTitle - 5)].TrimEnd()} ({n})";
            if (taken.Add(candidate)) return candidate;
        }
    }
}
