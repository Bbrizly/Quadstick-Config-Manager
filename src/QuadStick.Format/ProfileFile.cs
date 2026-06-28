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

    public bool CanUndo => _undo.Count > 0;

    void Snapshot()
    {
        Dirty = true;
        _undo.Add(Grid.Select(r => (string[])r.Clone()).ToList());
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
    }

    public void ClearUndo() => _undo.Clear();

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        Grid = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
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

    public int AddBindingRow(ModeSheet sheet)
    {
        Snapshot();
        int insertAt = sheet.Bindings.Count > 0
            ? sheet.Bindings[^1].Row
            : sheet.StartRow + 2;
        Grid.Insert(insertAt, sheet.Type == SheetType.ProfileName
            ? new[] { "", "normal", "" }
            : new[] { "", "" });
        return insertAt + 1;
    }

    public void DeleteRow(int row)
    {
        if (row < 1 || row > Grid.Count) return;
        Snapshot();
        Grid.RemoveAt(row - 1);
        Reparse();
    }

    public bool HasErrors => Issues.Any(i => i.Severity == Severity.Error);

    public bool Dirty { get; set; }
}
