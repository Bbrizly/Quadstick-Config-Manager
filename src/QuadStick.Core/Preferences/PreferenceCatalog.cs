using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("QuadStick.Format.Tests")]

namespace QuadStick.Format;

// "Integer" reads as a type name to the analyzer, but these four names are the
// editor kinds the preferences.json contract spells out, so they stay.
[SuppressMessage("Naming", "CA1720:Identifier contains type name",
    Justification = "The editor kinds are fixed by the preferences.json data contract.")]
public enum PreferenceEditor { Integer, Toggle, Choice, Text }

/// <summary>One preference the device understands, with only the metadata a
/// real source proves. Anything unproven stays <see cref="PreferenceEditor.Text"/>
/// with no bounds and no default: a guessed constraint can leave a disabled
/// user with hardware that no longer works.</summary>
public sealed record PreferenceDefinition(
    string Name,
    string Label,
    string Category,
    PreferenceEditor Editor,
    string? Default,
    int? Minimum,
    int? Maximum,
    string Unit,
    string Description,
    IReadOnlyList<string> Options,
    bool ModeOverride,
    string Risk,
    string Source,
    IReadOnlyList<string> OptionLabels,
    bool FirmwareMayAddMore)
{
    /// <summary>The plain-language name for one option, or the option itself
    /// when the catalog has no better word for it. Only ever shown: the token
    /// is what gets written to the file, letter for letter.</summary>
    public string LabelForOption(string option)
    {
        for (int i = 0; i < Options.Count && i < OptionLabels.Count; i++)
            if (string.Equals(Options[i], option, StringComparison.Ordinal))
                return OptionLabels[i];
        return option;
    }
}

// Preference metadata from Data/preferences.json. Every claimed range,
// default, option list or risk note carries a Source string tracing it to the
// QuadStick Manager Program 4 sources or the firmware 1476 snapshot. The file
// is validated the moment it loads, so a bad edit fails the build's tests
// rather than reaching a device.
public static class PreferenceCatalog
{
    // The order the editor shows these headings in.
    static readonly string[] CategoryOrder =
    {
        "Joystick", "Sip and puff", "Lip sensor", "Mouse", "Sound and lights",
        "Bluetooth", "Inputs and outputs", "USB and compatibility", "Advanced",
    };

    static readonly string[] KnownFields =
    {
        "name", "label", "category", "editor", "default", "minimum", "maximum",
        "unit", "description", "options", "optionLabels", "modeOverride", "risk", "source",
        "firmwareMayAddMore",
    };

    static PreferenceCatalog()
    {
        using var s = typeof(PreferenceCatalog).Assembly.GetManifestResourceStream("PreferencesJson")
            ?? throw new InvalidOperationException("Embedded preferences.json missing.");
        All = Parse(s);
        ByName = All.ToDictionary(d => d.Name, StringComparer.Ordinal);
    }

    /// <summary>Every preference, in the file's order, which groups them by
    /// <see cref="Categories"/>.</summary>
    public static IReadOnlyList<PreferenceDefinition> All { get; }

    /// <summary>Category headings in display order.</summary>
    public static IReadOnlyList<string> Categories => CategoryOrder;

    static readonly Dictionary<string, PreferenceDefinition> ByName;

    /// <summary>Looks a name up the way the device does, case sensitively. An
    /// unknown name is normal: firmware newer than this catalog will have
    /// names QCM has never heard of, and they must survive untouched.</summary>
    public static bool TryGet(string name, [MaybeNullWhen(false)] out PreferenceDefinition definition) =>
        ByName.TryGetValue(name ?? "", out definition);

    /// <summary>The catalog name an unknown one was most likely meant to be, or
    /// null when nothing is near enough to name. Only ever quoted back in a
    /// warning: nothing in this app rewrites a name on the strength of it.
    /// </summary>
    // A dropped or added word beats a typo, because the reported cases are
    // whole words rather than slips: "puff_threshold" for "sip_puff_threshold",
    // written by hand or by an older tool. Distance alone would answer
    // "joystick_warning" for "joystick_alarm", which is a different setting
    // spelled correctly, so the containment pass runs first and the distance
    // pass is kept tight enough that no two real preferences reach each other.
    public static string? Closest(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || ByName.ContainsKey(name)) return null;

