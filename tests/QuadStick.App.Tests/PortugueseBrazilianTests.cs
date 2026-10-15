using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace QuadStick.App.Tests;

// The pt files are Brazilian Portuguese. These words are European only, and
// one of them in a string reads as a different dialect from its neighbours.
public class PortugueseBrazilianTests
{
    static readonly Regex European = new(
        @"\b(ficheiros?|partilh\w*|defini[cç](ão|ões)|predefini[cç]\w*|guard(ar|a|e|ad[oa]s?)|elimin(ar|ad[oa]s?)|"
        + @"rato|percentagem|prem(ir|ido|ida)|prima|controlos?|consola|gerir|ecrã|utilizador(es)?|registo|"
        + @"liga[cç](ão|ões)|demasiado|libertar|manípulo|telem[óo]vel|descarreg\w*|palavra-passe|separador(es)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Theory]
    [InlineData("QuadStick.App")]
    [InlineData("QuadStick.Format")]
    public void No_European_only_words(string project)
    {
        var path = Path.Combine(RepoRoot, "src", project, "Strings.pt.resx");
        var hits = XDocument.Load(path).Descendants("data")
            .SelectMany(d => European.Matches((string?)d.Element("value") ?? "")
                .Select(m => $"{(string?)d.Attribute("name")}: {m.Value}"))
            .ToList();
        Assert.True(hits.Count == 0, "European Portuguese in pt: " + string.Join("; ", hits.Take(20)));
    }

    static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "QuadStick.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
