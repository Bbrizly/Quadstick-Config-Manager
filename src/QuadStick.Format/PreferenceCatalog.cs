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
    string Source);

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
        "unit", "description", "options", "modeOverride", "risk", "source",
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
        var modeOverride = OptionalBool(e, "modeOverride", name);

        if (editor != PreferenceEditor.Integer && (min.HasValue || max.HasValue))
            throw new InvalidOperationException($"Preference '{name}' is not an integer, so it cannot carry bounds.");
        if (min.HasValue && max.HasValue && min.Value > max.Value)
            throw new InvalidOperationException($"Preference '{name}' has minimum above maximum.");

        if (editor != PreferenceEditor.Choice && options.Count > 0)
            throw new InvalidOperationException($"Preference '{name}' is not a choice, so it cannot carry options.");
        if (editor == PreferenceEditor.Choice && options.Count == 0)
            throw new InvalidOperationException($"Preference '{name}' is a choice with no options.");

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
            options, modeOverride, risk, source);
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

    static IReadOnlyList<string> OptionalOptions(JsonElement e, string name)
    {
        if (!e.TryGetProperty("options", out var v)) return Array.Empty<string>();
        if (v.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Preference '{name}' has an 'options' field that is not an array.");

        var options = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException($"Preference '{name}' has a non-text option.");
            var token = item.GetString() ?? "";
            if (token.Length == 0)
                throw new InvalidOperationException($"Preference '{name}' has an empty option.");
            if (options.Contains(token, StringComparer.Ordinal))
                throw new InvalidOperationException($"Preference '{name}' repeats the option '{token}'.");
            options.Add(token);
        }
        return options;
    }
}