        var contains = All
            .Where(d => d.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                     || name.Contains(d.Name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => Math.Abs(d.Name.Length - name.Length))
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (contains is not null) return contains.Name;

        // Two edits, and never more than a quarter of what was typed, so a
        // short name cannot reach a different short name: "volume" and
        // "brightness" stay apart, and so do the four deflection_multiplier_*.
        int budget = Math.Min(2, name.Length / 4);
        if (budget == 0) return null;

        var best = All
            .Select(d => (d.Name, Distance: Distance(name, d.Name, budget)))
            .Where(x => x.Distance <= budget)
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        return best.Name;
    }

    // Levenshtein, stopped as soon as it passes the budget: the catalog is
    // walked once per unknown name and most candidates are nowhere near.
    static int Distance(string a, string b, int budget)
    {
        if (Math.Abs(a.Length - b.Length) > budget) return budget + 1;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            int rowBest = cur[0];
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                rowBest = Math.Min(rowBest, cur[j]);
            }
            if (rowBest > budget) return budget + 1;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    internal static IReadOnlyList<PreferenceDefinition> Parse(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        return Read(doc);
    }

    internal static IReadOnlyList<PreferenceDefinition> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Read(doc);
    }

    static List<PreferenceDefinition> Read(JsonDocument doc)
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("preferences.json must be a JSON array.");

        var list = new List<PreferenceDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var d = ReadOne(e);
            if (!seen.Add(d.Name))
                throw new InvalidOperationException($"Duplicate preference name '{d.Name}'.");
            list.Add(d);
        }
        return list;
    }

    static PreferenceDefinition ReadOne(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Each preference must be a JSON object.");

        foreach (var p in e.EnumerateObject())
            if (!KnownFields.Contains(p.Name, StringComparer.Ordinal))
                throw new InvalidOperationException($"Unknown preference field '{p.Name}'.");

        var name = Required(e, "name");
        var label = Required(e, "label", name);
        var category = Required(e, "category", name);
        if (!CategoryOrder.Contains(category, StringComparer.Ordinal))
            throw new InvalidOperationException($"Preference '{name}' has unknown category '{category}'.");

        var editorText = Required(e, "editor", name);
        var editor = editorText switch
        {
            "integer" => PreferenceEditor.Integer,
            "toggle" => PreferenceEditor.Toggle,
            "choice" => PreferenceEditor.Choice,
            "text" => PreferenceEditor.Text,
            _ => throw new InvalidOperationException($"Preference '{name}' has unknown editor '{editorText}'."),
        };

        var source = Required(e, "source", name);
        var unit = OptionalText(e, "unit", name);
        var description = OptionalText(e, "description", name);
        var risk = OptionalText(e, "risk", name);
        var def = OptionalDefault(e, name);
        var min = OptionalInt(e, "minimum", name);
        var max = OptionalInt(e, "maximum", name);
        var options = OptionalOptions(e, name);
        var optionLabels = OptionalStrings(e, "optionLabels", name);
        var modeOverride = OptionalBool(e, "modeOverride", name);
        var mayAddMore = OptionalBool(e, "firmwareMayAddMore", name);

        if (editor != PreferenceEditor.Integer && (min.HasValue || max.HasValue))
            throw new InvalidOperationException($"Preference '{name}' is not an integer, so it cannot carry bounds.");
        if (min.HasValue && max.HasValue && min.Value > max.Value)
            throw new InvalidOperationException($"Preference '{name}' has minimum above maximum.");

        if (editor != PreferenceEditor.Choice && options.Count > 0)
            throw new InvalidOperationException($"Preference '{name}' is not a choice, so it cannot carry options.");
        if (editor == PreferenceEditor.Choice && options.Count == 0)
            throw new InvalidOperationException($"Preference '{name}' is a choice with no options.");

        // A label list that does not line up with the options would put the
        // wrong plain-language name on a value, and the whole point of the
        // labels is that somebody picks one without reading the number.
        if (optionLabels.Count > 0 && optionLabels.Count != options.Count)
            throw new InvalidOperationException(
                $"Preference '{name}' has {optionLabels.Count} option labels for {options.Count} options.");

        if (mayAddMore && editor != PreferenceEditor.Choice)
            throw new InvalidOperationException(
                $"Preference '{name}' is not a choice, so 'firmwareMayAddMore' means nothing on it.");

        switch (editor)
        {
            case PreferenceEditor.Integer when def is not null:
                if (!int.TryParse(def, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    throw new InvalidOperationException($"Preference '{name}' has a default that is not a whole number.");
                if ((min.HasValue && n < min.Value) || (max.HasValue && n > max.Value))
                    throw new InvalidOperationException($"Preference '{name}' has a default outside its bounds.");
                break;
            case PreferenceEditor.Toggle when def is not null && def != "0" && def != "1":
                throw new InvalidOperationException($"Preference '{name}' is a toggle, so its default must be 0 or 1.");
            case PreferenceEditor.Choice when def is not null && !options.Contains(def, StringComparer.Ordinal):
                throw new InvalidOperationException($"Preference '{name}' has a default that is not one of its options.");
            default:
                break;
        }

        return new PreferenceDefinition(
            name, label, category, editor, def, min, max, unit, description,
            options, modeOverride, risk, source, optionLabels, mayAddMore);
    }

    static string Required(JsonElement e, string field, string? name = null)
    {
        var who = name is null ? "A preference" : $"Preference '{name}'";
        if (!e.TryGetProperty(field, out var v) || v.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"{who} is missing the '{field}' field.");
        var text = v.GetString() ?? "";
        if (text.Length == 0)
            throw new InvalidOperationException($"{who} has an empty '{field}' field.");
        return text;
    }

    static string OptionalText(JsonElement e, string field, string name)
    {
        if (!e.TryGetProperty(field, out var v)) return "";
        if (v.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"Preference '{name}' has a non-text '{field}' field.");
        var text = v.GetString() ?? "";
        if (text.Length == 0)
            throw new InvalidOperationException($"Preference '{name}' has an empty '{field}' field. Leave it out instead.");
        return text;
    }

    // A default of "" is meaningful: it says the device ships this one blank.
    static string? OptionalDefault(JsonElement e, string name)
    {
        if (!e.TryGetProperty("default", out var v)) return null;
        if (v.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"Preference '{name}' has a non-text 'default' field.");
        return v.GetString() ?? "";
    }

    static int? OptionalInt(JsonElement e, string field, string name)
    {
        if (!e.TryGetProperty(field, out var v)) return null;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n))
            throw new InvalidOperationException($"Preference '{name}' has a '{field}' field that is not a whole number.");
        return n;
    }

    static bool OptionalBool(JsonElement e, string field, string name)
    {
        if (!e.TryGetProperty(field, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidOperationException($"Preference '{name}' has a non-boolean '{field}' field."),
        };
    }

    static IReadOnlyList<string> OptionalOptions(JsonElement e, string name) =>
        OptionalStrings(e, "options", name);

    static IReadOnlyList<string> OptionalStrings(JsonElement e, string field, string name)
    {
        if (!e.TryGetProperty(field, out var v)) return Array.Empty<string>();
        if (v.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Preference '{name}' has a '{field}' field that is not an array.");

        var items = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException($"Preference '{name}' has a non-text entry in '{field}'.");
            var token = item.GetString() ?? "";
            if (token.Length == 0)
                throw new InvalidOperationException($"Preference '{name}' has an empty entry in '{field}'.");
            if (items.Contains(token, StringComparer.Ordinal))
                throw new InvalidOperationException($"Preference '{name}' repeats '{token}' in '{field}'.");
            items.Add(token);
        }
        return items;
    }
}
