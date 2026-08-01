using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// One rule for "is this row a setting or a binding", in one place. Three copies
// of it used to live in three files and one had dropped the increment_value
// exception, so the same cell read as a setting's value in the import review
// and as a physical input in the editor next to it.
public class PreferenceOverrideRuleTests
{
    [Theory]
    [InlineData("mouse_speed", "", true)]
    [InlineData("mouse_speed", "normal", true)]
    [InlineData("volume", "", true)]
    // These two adjust the setting live, so the row really is a binding and
    // column C really is an input.
    [InlineData("mouse_speed", "increment_value 5", false)]
    [InlineData("mouse_speed", "decrement_value 5", false)]
    // Not a setting name at all.
    [InlineData("left_trigger", "", false)]
    [InlineData("", "", false)]
    public void A_settings_row_is_told_from_a_binding_by_name_and_function(
        string output, string function, bool isSetting) =>
        Assert.Equal(isSetting, Vocab.IsPreferenceOverride(output, function));

    [Fact]
    public void The_validator_reads_an_override_value_out_of_column_C()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\nmouse_speed,,not a number\n");
        Assert.Contains(f.Issues, i =>
            i.Cell == "C4" && i.Severity == Severity.Error && i.Message.Contains("whole number"));
    }

    [Fact]
    public void An_increment_value_row_keeps_its_input_and_is_not_read_as_a_value()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\nmouse_speed,increment_value 5,right_sip\n");
        var b = Assert.Single(f.Document.Sheets[0].Bindings);
        Assert.Equal(new[] { "right_sip" }, b.Inputs);
        // "right_sip" is a real input, so nothing here complains that it is not
        // a whole number. That complaint would mean the row had been read as a
        // setting after all.
        Assert.DoesNotContain(f.Issues, i => i.Message.Contains("whole number"));
    }
}
