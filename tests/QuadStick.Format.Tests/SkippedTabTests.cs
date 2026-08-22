using System.IO.Compression;
using System.Text;
using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// From a real user's workbook on 2026-07-30. Five tabs, four imported. The
// "Dpad" tab held 13 valid dpad_* bindings and never arrived, because somebody
// had pasted a line of chat over cell A1 and the importer identifies a mode by
// A1 alone. Nothing on screen said a tab had been left out, so the profile just
// quietly came in short a mode.
//
// Skipping the tab is right (the device reads A1 the same way). Saying nothing
// is not.
public class SkippedTabTests
{
    static MemoryStream Workbook(params (string Tab, string[][] Rows)[] tabs) => TestWorkbook.Build(tabs);

    static string Esc(string s) => TestWorkbook.Esc(s);

    static string[][] ModeRows(string a1) =>
    [
        [a1, "", "Some mode"],
        ["mygame.csv"],
        ["XBox Outputs", "Function", "usb"],
        ["dpad_N", "normal", "mp_center_puff"],
        ["dpad_E", "normal", "mp_right_center_puff"],
        ["dpad_S", "normal", "mp_center_sip"],
        ["dpad_W", "normal", "mp_left_center_puff"],
    ];

    [Fact]
    public void A_tab_of_bindings_whose_A1_was_overwritten_is_named_not_swallowed()
    {
        using var wb = Workbook(
            ("Left Analog", ModeRows("Profile Name")),
            ("Dpad", ModeRows("Those are pretty cool. Sorry I'm a little busy right now.")));

        var csv = Xlsx.ToCsv(wb, out var skipped);

        Assert.Equal(new[] { "Dpad" }, skipped.Select(t => t.Name));
        // Still skipped: the device identifies a mode by A1 too.
        Assert.Single(ProfileFile.Load(csv).Document.Sheets);
    }

    // The same loss, one size down. A small mode (a menu, a voice layer) is two
    // bindings, and three was the count that decided a tab was worth naming, so
    // a two binding tab whose A1 was overwritten went the one way that is never
    // allowed: dropped, and the review still said the sheet came in clean.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_small_tab_of_bindings_is_named_too(int bindings)
    {
        string[][] rows =
        [
            ["Chat pasted over this cell", "", "Menu"],
            .. ModeRows("Profile Name")[3..(3 + bindings)],
        ];

        using var wb = Workbook(("Left Analog", ModeRows("Profile Name")), ("Menu", rows));

        Xlsx.ToCsv(wb, out var skipped);

        var tab = Assert.Single(skipped);
        Assert.Equal("Menu", tab.Name);
        Assert.Equal(SkippedTabKind.UnreadableA1, tab.Kind);
    }

    [Fact]
    public void A_workbook_where_every_tab_is_a_mode_reports_nothing()
    {
        using var wb = Workbook(
            ("Left Analog", ModeRows("Profile Name")),
            ("Mouse", ModeRows("Profile Name")));

        Xlsx.ToCsv(wb, out var skipped);

        Assert.Empty(skipped);
    }

    // A reference card, a scratch tab, a colour key: not somebody's lost mode,
    // and naming them on every import is noise that teaches the user to ignore
    // the message.
    [Fact]
    public void A_tab_that_is_not_bindings_is_not_reported()
    {
        using var wb = Workbook(
            ("Left Analog", ModeRows("Profile Name")),
            ("Sip Puff Reference Card",
            [
                ["Sip Puff Reference Card"],
                ["Tube:", "Left", "Left+Center", "Center"],
                ["Soft Puff:", "", "", "Dpad Up"],
                ["Puff:", "Touchpad", "[]", "R2"],
            ]));

        Xlsx.ToCsv(wb, out var skipped);

        Assert.Empty(skipped);
    }

