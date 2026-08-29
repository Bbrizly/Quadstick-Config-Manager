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
// A tab's mode name is cell C1, same as every other sheet. The tab title is
// not part of the profile: the CSV has nowhere to put it and the device never
// sees one. But the community keeps its names on the tabs and leaves C1 as the
// template wrote it, so a tab title is copied into C1 when C1 says nothing of
// its own (NameModesFromTabs), and every such naming is reported.
//
// xlsx is a zip of XML, so this is stdlib only. No spreadsheet library.
/// <summary>Why a tab did not import. The two reasons need different words and
/// different offers: one is a mode the user has lost, the other is a tab that
/// was never a mode and never will be.</summary>
public enum SkippedTabKind
{
    /// <summary>A1 does not say what kind of sheet this is, so neither the app
    /// nor the device reads it. Repairing A1 turns it into a mode.</summary>
    UnreadableA1,

    /// <summary>A tab QMP and the Sheets add-on write as documentation, never
    /// as profile data. Repairing it would invent a mode the user never had.
    /// </summary>
    Helper,
}

/// <summary>A tab that did not import, with the cells read from it. The device
/// skips it for the same reason the app does, so this is not a parsing failure:
/// it is either a mode the user has already lost and does not know about, or a
/// tab that was never profile data. Silence is what makes both look like a bug,
/// so both are reported.</summary>
public sealed record SkippedTab(
    string Name,
    IReadOnlyList<string[]> Rows,
    SkippedTabKind Kind = SkippedTabKind.UnreadableA1);

/// <summary>A mode this reader named after its sheet tab, and what cell C1 said
/// before. Reported, never done quietly: the name is the one thing the user
/// recognises a mode by.</summary>
public sealed record TabRename(int ModeNumber, string TabName, string CellC1);

/// <summary>Everything one workbook import produced.</summary>
public sealed record XlsxImport(
    string Csv,
    IReadOnlyList<SkippedTab> Skipped,
    string? Limitation,
    IReadOnlyList<TabRename> Renamed);

public static partial class Xlsx
{
    // The names QMP and the Sheets add-on give the tabs they write as
    // documentation. A hint about what a tab holds, not a verdict: a tab whose
    // A1 names a kind of sheet is a mode however it is titled.
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

    /// <summary>As above, and <paramref name="skippedTabs"/> reports every tab
    /// that did not import, of both kinds. Skipping them is correct, since the
    /// device reads A1 the same way, but a caller has to be able to say so: one
    /// overwritten A1 costs the user a whole mode, and silence makes that look
    /// like a parsing bug.
    ///
    /// <see cref="SkippedTabKind.UnreadableA1"/> tabs come back with their cells,
    /// so the caller can show what was left behind and offer to repair A1 rather
    /// than only naming the loss. <see cref="SkippedTabKind.Helper"/> tabs come
    /// back by name only: they are documentation, there is nothing to offer, and
    /// the caller's job is to stop the user hunting for a mode that was never
    /// there.</summary>
    public static string ToCsv(Stream xlsx, out IReadOnlyList<SkippedTab> skippedTabs) =>
        ToCsv(xlsx, out skippedTabs, out _);

    /// <summary>As above, and <paramref name="limitation"/> is set when the
    /// workbook's own bounds stopped the read before the last tab, so the
    /// caller can say a partial import is partial. A tab that comes in short a
    /// mode and is called clean is the worst thing this import can do, and the
    /// bounds used to stop reading without a word.</summary>
    public static string ToCsv(Stream xlsx, out IReadOnlyList<SkippedTab> skippedTabs, out string? limitation)
    {
        var result = Import(xlsx);
        skippedTabs = result.Skipped;
        limitation = result.Limitation;
        return result.Csv;
    }

