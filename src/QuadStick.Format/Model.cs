namespace QuadStick.Format;

public enum SheetType { ProfileName, Preferences, Infrared }

public enum Severity { Error, Warning }

// A machine-readable tag for issues the app can fix with one click.
// Matching on the message text would break the moment a wording changes.
public enum IssueKind { None, UnknownInput }

public sealed record Issue(Severity Severity, string Cell, string Message, string Fix, IssueKind Kind = IssueKind.None)
{
    public override string ToString() => $"{Severity} {Cell}: {Message} ({Fix})";
}

/// <summary>One binding row. InputCols carries each input's real 0-based grid
/// column, because inputs may sit in any of columns C..J with gaps between.</summary>
public sealed record Binding(int Row, string Output, string Function, IReadOnlyList<string> Inputs, IReadOnlyList<int> InputCols);

public sealed class ModeSheet
{
    public SheetType Type { get; init; }
    public string ModeName { get; init; } = "";
    public string? CsvFileName { get; init; }
    public string HeaderLabel { get; init; } = "";
    public string Channel { get; init; } = "";
    public int StartRow { get; init; }
    public List<Binding> Bindings { get; } = new();
}

public sealed class ProfileDocument
{
    public List<ModeSheet> Sheets { get; } = new();
    public string? CsvFileName => Sheets.Count > 0 ? Sheets[0].CsvFileName : null;
    public int FileNameCellRow => Sheets.Count > 0 ? Sheets[0].StartRow + 1 : 2;
    public bool HasVersionHeader { get; set; }
    public string HeaderName { get; set; } = "";
    public bool IsDefaultConfig =>
        string.Equals(CsvFileName, "default.csv", StringComparison.OrdinalIgnoreCase);
}
