using System.IO.Compression;
using System.Text;
using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// What a workbook does when it is not a tidy one. Half-downloaded files,
// spreadsheets with junk left at the bottom, and references no real cell has.
// The rule for all of them is the same: say the file cannot be read, or read
// the part that is a profile. Never crash, and never turn a twenty row profile
// into a million rows because one stray cell sat at the bottom of the sheet.
public class XlsxJunkTests
{
    // The smallest workbook that opens: one sheet, whatever XML you hand it.
    static MemoryStream Workbook(string sheetXml)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Put(string path, string body)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                w.Write(body);
            }
            Put("[Content_Types].xml",
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "</Types>");
            Put("_rels/.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");
            Put("xl/workbook.xml",
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets><sheet name=\"Profile\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Put("xl/_rels/workbook.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "</Relationships>");
            Put("xl/worksheets/sheet1.xml",
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
                sheetXml + "</sheetData></worksheet>");
        }
        ms.Position = 0;
        return ms;
    }

    static string Cell(string reference, string text) =>
        $"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{text}</t></is></c>";

    // A real one-mode profile, so a test can add junk to something that works.
    const string RealRows =
        "<row r=\"1\">" + /* A1 */ "<c r=\"A1\" t=\"inlineStr\"><is><t>Profile Name</t></is></c>" +
        "<c r=\"C1\" t=\"inlineStr\"><is><t>Solo</t></is></c></row>" +
        "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>game.csv</t></is></c></row>" +
        "<row r=\"3\"><c r=\"A3\" t=\"inlineStr\"><is><t>Outputs</t></is></c>" +
        "<c r=\"B3\" t=\"inlineStr\"><is><t>Function</t></is></c>" +
        "<c r=\"C3\" t=\"inlineStr\"><is><t>usb</t></is></c></row>" +
        "<row r=\"4\"><c r=\"A4\" t=\"inlineStr\"><is><t>mouse_left</t></is></c>" +
        "<c r=\"B4\" t=\"inlineStr\"><is><t>normal</t></is></c>" +
        "<c r=\"C4\" t=\"inlineStr\"><is><t>lip</t></is></c></row>";

    [Fact]
    public void A_row_number_that_is_not_a_number_says_the_file_cannot_be_read()
    {
        using var wb = Workbook("<row r=\"abc\">" + Cell("A1", "Profile Name") + "</row>");
        // Not FormatException: both import paths catch InvalidDataException and
        // neither catches that, so the app used to die on a corrupt sheet
        // instead of saying it could not read it.
        Assert.Throws<InvalidDataException>(() => Xlsx.ToCsv(wb));
    }

    [Fact]
    public void A_row_number_too_big_for_an_int_says_the_same()
    {
        using var wb = Workbook("<row r=\"99999999999999\">" + Cell("A1", "x") + "</row>");
        Assert.Throws<InvalidDataException>(() => Xlsx.ToCsv(wb));
    }

    [Fact]
    public void A_stray_cell_at_the_bottom_of_the_sheet_does_not_become_a_million_rows()
    {
        // Google Sheets leaves one of these behind after a paste and a delete.
        // Honouring it padded the profile out to Excel's last row: a two
        // megabyte file of nothing, saved into the user's library.
        using var wb = Workbook(RealRows +
            "<row r=\"1048576\">" + Cell("A1048576", "x") + "</row>");
        var file = ProfileFile.Load(Xlsx.ToCsv(wb));

        Assert.True(file.Grid.Count < 100, $"grid has {file.Grid.Count} rows");
        // And the profile itself came through untouched.
        var sheet = Assert.Single(file.Document.Sheets);
        var binding = Assert.Single(sheet.Bindings);
        Assert.Equal("mouse_left", binding.Output);
        Assert.Equal(new[] { "lip" }, binding.Inputs);
    }

    [Fact]
    public void A_column_reference_past_the_last_real_column_is_bounded()
    {
        // "ZZZZZZ" is 321 million columns, and the unchecked arithmetic that
        // computed it would wrap on the way there.
        using var wb = Workbook(RealRows +
            "<row r=\"5\">" + Cell("ZZZZZZ5", "junk") + "</row>");
        var csv = Xlsx.ToCsv(wb);

        Assert.True(csv.Length < 200_000, $"csv is {csv.Length} characters");
        Assert.Contains("mouse_left", csv);
    }

    [Fact]
    public void An_ordinary_workbook_is_untouched_by_any_of_this()
    {
        using var wb = Workbook(RealRows);
        var file = ProfileFile.Load(Xlsx.ToCsv(wb));
        var sheet = Assert.Single(file.Document.Sheets);
        Assert.Equal("Solo", sheet.ModeName);
        Assert.Equal("game.csv", file.Document.CsvFileName);
        Assert.Equal("lip", Assert.Single(sheet.Bindings).Inputs[0]);
    }

    [Fact]
    public void A_row_after_a_skipped_one_does_not_land_back_inside_the_profile()
    {
        // The r attribute is optional. Bounding the sheet by skipping far rows
        // made the fallback for a bare <row> count only the rows that were
        // kept, so junk from the bottom of the sheet arrived as a live binding
        // four rows down, which is worse than the padding it replaced.
        using var wb = Workbook(RealRows +
            "<row r=\"30000\">" + Cell("A30000", "junk_far_below") + "</row>" +
            "<row>" + Cell("A1", "kb_z") + Cell("B1", "normal") + Cell("C1", "lip") + "</row>");
        var file = ProfileFile.Load(Xlsx.ToCsv(wb));

        var sheet = Assert.Single(file.Document.Sheets);
        var binding = Assert.Single(sheet.Bindings);
        Assert.Equal("mouse_left", binding.Output);
        Assert.DoesNotContain(file.Document.Sheets.SelectMany(s => s.Bindings), b => b.Output == "kb_z");
    }

    [Fact]
    public void One_far_right_cell_per_row_does_not_blow_the_file_up()
    {
        // Clamping a stray reference to the last column instead of dropping it
        // still built 16,383 blanks for every one of them.
        var sheet = new System.Text.StringBuilder(RealRows);
        for (int r = 5; r <= 200; r++)
            sheet.Append($"<row r=\"{r}\">" + Cell($"XFD{r}", "x") + "</row>");
        using var wb = Workbook(sheet.ToString());
        var csv = Xlsx.ToCsv(wb);

        Assert.True(csv.Length < 5_000, $"csv is {csv.Length} characters");
        Assert.Contains("mouse_left", csv);
    }

    [Fact]
    public void The_widest_real_workbook_still_keeps_every_column_it_uses()
    {
        // multi-tab.xlsx is a real community workbook and reaches column Z, so
        // the column bound must be nowhere near it.
        using var stream = File.OpenRead(Path.Combine("corpus", "multi-tab.xlsx"));
        var file = ProfileFile.Load(Xlsx.ToCsv(stream));
        Assert.Equal(4, file.Document.Sheets.Count);
        Assert.Equal("kb_w", file.Document.Sheets[0].Bindings[0].Output);
    }
}
