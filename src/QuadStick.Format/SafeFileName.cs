namespace QuadStick.Format;

// Google Sheets names are arbitrary. This turns one into a CSV file name
// that is safe on both macOS and Windows, for Drive restore.
public static class SafeFileName
{
    static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    const int MaxBaseLength = 100;

    // Invalid chars this platform rejects, plus path separators and the
    // drive colon that are legal on macOS but break a synced file on
    // Windows. This app runs on both, so a name must be safe on both.
    static readonly HashSet<char> InvalidChars = Path.GetInvalidFileNameChars()
        .Concat(new[] { '/', '\\', ':' }).ToHashSet();

    public static string ForCsv(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return "Untitled.csv";

        if (trimmed.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];

        var cleaned = string.Concat(trimmed.Select(c => InvalidChars.Contains(c) ? '_' : c));

        // Windows rejects a trailing dot or space.
        var baseName = cleaned.TrimEnd('.', ' ');
        if (baseName.Length == 0) baseName = "Untitled";

        if (ReservedWindowsNames.Contains(baseName)) baseName += "_file";

        // The cap can cut right after a dot or space; trim again so the
        // shortened name stays legal on Windows.
        if (baseName.Length > MaxBaseLength) baseName = baseName[..MaxBaseLength].TrimEnd('.', ' ');
        if (baseName.Length == 0) baseName = "Untitled";

        return baseName + ".csv";
    }
}
