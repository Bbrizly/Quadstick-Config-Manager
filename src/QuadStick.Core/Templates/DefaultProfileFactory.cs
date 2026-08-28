namespace QuadStick.Format;

/// <summary>Creates an editable profile from the embedded official template.</summary>
public static class DefaultProfileFactory
{
    public static ProfileFile Create(string csvFileName)
    {
        if (string.IsNullOrWhiteSpace(csvFileName))
            throw new ArgumentException("A profile file name is required.", nameof(csvFileName));

        using var stream = typeof(DefaultProfileFactory).Assembly.GetManifestResourceStream("DefaultTemplate")
            ?? throw new InvalidOperationException("Embedded default template missing.");
        using var reader = new StreamReader(stream);
        var profile = ProfileFile.Load(reader.ReadToEnd());
        profile.SetCell(profile.Document.FileNameCellRow, 0, csvFileName);
        profile.ClearUndo();
        profile.Dirty = false;
        return profile;
    }
}