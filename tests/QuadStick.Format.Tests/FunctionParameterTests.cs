using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// Drew asked for the ranges and defaults on the numbers a function takes,
// because somebody typing "greater_than 250" has no way to know 250 is a
// strength no input reaches. Every bound here is read off firmware 2373, and
// nothing in this file rewrites a value: it says what the device will do.
public class FunctionParameterTests
{
    static List<Issue> Check(string function) =>
        Validator.Validate(ProfileFile.Load(
            "Profile Name,,Solo\n"
            + "game.csv\n"
            + "Outputs,Function,usb\n"
            + $"x,{function},lip\n").Document);

    static bool Says(List<Issue> issues, string fragment) =>
        issues.Any(i => i.Message.Contains(fragment, StringComparison.Ordinal)
                     || i.Fix.Contains(fragment, StringComparison.Ordinal));

    // DataFlow.c:1848 scales a percent with `value * 1023 / 100`, so 100 is the
    // top of the scale the input is measured on. 250 is a threshold nothing
    // reaches and the row simply never fires.
    [Fact]
    public void A_percent_over_100_says_the_row_never_fires()
    {
        var issues = Check("greater_than 250");
        Assert.True(Says(issues, "never fires"),
            "expected the over-100 threshold to be called out: " + string.Join(" | ", issues.Select(i => i.Message)));
    }

    // The same number inside the range is ordinary and must stay silent, or the
    // rule would put a warning on most working profiles.
    [Fact]
    public void A_percent_inside_the_range_is_quiet()
    {
        var issues = Check("greater_than 80");
        Assert.False(Says(issues, "past the device's limit"));
        Assert.False(Says(issues, "under the device's minimum"));
    }

    // Configuration.c:302 packs the second number above the first with a 14 bit
    // shift, and DataFlow.c masks the first with 0x3FFF. A second parameter
    // past 16383 is read as something else entirely.
    [Fact]
    public void The_second_number_has_the_same_14_bit_ceiling()
    {
        var issues = Check("pulse 50 20000");
        Assert.True(Says(issues, "past the device's limit"),
            "expected the second parameter's ceiling to be checked: " + string.Join(" | ", issues.Select(i => i.Message)));
    }

    // A typed 0 is not "too low", it is how a file leaves a number out, and the
    // device puts its own default there. Saying which default is the useful part.
    [Fact]
    public void A_typed_zero_names_the_default_the_device_will_use()
    {
        var issues = Check("delay_off 0");
        Assert.True(Says(issues, "100 ms"),
            "expected the substituted default to be named: " + string.Join(" | ", issues.Select(i => i.Message)));
    }

    // Every function the app offers has to be able to answer "what does this
    // number do", or the editor has nothing to show beside the box.
    [Fact]
    public void Every_function_with_parameters_describes_each_one()
    {
        foreach (var (name, arity) in Vocab.FunctionArity)
        {
            var spec = FunctionParameters.For(name);
            Assert.Equal(arity.Max, spec.Count);
            foreach (var p in spec)
            {
                Assert.NotEmpty(p.Label);
                Assert.NotEmpty(p.Default);
                Assert.NotEmpty(p.What);
                Assert.True(p.Minimum < p.Maximum, $"{name}: {p.Label} has no range");
                Assert.True(p.Maximum <= FunctionParameters.Ceiling,
                    $"{name}: {p.Label} claims a maximum the device's 14 bits cannot hold");
            }
        }
    }

    // FunctionArity used to be a second hand-written table beside this one.
    // Two tables of the same fact drift, and the one that drifts teaches
    // somebody their device wrong.
    [Fact]
    public void The_arity_table_is_the_parameter_table()
    {
        Assert.Equal(14, Vocab.FunctionArity.Count);
        Assert.Empty(FunctionParameters.For("normal"));
        Assert.Empty(FunctionParameters.For("toggle"));
        Assert.Empty(FunctionParameters.For("not_a_function"));
    }
}
