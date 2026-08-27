using System.Collections;
using System.Globalization;
using System.Resources;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// A row in Localization.Languages is a promise: pick this and the app answers
// in it. This holds each shipped tag to that. A translation import that lost a
// chunk, or a satellite assembly that never built, fails here instead of
// shipping as an app that says it speaks a language and then mostly does not.
public class ShippedLanguageTests
{
    public static TheoryData<string> Shipped()
    {
        var data = new TheoryData<string>();
        foreach (var (tag, _) in Localization.Languages)
            if (tag != "en" && tag != "qps-ploc") data.Add(tag);
        return data;
    }

    [Theory]
    [MemberData(nameof(Shipped))]
    public void A_listed_language_answers_in_its_own_words(string tag)
    {
        var culture = CultureInfo.GetCultureInfo(tag);
        foreach (var rm in new[] { Strings.ResourceManager, QuadStick.Format.Strings.ResourceManager })
        {
            // tryParents false: the satellite for this exact tag, or nothing.
            var set = rm.GetResourceSet(culture, true, false);
            Assert.NotNull(set);
            var said = set!.Cast<DictionaryEntry>().Count();
            var english = rm.GetResourceSet(CultureInfo.InvariantCulture, true, false)!
                .Cast<DictionaryEntry>().Count();
            // A missing line falls back to English on purpose, so not every
            // key has to be here. Nine in ten distinguishes a translation from
            // a stub that lost a whole chunk on the way in.
            Assert.True(said >= english * 9 / 10,
                $"{rm.BaseName} in {tag}: {said} of {english} strings");
        }
    }

    [Theory]
    [MemberData(nameof(Shipped))]
    public void A_listed_language_translates_the_preference_catalog(string tag)
    {
        var have = typeof(PreferenceCatalog).Assembly.GetManifestResourceNames();
        Assert.Contains(have, n => string.Equals(n, "PreferencesJson." + tag, StringComparison.OrdinalIgnoreCase));
    }
}
