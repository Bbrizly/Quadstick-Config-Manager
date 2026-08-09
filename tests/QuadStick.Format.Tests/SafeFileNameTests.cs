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

    // The device stores each root file name in a 31 character slot, so the
    // whole name including ".csv" has to fit in 31: 27 characters of base name.
    [Fact]
    public void Overlong_name_is_capped()
    {
        var result = SafeFileName.ForCsv(new string('a', 200));
        Assert.Equal(new string('a', 27) + ".csv", result);
        Assert.Equal(31, result.Length);
    }

    // The exact boundary, both sides of it. 27 characters of base name is the
    // longest that survives untouched.
    [Fact]
    public void Name_that_exactly_fills_the_device_slot_is_left_alone()
    {
        var result = SafeFileName.ForCsv(new string('a', 27));
        Assert.Equal(new string('a', 27) + ".csv", result);
        Assert.False(SafeFileName.IsTooLongForDevice(result));
    }

    [Fact]
    public void Name_one_character_over_is_cut_to_fit()
    {
        var result = SafeFileName.ForCsv(new string('a', 28));
        Assert.Equal(new string('a', 27) + ".csv", result);
    }

    // ".csv" is part of the 31, not on top of it. A name that already ends in
    // .csv must not be measured without its extension and come back at 35.
    [Fact]
    public void The_csv_extension_counts_towards_the_limit()
    {
        var result = SafeFileName.ForCsv(new string('a', 31) + ".csv");
        Assert.Equal(31, result.Length);
        Assert.Equal(new string('a', 27) + ".csv", result);
    }

    [Theory]
    [InlineData(30, false)]
    [InlineData(31, false)]
    [InlineData(32, true)]
    public void Too_long_for_device_starts_at_32(int length, bool tooLong)
        => Assert.Equal(tooLong, SafeFileName.IsTooLongForDevice(new string('a', length)));

    // The reason the cap needs a dedupe at all: at 27 characters two different
    // sheets collide where at 100 they did not, and the second import would
    // have overwritten the first.
    [Fact]
    public void Two_long_names_with_the_same_first_27_chars_do_not_collide()
    {
        var taken = new List<string>();
        var first = SafeFileName.ForCsv("Halo Infinite campaign settings v1", taken);
        taken.Add(first);
        var second = SafeFileName.ForCsv("Halo Infinite campaign settings v2", taken);

        Assert.NotEqual(first, second);
        Assert.False(SafeFileName.IsTooLongForDevice(second));
    }

    // The number has to be paid for out of the 27, not added after it.
    [Fact]
    public void Deduped_names_still_fit_the_device()
    {
        var taken = new List<string>();
        for (int i = 0; i < 12; i++)
        {
            var name = SafeFileName.ForCsv(new string('a', 40), taken);
            Assert.False(SafeFileName.IsTooLongForDevice(name), $"{name} is {name.Length} characters");
            Assert.DoesNotContain(name, taken);
            taken.Add(name);
        }
    }

    // The device lowercases every name before it stores or compares it, so
    // "Game.csv" and "game.csv" are one file there.
    [Fact]
    public void Dedupe_ignores_case()
    {
        var second = SafeFileName.ForCsv("game", new[] { "GAME.csv" });
        Assert.Equal("game (2).csv", second);
    }

    [Fact]
    public void A_free_name_is_not_numbered()
        => Assert.Equal("game.csv", SafeFileName.ForCsv("game", new[] { "other.csv" }));

    [Fact]
    public void Csv_extension_is_not_doubled()
        => Assert.Equal("name.csv", SafeFileName.ForCsv("name.csv"));

    [Fact]
    public void Csv_extension_case_is_handled()
        => Assert.Equal("name.csv", SafeFileName.ForCsv("name.CSV"));
}
