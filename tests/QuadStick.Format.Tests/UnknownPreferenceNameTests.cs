using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// Reported by Steven F against 1.6.0, on his own preference sheets: three rows
// warned about, read as the app failing to understand his file.
//
// He was right and the app was behind. On 2026-08-09 Fred posted the firmware
// that splits the sip and puff limits apart: sip_threshold_soft, sip_threshold,
// sip_maximum, puff_threshold_soft, puff_threshold and puff_maximum, all
// defaulting to 0, where 0 leaves the older sip_puff_* setting in charge. The
// firmware snapshot under corpus/ is 1476, which predates all six, so the only
// thing the app could see was a name its table did not hold.
//
// Two things came out of it. The six names are catalogued, so his sheet is
// quiet. And the warning for a name that really is unknown says the device does
// not hold it, allows out loud that the firmware may be newer than the app, and
// never rewrites the row.
public class UnknownPreferenceNameTests
{
    // Steven F's sheet, verbatim.
    const string SplitSipAndPuff = "Preferences\nprefs.csv\nName,Value\n"
        + "puff_threshold_soft,15\npuff_threshold,30\npuff_maximum,60\n";

    // Names the device has never had, under any firmware.
    const string Unknown = "Preferences\nprefs.csv\nName,Value\n"
        + "sip_puff_threshod_soft,15\nsip_puff_threshld,30\nsip_puff_maximm,60\n";

    [Fact]
    public void The_six_split_sip_and_puff_names_are_not_warned_about()
    {
        var f = ProfileFile.Load(SplitSipAndPuff);

        Assert.DoesNotContain(f.Issues, i => i.Message.Contains("no preference called"));
    }

    [Theory]
    [InlineData("sip_threshold_soft")]
    [InlineData("sip_threshold")]
    [InlineData("sip_maximum")]
    [InlineData("puff_threshold_soft")]
    [InlineData("puff_threshold")]
    [InlineData("puff_maximum")]
    public void Each_one_is_a_percent_that_a_mode_may_override(string name)
    {
        Assert.True(PreferenceCatalog.TryGet(name, out var p));
        Assert.Equal(PreferenceEditor.Integer, p.Editor);
        Assert.Equal("percent", p.Unit);
        // 0 is the value that hands the setting back to sip_puff_*, so it has
        // to be reachable.
        Assert.Equal("0", p.Default);
        Assert.True(p.ModeOverride);
        Assert.True(Vocab.PreferenceOverrides.Contains(name));
    }

    [Fact]
    public void The_warning_says_the_device_does_not_know_the_name_not_the_app()
    {
        var f = ProfileFile.Load(Unknown);

        var issue = Assert.Single(f.Issues, i => i.Cell == "A4");
        Assert.Contains("The QuadStick has no preference called \"sip_puff_threshod_soft\"", issue.Message);
        Assert.Contains("skips this row", issue.Message);
        // The row itself is never touched, whatever we think of the name.
        Assert.Contains("saved exactly as you wrote it", issue.Message);
        Assert.DoesNotContain("this app knows", issue.Message);
    }

    [Theory]
    [InlineData("A4", "sip_puff_threshold_soft")]
    [InlineData("A5", "sip_puff_threshold")]
    [InlineData("A6", "sip_puff_maximum")]
    public void It_names_the_preference_that_was_probably_meant(string cell, string expected)
    {
        var f = ProfileFile.Load(Unknown);

        var issue = Assert.Single(f.Issues, i => i.Cell == cell);
        Assert.Contains($"Did you mean \"{expected}\"?", issue.Fix);
    }

    // Newer firmware than this app is the reason an unknown name is a warning
    // and never an error, so the warning has to say so or it reads as a verdict.
    // Steven F's sheet is what that sentence is for.
    [Fact]
    public void It_still_allows_that_the_firmware_may_be_newer_than_the_app()
    {
        var f = ProfileFile.Load(Unknown);

        // The fourth issue on this sheet is the file's own name, not a row.
        var names = f.Issues.Where(i => i.Message.Contains("no preference called")).ToList();
        Assert.Equal(3, names.Count);
        Assert.All(names, i => Assert.Contains("newer than this app", i.Fix));
        Assert.All(names, i => Assert.Equal(Severity.Warning, i.Severity));
    }

    // A guess is only worth printing while it cannot be a different real
    // setting. These are the pairs close enough to reach each other.
    [Theory]
    [InlineData("joystick_alarm", "joystick_warning")]
    [InlineData("volume", "brightness")]
    [InlineData("lip_position_minimum", "lip_position_maximum")]
    [InlineData("deflection_multiplier_up", "deflection_multiplier_down")]
    // The split pair are one word apart, which is the distance a whole real
    // setting sits at. Neither may ever be offered for the other.
    [InlineData("sip_threshold", "puff_threshold")]
    [InlineData("sip_maximum", "puff_maximum")]
    public void One_real_preference_is_never_offered_as_a_guess_at_another(string typed, string other)
    {
        // Typed as it stands it is known, so nothing is guessed at all.
        Assert.Null(PreferenceCatalog.Closest(typed));
        // And the two never reach each other from one edit away either.
        Assert.NotEqual(other, PreferenceCatalog.Closest(typed + "x"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("zzzzzzzzzzzz")]
    [InlineData("my own setting")]
    // Three letters out is a guess, not a correction. "brightless" is offered
    // "brightness" and "brihtles" is offered nothing, because past two edits the
    // app would be putting a word in somebody's mouth that the device would then
    // read as a setting they never asked for.
    [InlineData("brihtles")]
    [InlineData("mouse_curve")]
    public void Nothing_is_offered_when_nothing_is_close(string typed) =>
        Assert.Null(PreferenceCatalog.Closest(typed));

    [Theory]
    [InlineData("brightless", "brightness")]
    [InlineData("volme", "volume")]
    [InlineData("joystik_alarm", "joystick_alarm")]
    public void A_plain_typo_is_still_offered_the_right_name(string typed, string expected) =>
        Assert.Equal(expected, PreferenceCatalog.Closest(typed));

    [Fact]
    public void A_name_the_device_does_know_says_nothing_at_all()
    {
        var f = ProfileFile.Load("Preferences\nprefs.csv\nName,Value\nsip_puff_threshold,30\n");

        Assert.DoesNotContain(f.Issues, i => i.Message.Contains("no preference called"));
    }
}
