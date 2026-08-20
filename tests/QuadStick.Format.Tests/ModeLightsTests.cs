using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// The device says which mode it is in with five two-colour lights. These
// patterns are read off FW 2373 update_active_config_leds(), not off a manual:
// if the app draws a light the device would not light, someone reaches for a
// control that is not there.
public class ModeLightsTests
{
    static string Show(int mode) =>
        ModeLights.For(mode) is { } lights ? string.Concat(lights.Select(l => l switch
        {
            ModeLight.Purple => "P",
            ModeLight.Blue => "B",
            ModeLight.Red => "R",
            _ => ".",
        })) : "none";

    // "show purple leds for modes 1-5", walking left to right, one at a time.
    [Theory]
    [InlineData(1, "P....")]
    [InlineData(2, ".P...")]
    [InlineData(3, "..P..")]
    [InlineData(4, "...P.")]
    [InlineData(5, "....P")]
    public void The_first_five_modes_light_one_purple_light_each(int mode, string expected) =>
        Assert.Equal(expected, Show(mode));

    // Past five it is the rightmost light plus the walking one, then the
    // rightmost changes colour to mark each block of five.
    [Theory]
    [InlineData(6, "P...P")]
    [InlineData(9, "...PP")]
    [InlineData(10, "....B")]
    [InlineData(11, "P...B")]
    [InlineData(15, "....R")] // the firmware's own special case
    [InlineData(16, "P...R")]
    [InlineData(20, "BBBBB")]
    [InlineData(21, "PBBBB")]
    [InlineData(25, "BBBBP")]
    [InlineData(30, "RRRRP")]
    [InlineData(34, "RRRPR")]
    public void Later_modes_combine_lights(int mode, string expected) =>
        Assert.Equal(expected, Show(mode));

    // The firmware's pattern table runs out at 34 and reads off its own end
    // after that. An app that guessed here would be making the device up.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(35)]
    [InlineData(99)]
    public void There_is_no_pattern_outside_the_firmware_table(int mode) =>
        Assert.Null(ModeLights.For(mode));

    [Fact]
    public void Every_pattern_is_said_in_words_too()
    {
        Assert.Equal("light 3 purple", ModeLights.Describe(ModeLights.For(3)!));
        Assert.Equal("lights 1 and 5 purple", ModeLights.Describe(ModeLights.For(6)!));
        Assert.Equal("light 5 blue", ModeLights.Describe(ModeLights.For(10)!));
        Assert.Equal("light 1 purple, lights 2, 3, 4 and 5 blue",
                     ModeLights.Describe(ModeLights.For(21)!));
    }
}
