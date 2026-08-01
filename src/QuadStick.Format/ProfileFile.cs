namespace QuadStick.Format;

// Edits the raw CSV grid in place; re-parses after each change.
// Comments past column J and other oddities survive save.
public sealed class ProfileFile
{
    public List<string[]> Grid { get; private set; }
    public ProfileDocument Document { get; private set; } = new();
    public List<Issue> Issues { get; private set; } = new();

    ProfileFile(List<string[]> grid) { Grid = grid; Reparse(); }

    public static ProfileFile Load(string csvText) => new(Csv.Parse(csvText));

    public static ProfileFile NewFromTemplate(string csvFileName)
    {
        using var s = typeof(ProfileFile).Assembly.GetManifestResourceStream("DefaultTemplate")
            ?? throw new InvalidOperationException("Embedded default template missing.");
        using var r = new StreamReader(s);
        var file = Load(r.ReadToEnd());
        file.SetCell(file.Document.FileNameCellRow, 0, csvFileName);
        file.ClearUndo();
        file.Dirty = false;
        return file;
    }

    /// <summary>The file as it goes to disk and to the device. Two things are
    /// straightened out on the way, both because the device has no CSV parser
    /// at all: it reads a line at a time and scans it for separators.
    ///
    /// 1. Columns A..J are trimmed. search_for_keyword skips LEADING spaces
    ///    only and then compares the whole word, so an output like "x " throws
    ///    the row away and an input like "lip " is dropped from the binding.
    ///    The app trims every cell when it parses, so it showed a working
    ///    binding either way.
    ///
    /// 2. Newlines inside a cell become spaces. A quoted cell holding several
    ///    lines is one row to every spreadsheet and several lines to f_gets,
    ///    and if one of them is blank the binding loop treats it as the end of
    ///    the mode and drops every row below. One published community profile
    ///    loses thirty bindings to a paragraph break in a comment.</summary>
    public string ToCsvText() => Csv.Write(Grid.Select(DeviceSafe));

    // The grid exactly as the user has it. The parser and the validator read
    // this, so they still see what ToCsvText straightens out and can say so.
    string RawCsvText() => Csv.Write(Grid);

    static readonly char[] NewLines = { '\n', '\r' };

    static string[] DeviceSafe(string[] row)
    {
        string[]? fixedUp = null;
        for (int c = 0; c < row.Length; c++)
        {
            var v = row[c];
            if (c < Parser.KeywordColumns) v = v.Trim();
            if (v.AsSpan().IndexOfAny(NewLines) >= 0)
                v = string.Join(" ", v.Split(NewLines, StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Trim())).Trim();
            if (v == row[c]) continue;
            fixedUp ??= (string[])row.Clone();
            fixedUp[c] = v;
        }
        return fixedUp ?? row;
    }

    // Temp file then rename, so a crash mid-write can't leave a half-written
    // profile. Same pattern Device uses.
    public static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".qscm-tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    readonly List<List<string[]>> _undo = new();
    const int MaxUndo = 200;

    // Bumped on every mutation so callers (e.g. autosave) can cheaply tell
    // whether anything changed without diffing the whole grid.
    public int Revision { get; private set; }

    void Snapshot()
    {
        Dirty = true;
        Revision++;
        _undo.Add(Grid.Select(r => (string[])r.Clone()).ToList());
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
    }

    public bool CanUndo => _undo.Count > 0;

