using System.Globalization;

namespace QuadStick.App;

// Every count in this app gets read aloud. "1 mode sheet(s)" is spoken as
// "one mode sheet open paren s close paren", which is the sort of thing that
// makes a screen reader user stop trusting the app to describe itself.
//
// The count and the noun cannot be glued together in code, because which
// wording a number takes is a fact about the language. Each noun is a set of
// resource keys, <prefix>_one and <prefix>_other, and the language picks.
static class Plural
{
    public static string Of(int n, string keyPrefix) =>
        string.Format(CultureInfo.CurrentCulture, Form(n, keyPrefix), n);

    static string Form(int n, string keyPrefix)
    {
        var rm = Strings.ResourceManager;
        var c = CultureInfo.CurrentUICulture;
        return rm.GetString($"{keyPrefix}_{Category(n, c)}", c)
            ?? rm.GetString($"{keyPrefix}_other", c)
            ?? keyPrefix;
    }

    // ponytail: one/other only, which covers the languages this app ships.
    // Polish and Russian want few/many and Arabic wants six; add the case here
    // and the extra keys to that language's resx when one of them ships.
    static string Category(int n, CultureInfo c) => c.TwoLetterISOLanguageName switch
    {
        "fr" => n is 0 or 1 ? "one" : "other",
        _ => n == 1 ? "one" : "other",
    };
}
