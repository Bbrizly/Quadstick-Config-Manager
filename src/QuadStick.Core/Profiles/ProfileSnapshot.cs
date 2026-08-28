namespace QuadStick.Format;

/// <summary>
/// Immutable handoff from the UI-owned editing session to save/install/backup
/// operations. Background work receives text and validation state, never the
/// live mutable ProfileFile instance.
/// </summary>
public sealed record ProfileSnapshot(
    string RawCsvText,
    string? CsvFileName,
    IReadOnlyList<Issue> Issues)
{
    public bool HasErrors => Issues.Any(i => i.Severity == Severity.Error);

    public static ProfileSnapshot From(ProfileFile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var raw = Csv.Write(profile.Grid.Select(row => (string[])row.Clone()));
        var (document, parseIssues) = Parser.Parse(raw);
        var issues = parseIssues.Concat(Validator.Validate(document)).ToArray();
        return new ProfileSnapshot(raw, document.CsvFileName, issues);
    }
}