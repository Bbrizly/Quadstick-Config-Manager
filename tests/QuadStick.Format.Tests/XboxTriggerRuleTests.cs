using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// USB emulation modes 2 and 3 are the only two whose report has no L2/R2
// switch bit: DataFlow.c:2721 and :2768 set left_trigger_axis from
// ps3.press_L2 and stop there, while :2659, :2792 and :2871 also copy ps3.L2
// across as a button. press_L2 is the sip's pressure (DataFlow.c:2153, value
// >> 2) and a sip that only just crosses the threshold is 4 (DataFlow.c:368),
// so on Xbox the row arrives as 1 of 255 and reads as a dead trigger while the
// same file works on every other mode. The file cannot show that, so the app
// has to.
public class XboxTriggerRuleTests
{
    const string Mode = "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\n";

    static List<Issue> Load(string csv) => ProfileFile.Load(csv).Issues;

    static Issue? Trigger(List<Issue> issues) =>
        issues.FirstOrDefault(i => i.Message.Contains("pressure axis"));

    [Theory]
    [InlineData("2")]
    [InlineData("3")]
    public void An_xbox_mode_with_a_trigger_row_says_the_trigger_is_an_axis(string mode)
    {
        var issues = Load(
            $"Preferences\nprefs.csv\nName,Value\nenable_DS3_emulation,{mode}\n"
            + Mode + "left_trigger,normal,right_sip\n");

        var issue = Assert.Single(issues, i => i.Message.Contains("pressure axis"));
        Assert.Equal(Severity.Warning, issue.Severity);
        Assert.Contains("left_trigger", issue.Message);
        Assert.Contains("sip_puff_maximum", issue.Fix);
    }

    // left_2 is the same channel under its PlayStation spelling, so hiding one
    // spelling would leave half the files unwarned.
    [Fact]
    public void The_playstation_spelling_of_the_trigger_counts_too()
    {
        var issues = Load(
            "Preferences\nprefs.csv\nName,Value\nenable_DS3_emulation,3\n"
            + Mode + "right_2,normal,right_puff\n");

        Assert.NotNull(Trigger(issues));
    }

    // Every other mode sends the button as well, so the row works and the
    // warning would be noise.
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("4")]
    [InlineData("5")]
    [InlineData("7")]
    public void A_mode_that_still_sends_the_button_says_nothing(string mode)
    {
        var issues = Load(
            $"Preferences\nprefs.csv\nName,Value\nenable_DS3_emulation,{mode}\n"
            + Mode + "left_trigger,normal,right_sip\n");

        Assert.Null(Trigger(issues));
    }

    // A file that never sets the mode leaves it to whatever the device booted
    // with, which this app cannot see.
    [Fact]
    public void A_file_that_does_not_set_the_mode_says_nothing()
    {
        Assert.Null(Trigger(Load(Mode + "left_trigger,normal,right_sip\n")));
    }

    [Fact]
    public void An_xbox_mode_with_no_trigger_row_says_nothing()
    {
        var issues = Load(
            "Preferences\nprefs.csv\nName,Value\nenable_DS3_emulation,3\n"
            + Mode + "x,normal,right_sip\n");

        Assert.Null(Trigger(issues));
    }

    // A mode sets the emulation from its own rows too, and the device applies
    // that on the way into the mode, so it has to beat the Preferences sheet.
    [Fact]
    public void A_modes_own_override_beats_the_preferences_sheet()
    {
        var withOverride = Load(
            "Preferences\nprefs.csv\nName,Value\nenable_DS3_emulation,0\n"
            + Mode + "enable_DS3_emulation,,3\nleft_trigger,normal,right_sip\n");
        Assert.NotNull(Trigger(withOverride));

        var backToPlayStation = Load(
            "Preferences\nprefs.csv\nName,Value\nenable_DS3_emulation,3\n"
            + Mode + "enable_DS3_emulation,,0\nleft_trigger,normal,right_sip\n");
        Assert.Null(Trigger(backToPlayStation));
    }

    // One line per mode, not one per row: a mode with both triggers bound is
    // one fact about the mode, and saying it twice teaches people to scroll.
    [Fact]
    public void Both_triggers_in_one_mode_are_one_line()
    {
        var issues = Load(
            "Preferences\nprefs.csv\nName,Value\nenable_DS3_emulation,3\n"
            + Mode + "left_trigger,normal,right_sip\nright_trigger,normal,right_puff\n");

        Assert.Single(issues, i => i.Message.Contains("pressure axis"));
    }
}

// The "no drive is plugged in" message used to carry its own copy of the list
// and named only mode 6, so somebody stuck in DS3 mode read a message about a
// mode they were not on. It reads the rule now, and this holds the two together.
public class NoDriveModeListTests
{
    [Fact]
    public void The_list_is_every_mode_that_hides_the_drive()
    {
        // 3 is here on Joystick.c:399, not on its descriptor. X360_t declares a
        // mass-storage interface like the safe modes do, but the main loop calls
        // MS_Device_USBTask for 0, 2 and 4 only, so mode 3 offers the computer a
        // drive and then answers nothing.
        Assert.Equal(new[] { 1, 3, 5, 6, 7 }, Validator.ModesWithNoDrive);
    }


    [Theory]
    [InlineData("1")]
    [InlineData("5")]
    [InlineData("6")]
    [InlineData("7")]
    public void A_mode_on_the_list_is_one_the_rule_also_calls_driveless(string mode)
    {
        Assert.False(Validator.EmulationKeepsTheDrive(mode));
    }
}
