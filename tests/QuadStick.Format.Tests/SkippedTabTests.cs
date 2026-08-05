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
    // A minimal workbook built here rather than checked in as a fixture, so the
    // shape under test is readable in the diff. Inline strings only: no
    // sharedStrings part to keep in step.
    static MemoryStream Workbook(params (string Tab, string[][] Rows)[] tabs)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Write(string path, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }

            var sheetTags = string.Concat(tabs.Select((t, i) =>
                $"<sheet name=\"{Esc(t.Tab)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>"));
            Write("xl/workbook.xml",
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
                + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                + $"<sheets>{sheetTags}</sheets></workbook>");

            var relTags = string.Concat(tabs.Select((_, i) =>
                $"<Relationship Id=\"rId{i + 1}\" Target=\"worksheets/sheet{i + 1}.xml\" "
                + "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\"/>"));
            Write("xl/_rels/workbook.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + relTags + "</Relationships>");

            for (int i = 0; i < tabs.Length; i++)
            {
                var sb = new StringBuilder(
                    "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
                for (int r = 0; r < tabs[i].Rows.Length; r++)
                {
                    sb.Append($"<row r=\"{r + 1}\">");
                    for (int c = 0; c < tabs[i].Rows[r].Length; c++)
                    {
                        var v = tabs[i].Rows[r][c];
                        if (v.Length == 0) continue;
                        sb.Append($"<c r=\"{(char)('A' + c)}{r + 1}\" t=\"inlineStr\"><is><t>{Esc(v)}</t></is></c>");
                    }
                    sb.Append("</row>");
                }
                Write($"xl/worksheets/sheet{i + 1}.xml", sb.Append("</sheetData></worksheet>").ToString());
            }
        }
        ms.Position = 0;
        return ms;
    }

    static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;");

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

    // The named helper tabs are deliberately never modes, whatever is in A1.
    [Fact]
    public void The_known_helper_tabs_are_not_reported()
    {
        using var wb = Workbook(
            ("Left Analog", ModeRows("Profile Name")),
            ("Outputs", ModeRows("PS3 D-Pad Button North")));

        Xlsx.ToCsv(wb, out var skipped);

        Assert.Empty(skipped);
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
