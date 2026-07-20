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

    public string ToCsvText() => Csv.Write(Grid);

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
        var (doc, parseIssues) = Parser.Parse(ToCsvText());
        Document = doc;
        Issues = parseIssues.Concat(Validator.Validate(doc)).ToList();
    }

    // QMP puts a version header on every file it writes; match that on save/install.
    public void EnsureVersionHeader()
    {
        if (Document.HasVersionHeader) return;
        Snapshot();
        var name = Path.GetFileNameWithoutExtension(Document.CsvFileName ?? "config");
        Grid.Insert(0, new[] { "QuadStick Configuration", "Version 1.5", "", name });
        Reparse();
    }

    public string GetCell(int row, int col) =>
        row >= 1 && row <= Grid.Count && col < Grid[row - 1].Length ? Grid[row - 1][col].Trim() : "";

    public void SetCell(int row, int col, string value)
    {
        Snapshot();
        while (Grid.Count < row) Grid.Add(Array.Empty<string>());
        var r = Grid[row - 1];
        if (r.Length <= col)
        {
            var wider = new string[col + 1];
            r.CopyTo(wider, 0);
            for (int i = r.Length; i < wider.Length; i++) wider[i] = "";
            Grid[row - 1] = wider;
            r = wider;
        }
        r[col] = value;
        Reparse();
    }

    static readonly string[] NewBindingRowCells = { "", "normal", "" };
    static readonly string[] NewPrefsRowCells = { "", "" };

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
        var r = Grid[row - 1];
        if (r.Length <= noteCol)
        {
            var wider = new string[noteCol + 1];
            r.CopyTo(wider, 0);
            for (int i = r.Length; i < wider.Length; i++) wider[i] = "";
            Grid[row - 1] = wider;
            r = wider;
        }
        var existing = r[noteCol].Trim();
        r[noteCol] = existing.Length > 0 ? existing + "; " + val : val;
        r[col] = "";
        Reparse();
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
        var r = Grid[row - 1];
        int needed = 2 + remaining.Count;
        if (r.Length < needed)
        {
            var wider = new string[needed];
            r.CopyTo(wider, 0);
            for (int i = r.Length; i < needed; i++) wider[i] = "";
            Grid[row - 1] = wider;
            r = wider;
        }
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

    // Move a mode one slot up or down by swapping its whole row block with the
    // next mode's. delta is +1 (down) or -1 (up).
    //
    // "Next mode" means the nearest mode in that direction, not the neighbouring
    // sheet. A Preferences or Infrared sheet in between used to freeze both modes
    // either side of it, which is how a tester ended up unable to move the first
    // mode at all. Anything sitting between the two modes keeps its place.
    public bool MoveMode(int sheetIndex, int delta)
    {
        var sheets = Document.Sheets;
        if (delta == 0) return false;
        if (sheetIndex < 0 || sheetIndex >= sheets.Count) return false;
        if (sheets[sheetIndex].Type != SheetType.ProfileName) return false;

        int step = Math.Sign(delta);
        int other = -1;
        for (int i = sheetIndex + step; i >= 0 && i < sheets.Count; i += step)
            if (sheets[i].Type == SheetType.ProfileName) { other = i; break; }
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
