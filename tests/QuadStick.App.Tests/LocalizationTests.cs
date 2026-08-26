using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

// English written straight into a window is invisible until someone runs the
// app in another language and finds half of it still in English. This reads
// the source and refuses that, file by file, so the rest of the move can
// happen a file at a time without the finished ones sliding back.
public class LocalizationTests
{
    // Switched off (AgentFeature.Enabled is false) and out of the layout, or a
    // build tool nothing in the app links to. Translating text nobody can
    // reach buys nothing. The agent files come back to the list with the
    // feature.
    static readonly string[] NotShipped =
    {
        "AgentWindow.cs", "AgentGuide.cs", "AgentBridge.cs", "GalleryWindow.cs",
    };

    // Nothing left waiting. Any new file is held to the rule from its first
    // line, which is the point.
    static readonly string[] StillEnglish = Array.Empty<string>();

    // Text that is right to leave in English, with the reason it is right.
    static readonly Dictionary<string, string[]> Keep = new()
    {
        // A language names itself in its own words, so someone who opened the
        // app in a language they cannot read can still find their own.
        ["Localization.cs"] = new[] { "\"English\"", "\"Pseudo (finds missed text)\"" },
        // A company's name, not a word.
        ["SettingsWindow.cs"] = new[] { "\"LinkedIn\"" },
        // A Drive search query and an error for whoever is reading the log.
        ["DriveClient.cs"] = new[]
        {
            "\"mimeType='application/vnd.google-apps.spreadsheet' and trashed=false\"",
            "$\"Drive API returned {(int)status}: {body}\"",
        },
        // The page the browser lands on after signing in, around the message.
        ["GoogleAuth.cs"] = new[] { "$\"<!doctype html><html><body><p>{message}</p></body></html>\"" },
    };

    const string Literal = @"\$?@?""(?:[^""\\\n]|\\.)*""";

    // Assigned to something that draws or is spoken, or handed to one of this
    // app's own text helpers.
    static readonly Regex Sink = new(
        @"(?:\b(?:Text|Content|Title|Header|Watermark)\s*=\s*"
        + @"|(?:SetName|SetHelpText|SetTip)\([^,]+,\s*"
        + @"|\b(?:Field|Heading|Label|Caption|LinkButton|ShowHelp|ConfirmAsync|Status)\(\s*)"
        + "(" + Literal + ")");

    // Anything that reads like a sentence, wherever it sits. This is what
    // catches text passed to a helper the rule above has never heard of.
    static readonly Regex AnyLiteral = new(Literal);
    static readonly Regex Prose = new("[a-zA-Z] [a-z]");

    public static TheoryData<string> AppSources()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(SrcDir(), "*.cs"))
        {
            var name = Path.GetFileName(f);
            if (!NotShipped.Contains(name) && !StillEnglish.Contains(name)) data.Add(name);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AppSources))]
    public void A_translated_file_has_no_English_left_in_it(string name)
    {
        var keep = Keep.TryGetValue(name, out var k) ? k : Array.Empty<string>();
        var found = new List<string>();
        var lineNo = 0;
        foreach (var raw in File.ReadAllLines(Path.Combine(SrcDir(), name)))
        {
            lineNo++;
            var line = CodeOnly(raw);
            // An exception message is read by whoever is debugging, not by the
            // person using the app.
            if (line.Contains("throw ", StringComparison.Ordinal)) continue;

            foreach (var lit in Sink.Matches(line).Select(m => m.Groups[1].Value)
                         .Concat(AnyLiteral.Matches(line).Select(m => m.Value).Where(v => Prose.IsMatch(v)))
                         .Distinct())
                if (Regex.IsMatch(lit, "[A-Za-z]") && !keep.Contains(lit))
                    found.Add($"{name}:{lineNo}  {lit}");
        }

        Assert.True(found.Count == 0,
            "Move this text into Strings.resx, or say why it stays English in LocalizationTests.Keep:\n"
            + string.Join("\n", found.Distinct()));
    }

    [Fact]
    public void Every_counted_noun_has_a_wording_for_one_and_for_more()
    {
        var used = new HashSet<string>();
        foreach (var f in Directory.GetFiles(SrcDir(), "*.cs"))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"Plural\.(?:Of|Wording)\([^;]*?""([A-Za-z_]+)""\)"))
                used.Add(m.Groups[1].Value);

        Assert.NotEmpty(used);
        foreach (var prefix in used)
            foreach (var form in new[] { "one", "other" })
                Assert.True(Strings.ResourceManager.GetString($"{prefix}_{form}", CultureInfo.GetCultureInfo("en")) is not null,
                    $"Strings.resx has no {prefix}_{form}, so a count would print its own key.");
    }

    [Fact]
    public void A_language_the_build_does_not_carry_falls_back_instead_of_throwing()
    {
        // Apply changes the culture for the whole process, so put back what
        // the rest of the suite was running under.
        var was = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            Localization.Apply(Localization.FollowSystem);
            var system = CultureInfo.CurrentUICulture;
            // ICU will happily build a culture out of almost any tag, so this
            // has to be turned away by name, not by waiting for a throw.
            Localization.Apply("not-a-language");
            Assert.Equal(system, CultureInfo.CurrentUICulture);
        }
        finally { CultureInfo.DefaultThreadCurrentUICulture = was; }
    }

    [Fact]
    public void The_picker_and_the_stored_language_agree()
    {
        Assert.Equal(Localization.Choices().Length, Localization.Languages.Length + 1);
        Assert.Equal(Localization.FollowSystem, Localization.TagAt(0));
        for (var i = 0; i < Localization.Languages.Length; i++)
        {
            var tag = Localization.Languages[i].Tag;
            Assert.Equal(tag, Localization.TagAt(Localization.IndexOf(tag)));
        }
        // A tag from a newer build lands on "same as my computer", not out of range.
        Assert.Equal(0, Localization.IndexOf("xx"));
        // Every tag in the list has to be a language the runtime knows, or the
        // app throws on the way to its first window. The runtime writes the
        // region half in its own case, so only the letters have to match.
        foreach (var (tag, _) in Localization.Languages)
            Assert.Equal(tag, CultureInfo.GetCultureInfo(tag).Name, ignoreCase: true);
    }

#if DEBUG
    // Proves the whole chain in one go: a resx beside Strings.resx becomes a
    // satellite assembly, and asking for that language really does return its
    // text. A translation that lands and never shows would look exactly like
    // no translation at all.
    [Fact]
    public void Asking_for_a_language_returns_that_languages_text()
    {
        var pseudo = Strings.ResourceManager.GetString(nameof(Strings.Settings_Close), CultureInfo.GetCultureInfo("qps-ploc"));
        Assert.NotNull(pseudo);
        Assert.NotEqual(Strings.Settings_Close, pseudo);
        Assert.StartsWith("[", pseudo, StringComparison.Ordinal);
    }
#endif

    [Fact]
    public void The_chosen_language_survives_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            Settings.Save(new AppSettings { Language = "en" }, path);
            Assert.Equal("en", Settings.Load(path).Language);
            Assert.Equal(Localization.FollowSystem, new AppSettings().Language);
        }
        finally { File.Delete(path); }
    }

    // A // inside a string is not the start of a comment, and a comment that
    // quotes a sentence is not text the app shows.
    static string CodeOnly(string line)
    {
        bool inString = false, escaped = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (escaped) { escaped = false; continue; }
            if (line[i] == '\\' && inString) { escaped = true; continue; }
            if (line[i] == '"') { inString = !inString; continue; }
            if (!inString && line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/') return line[..i];
        }
        return line;
    }

    static string SrcDir([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "QuadStick.App"));
}
