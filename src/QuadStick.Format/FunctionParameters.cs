namespace QuadStick.Format;

/// <summary>One number a function takes, in the words a person editing it
/// needs: what it changes, what unit it is in, how far it can go, and what the
/// device does when the cell leaves it out.</summary>
/// <param name="Label">Short name for the box, e.g. "Rate".</param>
/// <param name="Unit">The unit, e.g. "milliseconds". Empty when it has none.</param>
/// <param name="Minimum">Smallest value the device acts on.</param>
/// <param name="Maximum">Largest value the device reads correctly.</param>
/// <param name="Default">What the device uses when this number is left out,
/// already in plain words ("100 ms", "one pulse", "no upper limit").</param>
/// <param name="What">One sentence on what the number changes.</param>
public sealed record FunctionParameter(
    string Label,
    string Unit,
    int Minimum,
    int Maximum,
    string Default,
    string What)
{
    /// <summary>The whole thing as one sentence, for a hint line or a screen
    /// reader: "Rate, taps per second, 1 to 1000. Left out: 10 a second."
    /// </summary>
    public string Sentence =>
        $"{Label}{(Unit.Length > 0 ? ", " + Unit : "")}, {Minimum} to {Maximum}. Left out: {Default}. {What}";
}

/// <summary>
/// What each output function's numbers mean, ported from firmware 2373 rather
/// than from the manual.
/// </summary>
/// <remarks>
/// <para><c>Joystick/Configuration.c:302</c> packs a function cell into one
/// word: <c>(((parameter2 &lt;&lt; 14) + parameter) &lt;&lt; 4) + function_code</c>.
/// <c>Joystick/DataFlow.c:1465</c> unpacks it the same way and masks the first
/// number with <c>&amp; 0x3FFF</c>. So both numbers live in 14 bits and neither
/// can go past <see cref="Ceiling"/>: a bigger first number carries into the
/// second one, and the row does something nobody asked for.</para>
/// <para>Every default below is the literal the firmware substitutes when the
/// number is absent, quoted from the <c>set_output</c> switch. They are not
/// the manual's suggestions.</para>
/// </remarks>
public static class FunctionParameters
{
    /// <summary>The largest value either number holds. Both are 14 bits wide.</summary>
    public const int Ceiling = 16383;

    // A percent is turned into the device's 0-1023 scale with
    // `value * 1023 / 100`, so 100 is the top of the scale and anything above
    // it is a threshold no input can reach.
    const int Percent = 100;

