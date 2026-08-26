#if DEBUG
using System.Globalization;
using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// The catalog is the biggest body of writing in the app, and the one where a
// mistranslation is most expensive: these sentences say what a setting does to
// somebody's hardware. This proves the two halves stay apart. The words a
// person reads come from the translation, and the name, the bounds and the
// default, which are what reach the device, never do.
//
// The catalog reads its language once, when it is first touched, so this runs
// the reader by hand rather than through the static PreferenceCatalog.All.
public class PreferenceTranslationTests
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

    [Fact]
    public void A_translation_changes_the_words_and_nothing_else()
    {
        var english = In("en");
        var said = In("qps-ploc");
        Assert.Equal(english.Count, said.Count);

        for (var i = 0; i < english.Count; i++)
        {
            var (e, s) = (english[i], said[i]);
            // An entry with nothing written for it has nothing to translate.
            Assert.NotEqual(e.Label, s.Label);
            if (e.Description.Length > 0) Assert.NotEqual(e.Description, s.Description);
            if (e.Risk.Length > 0) Assert.NotEqual(e.Risk, s.Risk);

            // Everything the device sees.
            Assert.Equal(e.Name, s.Name);
            Assert.Equal(e.Category, s.Category);
            Assert.Equal(e.Editor, s.Editor);
            Assert.Equal(e.Default, s.Default);
            Assert.Equal(e.Minimum, s.Minimum);
            Assert.Equal(e.Maximum, s.Maximum);
            Assert.Equal(e.Options, s.Options);
            Assert.Equal(e.Source, s.Source);
            Assert.Equal(e.ModeOverride, s.ModeOverride);
        }
    }

    [Fact]
    public void A_language_with_no_file_reads_as_English()
    {
        Assert.Equal(In("en").Select(d => d.Label), In("de").Select(d => d.Label));
    }

    [Fact]
    public void An_option_list_is_translated_whole_or_not_at_all()
    {
        foreach (var (e, s) in In("en").Zip(In("qps-ploc")))
            Assert.Equal(e.OptionLabels.Count, s.OptionLabels.Count);
    }
}
#endif