    /// <summary>The whole import: the CSV, both kinds of skipped tab, the
    /// limitation when the read stopped short, and every mode this reader named
    /// after its sheet tab.</summary>
    public static XlsxImport Import(Stream xlsx)
    {
        // A half-downloaded workbook unzips but does not parse. Both are the
        // same thing to the caller: this file is not readable.
        //
        // FormatException and OverflowException belong here too. A row or cell
        // whose r="..." is not a number reaches the int conversion below, and
        // the two import paths both catch InvalidDataException and neither
        // catches those, so a corrupt sheet took the app down instead of
        // saying it could not be read.
        try { return Read(xlsx); }
        catch (System.Xml.XmlException ex) { throw new InvalidDataException("Not a readable spreadsheet.", ex); }
        catch (FormatException ex) { throw new InvalidDataException("Not a readable spreadsheet.", ex); }
        catch (OverflowException ex) { throw new InvalidDataException("Not a readable spreadsheet.", ex); }
    }

    static XlsxImport Read(Stream xlsx)
    {
        using var zip = new ZipArchive(xlsx, ZipArchiveMode.Read);
        var shared = SharedStrings(zip);
        var rows = new List<string[]>();
        var skipped = new List<SkippedTab>();
        // Where each mode's keyword row landed in rows, what its tab was
        // called, and what its C1 said. Named after the loop, because whether a
        // name is worth replacing depends on the other tabs in the workbook.
        var modes = new List<(int Row, string Tab, string C1)>();
        int kept = 0;

        // Listed up front rather than read one at a time, so a tab that is
        // never reached can still be counted. Names and part paths only, one
        // past the cap so the cap itself can be seen, which keeps a workbook
        // that lists a hundred thousand tabs from being listed a hundred
        // thousand times.
        var parts = SheetParts(zip).Take(MaxSheets + 1).ToList();
        string? limit = null;
        if (parts.Count > MaxSheets)
        {
            limit = Truncated(string.Format(CultureInfo.CurrentCulture, Strings.Sheet_ThisSpreadsheetHasMoreThan, MaxSheets, MaxSheets), Strings.Sheet_EveryTabPastThatWas);
            parts.RemoveAt(parts.Count - 1);
        }

        for (int i = 0; i < parts.Count; i++)
        {
            var (name, part) = parts[i];
            bool named = HelperTabs.Contains(name.Trim());

            // A tab that will not open only sinks the whole import when it
            // might have been a mode. Asking A1 first means the part is opened
            // now, and every workbook QMP writes carries a Reference Card, so a
            // corrupt or enormous one would have cost the user every mode in
            // the file over a tab that was never going to import anyway.
            List<string[]> grid;
            bool lostRows;
            try { grid = Sheet(zip, part, shared, out lostRows); }
            catch (Exception ex) when (named && ex is InvalidDataException or System.Xml.XmlException)
            {
                skipped.Add(new SkippedTab(name, Array.Empty<string[]>(), SkippedTabKind.Helper));
                continue;
            }

            // A cell holding something, below the row where this reader stops.
            // A profile is under two hundred rows and the device reads 128 per
            // mode, so this is not a shape a real workbook has; it is still a
            // read that did not finish, and one of those has to say so.
            if (lostRows)
                limit ??= string.Format(CultureInfo.CurrentCulture, Strings.Sheet_TabPastTheRowCap,
                    name, MaxRows.ToString("N0", CultureInfo.CurrentCulture));

            // A tab is a mode only if its A1 says so. Everything else in the
            // workbook (Inputs, Outputs, notes, scratch) is not a profile.
            bool keyword = grid.Count > 0 && grid[0].Length > 0 && Vocab.IsSheetKeyword(grid[0][0].Trim());

            // Or the whole file, written flat onto one tab: A1 is the version
            // header line, and the sheet keywords are further down. This app's
            // own backup wrote that shape, so a user who copied their share
            // link and pasted it into Import got their profile back as "no
            // profile tab". The device reads such a file without complaint.
            bool flatProfile = !keyword && grid.Count > 0 && grid[0].Length > 0
                && Vocab.IsFileHeader(grid[0][0]);

            // Named, not passed over. Skipping these is right, but doing it in
            // silence is what made a correct import read as a broken one: a user
            // whose workbook held Left Analog, Drive and Reference Card reported
            // that the second and third tabs "will not import", and the app had
            // said nothing about either one.
            //
            // A1 is asked first, because the tab name is a hint and A1 is the
            // truth. "Voice" is a fine name for a mode, the CSV has nowhere to
            // put a tab name and the device never sees one, so a tab that says
            // "Profile Name" comes in whatever it is called. Deciding by name
            // alone would have thrown a real mode away and told the user in the
            // same breath that nothing was lost.
            //
            // The name is recorded and the cells are dropped. Nothing is ever
            // offered from a helper tab, so its rows are not counted against the
            // workbook's budget below, where documentation could truncate a real
            // mode further down the file.
            if (!keyword && !flatProfile && named)
            {
                skipped.Add(new SkippedTab(name, Array.Empty<string[]>(), SkippedTabKind.Helper));
                continue;
            }

            if (keyword || flatProfile)
            {
                // A blank line between tabs, because that is what the device
                // needs: it ends a mode at an empty line and only looks for the
                // next sheet keyword on the line after one. Stacking the tabs
                // straight onto each other left the second tab's rows being read
                // as more bindings of the first mode, and a tab whose A1 only
                // loosely matches ("GTA Profile") was then folded in without a
                // word instead of being named as a sheet the device will skip.
                // Saving used to put this line in; putting it in at import means
                // what the app shows is what the device would read.
                if (rows.Count > 0) rows.Add(Array.Empty<string>());
                int keywordRow = rows.Count;
                rows.AddRange(grid);
                // A flat tab is the whole file and its title is the file name,
                // so only a mode tab is a candidate for its own tab's name.
                if (keyword && Vocab.KeywordToType(grid[0][0].Trim()) == SheetType.ProfileName)
                    modes.Add((keywordRow, name.Trim(), grid[0].Length > 2 ? grid[0][2].Trim() : ""));
            }
            else if (LooksLikeBindings(grid)) skipped.Add(new SkippedTab(name, grid));
            else continue; // nothing was retained, so nothing was spent

            // Skipped tabs are held on to as well as imported ones, so both
            // count against the workbook's budget. Stopping here rather than
            // part way through a tab keeps every mode that did come in whole.
            kept += grid.Count;
            if (kept < MaxWorkbookRows) continue;
            // Said, not just done. The bounds exist because a 25 KB download
            // could expand into gigabytes, but a stop with nothing said is how
            // a profile arrives short a mode and the import still gets called
            // clean, which is the worst thing this window can tell a user.
            int left = parts.Count - 1 - i;
            if (left > 0)
                limit = Truncated(
                    string.Format(CultureInfo.CurrentCulture, Strings.Sheet_WorkbookPastTheRowCap,
                        MaxWorkbookRows.ToString("N0", CultureInfo.CurrentCulture)),
                    // A count would be a lie when the tab cap has already
                    // fired: the tabs past that one are not in this list to be
                    // counted, so the message names none rather than too few.
                    limit is not null ? Strings.Sheet_EveryTabFromThereOn
                    : left == 1 ? Strings.Sheet_OneMoreTabWas : string.Format(CultureInfo.CurrentCulture, Strings.Sheet_LeftMoreTabsWere, left));
            break;
        }

        // Names go in before the CSV is written from the rows.
        var renamed = NameModesFromTabs(rows, modes);
        return new XlsxImport(Csv.Write(rows), skipped, limit, renamed);
    }

