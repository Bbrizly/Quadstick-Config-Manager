using System.Text.Json;

namespace QuadStick.Format;

// Legal input/output/function names from validation.json (validation.quadstick.com).
public static class Vocab
{
    static Vocab()
    {
        using var s = typeof(Vocab).Assembly.GetManifestResourceStream("ValidationJson")
            ?? throw new InvalidOperationException("Embedded validation.json missing.");
        using var doc = JsonDocument.Parse(s);
        var root = doc.RootElement;

        static HashSet<string> Set(JsonElement e) =>
            e.EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);

        Inputs = Set(root.GetProperty("inputs"));
        OutputsPs3 = Set(root.GetProperty("outputs_ps3"));
        OutputsXbox = Set(root.GetProperty("outputs_xbox"));
        var known = new HashSet<string>(OutputsPs3, StringComparer.Ordinal);
        known.UnionWith(OutputsXbox);
        KnownOutputs = known;

        var fnNames = Set(root.GetProperty("functions"));
        FunctionArity = FunctionParams.Where(kv => fnNames.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    public static readonly IReadOnlySet<string> Inputs;
    public static readonly IReadOnlySet<string> OutputsPs3;
    public static readonly IReadOnlySet<string> OutputsXbox;
    public static readonly IReadOnlySet<string> KnownOutputs;

    public static readonly IReadOnlyDictionary<string, (int Min, int Max)> FunctionArity;

    static readonly Dictionary<string, (int Min, int Max)> FunctionParams = new(StringComparer.Ordinal)
    {
        ["normal"] = (0, 0),
        ["toggle"] = (0, 0),
        ["repeat"] = (0, 2),
        ["pulse"] = (0, 2),
        ["duty"] = (0, 1),
        ["greater_than"] = (0, 2),
        ["less_than"] = (0, 1),
        ["force_off"] = (0, 1),
        ["delayed_latch"] = (0, 1),
        ["delay_off"] = (0, 1),
        ["delay_on"] = (0, 2),
        ["tap"] = (0, 2),
        ["increment_value"] = (0, 2),
        ["decrement_value"] = (0, 2),
    };

    public static readonly IReadOnlySet<string> SheetKeywords =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Profile Name", "Preferences", "Infrared" };

    /// <summary>A1 must contain "Profile", or be Preferences / Infrared.</summary>
    public static bool IsSheetKeyword(string a1) =>
        a1.Contains("Profile", StringComparison.OrdinalIgnoreCase)
        || a1.Trim().Equals("Preferences", StringComparison.OrdinalIgnoreCase)
        || a1.Trim().Equals("Infrared", StringComparison.OrdinalIgnoreCase);

    public static SheetType KeywordToType(string a1) =>
        a1.Contains("Profile", StringComparison.OrdinalIgnoreCase) ? SheetType.ProfileName
        : a1.Trim().Equals("Preferences", StringComparison.OrdinalIgnoreCase) ? SheetType.Preferences
        : SheetType.Infrared;

    public static bool IsKnownOutput(string name) => KnownOutputs.Contains(name);

    public static readonly IReadOnlySet<string> Channels =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "usb", "bluetooth", "usb bluetooth", "bluetooth usb" };
}
