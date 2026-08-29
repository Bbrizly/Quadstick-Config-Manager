using System.Collections;
using System.Linq;
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

    // Nine in ten is a floor for scattered gaps. It cannot see a whole screen
    // arriving untranslated, which is exactly what happened: the Device page
    // landed after the translation pass and its forty five strings were
    // English in all twelve languages while the count still read 96%.
    //
    // Keys are named for the screen they belong to, so a screen is a prefix.
    // A screen that is entirely missing from a language is a screen nobody in
    // that language can read, however healthy the total looks.
    [Theory]
    [MemberData(nameof(Shipped))]
    public void No_whole_screen_is_missing_from_a_language(string tag)
    {
        var culture = CultureInfo.GetCultureInfo(tag);
        var english = Strings.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!
            .Cast<DictionaryEntry>().Select(e => (string)e.Key).ToList();
        var said = Strings.ResourceManager.GetResourceSet(culture, true, false)!
            .Cast<DictionaryEntry>().Select(e => (string)e.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var screen in english.Select(Screen).Distinct())
        {
            var keys = english.Where(k => Screen(k) == screen).ToList();
            // A screen of one or two strings is a helper, not a screen.
            if (keys.Count < 5) continue;
            int have = keys.Count(said.Contains);
            Assert.True(have > 0, $"{tag}: the whole {screen} screen is missing, {keys.Count} strings");
            Assert.True(have >= keys.Count / 2,
                $"{tag}: {screen} has {have} of {keys.Count} strings");
        }
    }

    // Keys read Screen_WhatItSays, so the screen is everything up to the
    // first underscore.
    static string Screen(string key)
    {
        int cut = key.IndexOf('_');
        return cut < 0 ? key : key[..cut];
    }

    // The label class that slipped through the first translation pass: device
    // part names on the diagram, which is the screen the app is for. This one
    // is a long, plain phrase no language borrows, so equal-to-English here
    // means untranslated, not loanword.
    [Theory]
    [MemberData(nameof(Shipped))]
    public void A_listed_language_names_the_device_parts_in_its_own_words(string tag)
    {
        var said = Strings.ResourceManager.GetString(
            "Main_LeftMouthpieceHole", CultureInfo.GetCultureInfo(tag));
        Assert.NotNull(said);
        Assert.NotEqual(Strings.ResourceManager.GetString(
            "Main_LeftMouthpieceHole", CultureInfo.InvariantCulture), said);
    }

    [Theory]
    [MemberData(nameof(Shipped))]
    public void A_listed_language_translates_the_preference_catalog(string tag)
    {
        var have = typeof(PreferenceCatalog).Assembly.GetManifestResourceNames();
        Assert.Contains(have, n => string.Equals(n, "PreferencesJson." + tag, StringComparison.OrdinalIgnoreCase));
    }
}
