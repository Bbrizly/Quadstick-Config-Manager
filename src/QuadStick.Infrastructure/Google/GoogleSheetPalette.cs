namespace QuadStick.Infrastructure.Google;

/// <summary>
/// Provider-side spreadsheet formatting constants. These intentionally mirror
/// the existing light editor tints, but Google export must not depend on the
/// Avalonia palette or presentation assembly.
/// </summary>
internal static class GoogleSheetPalette
{
    public static readonly IReadOnlyDictionary<string, string> Light =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OutputTint"] = "#F3E2AE",
            ["FunctionTint"] = "#F6DCE4",
            ["InputTint"] = "#D3E6F5",
        };
}
