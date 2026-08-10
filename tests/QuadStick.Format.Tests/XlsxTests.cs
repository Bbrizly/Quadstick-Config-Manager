using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// The fixtures are real community workbooks: one profile per mode tab
// (No Man's Sky: Main, Flight, Mouse, Preferences, plus hidden Inputs and
// Outputs reference tabs) and the flat single-tab style (Division 2).
public class XlsxTests
{
    static string ToCsv(string name)
    {
        using var stream = File.OpenRead(Path.Combine("corpus", name));
        return Xlsx.ToCsv(stream);
    }

    // Three tabs called Main, Flight and Mouse, and cell C1 on every one of
    // them still says "Mouse Mode" from whichever tab was copied first. The
    // names the user reads in their own spreadsheet are the tab titles, so
    // those are the names the modes get, and the import review says so.
    [Fact]
    public void EveryModeTabBecomesAMode()
    {
        var file = ProfileFile.Load(ToCsv("multi-tab.xlsx"));
        Assert.Equal(
            new[] { "Main", "Flight", "Mouse", "" },
            file.Document.Sheets.Select(s => s.ModeName).ToArray());
        Assert.Equal(
            new[] { SheetType.ProfileName, SheetType.ProfileName, SheetType.ProfileName, SheetType.Preferences },
            file.Document.Sheets.Select(s => s.Type).ToArray());
        // Tab order and content survive: the first tab's first binding, and a
        // binding only the third tab has.
        Assert.Equal("kb_w", file.Document.Sheets[0].Bindings[0].Output);
        Assert.Contains(file.Document.Sheets[2].Bindings, b => b.Output == "kb_keypad_1");
        // The name comes from the first tab, not from the copy-pasted one the
        // third tab still carries.
        Assert.Equal("nomanssky.csv", file.Document.CsvFileName);
    }

    [Fact]
    public void ReferenceTabsAreNotModes()
    {
        var csv = ToCsv("multi-tab.xlsx");
        Assert.DoesNotContain("Mouthpiece Hard Sip Left", csv); // the Inputs tab
        Assert.DoesNotContain("PS3 D-Pad Button North", csv);   // the Outputs tab
    }

    // Not modes, and not silent either. A user who can see two tabs in their own
    // workbook and only one list in the app has no way to tell a skipped
    // reference tab from a failed import, and reported the second as the first.
    [Fact]
    public void ReferenceTabsAreNamedAsHelpers()
    {
        using var stream = File.OpenRead(Path.Combine("corpus", "multi-tab.xlsx"));
        Xlsx.ToCsv(stream, out var skipped);

        Assert.Equal(new[] { "Inputs", "Outputs" }, skipped.Select(t => t.Name));
        Assert.All(skipped, t => Assert.Equal(SkippedTabKind.Helper, t.Kind));
    }

    // The tabs QMP keeps for its own use carry no bindings, so they are passed
    // over without a word. Naming every stray tab on every import is the noise
    // that teaches people to stop reading the one message that matters.
    [Fact]
    public void QmpsOwnMachineryTabsStaySilent()
    {
        using var stream = File.OpenRead(Path.Combine("corpus", "single-tab.xlsx"));
        Xlsx.ToCsv(stream, out var skipped);

        Assert.Empty(skipped); // allowedinputs, allowedoutputs, IRCommands
    }

    [Fact]
    public void PreferencesValuesAndCommentsSurvive()
    {
        var file = ProfileFile.Load(ToCsv("multi-tab.xlsx"));
        var prefs = file.Document.Sheets[^1];
        var row = Assert.Single(prefs.Bindings);
        Assert.Equal("sip_puff_delay_soft", row.Output);
        Assert.Equal("130", row.Function); // a number cell, not a formatted date
        // The 996 empty template rows below it are gone.
        Assert.True(file.Grid.Count < 210, $"grid still has {file.Grid.Count} rows");
    }

    [Fact]
    public void SingleTabWorkbookStillImports()
    {
        var file = ProfileFile.Load(ToCsv("single-tab.xlsx"));
        var sheet = Assert.Single(file.Document.Sheets);
        Assert.Equal("Keyboard & Mouse", sheet.ModeName);
        Assert.Equal("div2.csv", file.Document.CsvFileName);
        // Comments past column J are the user's notes; they must not be lost.
        Assert.Contains("inventory", file.Grid[3]);
    }

    [Fact]
    public void ImportedWorkbookIsValid()
    {
        foreach (var name in new[] { "multi-tab.xlsx", "single-tab.xlsx" })
        {
            var file = ProfileFile.Load(ToCsv(name));
            Assert.DoesNotContain(file.Issues, i => i.Severity == Severity.Error);
        }
    }

    [Fact]
    public void NotAWorkbookThrows()
    {
        using var stream = new MemoryStream("<html>nope</html>"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => Xlsx.ToCsv(stream));
    }

    [Fact]
    public void LinkImportAsksForTheWholeWorkbook()
    {
        // The gid names one tab, so the workbook export drops it.
        Assert.True(SheetsUrl.TryGetXlsxExportUrl(
            "https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/edit#gid=7", out var url));
        Assert.Equal("https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/export?format=xlsx", url);
    }
}
