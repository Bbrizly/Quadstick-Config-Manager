using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// One rule for "is this row a setting or a binding", in one place. Three copies
// of it used to live in three files and they had drifted apart, so the same
// cell read as a setting's value in the import review and as a physical input
// in the editor next to it.
public class PreferenceOverrideRuleTests
{
    [Theory]
    [InlineData("mouse_speed", "", true)]
    [InlineData("mouse_speed", "normal", true)]
    [InlineData("volume", "", true)]
    // The function cell does not get a say. The device takes the preference
    // branch on the output name and then skips column B without reading it, so
    // whatever is written there changes nothing.
    [InlineData("mouse_speed", "increment_value 5", true)]
    [InlineData("mouse_speed", "decrement_value 5", true)]
    [InlineData("mouse_speed", "wibble", true)]
    // Not a setting name at all.
    [InlineData("left_trigger", "", false)]
    [InlineData("left_trigger", "increment_value 5", false)]
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

    // The app used to read this row as a live binding, on the theory that a
    // newer firmware than the 2017 source would honour increment_value here.
    // Firmware 2373 arrived and it does not: the preference branch skips column
    // B without looking at it and runs atoi over column C, so this row sets
    // mouse_speed to atoi("right_sip"), which is zero.
    //
    // Quietly zeroing somebody's mouse speed is exactly the failure this app
    // exists to prevent, so the row has to be called what it is.
    [Fact]
    public void An_increment_value_row_on_a_setting_name_is_still_read_as_a_value()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\nmouse_speed,increment_value 5,right_sip\n");

        Assert.True(Vocab.IsPreferenceOverride("mouse_speed", "increment_value 5"));
        Assert.Contains(f.Issues, i =>
            i.Cell == "C4" && i.Severity == Severity.Error && i.Message.Contains("whole number"));
    }

    // The two functions are real, they just belong on a real output. There the
    // device does run them, stepping the channel's analog value and latching it.
    [Fact]
    public void An_increment_value_row_on_a_real_output_is_a_binding()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\nleft_joy_up,increment_value 5,mp_center_puff\n");
        var b = Assert.Single(f.Document.Sheets[0].Bindings);

        Assert.False(Vocab.IsPreferenceOverride("left_joy_up", "increment_value 5"));
        Assert.Equal(new[] { "mp_center_puff" }, b.Inputs);
        Assert.DoesNotContain(f.Issues, i => i.Message.Contains("whole number"));
    }

    // The firmware's output table still has the unaxed gyroscope aliases in
    // 2373, so a profile using one works. The app used to tell its owner to pick
    // a different name, and FORMAT.md already said it did the opposite.
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

    // Which preferences take a word is the firmware's business, not the
    // catalog's. Load_Preferences_File has a switch with exactly three cases:
    // two keyword tables and one strncpy. Everything else falls through to a
    // bare atoi, so a word there is zero and the app has to say so.
    //
    // The rule has been wrong twice. A "bluetooth_" name prefix also exempted
    // bluetooth_authentication_mode, bluetooth_remote_adapter and
    // bluetooth_throttle, which the device reads as numbers. Asking the catalog
    // was worse: its "text" editor means only that no range has been proven, so
    // anti_dead_zone and debug stopped being checked at all.
    [Theory]
    [InlineData("bluetooth_device_mode", "keyboard")]      // keyword table
    [InlineData("bluetooth_connection_mode", "pair")]      // keyword table
    [InlineData("bluetooth_remote_address", "00110022ab")] // strncpy, never a number
    public void A_preference_the_firmware_reads_as_a_word_is_not_asked_to_be_a_number(string name, string value)
    {
        var f = ProfileFile.Load($"Preferences\nprefs.csv\nName,Value\n{name},{value}\n");

        Assert.DoesNotContain(f.Issues, i => i.Message.Contains("is not a whole number"));
    }

    // Everything else in that switch is an atoi, whatever the catalog says it
    // looks like in the editor.
    [Theory]
    [InlineData("volume", "wibble")]
    [InlineData("anti_dead_zone", "wibble")]
    [InlineData("debug", "wibble")]
    [InlineData("bluetooth_throttle", "wibble")]
    [InlineData("bluetooth_remote_adapter", "wibble")]
    [InlineData("bluetooth_authentication_mode", "wibble")]
    public void A_preference_the_firmware_reads_with_atoi_still_has_to_be_a_number(string name, string value)
    {
        var f = ProfileFile.Load($"Preferences\nprefs.csv\nName,Value\n{name},{value}\n");

        Assert.Contains(f.Issues, i => i.Message.Contains("is not a whole number"));
    }

    // A remote address is twelve digits and is strncpy'd, not converted, so the
    // 32 bit bound must not touch it. Bounding it blocked an install over an
    // address that was perfectly fine.
    [Fact]
    public void A_bluetooth_address_of_all_digits_is_not_bounded_like_a_number()
    {
        var f = ProfileFile.Load(
            "Preferences\nprefs.csv\nName,Value\nbluetooth_remote_address,123456789012\n");

        Assert.DoesNotContain(f.Issues, i => i.Message.Contains("too big for the device"));
    }
}
