using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// Copy share link, then paste that link into Import on Home. The user's own
// profile came back as "That spreadsheet has no profile tab".
//
// The backup pushed the whole file flat onto one worksheet, so cell A1 of that
// tab was the file's version header ("QuadStick Configuration"), not a sheet
// keyword. The workbook reader identifies a mode by A1 and nothing else, so it
// passed the only tab over and handed back an empty profile. The device would
// have read the same file happily; only the trip through a worksheet lost it.
//
// Two halves, both needed: the reader has to understand a flat tab (every sheet
// pushed by an older build still looks like that), and the writer now puts one
// mode per tab, which is what the community workbooks look like.
public class ShareImportRoundTripTests
{
    // What a saved profile looks like on disk: version header row, then the
    // template's mode.
    static ProfileFile Saved(string fileName = "mygame.csv")
    {
        var file = ProfileFile.NewFromTemplate(fileName);
        file.NormalizeForDeviceCsv();
        return file;
    }

    static string[][] Rows(IEnumerable<string[]> rows) => rows.ToArray();

    // The import path Home runs on a downloaded workbook.
    static ProfileFile Import(MemoryStream workbook) => ProfileFile.Load(Xlsx.ToCsv(workbook));

    [Fact]
    public void A_flat_tab_this_app_pushed_still_imports()
    {
        var saved = Saved();
        using var wb = TestWorkbook.Build(("Sheet1", Rows(saved.Grid)));

        var imported = Import(wb);

        Assert.NotEmpty(imported.Document.Sheets);
        Assert.Equal("mygame.csv", imported.Document.CsvFileName);
        Assert.Equal(saved.Document.Sheets[0].Bindings.Count, imported.Document.Sheets[0].Bindings.Count);
    }

    // The header row is the sheet id and the profile's own name. It is the
    // user's, so it comes back with them.
    [Fact]
    public void The_version_header_survives_the_round_trip()
    {
        var saved = Saved();
        using var wb = TestWorkbook.Build(("Sheet1", Rows(saved.Grid)));

        var imported = Import(wb);

        Assert.True(imported.Document.HasVersionHeader);
        Assert.Equal(saved.Document.HeaderVersion, imported.Document.HeaderVersion);
    }

    // What the share push writes now: one tab per sheet, titled with the mode.
    [Fact]
    public void One_tab_per_mode_imports_as_the_same_profile()
    {
        var saved = Saved();
        var tabs = SheetTabs.Split(saved);
        using var wb = TestWorkbook.Build(tabs.Select(t => (t.Title, Rows(t.Rows))).ToArray());

        var imported = Import(wb);

        Assert.Equal(saved.Document.Sheets.Count, imported.Document.Sheets.Count);
        Assert.Equal(
            saved.Document.Sheets.Select(s => s.ModeName),
            imported.Document.Sheets.Select(s => s.ModeName));
        // Cell for cell, not byte for byte: a spreadsheet drops the trailing
        // empty cells the template pads its rows out with, and so does every
        // real Google export.
        Assert.Equal(
            saved.Document.Sheets[0].Bindings.Select(b => (b.Output, b.Function, b.Inputs.Count)),
            imported.Document.Sheets[0].Bindings.Select(b => (b.Output, b.Function, b.Inputs.Count)));
    }

    [Fact]
    public void The_first_tab_carries_the_version_header()
    {
        var tabs = SheetTabs.Split(Saved());

        Assert.StartsWith("QuadStick Configuration", tabs[0].Rows[0][0]);
        Assert.Equal("Profile Name", tabs[0].Rows[1][0]);
    }

    // The device tells modes apart by position and two modes may share a name,
    // but two worksheet tabs may not. The second one gets a suffix rather than
    // losing the push.
    [Fact]
    public void Two_modes_with_one_name_get_two_tabs()
    {
        var file = ProfileFile.Load(
            "Profile Name,,Menu\r\nmygame.csv\r\nXBox Outputs,Function,usb\r\ndpad_N,normal,lip\r\n"
            + "\r\nProfile Name,,Menu\r\n,,\r\nXBox Outputs,Function,usb\r\ndpad_S,normal,lip\r\n");

        var tabs = SheetTabs.Split(file);

        Assert.Equal(new[] { "Menu", "Menu (2)" }, tabs.Select(t => t.Title));
    }

    [Fact]
    public void Preferences_and_infrared_are_named_after_themselves()
    {
        var file = ProfileFile.Load(
            "Profile Name,,Menu\r\nmygame.csv\r\nXBox Outputs,Function,usb\r\ndpad_N,normal,lip\r\n"
            + "\r\nPreferences\r\nsip_puff_delay_soft,130\r\n"
            + "\r\nInfrared\r\n");

        var tabs = SheetTabs.Split(file);

        Assert.Equal(new[] { "Menu", "Preferences", "Infrared" }, tabs.Select(t => t.Title));
    }

    // A file the parser makes nothing of still has to reach the backup whole.
    // The sheet is the user's only off-machine copy; a grid we cannot name is
    // not a grid we may drop.
    [Fact]
    public void A_grid_with_no_sheets_is_still_pushed_whole()
    {
        var file = ProfileFile.Load("x,circle\r\ny,cross\r\n");

        var tabs = SheetTabs.Split(file);

        var tab = Assert.Single(tabs);
        Assert.Equal(2, tab.Rows.Count);
    }
}
