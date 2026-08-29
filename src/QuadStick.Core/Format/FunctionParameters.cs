using System.Globalization;

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
    /// <summary>The whole thing as one line, for a hint under the box or a
    /// screen reader: "Rate: 1 to 1000 taps a second. Blank means 10 a second.
    /// How fast it taps while you hold the input."</summary>
    public string Sentence =>
        string.Format(CultureInfo.CurrentCulture,
            Unit.Length > 0 ? Strings.Fn_SentenceWithUnit : Strings.Fn_Sentence,
            Label, Minimum, Maximum, Default, What, Unit);
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
            new FunctionParameter("Rate", Strings.Fn_TapsASecond, 1, 1000, Strings.Fn_10ASecond,
                Strings.Fn_HowFastItTapsWhile),
            new FunctionParameter(Strings.Fn_FirstHold, "milliseconds", 0, Ceiling, Strings.Fn_NoExtraHold,
                Strings.Fn_HoldsTheFirstTapThis),
        },

        // DataFlow.c:1689 defaults the length to 100 ms; :1694 makes a missing
        // or zero count one pulse.
        ["pulse"] = new[]
        {
            new FunctionParameter("Length", "milliseconds", 1, Ceiling, "100 ms",
                Strings.Fn_HowLongEachPressLasts),
            new FunctionParameter("Presses", "count", 1, Ceiling, Strings.Fn_OnePress,
                Strings.Fn_HowManyPressesOneActivation),
        },

        // DataFlow.c:1624 defaults to 100 ms. The cycle stretches with how hard
        // you are sipping or puffing, so this is the length at full strength.
        ["duty"] = new[]
        {
            new FunctionParameter("Cycle", "milliseconds", 1, Ceiling, "100 ms",
                Strings.Fn_TheOnAndOffCycle),
        },

        // DataFlow.c:1846 `if (!function_parameter) function_parameter = 100;`
        // and :1850 turns a missing upper limit into 9999, higher than any
        // input can read, which is the same as having no ceiling.
        ["greater_than"] = new[]
        {
            new FunctionParameter("Threshold", "percent", 1, Percent, "100 percent",
                Strings.Fn_FiresOnceYourInputIs),
            new FunctionParameter(Strings.Fn_UpperLimit, "percent", 1, Percent, Strings.Fn_NoUpperLimit,
                Strings.Fn_StopsFiringAgainOnceYour),
        },

        // DataFlow.c:1874 defaults to 50 percent.
        ["less_than"] = new[]
        {
            new FunctionParameter("Threshold", "percent", 1, Percent, "50 percent",
                Strings.Fn_FiresWhileYourInputStays),
        },

        // DataFlow.c:1902 `pOutputStatus->timer = function_parameter ?
        // calibrated_time(...) : 1`, so no number means it releases at once.
        ["force_off"] = new[]
        {
            new FunctionParameter("Delay", "milliseconds", 1, Ceiling, Strings.Fn_ReleasedAtOnce,
                Strings.Fn_WaitsThisLongBeforeReleasing),
        },

        // DataFlow.c:1934 defaults to 1000 ms.
        ["delayed_latch"] = new[]
        {
            new FunctionParameter(Strings.Fn_HoldToLatch, "milliseconds", 1, Ceiling, "1000 ms",
                Strings.Fn_HoldTheInputLongerThan),
        },

        // DataFlow.c:1723 defaults to 100 ms.
        ["delay_off"] = new[]
        {
            new FunctionParameter(Strings.Fn_HoldOn, "milliseconds", 1, Ceiling, "100 ms",
                Strings.Fn_KeepsTheOutputPressedThis),
        },

        // DataFlow.c:1739 defaults the wait to 1000 ms. The second number is
        // read at :1741 and :1748: exactly 1 means latch it on instead, and
        // anything above 1 is a press length in milliseconds.
        ["delay_on"] = new[]
        {
            new FunctionParameter("Wait", "milliseconds", 1, Ceiling, "1000 ms",
                Strings.Fn_WaitsThisLongAfterYou),
            new FunctionParameter("Then", "milliseconds", 1, Ceiling, Strings.Fn_HoldsWhileYouHold,
                Strings.Fn_1LatchesTheOutputOn),
        },

        // DataFlow.c:1960 defaults the tap window to 500 ms; :1976 defaults the
        // press to 100 ms, and :1957 reads a second number of exactly 1 as
        // "latch instead of tapping".
        ["tap"] = new[]
        {
            new FunctionParameter(Strings.Fn_CountsAsATap, "milliseconds", 1, Ceiling, "500 ms",
                Strings.Fn_LetGoInsideThisWindow),
            new FunctionParameter("Press", "milliseconds", 1, Ceiling, "100 ms",
                Strings.Fn_HowLongTheTapPresses),
        },

        // DataFlow.c:1797 and :1821 both step by `(param ? param : 10) * 1023 /
        // 100`, a percent of the full range. The second number is a repeat
        // interval: without it, one activation is one step.
        ["increment_value"] = new[]
        {
            new FunctionParameter("Step", "percent", 1, Percent, "10 percent",
                Strings.Fn_HowFarUpTheAnalog),
            new FunctionParameter(Strings.Fn_RepeatEvery, "milliseconds", 1, Ceiling, Strings.Fn_OneStepPerActivation,
                Strings.Fn_KeepsSteppingThisOftenWhile),
        },
        ["decrement_value"] = new[]
        {
            new FunctionParameter("Step", "percent", 1, Percent, "10 percent",
                Strings.Fn_HowFarDownTheAnalog),
            new FunctionParameter(Strings.Fn_RepeatEvery, "milliseconds", 1, Ceiling, Strings.Fn_OneStepPerActivation,
                Strings.Fn_KeepsSteppingThisOftenWhile),
        },
    };

    /// <summary>The numbers a function takes, in order. Empty for a function
    /// that takes none, and for one this app has never heard of.</summary>
    public static IReadOnlyList<FunctionParameter> For(string function) =>
        Table.TryGetValue((function ?? "").Trim(), out var p) ? p : Array.Empty<FunctionParameter>();

    /// <summary>Every function the table covers, for the arity check.</summary>
    internal static IEnumerable<KeyValuePair<string, FunctionParameter[]>> All => Table;
}