    // The named helper tabs are documentation, and their A1 says so too.
    // They used to be passed over in silence, which is what a real user hit on
    // 2026-08-01: a workbook of Left Analog, Drive and Reference Card, reported
    // as "second sheet and reference card sheet will not import". The Reference
    // Card had never been importable and the app had never said so, so a
    // correct import read as a broken one. Named now, as a fact and not as a
    // decision.
    [Fact]
    public void The_known_helper_tabs_are_reported_as_helpers()
    {
        using var wb = Workbook(
            ("Left Analog", ModeRows("Profile Name")),
            ("Outputs", ModeRows("PS3 D-Pad Button North")));

        Xlsx.ToCsv(wb, out var skipped);

        var tab = Assert.Single(skipped);
        Assert.Equal("Outputs", tab.Name);
        Assert.Equal(SkippedTabKind.Helper, tab.Kind);
    }

    // The tab name is a hint, A1 is the truth. "Voice" is a fine name for a
    // mode, and the device would read this one: the CSV has nowhere to put a
    // tab name, so by the time the file reaches the QuadStick the title is
    // gone. Deciding by the title alone threw the mode away and said in the
    // same breath that nothing had been lost.
    [Fact]
    public void A_mode_titled_like_a_helper_tab_still_imports()
    {
        using var wb = Workbook(
            ("Left Analog", ModeRows("Profile Name")),
            ("Voice", ModeRows("Profile Name")));

        var file = ProfileFile.Load(Xlsx.ToCsv(wb, out var skipped));

        Assert.Equal(2, file.Document.Sheets.Count);
        Assert.Empty(skipped);
    }

    // A helper tab is named, never offered. Its cells are dropped rather than
    // kept: there is nothing to repair, and counting them would spend the
    // workbook's row budget on documentation and could truncate a real mode
    // further down.
    [Fact]
    public void A_helper_tab_carries_no_cells_and_cannot_be_repaired_into_a_mode()
    {
        using var wb = Workbook(
            ("Left Analog", ModeRows("Profile Name")),
            ("Reference Card", ModeRows("Reference Card")));

        Xlsx.ToCsv(wb, out var skipped);

        var tab = Assert.Single(skipped);
        Assert.Empty(tab.Rows);
        Assert.Empty(Xlsx.RepairedAsMode(tab));
    }

    // The two kinds travel together and stay apart. One is a mode the user has
    // lost and can take back; the other never was one.
    [Fact]
    public void A_lost_mode_and_a_helper_tab_are_reported_as_different_kinds()
    {
        using var wb = Workbook(
            ("Left Analog", ModeRows("Profile Name")),
            ("Dpad", ModeRows("pasted chat over A1")),
            ("Reference Card", ModeRows("Reference Card")));

        Xlsx.ToCsv(wb, out var skipped);

        Assert.Equal(
            new[] { ("Dpad", SkippedTabKind.UnreadableA1), ("Reference Card", SkippedTabKind.Helper) },
            skipped.Select(t => (t.Name, t.Kind)));
    }

    // The shape of the real workbook from the report: three tabs, two of them
    // modes that happen to share a name, one of them the reference card. Both
    // modes come in. Only the card is skipped, and it is named.
    [Fact]
    public void The_reported_workbook_shape_imports_both_modes_and_names_the_card()
    {
        using var wb = Workbook(
            ("Left Analog", ModeRows("Profile Name")),
            ("Drive", ModeRows("Profile Name")),
            ("Reference Card", ModeRows("Reference Card")));

        var file = ProfileFile.Load(Xlsx.ToCsv(wb, out var skipped));

        Assert.Equal(2, file.Document.Sheets.Count);
        // Both tabs carry the same copy-pasted C1, so both take their own tab's
        // name instead of coming in as two modes called the same thing.
        Assert.Equal(new[] { "Left Analog", "Drive" }, file.Document.Sheets.Select(s => s.ModeName));
        Assert.Equal(new[] { "Reference Card" }, skipped.Select(t => t.Name));
    }