    public void ClearUndo() => _undo.Clear();

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        Grid = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Dirty = true; // undo AFTER a save diverges memory from disk again
        Revision++;   // content changed; autosave should redraft
        Reparse();
        return true;
    }

    public void Reparse()
    {
        var (doc, parseIssues) = Parser.Parse(RawCsvText());
        Document = doc;
        Issues = parseIssues.Concat(Validator.Validate(doc)).ToList();
    }

    // Shape the grid the way the device reads it. Two rules, both from the
    // firmware (S6, Configuration.c):
    //
    // 1. QMP puts a version header on every file it writes, and the device
    //    rejects a file whose first line does not start with "QuadStick".
    //    It compares those nine bytes case sensitively, so a hand-typed
    //    "quadstick configuration" is not a header to the device at all: it
    //    ignores the whole file and boots its built-in configuration. The
    //    parser is deliberately case insensitive so such a file still opens
    //    and still shows its modes, which means the casing has to be put
    //    back here, on the way out.
    // 2. A segment ends ONLY at a line whose first byte is \n or \r
    //    (`line_buffer[0] != '\n' && line_buffer[0] != '\r'`, the binding loop
    //    and the preferences loop both). A row of commas is not a blank line
    //    there, and a sheet keyword row is not a terminator either: its output
    //    cell simply matches nothing and the row is skipped. So without an
    //    EMPTY row before it, a sheet's bindings are read as part of the sheet
    //    above, up to the 128-row cap, and the sheet itself never loads.
    //
    // Both edits shift row numbers, so callers rebind their views afterwards.
    public void NormalizeForDeviceCsv()
    {
        var wrongCase = Document.HasVersionHeader && !HeaderCasedForDevice();
        if (Document.HasVersionHeader && !wrongCase && SheetsMissingSeparator().Count == 0) return;
        Snapshot();
        if (!Document.HasVersionHeader)
        {
            var name = Path.GetFileNameWithoutExtension(Document.CsvFileName ?? "config");
            Grid.Insert(0, new[] { "QuadStick Configuration", "Version 1.5", "", name });
            Reparse();
        }
        else if (wrongCase)
        {
            var rest = Grid[0][0].TrimStart()[HeaderKeyword.Length..];
            Grid[0] = (string[])Grid[0].Clone();
            Grid[0][0] = HeaderKeyword + rest;
        }
        // Bottom up, so inserting a row cannot move the rows still to fix.
        foreach (var row in SheetsMissingSeparator().OrderByDescending(r => r))
        {
            // An all-blank row is already the separator, just written with
            // commas; emptying it keeps the user's row count. Anything else
            // means there is no separator at all.
            if (Grid[row - 2].All(c => c.Trim().Length == 0)) Grid[row - 2] = Array.Empty<string>();
            else Grid.Insert(row - 1, Array.Empty<string>());
        }
        Reparse();
    }

    const string HeaderKeyword = "QuadStick Configuration";

    // The header as the device needs it: the line has to BEGIN with the
    // keyword, spelled this way. ToCsvText trims the cell, so leading spaces
    // are already gone by the time the file is written.
    bool HeaderCasedForDevice() =>
        Grid.Count > 0 && Grid[0].Length > 0
        && Grid[0][0].TrimStart().StartsWith(HeaderKeyword, StringComparison.Ordinal);

    // 1-based keyword rows of every sheet after the first whose preceding row
    // is not an empty line to the firmware.
    List<int> SheetsMissingSeparator() =>
        Document.Sheets.Skip(1)
            .Select(s => s.StartRow)
            .Where(row => row >= 2 && Grid[row - 2].Length > 0)
            .ToList();

    public string GetCell(int row, int col) =>
        row >= 1 && row <= Grid.Count && col < Grid[row - 1].Length ? Grid[row - 1][col].Trim() : "";

    public void SetCell(int row, int col, string value)
    {
        Snapshot();
        Widen(row, col)[col] = value;
        Reparse();
    }

    // Grow a row so `col` exists, and hand it back. Callers that write more
    // than one cell in one undo step take their own Snapshot first.
    string[] Widen(int row, int col)
    {
        while (Grid.Count < row) Grid.Add(Array.Empty<string>());
        var r = Grid[row - 1];
        if (r.Length > col) return r;
        var wider = new string[col + 1];
        r.CopyTo(wider, 0);
        for (int i = r.Length; i < wider.Length; i++) wider[i] = "";
        return Grid[row - 1] = wider;
    }

    // The profile's own name for a row's output, in column L. Names are per
    // row, so "Left click" can be Shoot in one mode and Select in another.
    public const int ActionColumn = Parser.ActionColumn;

    // Longest action name, matching the mode name box's cap.
    public const int MaxActionName = 40;

    // The sheet a grid row belongs to: the last one whose keyword row is at or
    // above it. Null for rows above the first sheet, like the version header.
    ModeSheet? SheetAt(int row) => Document.Sheets.LastOrDefault(s => s.StartRow <= row);

    // A name that reads as a real output would appear twice in the picker
    // meaning two different things. Matched the way the picker shows a token,
    // not the way the file spells it: the list says "Triangle" for `triangle`
    // and "Mouse left" for `mouse_left`, so case and the space-for-underscore
    // swap both have to count as the same word.
    public static bool IsLegalActionName(string name)
    {
        var t = name.Trim();
        return t.Length is > 0 and <= MaxActionName
            && !Vocab.KnownOutputsLoose.Contains(t.Replace(' ', '_'));
    }

    // Two rows spelling one name differently still mean one name to whoever
    // reads them, so every lookup over names ignores case.
    static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    static bool SameName(string a, string b) => NameComparer.Equals(a, b);

    /// <summary>Set a binding row's output and its action name together, as one
    /// undoable change. A blank name clears the row's name, which is what
    /// picking a plain token does: the old name described an output the row no
    /// longer has.</summary>
    public bool SetOutput(int row, string token, string actionName = "")
    {
        var name = actionName.Trim();
        if (name.Length > 0 && !IsLegalActionName(name)) return false;
        if (name.Length > 0 && SheetAt(row)?.Type != SheetType.ProfileName) return false;
        // One name, one output. Two rows calling different tokens "Shoot"
        // would leave the picker's "Shoot" meaning whichever came first.
        if (name.Length > 0 && NameableBindings()
                .Any(b => b.Row != row && SameName(b.ActionName, name) && b.Output != token)) return false;
        if (GetCell(row, 0) == token && GetCell(row, ActionColumn) == name) return false;

        Snapshot();
        var r = Widen(row, ActionColumn);
        r[0] = token;
        r[ActionColumn] = name;
        if (name.Length > 0) LabelActionColumn(row);
        Reparse();
        return true;
    }

    // Title the column so a shared Google Sheet reads properly. Goes on the
    // sheet's label row, beside "Function", never on a binding row.
    void LabelActionColumn(int row)
    {
        var sheet = SheetAt(row);
        if (sheet is null || sheet.Type != SheetType.ProfileName) return;
        int labelRow = sheet.StartRow + 2;
        if (labelRow >= row || GetCell(labelRow, ActionColumn).Length > 0) return;
        Widen(labelRow, ActionColumn)[ActionColumn] = "Action";
    }

    // Only modes hold action names. A Preferences row's column L is somebody's
    // spreadsheet note, not a name for an output.
    IEnumerable<Binding> NameableBindings() => Document.Sheets
        .Where(s => s.Type == SheetType.ProfileName).SelectMany(s => s.Bindings);

    /// <summary>Every action name in the profile, in the order rows appear.</summary>
    public IReadOnlyList<string> ActionNames() => NameableBindings()
        .Select(b => b.ActionName).Where(n => n.Length > 0)
        .Distinct(NameComparer).ToList();

    /// <summary>The output token each action name stands for. First row wins
    /// when the same name is used for two different tokens.</summary>
    public IReadOnlyDictionary<string, string> ActionTokens()
    {
        var map = new Dictionary<string, string>(NameComparer);
        foreach (var b in NameableBindings())
            if (b.ActionName.Length > 0 && b.Output.Length > 0) map.TryAdd(b.ActionName, b.Output);
        return map;
    }

    /// <summary>Point every row carrying this name at a different output, in
    /// one undo step. One name means one output, so changing the output in the
    /// names table has to move every row that carries the name.</summary>
    public bool RetargetAction(string name, string token)
    {
        if (name.Length == 0 || token.Length == 0) return false;
        var rows = NameableBindings().Where(b => SameName(b.ActionName, name) && b.Output != token)
            .Select(b => b.Row).ToList();
        if (rows.Count == 0) return false;

        Snapshot();
        foreach (var r in rows) Widen(r, 0)[0] = token;
        Reparse();
        return true;
    }

    /// <summary>Take a name off every row carrying it, in one undo step. Those
    /// rows keep their output and go back to showing its real token.</summary>
    public bool ClearAction(string name)
    {
        if (name.Length == 0) return false;
        var rows = NameableBindings().Where(b => SameName(b.ActionName, name)).Select(b => b.Row).ToList();
        if (rows.Count == 0) return false;

        Snapshot();
        foreach (var r in rows) Widen(r, ActionColumn)[ActionColumn] = "";
        Reparse();
        return true;
    }

    /// <summary>Rename an action everywhere it appears, in one undo step.</summary>
    public bool RenameAction(string oldName, string newName)
    {
        var to = newName.Trim();
        if (oldName.Length == 0 || to == oldName || !IsLegalActionName(to)) return false;
        var rows = NameableBindings().Where(b => SameName(b.ActionName, oldName)).Select(b => b.Row).ToList();
        if (rows.Count == 0) return false;

        Snapshot();
        foreach (var r in rows) Widen(r, ActionColumn)[ActionColumn] = to;
        Reparse();
        return true;
    }

    static readonly string[] NewBindingRowCells = { "", "normal", "" };
    // The value cell starts at "0", not blank. A row with nothing in A..J is a
    // blank line: the device stops reading the sheet there, and the parser drops
    // it, so an all-blank new row disappeared the moment it was added.
    static readonly string[] NewPrefsRowCells = { "", "0" };

    public int AddBindingRow(ModeSheet sheet)
    {
        Snapshot();
        int insertAt = sheet.Bindings.Count > 0
            ? sheet.Bindings[^1].Row
            : sheet.StartRow + 2;
        // Insert a CLONE: rows in the grid are mutated in place by SetCell,
        // so sharing one template array would link every added row together.
        Grid.Insert(insertAt, (string[])(sheet.Type == SheetType.ProfileName
            ? NewBindingRowCells.Clone()
            : NewPrefsRowCells.Clone()));
        Reparse(); // Document must never be stale after a mutation
        return insertAt + 1;
    }

    // Append a new empty Profile sheet. The section mirrors a mode header:
    // the keyword row must START with "Profile" or the firmware skips the whole
    // sheet, the second row is the (ignored) filename slot, the third carries
    // the output label/channel. No binding rows: an empty mode shows the "No
    // bindings yet" hint, not an instant validation error.
    public int AddModeSheet(string modeName)
    {
        Snapshot();
        var first = Document.Sheets.FirstOrDefault(s => s.Type == SheetType.ProfileName);
        var label = first is { HeaderLabel.Length: > 0 } ? first.HeaderLabel : "PlayStation Outputs";
        Grid.Add(new[] { "Profile Name", "", modeName });
        Grid.Add(Array.Empty<string>()); // filename slot: ignored on a non-first sheet
        Grid.Add(new[] { label, "Function", first?.Channel ?? "" });
        Reparse();
        return Document.Sheets.Count - 1;
    }

    /// <summary>Append a whole sheet's rows as one undoable step, for a mode
    /// that came out of the same workbook but was left behind. The rows have to
    /// begin with a sheet keyword, because that is the only thing that makes
    /// them a sheet to the parser and to the device. Returns the new sheet's
    /// index, or -1 when the rows are not a sheet.</summary>
    public int AppendSheetRows(IReadOnlyList<string[]> rows)
    {
        if (rows.Count == 0 || rows[0].Length == 0 || !Vocab.IsSheetKeyword(rows[0][0].Trim()))
            return -1;
        Snapshot();
        // Blank separator first: without it the parser reads a keyword row as a
        // dead binding and the device runs the new mode's rows on as part of
        // the sheet above. NormalizeForDeviceCsv repairs this on the way out
        // too, but not until save, and the editor shows the grid before that.
        if (Grid.Count > 0) Grid.Add(Array.Empty<string>());
        Grid.AddRange(rows.Select(r => (string[])r.Clone()));
        Reparse();
        return Document.Sheets.Count - 1;
    }

    // Append a Preferences sheet shaped like the official template: keyword
    // row, blank slot row, then the annotated column header. Refused when one
    // already exists; the device only reads one.
    static readonly string[] PrefsKeywordCells = { "Preferences" };
    static readonly string[] PrefsHeaderCells = { "Preference", "Value", "Units", "Description" };

    public int AddPreferencesSheet()
    {
        if (Document.Sheets.Any(s => s.Type == SheetType.Preferences)) return -1;
        Snapshot();
        // Clones, not the templates: SetCell mutates grid rows in place.
        Grid.Add((string[])PrefsKeywordCells.Clone());
        Grid.Add(Array.Empty<string>());
        Grid.Add((string[])PrefsHeaderCells.Clone());
        Reparse();
        return Document.Sheets.Count - 1;
    }

    // Move one grid row to another row's position (both 1-based); the rows
    // between them shift by one. Drag and drop in List View lands here; the
    // caller keeps both rows inside the same mode.
    public void MoveRow(int fromRow, int toRow)
    {
        if (fromRow == toRow) return;
        if (fromRow < 1 || toRow < 1 || fromRow > Grid.Count || toRow > Grid.Count) return;
        Snapshot();
        var moved = Grid[fromRow - 1];
        Grid.RemoveAt(fromRow - 1);
        Grid.Insert(toRow - 1, moved);
        Reparse();
    }

    // Move several grid rows as one contiguous block, keeping their relative
    // order, in one undoable step. Same landing rule as MoveRow: dragging
    // down lands the block after the target, dragging up lands it before.
    // Dropping onto a row that is itself moving does nothing.
    public void MoveRows(IEnumerable<int> fromRows, int toRow)
    {
        var moving = fromRows.Where(r => r >= 1 && r <= Grid.Count)
            .Distinct().OrderBy(r => r).ToList();
        if (moving.Count == 0 || toRow < 1 || toRow > Grid.Count || moving.Contains(toRow)) return;
        Snapshot();
        var block = moving.Select(r => Grid[r - 1]).ToList();
        for (int i = moving.Count - 1; i >= 0; i--) Grid.RemoveAt(moving[i] - 1);
        Grid.InsertRange(Math.Min(toRow - 1, Grid.Count), block);
        Reparse();
    }

    // The Move menu's "to the top" and "to the bottom": land the block just
    // before or just after an anchor row. The anchor's index is adjusted for
    // moving rows removed above it, so a selection that already sits partly
    // above the anchor still lands exactly where asked.
    public void MoveRowsBefore(IEnumerable<int> fromRows, int anchorRow) =>
        MoveRowsAt(fromRows, anchorRow, after: false);

    public void MoveRowsAfter(IEnumerable<int> fromRows, int anchorRow) =>
        MoveRowsAt(fromRows, anchorRow, after: true);

    void MoveRowsAt(IEnumerable<int> fromRows, int anchorRow, bool after)
    {
        var moving = fromRows.Where(r => r >= 1 && r <= Grid.Count && r != anchorRow)
            .Distinct().OrderBy(r => r).ToList();
        if (moving.Count == 0 || anchorRow < 1 || anchorRow > Grid.Count) return;
        Snapshot();
        var block = moving.Select(r => Grid[r - 1]).ToList();
        for (int i = moving.Count - 1; i >= 0; i--) Grid.RemoveAt(moving[i] - 1);
        int idx = (anchorRow - 1) - moving.Count(r => r < anchorRow) + (after ? 1 : 0);
        Grid.InsertRange(Math.Min(idx, Grid.Count), block);
        Reparse();
    }

    // Swap two whole grid rows, so column-K comments travel with their row.
    public void SwapRows(int rowA, int rowB)
    {
        if (rowA == rowB || rowA < 1 || rowB < 1 || rowA > Grid.Count || rowB > Grid.Count) return;
        Snapshot();
        (Grid[rowA - 1], Grid[rowB - 1]) = (Grid[rowB - 1], Grid[rowA - 1]);
        Reparse();
    }

    // Heal the "note kept in an input column" habit: move the cell's text into
    // the notes area (column K, which the device ignores) and clear the cell.
    public void MoveInputToNotes(int row, int col)
    {
        const int noteCol = 10;
        var val = GetCell(row, col);
        if (val.Length == 0 || col is < 2 or > 9) return;
        Snapshot();
        var r = Widen(row, noteCol);
        var existing = r[noteCol].Trim();
        r[noteCol] = existing.Length > 0 ? existing + "; " + val : val;
        r[col] = "";
        Reparse();
    }

    // The other half of that habit: a word in an input column that is not a
    // note but the profile's own name for the row, like "aim" beside a trigger.
    // Column L holds those, and the device ignores it as thoroughly as it
    // ignores the stray input, so this loses nothing and keeps the word.
    //
    // Same rules as SetOutput's name argument, because the name lands in the
    // same cell: legal, in a mode and not Preferences, not already taken by a
    // row with a different output, and not overwriting a name already there.
    public bool CanMoveInputToActionName(int row, int col) =>
        MoveInputToActionName(row, col, apply: false);

    public bool MoveInputToActionName(int row, int col) =>
        MoveInputToActionName(row, col, apply: true);

    bool MoveInputToActionName(int row, int col, bool apply)
    {
        var val = GetCell(row, col).Trim();
        if (val.Length == 0 || col is < 2 or > 9) return false;
        if (!IsLegalActionName(val)) return false;
        if (SheetAt(row)?.Type != SheetType.ProfileName) return false;
        if (GetCell(row, ActionColumn).Trim().Length > 0) return false;
        var output = GetCell(row, 0);
        if (NameableBindings().Any(b => b.Row != row && SameName(b.ActionName, val) && b.Output != output))
            return false;
        if (!apply) return true;

        Snapshot();
        var r = Widen(row, ActionColumn);
        r[ActionColumn] = val;
        r[col] = "";
        LabelActionColumn(row);
        Reparse();
        return true;
    }

    // Delete several rows as one undoable step (the selection's Delete
    // button). Highest first, so earlier removals cannot shift the rest.
    public void DeleteRows(IEnumerable<int> rows)
    {
        var valid = rows.Where(r => r >= 1 && r <= Grid.Count)
            .Distinct().OrderByDescending(r => r).ToList();
        if (valid.Count == 0) return;
        Snapshot();
        foreach (var r in valid) Grid.RemoveAt(r - 1);
        Reparse();
    }

    public void DeleteRow(int row)
    {
        if (row < 1 || row > Grid.Count) return;
        Snapshot();
        Grid.RemoveAt(row - 1);
        Reparse();
    }

    // Remove one input (index 0 = first NON-EMPTY input) from a binding row.
    // Inputs may sit in any of columns C..J with gaps, so the index is mapped
    // to its real column via the parsed binding, and the remaining inputs are
    // repacked from column C. Columns A, B, and K onward (comments) are never
    // touched: removing an input must not shift a comment into the data area.
    public void RemoveInput(int row, int inputIndex)
    {
        var binding = Document.Sheets.SelectMany(s => s.Bindings).FirstOrDefault(b => b.Row == row);
        if (binding is null || inputIndex < 0 || inputIndex >= binding.Inputs.Count) return;

        Snapshot();
        var remaining = binding.Inputs.Where((_, i) => i != inputIndex).ToList();
        var r = Widen(row, 1 + remaining.Count);
        for (int c = 2; c < 10 && c < r.Length; c++)
            r[c] = c - 2 < remaining.Count ? remaining[c - 2] : "";
        Reparse();
    }

    // A sheet's inclusive 1-based grid row range. The next sheet's keyword row
    // marks the end; the last sheet runs to the bottom of the grid.
    (int Start, int End) SheetRowRange(int sheetIndex)
    {
        var sheets = Document.Sheets;
        int start = sheets[sheetIndex].StartRow;
        int end = sheetIndex + 1 < sheets.Count ? sheets[sheetIndex + 1].StartRow - 1 : Grid.Count;
        return (start, end);
    }

    // Rename a mode. The name lives in column C of the keyword row, so SetCell
    // does the snapshot and reparse; guarding first keeps a no-op undo-free.
    public bool RenameMode(int sheetIndex, string name)
    {
        if (sheetIndex < 0 || sheetIndex >= Document.Sheets.Count) return false;
        var sheet = Document.Sheets[sheetIndex];
        if (sheet.Type != SheetType.ProfileName) return false;
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed == sheet.ModeName) return false;
        SetCell(sheet.StartRow, 2, trimmed);
        return true;
    }

    // Copy a whole mode to the end of the grid under a new name. Returns the new
    // sheet's index, or -1 if the target is not a nameable mode.
    public int DuplicateMode(int sheetIndex, string newName)
    {
        if (sheetIndex < 0 || sheetIndex >= Document.Sheets.Count) return -1;
        var sheet = Document.Sheets[sheetIndex];
        if (sheet.Type != SheetType.ProfileName) return -1;
        var trimmed = newName.Trim();
        if (trimmed.Length == 0) return -1;

        Snapshot();
        var (start, end) = SheetRowRange(sheetIndex);
        // Clone each row: SetCell mutates rows in place, so sharing the arrays
        // would couple the original and the copy.
        var clones = new List<string[]>();
        for (int row = start; row <= end; row++)
            clones.Add((string[])Grid[row - 1].Clone());

        // Name the copy in column C of its keyword row. Widen by hand rather
        // than via SetCell, which would take a second snapshot.
        var header = clones[0];
        if (header.Length <= 2)
        {
            var wider = new string[3];
            header.CopyTo(wider, 0);
            for (int i = header.Length; i < wider.Length; i++) wider[i] = "";
            clones[0] = header = wider;
        }
        header[2] = trimmed;

        // Only the first sheet's second row holds the profile filename; clear it
        // so a duplicated first sheet does not carry a stray filename cell.
        if (clones.Count > 1 && clones[1].Length > 0) clones[1][0] = "";

        Grid.AddRange(clones);
        Reparse();
        return Document.Sheets.Count - 1;
    }

    // Delete a mode or the Preferences sheet. The profile must keep at least
    // one mode, and the Infrared sheet is not ours to remove, so both are
    // refused before snapshot. Sheet 0 goes like any other: the profile
    // filename it carries belongs to the file, so it is handed to whichever
    // sheet becomes first.
    public bool DeleteMode(int sheetIndex)
    {
        if (sheetIndex < 0 || sheetIndex >= Document.Sheets.Count) return false;
        var type = Document.Sheets[sheetIndex].Type;
        if (type == SheetType.Infrared) return false;
        if (type == SheetType.ProfileName
            && Document.Sheets.Count(s => s.Type == SheetType.ProfileName) <= 1) return false;
        // Deleting sheet 0 needs a second row on the incoming first sheet to
        // carry the filename; a degenerate sheet without one stays put.
        if (sheetIndex == 0)
        {
            if (Document.Sheets.Count < 2) return false;
            var (nextStart, nextEnd) = SheetRowRange(1);
            if (nextEnd - nextStart < 1) return false;
        }

        Snapshot();
        var (start, end) = SheetRowRange(sheetIndex);
        if (sheetIndex == 0)
        {
            var fname = end - start >= 1 && Grid[start].Length > 0 ? Grid[start][0] : "";
            // Row 2 of the sheet that is about to become first.
            int slot = end + 1;
            if (Grid[slot].Length == 0) Grid[slot] = new[] { fname };
            else Grid[slot][0] = fname;
        }
        Grid.RemoveRange(start - 1, end - start + 1);
        Reparse();
        return true;
    }

    // Move a sheet one slot up or down by swapping its whole row block with the
    // next sheet's. delta is +1 (down) or -1 (up).
    //
    // Modes and the Preferences sheet both move: they are the rows the Modes
    // window lists, so one press moves one row, whichever kind it is. Only the
    // Infrared sheet is stepped over, because it is not shown and not ours to
    // reorder. Anything stepped over keeps its place.
    public bool MoveMode(int sheetIndex, int delta)
    {
        var sheets = Document.Sheets;
        if (delta == 0) return false;
        if (sheetIndex < 0 || sheetIndex >= sheets.Count) return false;
        if (sheets[sheetIndex].Type == SheetType.Infrared) return false;

        int step = Math.Sign(delta);
        int other = -1;
        for (int i = sheetIndex + step; i >= 0 && i < sheets.Count; i += step)
            if (sheets[i].Type != SheetType.Infrared) { other = i; break; }
        if (other < 0) return false;

        int lo = Math.Min(sheetIndex, other);
        int hi = Math.Max(sheetIndex, other);
        var (loStart, loEnd) = SheetRowRange(lo);
        var (hiStart, hiEnd) = SheetRowRange(hi);
        // Moving the first sheet needs a second row on the incoming sheet to
        // carry the profile filename; a degenerate sheet without one stays put.
        if (lo == 0 && hiEnd - hiStart < 1) return false;

        Snapshot();
        // Swap the two blocks in place so column-K comments travel with their
        // rows. Whatever sits between them (a Preferences or Infrared sheet)
        // is lifted and put back unchanged, so only the modes move.
        var hiBlock = Grid.GetRange(hiStart - 1, hiEnd - hiStart + 1);
        var loBlock = Grid.GetRange(loStart - 1, loEnd - loStart + 1);
        var midBlock = Grid.GetRange(loEnd, hiStart - 1 - loEnd);
        Grid.RemoveRange(loStart - 1, hiEnd - loStart + 1);
        Grid.InsertRange(loStart - 1, hiBlock.Concat(midBlock).Concat(loBlock));

        // The profile filename lives on the first sheet's second row, so it
        // belongs to the file, not the mode: hand it to the new first sheet.
        if (lo == 0)
        {
            var fname = loBlock.Count > 1 && loBlock[1].Length > 0 ? loBlock[1][0] : "";
            if (loBlock.Count > 1 && loBlock[1].Length > 0) loBlock[1][0] = "";
            if (hiBlock[1].Length == 0) hiBlock[1] = new[] { fname };
            else hiBlock[1][0] = fname;
            // hiBlock rows were re-inserted by reference except a fresh array:
            // put the widened row back into the grid at the new first sheet.
            Grid[loStart] = hiBlock[1];
        }
        Reparse();
        return true;
    }

    public bool HasErrors => Issues.Any(i => i.Severity == Severity.Error);

    public bool Dirty { get; set; }
}
