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

    // Firmware 1476's output table still has the unaxed gyroscope aliases, so a
    // profile using one works. The app used to tell its owner to pick a
    // different name, and FORMAT.md already said it did the opposite.
    [Theory]
    [InlineData("gyroscope_cw")]
    [InlineData("gyroscope_ccw")]
    public void A_legacy_output_name_is_called_legacy_and_not_wrong(string output)
    {
        var f = ProfileFile.Load(
            $"Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\n{output},normal,lip\n");
        var issue = Assert.Single(f.Issues, i => i.Cell == "A4");
        Assert.Equal(Severity.Warning, issue.Severity);
        Assert.Contains("legacy output name", issue.Message);
        Assert.DoesNotContain("not a documented output name", issue.Message);
    }

    [Fact]
    public void A_name_nothing_knows_is_still_called_undocumented()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\nnot_a_real_output,normal,lip\n");
        var issue = Assert.Single(f.Issues, i => i.Cell == "A4");
        Assert.Contains("not a documented output name", issue.Message);
    }

    // The device reads a value with atoi, which is 32 bits. long.TryParse
    // accepted anything up to 63 bits and said nothing, so a ten digit value
    // passed and then arrived on the device as an entirely different number.
    [Fact]
    public void A_value_too_big_for_the_devices_atoi_is_reported()
    {
        var f = ProfileFile.Load(
            "Preferences\nprefs.csv\nName,Value\ndeflection_multiplier_up,4294967296\n");

        var issue = Assert.Single(f.Issues, i => i.Message.Contains("too big for the device"));
        Assert.Equal(Severity.Error, issue.Severity);
    }

    // Same rule on a mode row, which reads its value from column C.
    [Fact]
    public void A_mode_override_value_too_big_for_the_device_is_reported()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\nmouse_speed,,4294967296\n");

        Assert.Contains(f.Issues, i => i.Cell == "C4" && i.Message.Contains("too big for the device"));
    }

    // Which preferences take a word is the catalog's business. The rule used to
    // be a "bluetooth_" name prefix, which covered three more than the comment
    // beside it claimed and would have gone on covering whatever else was named
    // that way later.
    [Theory]
    [InlineData("bluetooth_device_mode", "keyboard")] // choice: a word is right here
    [InlineData("bluetooth_remote_adapter", "not-a-mac")] // text: left as typed
    public void A_preference_the_catalog_calls_a_word_is_not_asked_to_be_a_number(string name, string value)
    {
        var f = ProfileFile.Load($"Preferences\nprefs.csv\nName,Value\n{name},{value}\n");

        Assert.DoesNotContain(f.Issues, i => i.Message.Contains("is not a whole number"));
    }

    // And one the catalog calls a number still has to be one.
    [Fact]
    public void A_preference_the_catalog_calls_a_number_still_has_to_be_one()
    {
        var f = ProfileFile.Load("Preferences\nprefs.csv\nName,Value\nvolume,wibble\n");

        Assert.Contains(f.Issues, i => i.Message.Contains("is not a whole number"));
    }
}
