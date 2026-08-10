using System.IO.Compression;
using System.Text;

namespace QuadStick.Format.Tests;

// A minimal workbook built here rather than checked in as a fixture, so the
// shape under test is readable in the diff. Inline strings only: no
// sharedStrings part to keep in step.
static class TestWorkbook
{
    public static MemoryStream Build(params (string Tab, string[][] Rows)[] tabs)
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
                        sb.Append($"<c r=\"{Column(c)}{r + 1}\" t=\"inlineStr\"><is><t>{Esc(v)}</t></is></c>");
                    }
                    sb.Append("</row>");
                }
                Write($"xl/worksheets/sheet{i + 1}.xml", sb.Append("</sheetData></worksheet>").ToString());
            }
        }
        ms.Position = 0;
        return ms;
    }

    // 0 -> A, 26 -> AA. A profile reaches column L, and the comment column
    // past it puts a real workbook past Z.
    static string Column(int index)
    {
        var name = "";
        for (int n = index + 1; n > 0; n /= 26)
        {
            n -= 1;
            name = (char)('A' + n % 26) + name;
        }
        return name;
    }

    public static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
