namespace QuadStick.Format;

// QuadStick profile CSV → sheets and bindings.
// Row 1: sheet keyword + mode name. Row 2: filename (first sheet only).
// Row 3: output group label, "Function", channel. Row 4+: output, function, inputs (C–J).
// Stuff past column J is comments and stays untouched on save.
public static class Parser
{
    const int MaxInputColumns = 8; // columns C..J

    public static (ProfileDocument Doc, List<Issue> Issues) Parse(string csvText)
    {
        var grid = Csv.Parse(csvText);
        var doc = new ProfileDocument();
        var issues = new List<Issue>();

        // Device CSVs written by QMP start with a version header line:
        // "QuadStick Configuration,Version 1.5,<sheet id>,<name>". Preserve it.
        int scanFrom = 0;
        if (Cell(grid, 0, 0).TrimStart().StartsWith("QuadStick Configuration", StringComparison.OrdinalIgnoreCase))
        {
            doc.HasVersionHeader = true;
            doc.HeaderName = Cell(grid, 0, 3).Trim(); // the human name, e.g. "Grand Theft Auto"
            scanFrom = 1;
        }

        // Split the grid into sheet sections on A1 keyword rows.
        // QMP rule: A1 must CONTAIN "Profile", or equal Preferences/Infrared.
        var sectionStarts = new List<int>();
        for (int r = scanFrom; r < grid.Count; r++)
            if (Vocab.IsSheetKeyword(Cell(grid, r, 0).Trim()) && IsHeaderRow(grid, r))
                sectionStarts.Add(r);

        if (sectionStarts.Count == 0)
        {
            issues.Add(new Issue(Severity.Error, "A1",
                $"First cell must contain \"Profile\" or be \"Preferences\" or \"Infrared\". Found \"{Cell(grid, scanFrom, 0)}\".",
                "Set cell A1 to the sheet type keyword, e.g. \"Profile Name\"."));
            return (doc, issues);
        }
        if (sectionStarts[0] != scanFrom)
            issues.Add(new Issue(Severity.Warning, $"A{scanFrom + 1}",
                $"{sectionStarts[0] - scanFrom} row(s) before the first sheet keyword are not part of any sheet.",
                "Delete rows above the first sheet header."));

        for (int s = 0; s < sectionStarts.Count; s++)
        {
            int start = sectionStarts[s];
            int end = s + 1 < sectionStarts.Count ? sectionStarts[s + 1] : grid.Count;
            doc.Sheets.Add(ParseSheet(grid, start, end, isFirst: s == 0, issues));
        }
        return (doc, issues);
    }

    static bool IsHeaderRow(List<string[]> grid, int r)
    {
        // A keyword in column A only starts a sheet if it's not itself a
        // binding row: header rows never carry a function in column B.
        var b = Cell(grid, r, 1).Trim();
        return b.Length == 0;
    }

    static ModeSheet ParseSheet(List<string[]> grid, int start, int end, bool isFirst, List<Issue> issues)
    {
        string A(int offset, int col) => Cell(grid, start + offset, col).Trim();

        var sheet = new ModeSheet
        {
            Type = Vocab.KeywordToType(A(0, 0)),
            ModeName = A(0, 2),
            CsvFileName = isFirst ? A(1, 0) : null,
            HeaderLabel = A(2, 0),
            Channel = A(2, 2),
            StartRow = start + 1,
        };

        bool terminated = false;
        for (int r = start + 3; r < end; r++)
        {
            // Only columns A..J matter; columns after J are comments.
            bool hasContent = false;
            for (int c = 0; c < 2 + MaxInputColumns && !hasContent; c++)
                hasContent = Cell(grid, r, c).Trim().Length > 0;

            if (!hasContent) { terminated = true; continue; }

            var output = Cell(grid, r, 0).Trim();
            if (terminated)
            {
                // Blank lines separate sheets on the device, so a row after a
                // blank could be read as the start of a new sheet and scramble
                // the modes. Both official converters drop such rows entirely.
                issues.Add(new Issue(Severity.Error, $"A{r + 1}",
                    $"Row {r + 1} appears after a blank row. The device could read it as the start of a new sheet and scramble the modes.",
                    "Move it above the first blank row or delete it."));
                continue;
            }

            var inputs = new List<string>();
            var inputCols = new List<int>();
            for (int c = 2; c < 2 + MaxInputColumns; c++)
            {
                var v = Cell(grid, r, c).Trim();
                if (v.Length > 0) { inputs.Add(v); inputCols.Add(c); }
            }
            sheet.Bindings.Add(new Binding(r + 1, output, Cell(grid, r, 1).Trim(), inputs, inputCols));
        }
        return sheet;
    }

    static string Cell(List<string[]> grid, int r, int c) =>
        r < grid.Count && c < grid[r].Length ? grid[r][c] : "";
}
