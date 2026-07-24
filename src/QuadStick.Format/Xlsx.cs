using System.IO.Compression;
using System.Xml.Linq;

namespace QuadStick.Format;

// A QuadStick workbook (.xlsx) read as one profile CSV.
//
// The community convention is one mode per worksheet tab: Main, Flight,
// Mouse, Preferences. The device and this app read one flat grid where the
// modes are stacked, each starting with its own sheet keyword row. So import
// is just concatenation, and QMP does the same thing from the same xlsx
// export (FORMAT.md, "Sheet structure").
//
// Tab names are a spreadsheet convenience: the CSV has nowhere to put them
// and the device never sees them, so they are dropped here too. A tab's mode
// name is cell C1, same as every other sheet.
//
// xlsx is a zip of XML, so this is stdlib only. No spreadsheet library.
public static class Xlsx
{
    // Never exported by QMP or the Sheets add-on, whatever their A1 says.
    static readonly HashSet<string> HelperTabs = new(StringComparer.OrdinalIgnoreCase)
    { "Inputs", "Outputs", "Voice", "Reference Card" };

    static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace Pkg = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Zip magic. Google hands back HTML, not a workbook, when a link
    /// is not shared, so the caller checks the bytes before parsing them.</summary>
    public static bool LooksLikeXlsx(ReadOnlySpan<byte> content) =>
        content.Length >= 2 && content[0] == (byte)'P' && content[1] == (byte)'K';

    /// <summary>Every mode tab in the workbook, concatenated into profile CSV
    /// text. Throws InvalidDataException when the file is not a readable
    /// workbook; returns "" when it holds no profile tab at all.</summary>
    public static string ToCsv(Stream xlsx)
    {
        // A half-downloaded workbook unzips but does not parse. Both are the
        // same thing to the caller: this file is not readable.
        try { return Read(xlsx); }
        catch (System.Xml.XmlException ex) { throw new InvalidDataException("Not a readable spreadsheet.", ex); }
    }

    static string Read(Stream xlsx)
    {
        using var zip = new ZipArchive(xlsx, ZipArchiveMode.Read);
        var shared = SharedStrings(zip);
        var rows = new List<string[]>();
        foreach (var (name, part) in SheetParts(zip))
        {
            if (HelperTabs.Contains(name.Trim())) continue;
            var grid = Sheet(zip, part, shared);
            // A tab is a mode only if its A1 says so. Everything else in the
            // workbook (Inputs, Outputs, notes, scratch) is not a profile.
            if (grid.Count > 0 && grid[0].Length > 0 && Vocab.IsSheetKeyword(grid[0][0].Trim()))
                rows.AddRange(grid);
        }
        return Csv.Write(rows);
    }

    // Every worksheet in tab order, hidden ones included: QMP exports by tab
    // name and A1, not by whether the tab is showing, and an import that
    // silently differs from the official converter is worse than an extra mode.
    static IEnumerable<(string Name, string Part)> SheetParts(ZipArchive zip)
    {
        var wb = Xml(zip, "xl/workbook.xml");
        var rels = Xml(zip, "xl/_rels/workbook.xml.rels");
        if (wb is null || rels is null) throw new InvalidDataException("Not a readable spreadsheet.");

        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in rels.Root!.Elements(Pkg + "Relationship"))
            if ((string?)r.Attribute("Id") is string id && (string?)r.Attribute("Target") is string t)
                targets[id] = t;

        foreach (var sheet in wb.Root!.Descendants(Main + "sheet"))
        {
            if ((string?)sheet.Attribute(Rel + "id") is not string id) continue;
            if (!targets.TryGetValue(id, out var target)) continue;
            target = target.TrimStart('/');
            yield return (
                (string?)sheet.Attribute("name") ?? "",
                target.StartsWith("xl/", StringComparison.Ordinal) ? target : "xl/" + target);
        }
    }

    static List<string[]> Sheet(ZipArchive zip, string part, string[] shared)
    {
        var rows = new List<string[]>();
        if (Xml(zip, part) is not XDocument doc) return rows;

        foreach (var row in doc.Root!.Descendants(Main + "row"))
        {
            var cells = new List<string>();
            foreach (var c in row.Elements(Main + "c"))
            {
                // Empty cells are skipped in the file, so place each one by its
                // own reference (C4 -> index 2) instead of counting.
                int col = ColumnIndex((string?)c.Attribute("r") ?? "");
                while (cells.Count < col) cells.Add("");
                cells.Add(Value(c, shared));
            }
            while (cells.Count > 0 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);

            // Blank rows are skipped in the file too, and a blank row means
            // "mode ends here" to the device, so their positions must survive.
            int number = (int?)row.Attribute("r") ?? rows.Count + 1;
            while (rows.Count < number - 1) rows.Add(Array.Empty<string>());
            rows.Add(cells.ToArray());
        }

        // The template ships 1000 rows per tab; only the used ones matter.
        while (rows.Count > 0 && rows[^1].Length == 0) rows.RemoveAt(rows.Count - 1);
        return rows;
    }

    static string Value(XElement cell, string[] shared)
    {
        var type = (string?)cell.Attribute("t");
        if (type == "inlineStr") return Text(cell.Element(Main + "is"));
        if (cell.Element(Main + "v") is not XElement v) return "";
        if (type == "s")
            return int.TryParse(v.Value, out var i) && i >= 0 && i < shared.Length ? shared[i] : "";
        if (type == "b") return v.Value == "1" ? "TRUE" : "FALSE";
        // ponytail: numbers come through raw, which is what profile values are
        // (130, 45, 0.5). A date cell would import as its serial number;
        // profiles have no dates, so no number-format table.
        return v.Value;
    }

    static string[] SharedStrings(ZipArchive zip) =>
        Xml(zip, "xl/sharedStrings.xml") is XDocument doc
            ? doc.Root!.Elements(Main + "si").Select(Text).ToArray()
            : Array.Empty<string>();

    // A string can be split into styled runs; the text is all of them joined.
    static string Text(XElement? element) =>
        element is null ? "" : string.Concat(element.Descendants(Main + "t").Select(t => t.Value));

    // "AB12" -> 27. Letters only, so a missing or odd ref lands in column A.
    static int ColumnIndex(string reference)
    {
        int n = 0;
        foreach (var ch in reference)
        {
            if (ch is >= 'A' and <= 'Z') n = n * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z') n = n * 26 + (ch - 'a' + 1);
            else break;
        }
        return n > 0 ? n - 1 : 0;
    }

    static XDocument? Xml(ZipArchive zip, string path)
    {
        if (zip.GetEntry(path) is not ZipArchiveEntry entry) return null;
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }
}