    // The bounds are right: a 25 KB download really can expand into gigabytes,
    // and this import runs on the UI thread. Stopping without a word is what
    // was wrong. A profile that arrives short four of its five modes was being
    // called a clean import, which is the worst thing this window can say.
    [Fact]
    public void A_workbook_of_more_tabs_than_we_read_says_it_stopped()
    {
        var tabs = Enumerable.Range(1, 66)
            .Select(i => ($"Mode {i}", ModeRows("Profile Name")))
            .ToArray();
        using var wb = Workbook(tabs);

        var file = ProfileFile.Load(Xlsx.ToCsv(wb, out _, out var limitation));

        Assert.Equal(64, file.Document.Sheets.Count);
        Assert.NotNull(limitation);
        Assert.Contains("more than 64 tabs", limitation);
        Assert.Contains("not read at all", limitation);
    }

    [Fact]
    public void A_workbook_of_more_rows_than_we_read_says_it_stopped()
    {
        // Two tabs past the workbook's row budget between them, and a third
        // that is never reached at all.
        var fat = ModeRows("Profile Name")
            .Concat(Enumerable.Repeat(new[] { "dpad_N", "normal", "mp_center_puff" }, 18_000))
            .ToArray();
        using var wb = Workbook(("One", fat), ("Two", fat), ("Three", ModeRows("Profile Name")));

        Xlsx.ToCsv(wb, out _, out var limitation);

        Assert.NotNull(limitation);
        Assert.Contains("30,000 rows", limitation);
        Assert.Contains("One more tab was not read at all", limitation);
    }

    // Asking A1 first means the part is opened before we know it is a helper,
    // and every workbook QMP writes carries a Reference Card. A corrupt or
    // enormous one would have cost the user every mode in the file over a tab
    // that was never going to import.
    [Fact]
    public void A_helper_tab_that_will_not_open_does_not_sink_the_import()
    {
        using var wb = BrokenSecondTab("Reference Card");

        var file = ProfileFile.Load(Xlsx.ToCsv(wb, out var skipped, out _));

        Assert.Single(file.Document.Sheets);
        var tab = Assert.Single(skipped);
        Assert.Equal(SkippedTabKind.Helper, tab.Kind);
    }

    // The same tab under any other name might have been a mode, and quietly
    // taking a mode is what this whole file is about. That one still stops the
    // import, which is what the caller already handles.
    [Fact]
    public void A_mode_tab_that_will_not_open_still_stops_the_import()
    {
        using var wb = BrokenSecondTab("Dpad");

        Assert.Throws<InvalidDataException>(() => Xlsx.ToCsv(wb));
    }

    // A workbook whose second worksheet part is not XML at all.
    static MemoryStream BrokenSecondTab(string name)
    {
        var ms = Workbook(
            ("Left Analog", ModeRows("Profile Name")),
            (name, ModeRows("Profile Name")));
        var bytes = ms.ToArray();
        var rebuilt = new MemoryStream(bytes.Length);
        rebuilt.Write(bytes);
        rebuilt.Position = 0;
        using (var zip = new ZipArchive(rebuilt, ZipArchiveMode.Update, leaveOpen: true))
        {
            zip.GetEntry("xl/worksheets/sheet2.xml")!.Delete();
            using var w = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet2.xml").Open());
            w.Write("<worksheet><sheetData><row>");  // never closed
        }
        rebuilt.Position = 0;
        return rebuilt;
    }

    // A cell holding something below the row this reader stops at. Not a shape
    // a real profile has, and still a read that did not finish, so it says so.
    // A stray blank cell at the bottom of a Google sheet is dropped the same way
    // and must not say anything, or every ordinary import would claim to be
    // partial.
    [Fact]
    public void A_cell_below_the_last_row_we_read_is_reported_and_a_blank_one_is_not()
    {
        using var loud = Workbook(("Left Analog", Deep("dpad_N")));
        Xlsx.ToCsv(loud, out _, out var said);
        Assert.NotNull(said);
        Assert.Contains("below row 20,000", said);

        using var quiet = Workbook(("Left Analog", Deep("")));
        Xlsx.ToCsv(quiet, out _, out var silent);
        Assert.Null(silent);
    }