    /// <summary>Where the community keeps a mode's name: on the sheet tab. C1
    /// is the template's leftover ("Left Joystick" on every tab of a workbook
    /// whose tabs say Menu, Driving, Shooting), and it is what both this app
    /// and the device read, so a profile full of real names listed as three
    /// copies of one wrong one.
    ///
    /// C1 is written, not just displayed. A name shown in the editor and gone
    /// again on the next save would be its own kind of lying, and this cell is
    /// a label: the device reads it as the mode's name and nothing else, so no
    /// binding and no behaviour turns on it. Every rename is reported, and the
    /// user's own undo covers it like any other edit.
    ///
    /// A name the user chose is never touched: a tab whose C1 says something of
    /// its own keeps it.</summary>
    static List<TabRename> NameModesFromTabs(List<string[]> rows, List<(int Row, string Tab, string C1)> modes)
    {
        var renames = new List<TabRename>();
        // Two tabs with one C1 is the copy-paste that leaves a whole workbook
        // named after whichever mode was duplicated first.
        var shared = modes.GroupBy(m => m.C1, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < modes.Count; i++)
        {
            var (row, tab, c1) = modes[i];
            bool worthReplacing = c1.Length == 0 || GenericModeNames.Contains(c1) || shared.Contains(c1);
            if (!worthReplacing) continue;
            if (tab.Length == 0 || GenericTabName(tab) || tab.Equals(c1, StringComparison.OrdinalIgnoreCase))
                continue;

            var cells = (string[])rows[row].Clone();
            if (cells.Length < 3)
            {
                Array.Resize(ref cells, 3);
                for (int c = 0; c < cells.Length; c++) cells[c] ??= "";
            }
            cells[2] = tab;
            rows[row] = cells;
            renames.Add(new TabRename(i + 1, tab, c1));
        }
        return renames;
    }

