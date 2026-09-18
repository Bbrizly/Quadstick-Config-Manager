using System.Globalization;
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

    // Drew reported the second-number wording on 2026-09-05 and he was right,
    // though not about which function. `tap` and `delay_on` both give a second
    // number of exactly 1 a special meaning and they are not the same meaning,
    // which is the whole reason one sentence cannot serve both. delay_on
    // (DataFlow.c:1741) skips the clear, so the output stays on: a latch.
    // tap (DataFlow.c:1986-1999) flips the state, so the next tap releases it:
    // a toggle. The app called both of them latches and sent people looking
    // for a release that never came.
    [Fact]
    public void One_means_a_toggle_on_tap_and_a_latch_on_delay_on()
    {
        var tap = FunctionParameters.For("tap")[1].What;
        var delayOn = FunctionParameters.For("delay_on")[1].What;

        Assert.Contains("toggle", tap, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("latch", tap, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("the next turns it off", tap, StringComparison.Ordinal);

        Assert.Contains("latches", delayOn, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("toggle", delayOn, StringComparison.OrdinalIgnoreCase);
    }

    // "1" alone reads as either number in a two-number cell, which is what
    // Drew said was confusing. Any special value a function gives a number has
    // to say which number it is talking about.
    [Fact]
    public void A_special_value_says_which_number_it_means()
    {
        foreach (var name in new[] { "tap", "delay_on" })
        {
            var what = FunctionParameters.For(name)[1].What;
            Assert.Contains("exactly 1", what, StringComparison.Ordinal);
        }
    }

    // Both numbers are packed into one word (Configuration.c:302) and every
    // firmware default tests the whole word, not the first number's 14 bits.
    // So a second number keeps the word non-zero and the first number stays a
    // literal 0: `repeat 0 500` is not ten taps a second, it is a rate of 0.
    // The app used to promise the default here, which is the app telling
    // somebody their device will do something it will not do.
    [Theory]
    [InlineData("repeat 0 500")]
    [InlineData("greater_than 0 60")]
    [InlineData("pulse 0 3")]
    public void A_zero_beside_a_second_number_does_not_get_the_default(string function)
    {
        var issues = Check(function);
        Assert.True(Says(issues, "share one word"),
            "expected the packed-word reading: " + string.Join(" | ", issues.Select(i => i.Message)));
        Assert.False(Says(issues, "so the device uses"),
            "the app promised a default the device will not substitute");
    }

    // The plain case still reads the old way, because with nothing after it a
    // zero really is how a file leaves a number out.
    [Theory]
    [InlineData("repeat 0")]
    [InlineData("greater_than 0")]
    public void A_zero_on_its_own_still_names_the_default(string function)
    {
        var issues = Check(function);
        Assert.True(Says(issues, "as no value at all"),
            "expected the leave-it-out reading: " + string.Join(" | ", issues.Select(i => i.Message)));
    }

    // Drew, 2026-09-05: the descriptions read long. The numbers a person needs
    // before typing stay under the box; what the number does moved behind a
    // question mark. Summary is that split, so it has to keep the range and
    // drop the behaviour, not merely be shorter.
    [Theory]
    [InlineData("tap")]
    [InlineData("repeat")]
    [InlineData("delay_on")]
    [InlineData("greater_than")]
    public void The_short_line_keeps_the_range_and_drops_the_behaviour(string function)
    {
        foreach (var p in FunctionParameters.For(function))
        {
            Assert.Contains(p.Label, p.Summary, StringComparison.Ordinal);
            Assert.Contains(p.Default, p.Summary, StringComparison.Ordinal);
            Assert.Contains(p.Maximum.ToString(CultureInfo.InvariantCulture), p.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain(p.What, p.Summary, StringComparison.Ordinal);
            Assert.Contains(p.What, p.Sentence, StringComparison.Ordinal);
            // No orphaned space or stop where the behaviour used to be.
            Assert.Equal(p.Summary.TrimEnd(), p.Summary);
        }
    }

    // Summary is Sentence with the behaviour argument left empty, so it only
    // comes out clean while every translation ends on that placeholder. A
    // translator who moves it leaves a space in the middle of the short line.
    [Fact]
    public void Every_language_puts_the_behaviour_last_in_the_sentence()
    {
        foreach (var path in Directory.GetFiles(
            Path.Combine(RepoRoot(), "src", "QuadStick.Format"), "Strings*.resx"))
        {
            if (path.EndsWith("qps-ploc.resx", StringComparison.Ordinal)) continue;
            var doc = System.Xml.Linq.XDocument.Load(path);
            foreach (var key in new[] { "Fn_Sentence", "Fn_SentenceWithUnit" })
            {
                var value = doc.Root!.Elements("data")
                    .First(d => (string?)d.Attribute("name") == key)
                    .Element("value")!.Value;
                Assert.EndsWith("{4}", value.TrimEnd(), StringComparison.Ordinal);
            }
        }
    }

    static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "QuadStick.sln")))
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar))!;
        return dir;
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