    // A mode, then one cell at row 30,000 holding whatever is passed in.
    static string[][] Deep(string value)
    {
        var rows = ModeRows("Profile Name").ToList();
        while (rows.Count < 29_999) rows.Add(Array.Empty<string>());
        rows.Add(new[] { value });
        return rows.ToArray();
    }

    // The bound has to hold when it costs nothing to say so, or the message
    // becomes noise: a workbook that fits is not a partial read.
    [Fact]
    public void A_workbook_that_fits_reports_no_limitation()
    {
        using var wb = Workbook(
            ("Left Analog", ModeRows("Profile Name")),
            ("Reference Card", ModeRows("Reference Card")));

        Xlsx.ToCsv(wb, out _, out var limitation);

        Assert.Null(limitation);
    }

    [Fact]
    public void The_single_argument_overload_still_works()
    {
        using var wb = Workbook(("Left Analog", ModeRows("Profile Name")));

        Assert.Contains("dpad_N", Xlsx.ToCsv(wb));
    }

    // Tabs used to be stacked straight onto each other. The device ends a mode
    // at a blank line and only looks for the next sheet keyword on the line
    // after one, so without a separator a second tab's rows were read as more
    // bindings of the first mode. A tab whose A1 only loosely matches was then
    // folded in silently rather than named as a sheet the device will skip, so
    // a whole mode could go missing without a word.
    [Fact]
    public void A_second_tab_the_device_would_skip_is_still_reported()
    {
        using var wb = TwoTabs("Profile Name", "GTA Profile");

        var file = ProfileFile.Load(Xlsx.ToCsv(wb));

        Assert.Equal(2, file.Document.Sheets.Count);
        Assert.Contains(file.Issues, i => i.Message.Contains("does not START with"));
    }

    // And an ordinary second tab keeps both its rows and its own identity.
    [Fact]
    public void A_second_tab_lands_as_its_own_mode()
    {
        using var wb = TwoTabs("Profile Name", "Profile Name");

        var file = ProfileFile.Load(Xlsx.ToCsv(wb));

        Assert.Equal(2, file.Document.Sheets.Count);
        Assert.All(file.Document.Sheets, s => Assert.Single(s.Bindings));
        Assert.DoesNotContain(file.Issues, i => i.Message.Contains("does not START with"));
    }

    static MemoryStream TwoTabs(string firstA1, string secondA1)
    {
        string Tab(string a1, string output) =>
            $"<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>{a1}</t></is></c>"
            + "<c r=\"C1\" t=\"inlineStr\"><is><t>Solo</t></is></c></row>"
            + "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>game.csv</t></is></c></row>"
            + "<row r=\"3\"><c r=\"A3\" t=\"inlineStr\"><is><t>Outputs</t></is></c>"
            + "<c r=\"B3\" t=\"inlineStr\"><is><t>Function</t></is></c>"
            + "<c r=\"C3\" t=\"inlineStr\"><is><t>usb</t></is></c></row>"
            + $"<row r=\"4\"><c r=\"A4\" t=\"inlineStr\"><is><t>{output}</t></is></c>"
            + "<c r=\"B4\" t=\"inlineStr\"><is><t>normal</t></is></c>"
            + "<c r=\"C4\" t=\"inlineStr\"><is><t>lip</t></is></c></row>";

        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Put(string path, string body)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                w.Write(body);
            }
            Put("xl/workbook.xml",
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets><sheet name=\"One\" sheetId=\"1\" r:id=\"rId1\"/>" +
                "<sheet name=\"Two\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>");
            Put("xl/_rels/workbook.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/>" +
                "</Relationships>");
            Put("xl/worksheets/sheet1.xml",
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>"
                + Tab(firstA1, "mouse_left") + "</sheetData></worksheet>");
            Put("xl/worksheets/sheet2.xml",
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>"
                + Tab(secondA1, "circle") + "</sheetData></worksheet>");
        }
        ms.Position = 0;
        return ms;
    }
}
