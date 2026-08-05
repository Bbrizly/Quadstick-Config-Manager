using System.Globalization;
using System.IO.Compression;
using System.Xml;
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
/// <summary>A tab that holds bindings but did not import, with the cells read
/// from it. The device skips it for the same reason the app does, so this is
/// not a parsing failure: it is a mode the user has already lost and does not
/// know about.</summary>
public sealed record SkippedTab(string Name, IReadOnlyList<string[]> Rows);

public static class Xlsx
{
    // Never exported by QMP or the Sheets add-on, whatever their A1 says.
    static readonly HashSet<string> HelperTabs = new(StringComparer.OrdinalIgnoreCase)
    { "Inputs", "Outputs", "Voice", "Reference Card" };

    // Where a cell stops being profile and starts being junk, in both
    // directions. Cells past these are dropped, not clamped: clamping stacks
    // them all onto the boundary, which is its own kind of wrong.
    //
    // Rows: the shipped template is 1000 per tab and the device reads 128
    // binding rows per mode. Google Sheets leaves a stray cell at the bottom of
    // a sheet after a paste and a delete, and honouring one at r="1048576"
    // turned a twenty row profile into a million blank rows and a two megabyte
    // file.
    //
    // Columns: L is the last one a profile means anything by, and the widest
    // real community workbook in the corpus reaches Z. Excel's own last column
    // was useless as a bound, because 16,383 blanks per stray cell times a
    // thousand rows is still sixteen million strings.
    const int MaxColumn = 63; // BL, zero-based
    const int MaxRows = 20_000;

    // The three bounds above are per sheet, which left the workbook itself
    // unbounded: the cost of a tab is paid once per tab, and workbook.xml is so
    // repetitive that naming hundreds of them costs almost nothing to compress.
    // A 25 KB download expanded into gigabytes of strings that way, and many
    // <sheet> entries are allowed to point at the same part, so the same grid
    // could be built over and over.
    //
    // A real workbook is one tab per mode and the device loads 16 profiles, so
    // these are already far past anything a profile does.
    const int MaxSheets = 64;
    const int MaxWorkbookRows = 30_000;

    // The uncompressed size the archive declares for a part, read from the
    // central directory without inflating a byte. A sheet that expands to
    // hundreds of megabytes out of a few hundred kilobytes is not a profile:
    // one 408 KB file reached 120 MB and 1.2 GB of DOM.
    const long MaxPartBytes = 32L * 1024 * 1024;

