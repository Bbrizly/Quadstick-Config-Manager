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
    // their items flat.
    public static readonly string[] CategoryOrder =
    {
        "Controller", "Keyboard", "Mouse", "TV remote",
        "Xbox Adaptive Controller", "Mode switching", "Device settings",
    };

    public static readonly IReadOnlyDictionary<string, string[]> SubOrder =
        new Dictionary<string, string[]>
        {
            ["Controller"] = new[] { "Buttons", "D-pad", "Thumbsticks" },
            ["Keyboard"] = new[]
            {
                "Letters", "Numbers", "Space, Enter, arrows", "Function keys",
                "Modifier keys", "Number pad", "Other keys",
            },
        };

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
        _ when t.StartsWith("kb_keypad_", StringComparison.Ordinal) => ("Keyboard", "Number pad"),
        _ when Regex.IsMatch(t, "^kb_f([1-9]|1[0-9]|2[0-4])$") => ("Keyboard", "Function keys"),
        _ when Regex.IsMatch(t, "^kb_[a-z]$") => ("Keyboard", "Letters"),
        _ when Regex.IsMatch(t, "^kb_[0-9]$") => ("Keyboard", "Numbers"),
        _ when KbEveryday.Contains(t) => ("Keyboard", "Space, Enter, arrows"),
        _ when KbModifiers.Contains(t) => ("Keyboard", "Modifier keys"),
        _ when t.StartsWith("kb_", StringComparison.Ordinal) => ("Keyboard", "Other keys"),
        _ when t.StartsWith("mouse_", StringComparison.Ordinal) => ("Mouse", ""),
        _ when t.StartsWith("ir_", StringComparison.Ordinal) => ("TV remote", ""),
        _ when t.StartsWith("xac_", StringComparison.Ordinal) => ("Xbox Adaptive Controller", ""),
        _ when t.StartsWith("dpad_", StringComparison.Ordinal) => ("Controller", "D-pad"),
        _ when t.StartsWith("left_joy_", StringComparison.Ordinal)
            || t.StartsWith("right_joy_", StringComparison.Ordinal)
            || t is "left_stick" or "right_stick" => ("Controller", "Thumbsticks"),
        _ when ControllerButtons.Contains(t) => ("Controller", "Buttons"),
        _ when t is "increment_mode" or "decrement_mode" or "load_file" => ("Mode switching", ""),
        _ => ("Device settings", ""),
    };

    public static readonly TokenCatalog Catalog = new(Classify, CategoryOrder, SubOrder);
}
