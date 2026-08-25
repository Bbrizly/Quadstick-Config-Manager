using Avalonia.Media;
using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

// The tile on a profile card is what makes one profile recognisable from
// across the room. It is derived from the name, so it has to be stable, always
// legible, and never blank.
public class CardTileTests
{
    [Fact]
    public void The_same_name_always_gets_the_same_colour()
    {
        // String.GetHashCode is randomised per process, so a card keyed on it
        // would change colour between runs and stop being recognisable.
        var first = MainWindow.TileColorFor("rocket-league");
        var second = MainWindow.TileColorFor("rocket-league");
        Assert.Equal(first, second);
        Assert.Equal(Color.FromRgb(0x7A, 0x2E, 0x2E), MainWindow.TileColorFor("rocket-league"));
        Assert.NotEqual(MainWindow.TileColorFor("gta"), MainWindow.TileColorFor("rocket-league"));
    }

    [Fact]
    public void Different_names_spread_across_the_palette()
    {
        var names = new[] { "gta", "rocket-league", "fortnite", "avatar", "fifa", "minecraft", "celeste", "halo" };
        var used = names.Select(MainWindow.TileColorFor).Distinct().Count();
        Assert.True(used >= 6, $"only {used} distinct colours across 8 names, cards would not be told apart");
    }

    [Theory]
    [InlineData("rocket-league", "RL")]
    [InlineData("gta", "GT")]
    [InlineData("My Game", "MG")]
    [InlineData("halo_infinite", "HI")]
    [InlineData("f", "F")]
    public void Initials_read_the_way_people_say_the_name(string name, string expected)
        => Assert.Equal(expected, MainWindow.InitialsFor(name));

    [Fact]
    public void A_name_with_no_letters_still_gets_a_tile()
        => Assert.False(string.IsNullOrWhiteSpace(MainWindow.InitialsFor("---")));

    // White text sits on every one of these, so each has to clear the 4.5:1
    // body-text floor or somebody's profile name is unreadable on its own card.
    [Fact]
    public void Every_tile_colour_carries_white_text()
    {
        foreach (var c in MainWindow.TileColors)
        {
            var ratio = Contrast(c, Colors.White);
            Assert.True(ratio >= 4.5, $"#{c.R:X2}{c.G:X2}{c.B:X2} is {ratio:F2}:1 against white");
        }
    }

    static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
