// tests/QuadStick.Format.Tests/PaletteContrastTests.cs
using QuadStick.App;
using Xunit;

namespace QuadStick.Format.Tests;

public class PaletteContrastTests
{
    [Fact]
    public void KnownRatio_BlackOnWhite_Is21()
        => Assert.True(Math.Abs(Contrast.Ratio("#000000", "#FFFFFF") - 21.0) < 0.05);

    [Theory]
    [MemberData(nameof(Themes))]
    public void EveryTokenPair_MeetsAA(string theme, string fgKey, string bgKey, double min)
    {
        var map = theme == "light" ? Palette.Light : Palette.Dark;
        var ratio = Contrast.Ratio(map[fgKey], map[bgKey]);
        Assert.True(ratio >= min,
            $"{theme}: {fgKey} on {bgKey} = {ratio:F2}, need {min}");
    }

    public static IEnumerable<object[]> Themes()
    {
        foreach (var theme in new[] { "light", "dark" })
            foreach (var (fg, bg, min) in Palette.Pairs)
                yield return new object[] { theme, fg, bg, min };
    }
}