    // The same bound again, in the units the XML reader counts, so a part that
    // lies about its size in the zip header is still stopped. MaxCharactersFrom-
    // Entities and Prohibit are what keep a DTD from being the way around it;
    // XDocument.Load(Stream) does not use the hardened reader defaults, so the
    // safety here was resting on nothing written down.
    static readonly XmlReaderSettings PartLimits = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        MaxCharactersInDocument = 40_000_000,
        MaxCharactersFromEntities = 0,
        XmlResolver = null,
    };

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
    public static string ToCsv(Stream xlsx) => ToCsv(xlsx, out _);

    /// <summary>As above, and <paramref name="skippedTabs"/> reports the tabs
    /// that hold bindings but were not imported because their A1 does not say
    /// what kind of sheet they are. Skipping them is correct, since the device
    /// reads A1 the same way, but a caller has to be able to say so: one
    /// overwritten A1 costs the user a whole mode, and silence makes that look
    /// like a parsing bug. The cells come back with the name so the caller can
    /// show what was left behind and offer to repair A1, rather than only
    /// naming the loss.</summary>
    public static string ToCsv(Stream xlsx, out IReadOnlyList<SkippedTab> skippedTabs)
    {
        // A half-downloaded workbook unzips but does not parse. Both are the
        // same thing to the caller: this file is not readable.
        //
        // FormatException and OverflowException belong here too. A row or cell
        // whose r="..." is not a number reaches the int conversion below, and
        // the two import paths both catch InvalidDataException and neither
        // catches those, so a corrupt sheet took the app down instead of
        // saying it could not be read.
        try { return Read(xlsx, out skippedTabs); }
        catch (System.Xml.XmlException ex) { throw new InvalidDataException("Not a readable spreadsheet.", ex); }
        catch (FormatException ex) { throw new InvalidDataException("Not a readable spreadsheet.", ex); }
        catch (OverflowException ex) { throw new InvalidDataException("Not a readable spreadsheet.", ex); }
    }

    static string Read(Stream xlsx, out IReadOnlyList<SkippedTab> skippedTabs)
    {
        using var zip = new ZipArchive(xlsx, ZipArchiveMode.Read);
        var shared = SharedStrings(zip);
        var rows = new List<string[]>();
        var skipped = new List<SkippedTab>();
        int kept = 0;
        foreach (var (name, part) in SheetParts(zip))
        {
            if (HelperTabs.Contains(name.Trim())) continue;
            var grid = Sheet(zip, part, shared);
            // A tab is a mode only if its A1 says so. Everything else in the
            // workbook (Inputs, Outputs, notes, scratch) is not a profile.
            if (grid.Count > 0 && grid[0].Length > 0 && Vocab.IsSheetKeyword(grid[0][0].Trim()))
                rows.AddRange(grid);
            else if (LooksLikeBindings(grid)) skipped.Add(new SkippedTab(name, grid));
            else continue; // nothing was retained, so nothing was spent

            // Skipped tabs are held on to as well as imported ones, so both
            // count against the workbook's budget. Stopping here rather than
            // part way through a tab keeps every mode that did come in whole.
            kept += grid.Count;
            if (kept >= MaxWorkbookRows) break;
        }
        skippedTabs = skipped;
        return Csv.Write(rows);
    }

    /// <summary>The tab's rows as a mode the app and the device would both
    /// read: A1 given the keyword it is missing, and the tab's own name used
    /// as the mode name when the sheet does not carry one. Nothing else is
    /// touched, so what comes in is the user's own layout.</summary>
    public static List<string[]> RepairedAsMode(SkippedTab tab)
    {
        var rows = tab.Rows.Select(r => (string[])r.Clone()).ToList();
        if (rows.Count == 0) return rows;
        var first = rows[0];
        if (first.Length < 3) { Array.Resize(ref first, 3); rows[0] = first; }
        for (int c = 0; c < first.Length; c++) first[c] ??= "";
        first[0] = "Profile Name";
        if (first[2].Trim().Length == 0) first[2] = tab.Name;
        return rows;
    }

    // Rows with a real function in column B are what a mode is made of, and a
    // reference card or a scratch tab has none. Three is enough to tell them
    // apart without naming every stray tab on every import, which would just
    // teach people to ignore the message.
    static bool LooksLikeBindings(List<string[]> grid) =>
        grid.Count(r => r.Length > 1 && Vocab.FunctionArity.ContainsKey(r[1].Trim())) >= 3;

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

        var seen = new HashSet<string>(StringComparer.Ordinal);
        int count = 0;
        foreach (var sheet in wb.Root!.Descendants(Main + "sheet"))
        {
            if ((string?)sheet.Attribute(Rel + "id") is not string id) continue;
            if (!targets.TryGetValue(id, out var target)) continue;
            target = target.TrimStart('/');
            var part = target.StartsWith("xl/", StringComparison.Ordinal) ? target : "xl/" + target;
            // Nothing stops a workbook naming the same part from many <sheet>
            // entries, and each one used to be read and kept all over again.
            // One part is one sheet however many times it is listed.
            if (!seen.Add(part)) continue;
            if (++count > MaxSheets) yield break;
            yield return ((string?)sheet.Attribute("name") ?? "", part);
        }
    }

    static List<string[]> Sheet(ZipArchive zip, string part, string[] shared)
    {
        var rows = new List<string[]>();
        if (Xml(zip, part) is not XDocument doc) return rows;

        int lastNumber = 0;
        foreach (var row in doc.Root!.Descendants(Main + "row"))
        {
            var cells = new List<string>();
            int nextCol = 0;
            foreach (var c in row.Elements(Main + "c"))
            {
                // Empty cells are skipped in the file, so place each one by its
                // own reference (C4 -> index 2) instead of counting.
                //
                // Padding forward and appending was only right while the cells
                // arrived in order. The format asks for that and nothing
                // enforces it, so a workbook from another writer could hand back
                // C4, B4, A4 and land them as A4, B4, C4 reversed: the row's
                // output moved into an input column and the binding read as
                // something else entirely. Writing at the index says where each
                // cell goes whatever order they come in, and a repeated
                // reference overwrites, which is what a spreadsheet does too.
                var reference = (string?)c.Attribute("r") ?? "";
                int col = reference.Length > 0 && char.IsAsciiLetter(reference[0])
                    ? ColumnIndex(reference)
                    : nextCol; // no reference: the column after the one before it
                nextCol = col + 1;
                if (col > MaxColumn) continue; // debris: nothing out there is read
                while (cells.Count <= col) cells.Add("");
                cells[col] = Value(c, shared);
            }
            while (cells.Count > 0 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);

            // Blank rows are skipped in the file too, and a blank row means
            // "mode ends here" to the device, so their positions must survive.
            //
            // The r attribute is optional, and the fallback has to count the
            // rows that were skipped as well as the ones that were kept.
            // Counting kept rows alone let a bare <row> that followed a skipped
            // one land back inside the profile: junk from the bottom of the
            // sheet arrived as a live binding a few rows down.
            //
            // Placed by number, for the same reason the cells are placed by
            // reference. Appending trusted the rows to arrive in order twice
            // over: a descending r put a row at the end and left blanks in the
            // middle, and a blank row is where the device stops reading a mode,
            // so every binding under the gap went inert. Appending also meant
            // the row cap counted the row NUMBER and never the rows themselves,
            // so a file that repeated r="5" grew without limit: 1.8 MB of zip
            // became three million rows and a hundred seconds on the UI thread.
            // Writing at the index bounds the count by the cap, whatever the
            // file says.
            int number = (int?)row.Attribute("r") ?? lastNumber + 1;
            lastNumber = number;
            if (number > MaxRows || number < 1) continue;
            while (rows.Count < number) rows.Add(Array.Empty<string>());
            rows[number - 1] = cells.ToArray();
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
            return int.TryParse(v.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                && i >= 0 && i < shared.Length ? shared[i] : "";
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
            // Stop before the arithmetic wraps, and answer with a column the
            // caller drops rather than one it pads out to.
            if (n > MaxColumn + 1) return MaxColumn + 1;
        }
        return n > 0 ? n - 1 : 0;
    }

    static XDocument? Xml(ZipArchive zip, string path)
    {
        if (zip.GetEntry(path) is not ZipArchiveEntry entry) return null;
        // Checked before a byte is inflated. The whole grid was bounded and the
        // DOM under it was not, so the guards could only ever run after the
        // memory had already been spent.
        if (entry.Length > MaxPartBytes)
            throw new InvalidDataException(
                $"That spreadsheet holds a part of {entry.Length / (1024 * 1024)} MB, far larger than any "
                + "profile, so it was not read.");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, PartLimits);
        return XDocument.Load(reader);
    }
}
