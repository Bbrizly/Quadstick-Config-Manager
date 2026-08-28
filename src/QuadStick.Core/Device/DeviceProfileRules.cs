namespace QuadStick.Format;

/// <summary>Deterministic rules for which root files are selectable on a QuadStick
/// and which five-light pattern represents each position. No filesystem access.</summary>
public static class DeviceProfileRules
{
    public static bool IsProfileFileName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
        && !fileName.StartsWith('.');

    public static IReadOnlyList<string> SelectionOrder(IEnumerable<string> fileNames)
    {
        var csv = fileNames
            .Where(IsProfileFileName)
            .Where(n => !string.Equals(n, "prefs.csv", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var ordered = new List<string>();
        var first = csv.FirstOrDefault(n => string.Equals(n, "default.csv", StringComparison.OrdinalIgnoreCase));
        if (first is not null) ordered.Add(first);
        ordered.AddRange(csv
            .Where(n => !string.Equals(n, "default.csv", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        return ordered;
    }

    const string P = "purple", G = "grey", B = "blue", R = "red";
    static readonly string[][] LedPatterns =
    [
        [P, G, G, G, G], [G, P, G, G, G], [G, G, P, G, G], [G, G, G, P, G],
        [G, G, G, G, P], [P, G, G, G, P], [G, P, G, G, P], [G, G, P, G, P],
        [G, G, G, P, P], [G, G, G, G, B], [P, G, G, G, B], [G, P, G, G, B],
        [G, G, P, G, B], [G, G, G, P, B], [G, G, G, G, R], [P, G, G, G, R],
        [G, P, G, G, R], [G, G, P, G, R], [G, G, G, P, R], [B, B, B, B, B],
        [P, B, B, B, B], [B, P, B, B, B], [B, B, P, B, B], [B, B, B, P, B],
        [B, B, B, B, P], [P, B, B, B, P], [B, P, B, B, P], [B, B, P, B, P],
        [B, B, B, P, P], [R, R, R, R, P], [P, R, R, R, R], [R, P, R, R, R],
    ];

    public static IReadOnlyList<string> LedPattern(int fileNumber) =>
        fileNumber >= 1 && fileNumber <= LedPatterns.Length
            ? [.. LedPatterns[fileNumber - 1]]
            : [];
}
