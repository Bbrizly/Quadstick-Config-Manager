using System.Globalization;
using System.Resources;

// English is what Strings.resx holds, so a machine already running English
// loads no satellite assembly and does no lookup work.
[assembly: NeutralResourcesLanguage("en")]

namespace QuadStick.App;

/// <summary>Which language the interface speaks.</summary>
/// <remarks>
/// The device never sees any of this. Input, output and function names are
/// file bytes the firmware string-compares, so they stay in English whatever
/// the interface is set to, and a profile is written invariant either way.
/// </remarks>
public static class Localization
{
    public const string FollowSystem = "System";

    // Adding a language: drop Strings.<lang>.resx beside Strings.resx, add a
    // row here, and name it in its own words, so someone who opened the app in
    // a language they cannot read can still find their own. The list is
    // explicit rather than scanned off disk, so a half-finished translation
    // cannot ship by accident: a language appears here when a person who uses
    // a QuadStick has read it.
    public static readonly (string Tag, string Name)[] Languages =
    {
        ("en", "English"),
        ("ar", "العربية"),
        ("de", "Deutsch"),
        ("es", "Español"),
        ("fr", "Français"),
        ("hi", "हिन्दी"),
        ("it", "Italiano"),
        ("ja", "日本語"),
        ("ko", "한국어"),
        ("nl", "Nederlands"),
        ("pl", "Polski"),
        ("pt", "Português"),
        ("zh-Hans", "简体中文"),
#if DEBUG
        // Not a language. English, accented and padded out, built by
        // `make pseudo`. Run the app in it and anything still in plain English
        // is text that never made it into Strings.resx, and anything clipped is
        // a layout that assumed English is the widest it gets. Debug only: it
        // is a way of reading the app, not a way of using it.
        ("qps-ploc", "Pseudo (finds missed text)"),
#endif
    };

    /// <summary>Point resource lookups at a language. Runs before the first
    /// window, and again, followed by <see cref="Relocalize"/>, when the
    /// language is changed from Settings while the app is up.</summary>
    public static void Apply(string tag)
    {
        var c = Resolve(tag);
        CultureInfo.DefaultThreadCurrentUICulture = c;
        CultureInfo.CurrentUICulture = c;
        // CurrentCulture is deliberately left where the machine put it. Someone
        // reading the app in German on a Canadian machine still wants Canadian
        // dates and numbers, and nothing written to a profile reads it anyway.
    }

    // Only a language this build carries is honoured. A settings file can name
    // one it does not: hand-edited, or written by a newer version. That reads
    // as "same as my computer" rather than throwing on the way to the first
    // window. ICU accepts almost any well-formed tag as a culture, so asking it
    // to validate would not catch this.
    static CultureInfo Resolve(string tag) =>
        Array.Exists(Languages, l => l.Tag == tag) ? CultureInfo.GetCultureInfo(tag) : CultureInfo.InstalledUICulture;

    /// <summary>Rebuild everything that baked text in the old language: the
    /// preference catalog, the output picker's group names, and the window
    /// statics. Windows already on screen keep their words; the caller builds
    /// a new window after this and retires the old one.</summary>
    public static void Relocalize()
    {
        QuadStick.Format.PreferenceCatalog.Relocalize();
        OutputCatalog.Relocalize();
        MainWindow.RelocalizeStatics();
    }

    /// <summary>What the language picker lists. Row 0 follows the machine.</summary>
    public static string[] Choices()
    {
        var names = new string[Languages.Length + 1];
        names[0] = Strings.Settings_LanguageSystem;
        for (var i = 0; i < Languages.Length; i++) names[i + 1] = Languages[i].Name;
        return names;
    }

    public static string TagAt(int index) =>
        index <= 0 || index > Languages.Length ? FollowSystem : Languages[index - 1].Tag;

    public static int IndexOf(string tag)
    {
        var i = Array.FindIndex(Languages, l => l.Tag == tag);
        return i >= 0 ? i + 1 : 0;
    }
}
