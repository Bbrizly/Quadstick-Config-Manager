using System.Text.RegularExpressions;

namespace QuadStick.App;

// Everything a drill-down picker needs to group a token list: how to place
// one token, which categories exist and in what order, and which categories
// split further. One instance per kind of list (outputs, inputs, ...).
public sealed record TokenCatalog(
    Func<string, (string Cat, string Sub)> Classify,
    IReadOnlyList<string> CategoryOrder,
    IReadOnlyDictionary<string, string[]> SubOrder);

// Sorts the ~380 legal output tokens into the categories the Press picker
// shows. Membership is derived from the token text itself (prefixes plus one
// short explicit button set), so every token always lands in exactly one
// place, and anything unrecognized falls into "Device settings" instead of
// vanishing. OutputCatalogTests proves the whole vocabulary is covered.
public static class OutputCatalog
{
    // Display order for the picker. Categories missing a SubOrder entry list
    // their items flat. Not readonly: the group names above the tokens are in
    // the app's language, so Relocalize rebuilds all three when it changes.
    public static string[] CategoryOrder { get; private set; } = BuildCategoryOrder();

    static string[] BuildCategoryOrder() => new[]
    {
        "Controller", "Keyboard", "Mouse", Strings.Outputs_TVRemote,
        "Xbox Adaptive Controller", Strings.Outputs_ModeSwitching, Strings.Outputs_DeviceSettings,
    };

    public static IReadOnlyDictionary<string, string[]> SubOrder { get; private set; } = BuildSubOrder();

    static IReadOnlyDictionary<string, string[]> BuildSubOrder() =>
        new Dictionary<string, string[]>
        {
            ["Controller"] = new[] { "Buttons", "D-pad", "Thumbsticks" },
            ["Keyboard"] = new[]
            {
                "Letters", "Numbers", "Space, Enter, arrows", Strings.Outputs_FunctionKeys,
                Strings.Outputs_ModifierKeys, Strings.Outputs_NumberPad, Strings.Outputs_OtherKeys,
            },
        };

    /// <summary>Rebuild the category and group names in the current language.
    /// Classify already answers in the current language on every call; these
    /// three were built once, and have to agree with it.</summary>
    public static void Relocalize()
    {
        CategoryOrder = BuildCategoryOrder();
        SubOrder = BuildSubOrder();
        Catalog = new(Classify, CategoryOrder, SubOrder);
    }

    // Every controller button that is not a dpad_/joy_/stick token: the
    // PlayStation set and the Xbox set share this one list.
    static readonly HashSet<string> ControllerButtons = new(StringComparer.Ordinal)
    {
        "circle", "square", "triangle", "x", "select", "start", "ps3",
        "touch", "touch_down", "touch_left", "touch_right", "touch_up",
        "left_1", "left_2", "left_3", "right_1", "right_2", "right_3",
        "A", "B", "X", "Y", "back", "guide", "capture",
        "left_bumper", "right_bumper", "left_trigger", "right_trigger",
    };

    // The firmware's alias block: thirteen outputs that answer to two names
    // each (output_keywords.h, the rows under "// aliases"). left_bumper is
    // LEFT_1, A is X_, guide is PS3_. Both spellings reach the same channel,
    // so hiding one hides no output. A token with a single name, a custom name
    // included, is never filtered.
    public static readonly (string Ps, string Xbox)[] VocabularyPairs =
    {
        ("left_1", "left_bumper"), ("left_2", "left_trigger"), ("left_3", "left_stick"),
        ("right_1", "right_bumper"), ("right_2", "right_trigger"), ("right_3", "right_stick"),
        ("x", "A"), ("square", "X"), ("triangle", "Y"), ("circle", "B"),
        ("ps3", "guide"), ("select", "back"), ("touch", "capture"),
    };

    static readonly HashSet<string> PsSpellings =
        new(VocabularyPairs.Select(p => p.Ps), StringComparer.Ordinal);
    static readonly HashSet<string> XboxSpellings =
        new(VocabularyPairs.Select(p => p.Xbox), StringComparer.Ordinal);

    /// <summary>The list with the other vocabulary's spellings dropped.
    /// Anything but "PlayStation" or "Xbox" keeps every token.</summary>
    public static IReadOnlyList<string> InVocabulary(IReadOnlyList<string> tokens, string vocabulary)
    {
        var drop = vocabulary switch
        {
            "PlayStation" => XboxSpellings,
            "Xbox" => PsSpellings,
            _ => null,
        };
        return drop is null ? tokens : tokens.Where(t => !drop.Contains(t)).ToList();
    }

