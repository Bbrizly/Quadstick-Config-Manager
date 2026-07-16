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

    public bool HasErrors => Issues.Any(i => i.Severity == Severity.Error);

    public bool Dirty { get; set; }
}
