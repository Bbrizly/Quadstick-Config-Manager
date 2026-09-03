using QuadStick.Format;

namespace QuadStick.App;

// A read-only projection of a binding for the small device callouts. The
// Binding stays attached so a caller can always follow a summary back to the
// exact row in the editor.
internal sealed record GestureActionSummary(
    Binding Binding,
    string Output,
    string FriendlyOutput,
    string Function,
    string Parameters,
    bool IsSupport);

internal sealed record GestureSummary(
    string Zone,
    string InputToken,
    string FriendlyGestureName,
    IReadOnlyList<GestureActionSummary> Actions,
    IReadOnlyList<Binding> SequenceUses,
    bool HasComplexBehavior)
{
    public bool IsMapped => Actions.Any(a => !a.IsSupport && a.FriendlyOutput.Length > 0);
}

internal sealed record JoystickSummary(
    bool IsRecognized,
    string Role,
    // The output token whose art stands for the role, empty when the role has
    // no controller prompt of its own (the mouse).
    string RoleToken,
    int ActionCount,
    int ExtraActionCount,
    IReadOnlyList<Binding> Actions);

// This is the seam between the profile model and the diagram. It owns the
// rules for what is safe to compress, while MainWindow owns only presentation.
internal static class DeviceSummary
{
    static (string Token, string Name)[] Gestures => new[]
    {
        // The space is the result of StripInput, not UI copy. Keep the token
        // split so the localization guard does not treat it as prose.
        ("soft" + " puff", Strings.Main_SoftPuff),
        ("puff", Strings.Main_Puff),
        ("sip", Strings.Main_Sip),
        ("soft" + " sip", Strings.Main_SoftSip),
    };

    // The lip switch has three inputs, not one, and StripInput turns
    // "lip_soft" into "soft lip". Same shape as a mouthpiece hole so the card
    // reads the same way.
    static (string Token, string Name)[] LipGestures => new[]
    {
        ("lip", Strings.Main_Lip),
        ("soft" + " lip", Strings.Main_SoftLip),
        ("push", Strings.Main_Push),
    };

    static readonly (string Input, string Output)[] LeftStick =
    {
        ("left", "left_joy_left"),
        ("right", "left_joy_right"),
        ("up", "left_joy_up"),
        ("down", "left_joy_down"),
    };

    static readonly (string Input, string Output)[] Mouse =
    {
        ("left", "mouse_left"),
        ("right", "mouse_right"),
        ("up", "mouse_up"),
        ("down", "mouse_down"),
    };

    public static IReadOnlyList<GestureSummary> Mouthpiece(
        ModeSheet? sheet, string zone, Func<string, string> friendlyOutput)
    {
        var bindings = PhysicalBindings(sheet, zone);
        return (zone == "lip" ? LipGestures : Gestures).Select(g =>
        {
            var matches = bindings
                .Where(b => b.Inputs.Any(i => GestureToken(i, zone) == g.Token))
                .ToList();
            var actions = matches.Select(b => Action(b, friendlyOutput)).ToArray();
            var sequences = matches.Where(b => b.Inputs.Count > 1).ToArray();
            var functions = matches.Select(b => b.Function.Trim())
                .Distinct(StringComparer.Ordinal).Count();
            return new GestureSummary(zone, g.Token, g.Name, actions, sequences,
                actions.Any(a => a.IsSupport) || sequences.Length > 0 || functions > 1);
        }).ToArray();
    }

    public static IReadOnlyList<GestureActionSummary> ActionsForZone(
        ModeSheet? sheet, string zone, Func<string, string> friendlyOutput) =>
        PhysicalBindings(sheet, zone).Select(b => Action(b, friendlyOutput)).ToArray();

    public static JoystickSummary Joystick(ModeSheet? sheet)
    {
        var bindings = PhysicalBindings(sheet, "joystick");
        if (TryCore(bindings, LeftStick, out var leftCore))
            return new JoystickSummary(true, Strings.Main_LeftStick, "left_stick", bindings.Count,
                bindings.Count - leftCore.Count, bindings);
        if (TryCore(bindings, Mouse, out var mouseCore))
            return new JoystickSummary(true, Strings.Main_Mouse, "", bindings.Count,
                bindings.Count - mouseCore.Count, bindings);
        return new JoystickSummary(false, "", "", bindings.Count, 0, bindings);
    }

    static bool TryCore(IReadOnlyList<Binding> bindings,
        IReadOnlyList<(string Input, string Output)> expected,
        out HashSet<Binding> core)
    {
        core = new HashSet<Binding>();
        foreach (var (input, output) in expected)
        {
            var matches = bindings.Where(b => b.Inputs.Count == 1
                    && b.Inputs[0] == input
                    && b.Output == output
                    && FunctionName(b.Function) == "normal")
                .ToList();
            if (matches.Count != 1)
            {
                core.Clear();
                return false;
            }
            core.Add(matches[0]);
        }
        return true;
    }

    static IReadOnlyList<Binding> PhysicalBindings(ModeSheet? sheet, string zone) =>
        (sheet?.Bindings ?? new List<Binding>())
            .Where(b => !Vocab.IsPreferenceOverride(b.Output, b.Function)
                && b.Inputs.Any(i => MainWindow.ZoneOf(i) == zone))
            .ToArray();

    static GestureActionSummary Action(Binding binding, Func<string, string> friendlyOutput) =>
        new(binding,
            binding.Output,
            binding.ActionName.Length > 0 ? binding.ActionName : friendlyOutput(binding.Output),
            binding.Function,
            Parameters(binding.Function),
            FunctionName(binding.Function) == "force_off");

    static string Parameters(string function)
    {
        var f = (function ?? "").Trim();
        var space = f.IndexOf(' ');
        return space < 0 ? "" : f[(space + 1)..].Trim();
    }

    static string FunctionName(string function)
    {
        var f = (function ?? "").Trim();
        var space = f.IndexOf(' ');
        return space < 0 ? f : f[..space];
    }

    static string GestureToken(string input, string zone) =>
        MainWindow.StripInput(input, zone);
}