    static readonly HashSet<string> KbEveryday = new(StringComparer.Ordinal)
    {
        "kb_space", "kb_enter", "kb_return", "kb_tab", "kb_escape", "kb_backspace",
        "kb_up_arrow", "kb_down_arrow", "kb_left_arrow", "kb_right_arrow",
        "kb_home", "kb_end", "kb_page_up", "kb_page_down", "kb_insert", "kb_delete",
    };

    static readonly HashSet<string> KbModifiers = new(StringComparer.Ordinal)
    {
        "kb_left_shift", "kb_right_shift", "kb_left_control", "kb_right_control",
        "kb_left_alt", "kb_right_alt", "kb_left_gui", "kb_right_gui",
    };

    public static (string Category, string Sub) Classify(string t) => t switch
    {
        _ when t.StartsWith("kb_keypad_", StringComparison.Ordinal) => ("Keyboard", Strings.Outputs_NumberPad2),
        _ when Regex.IsMatch(t, "^kb_f([1-9]|1[0-9]|2[0-4])$") => ("Keyboard", Strings.Outputs_FunctionKeys2),
        _ when Regex.IsMatch(t, "^kb_[a-z]$") => ("Keyboard", "Letters"),
        _ when Regex.IsMatch(t, "^kb_[0-9]$") => ("Keyboard", "Numbers"),
        _ when KbEveryday.Contains(t) => ("Keyboard", "Space, Enter, arrows"),
        _ when KbModifiers.Contains(t) => ("Keyboard", Strings.Outputs_ModifierKeys2),
        _ when t.StartsWith("kb_", StringComparison.Ordinal) => ("Keyboard", Strings.Outputs_OtherKeys2),
        _ when t.StartsWith("mouse_", StringComparison.Ordinal) => ("Mouse", ""),
        _ when t.StartsWith("ir_", StringComparison.Ordinal) => (Strings.Outputs_TVRemote2, ""),
        _ when t.StartsWith("xac_", StringComparison.Ordinal) => ("Xbox Adaptive Controller", ""),
        _ when t.StartsWith("dpad_", StringComparison.Ordinal) => ("Controller", "D-pad"),
        _ when t.StartsWith("left_joy_", StringComparison.Ordinal)
            || t.StartsWith("right_joy_", StringComparison.Ordinal)
            || t is "left_stick" or "right_stick" => ("Controller", "Thumbsticks"),
        _ when ControllerButtons.Contains(t) => ("Controller", "Buttons"),
        _ when t is "increment_mode" or "decrement_mode" or "load_file" => (Strings.Outputs_ModeSwitching2, ""),
        _ => (Strings.Outputs_DeviceSettings2, ""),
    };

    public static TokenCatalog Catalog { get; private set; } = new(Classify, CategoryOrder, SubOrder);

    // The output picker for one open profile: the names that profile gives its
    // own outputs, listed first under "Custom", then the real tokens. Names are
    // per profile, so this cannot be the static catalog above.
    public sealed record ProfileOutputs(
        TokenCatalog Catalog,
        IReadOnlyList<string> Options,
        IReadOnlyDictionary<string, string> TokenFor)
    {
        // What a picked entry means: a name commits its token plus itself,
        // a plain token commits itself and clears any name.
        public (string Token, string Name) Resolve(string picked) =>
            TokenFor.TryGetValue(picked, out var token) ? (token, picked) : (picked, "");
    }

    // customNames is the profile's names table: the name you typed against the
    // output token it stands for. A name with no output yet is listed too, so
    // you can plan the names first and fill the outputs in after. Picking one
    // leaves the row without an output, which the problems list already calls
    // out in plain words.
    public static ProfileOutputs ForProfile(
        IReadOnlyList<(string Name, string Token)> customNames, IReadOnlyList<string> tokens)
    {
        // Ignoring case, like the rest of the naming: two rows spelling one
        // name differently must not both reach the list.
        var tokenFor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, token) in customNames) tokenFor.TryAdd(name, token);
        if (tokenFor.Count == 0)
            return new ProfileOutputs(Catalog, tokens, tokenFor);

        var options = tokenFor.Keys.Concat(tokens).ToList();
        var catalog = new TokenCatalog(
            t => tokenFor.ContainsKey(t) ? ("Custom", "") : Classify(t),
            new[] { "Custom" }.Concat(CategoryOrder).ToArray(),
            SubOrder);
        return new ProfileOutputs(catalog, options, tokenFor);
    }
}