    // The names a mode carries because nobody changed them: the shipped
    // template's, and the ones the community workbooks copy around.
    static readonly HashSet<string> GenericModeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Left Joystick", "Right Joystick", "Mouse Mode", Strings.Sheet_LeftJoy, Strings.Sheet_RightJoy,
        "Solo", "Mode", "Profile", "Profile Name", "Sheet1",
    };

    // A tab nobody has named either. Replacing "Solo" with "Profile", or
    // "Left Joystick" with "Sheet2", is a worse name, not a truer one.
    static bool GenericTabName(string tab) =>
        SheetNumberPattern().IsMatch(tab) || GenericModeNames.Contains(tab.Trim());

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*(sheet|tab|page)\s*\d*\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SheetNumberPattern();

    // The same ending on both bounds, because the user's question is the same
    // one either way: what am I missing, and what do I do about it.
    static string Truncated(string cause, string missed) =>
        string.Format(CultureInfo.CurrentCulture, Strings.Sheet_CauseMissedNotReadAt, cause, missed);

    /// <summary>The tab's rows as a mode the app and the device would both
    /// read: A1 given the keyword it is missing, and the tab's own name used
    /// as the mode name when the sheet does not carry one. Nothing else is
    /// touched, so what comes in is the user's own layout.
    ///
    /// Only <see cref="SkippedTabKind.UnreadableA1"/> tabs have anything to
    /// repair. A helper tab carries no cells, so this returns nothing for one
    /// and cannot invent a mode out of documentation.</summary>
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
    // reference card or a scratch tab has none. That is the whole test, so one
    // is the count: a menu or a voice layer is two bindings, and asking for
    // three dropped those tabs in silence while the review still called the
    // import clean. A tab with no function in column B is still passed over
    // without a word, which is what keeps the message worth reading.
    static bool LooksLikeBindings(List<string[]> grid) =>
        grid.Any(r => r.Length > 1 && Vocab.FunctionArity.ContainsKey(r[1].Trim()));

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
            // MaxSheets is applied by the caller, not here: a cap that stops
            // this enumerator cannot tell anyone how many tabs it stopped
            // short of, and an unread tab has to be counted to be reported.
            // The list is still bounded, because a part is only listed once.
            yield return ((string?)sheet.Attribute("name") ?? "", part);
        }
    }

    static List<string[]> Sheet(ZipArchive zip, string part, string[] shared) =>
        Sheet(zip, part, shared, out _);

    /// <param name="lostRows">True when a row holding something was dropped for
    /// sitting past the row cap. A stray cell left at the bottom of a Google
    /// sheet is dropped too and does not count: it is blank, nothing was in it,
    /// and saying so on every ordinary import would be noise.</param>
    static List<string[]> Sheet(ZipArchive zip, string part, string[] shared, out bool lostRows)
    {
        lostRows = false;
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
            if (number > MaxRows || number < 1)
            {
                if (cells.Count > 0) lostRows = true;
                continue;
            }
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
                string.Format(CultureInfo.CurrentCulture, Strings.Sheet_ThatSpreadsheetHoldsAPart, entry.Length / (1024 * 1024)));
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, PartLimits);
        return XDocument.Load(reader);
    }
}
