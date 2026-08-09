using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// Reported by Steven F against 1.6.0, on his own preference sheets: three rows
// warned about, read as the app failing to understand his file.
//
// The app was right and the device is the reason. preference_keywords.h has no
// puff_threshold, puff_threshold_soft or puff_maximum; the sip and puff limits
// are one pair of settings named sip_puff_*. Load_Preferences_File matches a
// row's first cell against that table and does `if (preference_index < 0)
// continue`, so a name it does not hold is skipped and the setting never
// reaches the device.
//
// So the bug was the sentence, not the check: it said "not a preference this
// app knows", which is a report about the app, and it did not say the one
// useful thing, which is what he meant to type.
public class UnknownPreferenceNameTests
{
    const string Sheet = "Preferences\nprefs.csv\nName,Value\n"
        + "puff_threshold_soft,15\npuff_threshold,30\npuff_maximum,60\n";

    [Fact]
    public void The_warning_says_the_device_does_not_know_the_name_not_the_app()
    {
        var f = ProfileFile.Load(Sheet);

        var issue = Assert.Single(f.Issues, i => i.Cell == "A4");
        Assert.Contains("The QuadStick has no preference called \"puff_threshold_soft\"", issue.Message);
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
        var f = ProfileFile.Load(Sheet);

        var issue = Assert.Single(f.Issues, i => i.Cell == cell);
        Assert.Contains($"Did you mean \"{expected}\"?", issue.Fix);
    }

    // Newer firmware than this app is the reason an unknown name is a warning
    // and never an error, so the warning has to say so or it reads as a verdict.
    [Fact]
    public void It_still_allows_that_the_firmware_may_be_newer_than_the_app()
    {
        var f = ProfileFile.Load(Sheet);

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
