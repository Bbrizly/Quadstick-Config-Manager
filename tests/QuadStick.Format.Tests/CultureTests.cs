using System.Globalization;
using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// A profile is bytes a device reads, not text for a person, so nothing in it
// may follow the language the computer is set to. A German machine formats
// 0.5 as "0,5", and a device handed that reads a different number or no number
// at all. This drives the real write and validate paths under a comma-decimal
// culture and demands the same answer as English.
public class CultureTests
{
    static void Under(string culture, Action body)
    {
        var uiWas = CultureInfo.CurrentUICulture;
        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            body();
        }
        finally { CultureInfo.CurrentCulture = was; CultureInfo.CurrentUICulture = uiWas; }
    }

    [Theory]
    [InlineData("default.csv")]
    [InlineData("device-style.csv")]
    [InlineData("gta-mode1.csv")]
    public void A_profile_is_written_the_same_in_any_language(string name)
    {
        var csv = File.ReadAllText(Path.Combine("corpus", name));
        string? english = null, german = null;
        Under("en-US", () => english = ProfileFile.Load(csv).ToCsvText());
        Under("de-DE", () => german = ProfileFile.Load(csv).ToCsvText());
        Assert.Equal(english, german);
    }

    [Fact]
    public void A_number_in_a_cell_reads_the_same_in_any_language()
    {
        var csv = File.ReadAllText(Path.Combine("corpus", "default.csv"));
        int english = 0, german = 0;
        Under("en-US", () => english = Validator.Validate(Parser.Parse(csv).Item1).Count);
        Under("de-DE", () => german = Validator.Validate(Parser.Parse(csv).Item1).Count);
        Assert.Equal(english, german);
    }
}
