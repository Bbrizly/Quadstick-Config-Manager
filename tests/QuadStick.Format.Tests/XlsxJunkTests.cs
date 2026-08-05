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

    // The row cap used to bound the row NUMBER and never the number of rows, so
    // a file that said r="5" over and over grew without any limit at all: 1.8 MB
    // of zip became three million rows and a hundred seconds of frozen window.
    [Fact]
    public void A_row_number_repeated_over_and_over_does_not_grow_the_grid()
    {
        var sheet = new StringBuilder(RealRows);
        for (int i = 0; i < 100_000; i++)
            sheet.Append("<row r=\"5\">" + Cell("A5", "circle") + "</row>");
        using var wb = Workbook(sheet.ToString());

        var csv = Xlsx.ToCsv(wb);

        Assert.True(csv.Length < 5_000, $"csv is {csv.Length} characters");
        Assert.Contains("mouse_left", csv);
    }

    // OOXML asks for cells in order and nothing enforces it. Padding forward and
    // appending read C4,B4,A4 back as A4,B4,C4 reversed, which moved the row's
    // output into an input column and made the binding something else entirely.
    [Fact]
    public void Cells_that_arrive_out_of_order_still_land_in_their_own_columns()
    {
        using var wb = Workbook(
            RealRows.Replace(
                "<row r=\"4\"><c r=\"A4\" t=\"inlineStr\"><is><t>mouse_left</t></is></c>" +
                "<c r=\"B4\" t=\"inlineStr\"><is><t>normal</t></is></c>" +
                "<c r=\"C4\" t=\"inlineStr\"><is><t>lip</t></is></c></row>",
                "<row r=\"4\">" + Cell("C4", "lip") + Cell("B4", "normal") + Cell("A4", "mouse_left") + "</row>"));

        var file = ProfileFile.Load(Xlsx.ToCsv(wb));

        var binding = Assert.Single(file.Document.Sheets[0].Bindings);
        Assert.Equal("mouse_left", binding.Output);
        Assert.Equal(new[] { "lip" }, binding.Inputs);
    }

    // Same again for rows. A descending r put the row at the end and left blanks
    // where it should have been, and a blank row is where the device stops
    // reading a mode, so every binding under the gap went inert.
    [Fact]
    public void A_row_that_arrives_out_of_order_still_lands_at_its_own_number()
    {
        using var wb = Workbook(
            RealRows
            + "<row r=\"6\">" + Cell("A6", "triangle") + Cell("B6", "normal") + Cell("C6", "lip") + "</row>"
            + "<row r=\"5\">" + Cell("A5", "circle") + Cell("B5", "normal") + Cell("C5", "lip") + "</row>");

        var file = ProfileFile.Load(Xlsx.ToCsv(wb));

        Assert.Equal(
            new[] { "mouse_left", "circle", "triangle" },
            file.Document.Sheets[0].Bindings.Select(b => b.Output).ToArray());
    }

    // A part that inflates to hundreds of megabytes out of a few hundred
    // kilobytes is not a profile. The zip says how big it will be before a byte
    // is inflated, and that was never read: every bound in this file could only
    // run after the whole DOM had already been built.
    [Fact]
    public void A_part_far_bigger_than_any_profile_is_refused_before_it_is_read()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry("xl/workbook.xml").Open(), new UTF8Encoding(false));
            w.Write("<workbook><sheets>");
            // Compresses to almost nothing, which is the whole point of the file.
            var filler = new string(' ', 64 * 1024);
            for (int i = 0; i < 40 * 16; i++) w.Write(filler);
            w.Write("</sheets></workbook>");
        }
        ms.Position = 0;

        var ex = Assert.Throws<InvalidDataException>(() => Xlsx.ToCsv(ms));
        Assert.Contains("far larger than any", ex.Message);
    }

    // Nothing stops a workbook naming the same part from hundreds of <sheet>
    // entries, and each one was read and kept all over again: workbook.xml is so
    // repetitive that 25 KB of zip reached 1.6 GB of strings that way.
    [Fact]
    public void A_workbook_naming_one_part_hundreds_of_times_reads_it_once()
    {
        var sheets = new StringBuilder();
        for (int i = 1; i <= 400; i++)
            sheets.Append($"<sheet name=\"Tab{i}\" sheetId=\"{i}\" r:id=\"rId1\"/>");

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
                "<sheets>" + sheets + "</sheets></workbook>");
            Put("xl/_rels/workbook.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "</Relationships>");
            Put("xl/worksheets/sheet1.xml",
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
                RealRows + "</sheetData></worksheet>");
        }
        ms.Position = 0;

        var csv = Xlsx.ToCsv(ms);

        Assert.True(csv.Length < 5_000, $"csv is {csv.Length} characters");
        Assert.Single(ProfileFile.Load(csv).Document.Sheets);
    }

    // XDocument.Load(Stream) does not use the hardened reader defaults, so a
    // DTD was processed and its entities expanded. Real workbooks have no DTD.
    [Fact]
    public void A_spreadsheet_carrying_a_dtd_is_not_read()
    {
        // Everything else about this workbook is valid, so without the reader
        // settings it imports cleanly and the DTD goes unnoticed.
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Put(string path, string body)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                w.Write(body);
            }
            Put("xl/workbook.xml",
                "<!DOCTYPE workbook [<!ENTITY tabname \"Solo\">]>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets><sheet name=\"&tabname;\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Put("xl/_rels/workbook.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "</Relationships>");
            Put("xl/worksheets/sheet1.xml",
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
                RealRows + "</sheetData></worksheet>");
        }
        ms.Position = 0;

        Assert.Throws<InvalidDataException>(() => Xlsx.ToCsv(ms));
    }
}
