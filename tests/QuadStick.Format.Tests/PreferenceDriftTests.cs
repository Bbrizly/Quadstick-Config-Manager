using System.Globalization;
using System.Linq;
using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// Twice now the English catalog has been rewritten shorter and the translated
// catalogs kept the old paragraph, so thirteen languages quietly went on saying
// something longer and out of date than any English reader saw. Nothing caught
// it: the coverage tests count keys, not freshness.
//
// A field that has drifted is several times the length of the one it
// translates, so measure each language against its own median expansion and
// fail the outliers. This runs in Release, unlike PreferenceTranslationTests,
// because these are the languages people actually read.
public class PreferenceDriftTests
{
    static IReadOnlyList<PreferenceDefinition> In(string culture)
    {
        var was = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            using var s = typeof(PreferenceCatalog).Assembly.GetManifestResourceStream("PreferencesJson")!;
            return PreferenceCatalog.TranslateForTest(PreferenceCatalog.Parse(s));
        }
        finally { CultureInfo.CurrentUICulture = was; }
    }

    [Theory]
    [InlineData("ar")] [InlineData("de")] [InlineData("es")] [InlineData("fr")]
    [InlineData("hi")] [InlineData("it")] [InlineData("ja")] [InlineData("ko")]
    [InlineData("nl")] [InlineData("pl")] [InlineData("pt")] [InlineData("zh-Hans")]
    public void A_translation_stays_the_length_of_the_line_it_translates(string tag)
    {
        var english = In("en");
        var said = In(tag);
        Assert.Equal(english.Count, said.Count);

        var ratios = new List<(double Ratio, string Where)>();
        for (var i = 0; i < english.Count; i++)
            foreach (var (e, s, field) in new[]
                     {
                         (english[i].Label, said[i].Label, "label"),
                         (english[i].Description, said[i].Description, "description"),
                         (english[i].Risk, said[i].Risk, "risk"),
                     })
                // A short string swings wildly on one word, so measure sentences.
                if (e.Length > 25 && s.Length > 0)
                    ratios.Add(((double)s.Length / e.Length, $"{english[i].Name}.{field}"));

        Assert.NotEmpty(ratios);
        var median = ratios.Select(r => r.Ratio).Order().ElementAt(ratios.Count / 2);
        // Two and a half times a language's own normal expansion is not a
        // translation of the same sentence.
        var drifted = ratios.Where(r => r.Ratio > median * 2.5)
                            .Select(r => $"{r.Where} runs {r.Ratio / median:0.0}x this language's median")
                            .ToList();
        Assert.True(drifted.Count == 0,
            $"{tag} still translates text that is no longer in preferences.json:\n  "
            + string.Join("\n  ", drifted));
    }
}