    static readonly Dictionary<string, FunctionParameter[]> Table = new(StringComparer.Ordinal)
    {
        ["normal"] = Array.Empty<FunctionParameter>(),
        ["toggle"] = Array.Empty<FunctionParameter>(),

        // DataFlow.c:1654 `if (!function_parameter) function_parameter = 10;`
        // then `1000 / (function_parameter & 0x3FFF)`, so the number is a rate
        // in hertz. Past 1000 that integer division is 0 and the taps stop.
        ["repeat"] = new[]
        {
            new FunctionParameter("Rate", "taps a second", 1, 1000, "10 a second",
                "How fast it taps while you hold the input."),
            new FunctionParameter("First hold", "milliseconds", 0, Ceiling, "no extra hold",
                "Holds the first tap this long before the rapid-fire starts."),
        },

        // DataFlow.c:1689 defaults the length to 100 ms; :1694 makes a missing
        // or zero count one pulse.
        ["pulse"] = new[]
        {
            new FunctionParameter("Length", "milliseconds", 1, Ceiling, "100 ms",
                "How long each press lasts."),
            new FunctionParameter("Presses", "count", 1, Ceiling, "one press",
                "How many presses one activation sends."),
        },

        // DataFlow.c:1624 defaults to 100 ms. The cycle stretches with how hard
        // you are sipping or puffing, so this is the length at full strength.
        ["duty"] = new[]
        {
            new FunctionParameter("Cycle", "milliseconds", 1, Ceiling, "100 ms",
                "The on-and-off cycle length at full strength. Softer input stretches it."),
        },

        // DataFlow.c:1846 `if (!function_parameter) function_parameter = 100;`
        // and :1850 turns a missing upper limit into 9999, higher than any
        // input can read, which is the same as having no ceiling.
        ["greater_than"] = new[]
        {
            new FunctionParameter("Threshold", "percent", 1, Percent, "100 percent",
                "Fires once your input is at least this strong."),
            new FunctionParameter("Upper limit", "percent", 1, Percent, "no upper limit",
                "Stops firing again once your input goes past this, so the two numbers make a band."),
        },

        // DataFlow.c:1874 defaults to 50 percent.
        ["less_than"] = new[]
        {
            new FunctionParameter("Threshold", "percent", 1, Percent, "50 percent",
                "Fires while your input stays under this strength."),
        },

        // DataFlow.c:1902 `pOutputStatus->timer = function_parameter ?
        // calibrated_time(...) : 1`, so no number means it releases at once.
        ["force_off"] = new[]
        {
            new FunctionParameter("Delay", "milliseconds", 1, Ceiling, "released at once",
                "Waits this long before releasing the other row's latched output."),
        },

        // DataFlow.c:1934 defaults to 1000 ms.
        ["delayed_latch"] = new[]
        {
            new FunctionParameter("Hold to latch", "milliseconds", 1, Ceiling, "1000 ms",
                "Hold the input longer than this and the output latches on instead of tapping."),
        },

        // DataFlow.c:1723 defaults to 100 ms.
        ["delay_off"] = new[]
        {
            new FunctionParameter("Hold on", "milliseconds", 1, Ceiling, "100 ms",
                "Keeps the output pressed this long after you let go."),
        },

        // DataFlow.c:1739 defaults the wait to 1000 ms. The second number is
        // read at :1741 and :1748: exactly 1 means latch it on instead, and
        // anything above 1 is a press length in milliseconds.
        ["delay_on"] = new[]
        {
            new FunctionParameter("Wait", "milliseconds", 1, Ceiling, "1000 ms",
                "Waits this long after you activate, then presses."),
            new FunctionParameter("Then", "1 to latch, or milliseconds", 1, Ceiling, "holds while you hold",
                "1 latches the output on. Anything above 1 is how long the press lasts."),
        },

        // DataFlow.c:1960 defaults the tap window to 500 ms; :1976 defaults the
        // press to 100 ms, and :1957 reads a second number of exactly 1 as
        // "latch instead of tapping".
        ["tap"] = new[]
        {
            new FunctionParameter("Counts as a tap", "milliseconds", 1, Ceiling, "500 ms",
                "Let go inside this window and it counts as a tap. Hold longer and nothing fires."),
            new FunctionParameter("Press", "1 to latch, or milliseconds", 1, Ceiling, "100 ms",
                "How long the tap presses for. 1 latches the output on instead."),
        },

        // DataFlow.c:1797 and :1821 both step by `(param ? param : 10) * 1023 /
        // 100`, a percent of the full range. The second number is a repeat
        // interval: without it, one activation is one step.
        ["increment_value"] = new[]
        {
            new FunctionParameter("Step", "percent", 1, Percent, "10 percent",
                "How far up the analog output moves each time."),
            new FunctionParameter("Repeat every", "milliseconds", 1, Ceiling, "one step per activation",
                "Keeps stepping this often while you hold the input."),
        },
        ["decrement_value"] = new[]
        {
            new FunctionParameter("Step", "percent", 1, Percent, "10 percent",
                "How far down the analog output moves each time."),
            new FunctionParameter("Repeat every", "milliseconds", 1, Ceiling, "one step per activation",
                "Keeps stepping this often while you hold the input."),
        },
    };

    /// <summary>The numbers a function takes, in order. Empty for a function
    /// that takes none, and for one this app has never heard of.</summary>
    public static IReadOnlyList<FunctionParameter> For(string function) =>
        Table.TryGetValue((function ?? "").Trim(), out var p) ? p : Array.Empty<FunctionParameter>();

    /// <summary>Every function the table covers, for the arity check.</summary>
    internal static IEnumerable<KeyValuePair<string, FunctionParameter[]>> All => Table;
}
