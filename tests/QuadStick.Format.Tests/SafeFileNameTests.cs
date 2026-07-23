using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

public class SafeFileNameTests
{
    [Theory]
    [InlineData(null, "Untitled.csv")]
    [InlineData("", "Untitled.csv")]
    [InlineData("   ", "Untitled.csv")]
    [InlineData("...", "Untitled.csv")]
    public void Blank_or_dots_only_becomes_untitled(string? input, string expected)
        => Assert.Equal(expected, SafeFileName.ForCsv(input));

    [Fact]
    public void Normal_name_passes_through()
        => Assert.Equal("my profile.csv", SafeFileName.ForCsv("my profile"));

    [Fact]
    public void Invalid_chars_are_replaced()
        => Assert.Equal("My FPS _ v2.csv", SafeFileName.ForCsv("My FPS / v2"));

    [Theory]
    [InlineData("CON")]
    [InlineData("com1")]
    public void Reserved_windows_names_get_suffixed(string input)
        => Assert.Equal(input + "_file.csv", SafeFileName.ForCsv(input));

    [Fact]
    public void Trailing_dots_and_spaces_are_trimmed()
        => Assert.Equal("name.csv", SafeFileName.ForCsv("name.. "));

    [Fact]
    public void Overlong_name_is_capped()
    {
        var result = SafeFileName.ForCsv(new string('a', 200));
        Assert.Equal(new string('a', 100) + ".csv", result);
    }

    [Fact]
    public void Csv_extension_is_not_doubled()
        => Assert.Equal("name.csv", SafeFileName.ForCsv("name.csv"));

    [Fact]
    public void Csv_extension_case_is_handled()
        => Assert.Equal("name.csv", SafeFileName.ForCsv("name.CSV"));
}
