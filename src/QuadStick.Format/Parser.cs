namespace QuadStick.Format;

// QuadStick profile CSV → sheets and bindings.
// Row 1: sheet keyword + mode name. Row 2: filename (first sheet only).
// Row 3: output group label, "Function", channel. Row 4+: output, function, inputs (C–J).
// Stuff past column J is comments and stays untouched on save.
public static class Parser
{
    const int MaxInputColumns = 8; // columns C..J

    /// <summary>Columns A..J, the only ones the device reads keywords out of.
    /// Everything from K on is comments.</summary>
    public const int KeywordColumns = 2 + MaxInputColumns;

    // Column L, one past the notes column. The profile's own name for a row's
    // output lives here; the device and both official converters stop at J.
    public const int ActionColumn = 11;

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
            doc.HeaderVersion = Cell(grid, 0, 1).Trim(); // e.g. "Version 1.5"
            doc.HeaderSource = Cell(grid, 0, 2).Trim();  // the source sheet URL or id
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

            // The device dispatches sheets by the START of the raw A1 line,
            // case sensitively (Configuration.c). "GTA Profile" or "profile"
            // passes the converters but the device skips the whole sheet.
            var rawA1 = Cell(grid, start, 0);
            if (!Vocab.FirmwareAcceptsSheetKeyword(rawA1))
                issues.Add(new Issue(Severity.Error, $"A{start + 1}",
                    $"\"{rawA1}\" does not START with \"Profile\", \"Preferences\" or \"Infrared\" (capitalized exactly), so the device skips this whole sheet.",
                    "Begin the cell with the sheet keyword, e.g. \"Profile Name\"."));
        }

        CheckDeviceLineLimits(grid, issues);
        return (doc, issues);
    }

    // Two hard limits in the device's reader, both read off the firmware
    // (Configuration.c and fatfs f_gets, FW_VERSION 1476):
    //
    // 1. `char line_buffer[1024]` read by `f_gets(line_buffer, sizeof(...))`,
    //    which stores at most len-1 = 1023 characters and stops at '\n'. A
    //    longer line comes back in two pieces, and the second piece is read as
    //    if it were the next row. Usually that tail is comment text, no output
    //    keyword matches, and the binding loop skips it. But the split lands
    //    wherever byte 1023 falls: if the remainder begins with '\n' or '\r',
    //    the loop's own `line_buffer[0] != '\n'` test ends the mode there and
    //    every binding below it is silently dropped. That is the one limit here
    //    whose damage is not confined to its own row, so it stays an error.
    //
    // 2. `#define MAX_KEYWORD_LENGTH 64`, used by next_word as
    //    `for (i=0;i<MAX_KEYWORD_LENGTH;i++)`. It looks at answer[0..63] only,
    //    so a field of exactly 64 characters has its comma at index 64, is
    //    never terminated, and next_word returns NULL. 63 is the real cap. On
    //    NULL it also leaves *index alone, so every later field on that row
    //    returns NULL too: the row loses its inputs, and nothing else does.
    //    Row-local, so a warning.
    const int MaxKeywordLength = 64; // fields must be shorter than this
    const int MaxLineBytes = 1023;   // f_gets keeps len-1 characters

    static void CheckDeviceLineLimits(List<string[]> grid, List<Issue> issues)
    {
        var sheet = SheetType.ProfileName;
        int sheetStart = 0;
        for (int r = 0; r < grid.Count; r++)
        {
            if (Vocab.IsSheetKeyword(Cell(grid, r, 0).Trim()) && IsHeaderRow(grid, r))
            {
                sheet = Vocab.KeywordToType(Cell(grid, r, 0).Trim());
                sheetStart = r;
            }

            // The device has no idea that a quoted cell can hold more than one
            // line: f_gets hands the binding loop each line separately, so the
            // extra lines arrive as rows of their own, and a blank one among
            // them ends the mode and drops every row below it. Saving joins
            // them back into one line, which is worth saying out loud because
            // it changes the user's own text.
            if (grid[r].Any(c => c.AsSpan().IndexOfAny('\n', '\r') >= 0))
                issues.Add(new Issue(Severity.Warning, $"A{r + 1}",
                    $"Row {r + 1} has a cell holding more than one line. The device reads each line as a separate row, and a blank line among them stops it reading the rest of this mode, so saving joins them into one line.",
                    "Keep a note on a single line."));

            var line = Csv.Write(new[] { grid[r] });
            if (System.Text.Encoding.UTF8.GetByteCount(line) > MaxLineBytes)
                issues.Add(new Issue(Severity.Error, $"A{r + 1}",
                    $"Row {r + 1} is longer than {MaxLineBytes} characters including comments. The device reads a row into a 1024-byte buffer and hands back the overflow as if it were the next row. Depending on where the row happens to break, that can end the mode early and drop every row below it.",
                    "Shorten the row's comments."));
            // A preferences row is name,value: the device stops after column B,
            // so the long descriptions the official prefs.csv keeps in C and
            // beyond are as safe as a profile's comment columns.
            int keywordCols = sheet == SheetType.Preferences ? 2 : KeywordColumns;
            for (int c = 0; c < keywordCols && c < grid[r].Length; c++)
            {
                var value = grid[r][c];
                if (value.Length >= MaxKeywordLength && !(r == 0 && grid[r].Length > 0 && grid[r][0].StartsWith("QuadStick", StringComparison.Ordinal)))
                    issues.Add(new Issue(Severity.Warning, $"{(char)('A' + c)}{r + 1}",
                        $"This cell is {value.Length} characters. The device stops looking for the end of a cell after 64, so it reads this cell and everything after it on this row as empty.",
                        "Shorten it to 63 characters or fewer."));

                // Only the rows the device actually runs next_word over: the
                // label row it takes the channel from, and the rows below it.
                // It never looks past the first word of a keyword row, and it
                // skips the filename row whole. An Infrared sheet is read by
                // next_hex_code, which has its own separator rules, and the
                // validator leaves those sheets alone anyway.
                if (sheet != SheetType.Infrared && r >= sheetStart + 2 && SplitPoint(value) is int at)
                    issues.Add(new Issue(Severity.Warning, $"{(char)('A' + c)}{r + 1}",
                        $"\"{value}\" contains \"{value[at]}\". The device ends a cell at that character, so it reads it as two cells (\"{value[..at]}\" and \"{value[(at + 1)..]}\") and everything after it on this row moves along one column.",
                        "Keep letters, numbers, spaces, and \"_ . -\" only, or move the text to the notes column."));
            }
        }
    }

    // next_word ends a field at the first character that is not alphanumeric,
    // '_', '.', ' ' or '-'. Returns where the device would cut the cell, or
    // null when it reads the cell whole.
    static int? SplitPoint(string value)
    {
        for (int i = 0; i < value.Length && i < MaxKeywordLength; i++)
        {
            var c = value[i];
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ' ' or '-') continue;
            return i;
        }
        return null;
    }

    static bool IsHeaderRow(List<string[]> grid, int r)
    {
        // The device splits sections on the START of the raw line and never
        // looks at column B (Configuration.c), so a row the firmware accepts
        // as a keyword opens a sheet whatever else sits beside it. Community
        // IR tabs write the set name right next to the word, as in
        // "Infrared,Samsung Most Models - Set #: 595"; reading those rows as
        // part of the sheet above put IR hex codes where preference values go.
        if (Vocab.FirmwareAcceptsSheetKeyword(Cell(grid, r, 0))) return true;

        // Otherwise the keyword only matched loosely, e.g. "GTA Profile". Those
        // are still treated as sheets so the "the device skips this" error below
        // can name them, but a binding row that happens to contain the word must
        // not split the file, so it has to have an empty function cell.
        return Cell(grid, r, 1).Trim().Length == 0;
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

            // The device ends a mode only at a line whose first byte is '\n' or
            // '\r' (the binding loop's own test), which means a line with
            // nothing on it at all. A row of commas, or a note parked in the
            // comments columns, is a row it reads and skips like any other row
            // with no output name. Reading those as the end of the mode hid
            // every binding below them from the editor.
            if (!hasContent) { terminated |= IsBlankLine(grid[r]); continue; }

            var output = Cell(grid, r, 0).Trim();
            if (terminated)
            {
                // A blank line ends the mode on the device; rows after it are
                // ignored (or, if one starts with a sheet keyword, read as a
                // phantom sheet). Both official converters drop such rows.
                issues.Add(new Issue(Severity.Warning, $"A{r + 1}",
                    $"Row {r + 1} appears after a blank row, where the device stops reading this mode, so this row does nothing.",
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
            sheet.Bindings.Add(new Binding(r + 1, output, Cell(grid, r, 1).Trim(), inputs, inputCols,
                Cell(grid, r, ActionColumn).Trim()));
        }
        return sheet;
    }

    // A line the device sees as empty: one that writes back as nothing at all,
    // so f_gets hands the binding loop a buffer starting with '\n' or '\r'.
    //
    // The cell is trimmed first because that is what ToCsvText does to it on
    // the way out. A single cell holding spaces, or a quoted cell holding only
    // a newline, looks like a row in every spreadsheet and used to read as one
    // here too, then went to the device as an empty line and ended the mode.
    // A row of two empty cells is different: it still writes a comma.
    static bool IsBlankLine(string[] row) =>
        row.Length == 0 || (row.Length == 1 && row[0].Trim().Length == 0);

    static string Cell(List<string[]> grid, int r, int c) =>
        r < grid.Count && c < grid[r].Length ? grid[r][c] : "";
}
