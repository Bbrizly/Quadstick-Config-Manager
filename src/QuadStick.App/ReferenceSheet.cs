using System.Globalization;
using System.Net;
using System.Text;
using QuadStick.Format;

namespace QuadStick.App;

// The page somebody keeps by the chair: every control, every mode, on paper in
// black and white. Avalonia cannot print, so this is one self contained HTML
// file for a browser to print. No script, no external resource, and a policy
// that forbids both, because a profile from the community catalog is untrusted
// and everything in it goes through Enc before it reaches the page. Nothing from
// the file is ever written into an attribute.
internal static class ReferenceSheet
{
    // More columns than this and the text stops being readable on a landscape
    // page, so a big profile takes more paper rather than smaller print.
    internal const int ModesPerTable = 6;

    sealed class Row
    {
        public required string Zone { get; init; }
        public required string Label { get; init; }
        public required int Rank { get; init; }
        public required bool Sequence { get; init; }
        public required List<string>[] Cells { get; init; }
    }

    static string Enc(string text) => WebUtility.HtmlEncode(text);

    /// <param name="xboxNames">Words the buttons the way the Xbox style does.
    /// Raw list names are never used here: this page is for the person in the
    /// chair, not for somebody checking the file.</param>
    internal static string Build(ProfileDocument doc, DateTime date, bool xboxNames = false)
    {
        // By position. Two modes may share a name, and the firmware counts
        // Profile Name segments and never reads the name.
        var modes = doc.Sheets.Where(s => s.Type == SheetType.ProfileName).ToList();
        var rows = Rows(modes, xboxNames);
        var title = doc.HeaderName.Length > 0 ? doc.HeaderName
            : doc.CsvFileName is { Length: > 0 } file ? Path.GetFileNameWithoutExtension(file)
            : doc.Title.Length > 0 ? doc.Title
            : Strings.Sheet_Untitled;
        var ui = CultureInfo.CurrentUICulture;
        var lang = ui.Name.Length > 0 ? ui.Name : "en";
        var dir = ui.TextInfo.IsRightToLeft ? "rtl" : "ltr";
        var when = date.ToString("d", CultureInfo.CurrentCulture) + " · " + Plural.Of(modes.Count, "Count_Mode");

        var page = new StringBuilder();
        page.Append($$"""
            <!doctype html>
            <html lang={{lang}} dir={{dir}}>
            <head>
            <meta charset=utf-8>
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'">
            <title>{{Enc(title)}}</title>
            <style>
            @page { size: landscape; margin: 12mm; }
            html { background: #fff; color: #000; }
            body { font: 11pt/1.3 Arial, Helvetica, sans-serif; margin: 12mm; }
            @media print { body { margin: 0; } .hint { display: none; } }
            h1 { font-size: 18pt; margin: 0; }
            h2 { font-size: 12pt; margin: 14pt 0 4pt; }
            p { margin: 2pt 0; }
            table { border-collapse: collapse; width: 100%; table-layout: fixed; margin-top: 10pt; }
            table + table { break-before: page; }
            caption { text-align: start; font-weight: bold; padding-bottom: 3pt; }
            th, td { border: 1px solid #000; padding: 3pt 4pt; text-align: start; vertical-align: top; overflow-wrap: anywhere; }
            thead { display: table-header-group; }
            tr { break-inside: avoid; }
            .group { break-after: avoid; }
            .group th { border-top: 2.5pt solid #000; font-size: 12pt; }
            tbody td div + div { margin-top: 3pt; }
            dt { font-weight: bold; }
            dd { margin: 0 0 3pt; }
            </style>
            </head>
            <body>
            <h1>{{Enc(title)}}</h1>
            <p>{{Enc(when)}}</p>
            <p class=hint>{{Enc(Strings.Sheet_PrintHint)}}</p>

            """);

        if (modes.Count == 0) Paragraph(page, Strings.Sheet_NoModes);
        else if (rows.Count == 0) Paragraph(page, Strings.Sheet_NoControls);
        else
        {
            for (int first = 0; first < modes.Count; first += ModesPerTable)
                Table(page, modes, rows, first, Math.Min(ModesPerTable, modes.Count - first));
            Paragraph(page, Strings.Sheet_DashMeans);
            if (rows.Any(r => r.Sequence)) Paragraph(page, Strings.Sheet_SequenceNote);
        }
        if (doc.Sheets.Count > modes.Count) Paragraph(page, Strings.Sheet_OtherSheetsNotListed);
        Legend(page, modes);
        page.Append("</body>\n</html>\n");
        return page.ToString();
    }

    static void Paragraph(StringBuilder page, string text) =>
        page.Append("<p>").Append(Enc(text)).Append("</p>\n");

    static void Table(StringBuilder page, List<ModeSheet> modes, List<Row> rows, int first, int count)
    {
        page.Append("<table>\n");
        if (modes.Count > ModesPerTable)
            page.Append("<caption>").Append(Enc(string.Format(CultureInfo.CurrentCulture,
                Strings.Sheet_ModesRange, first + 1, first + count))).Append("</caption>\n");
        page.Append($$"""
            <thead><tr><th scope=col>{{Enc(Strings.Sheet_Control)}}</th>
            """);
        for (int m = first; m < first + count; m++)
            page.Append($$"""
                <th scope=col>{{Enc(ModeHeading(m, modes[m].ModeName))}}</th>
                """);
        page.Append("</tr></thead>\n");

        foreach (var zone in MainWindow.AllZones)
        {
            var inZone = rows.Where(r => r.Zone == zone.Id)
                .OrderBy(r => r.Sequence).ThenBy(r => r.Rank)
                .ThenBy(r => r.Label, StringComparer.Ordinal).ToList();
            if (inZone.Count == 0) continue;
            page.Append($$"""
                <tbody><tr class=group><th colspan={{count + 1}} scope=rowgroup>{{Enc(zone.Title)}}</th></tr>

                """);
            foreach (var row in inZone)
            {
                page.Append($$"""
                    <tr><th scope=row>{{Enc(row.Label)}}</th>
                    """);
                for (int m = first; m < first + count; m++)
                {
                    var lines = row.Cells[m];
                    page.Append("<td>");
                    if (lines.Count == 0) page.Append('-');
                    else foreach (var line in lines) page.Append("<div>").Append(line).Append("</div>");
                    page.Append("</td>");
                }
                page.Append("</tr>\n");
            }
            page.Append("</tbody>\n");
        }
        page.Append("</table>\n");
    }

    static string ModeHeading(int index, string name)
    {
        var number = (index + 1).ToString(CultureInfo.InvariantCulture);
        var heading = name.Length > 0 ? number + " " + name : number;
        // Still a column, because nothing in the file is hidden from this page.
        return index < ProfileFile.MaxDeviceModes ? heading
            : string.Format(CultureInfo.CurrentCulture, Strings.Sheet_ModeNeverLoaded, heading);
    }

    // One row per control, or per ordered run of controls, across all modes.
    static List<Row> Rows(List<ModeSheet> modes, bool xboxNames)
    {
        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        for (int m = 0; m < modes.Count; m++)
            foreach (var b in modes[m].Bindings)
            {
                var (key, zone, label, rank, sequence) = Place(b, xboxNames);
                if (!rows.TryGetValue(key, out var row))
                    rows[key] = row = new Row
                    {
                        Zone = zone, Label = label, Rank = rank, Sequence = sequence,
                        Cells = modes.Select(_ => new List<string>()).ToArray(),
                    };
                row.Cells[m].Add(Line(b, xboxNames));
            }
        return rows.Values.ToList();
    }

    static (string Key, string Zone, string Label, int Rank, bool Sequence) Place(Binding b, bool xboxNames)
    {
        // A setting row has no inputs: its column C is the value (Configuration.c,
        // the preference branch). It belongs under the mode's settings, by name.
        if (Vocab.IsPreferenceOverride(b.Output, b.Function))
            return ("setting\n" + b.Output, "settings", OutputName(b.Output, xboxNames), 0, false);

        // What the firmware keeps of a row's inputs (Configuration.c, the parsing
        // loop): each cell is looked up, a none or a name it does not know becomes
        // _NONE, and the _NONEs are shifted out. So those do not count towards a
        // sequence, but a name it does not know is still shown and still said.
        var named = b.Inputs.Where(i => i.Length > 0 && i != Vocab.NoneInput).ToList();
        var known = named.Where(Vocab.AllInputs.Contains).ToList();
        var unknown = named.Where(i => !Vocab.AllInputs.Contains(i)).ToList();
        if (named.Count == 0)
            return ("unset", "unset", Strings.Main_NoInput, 0, false);

        var zone = MainWindow.ZoneOf(named[0]);
        var parts = new List<string>();
        // Several known inputs are a sequence in time, never a chord: DataFlow.c
        // compares them with the inputs last used, newest first, and a row of more
        // than one stays off until the whole run matches. A chord is its own input
        // name (mp_left_center_sip) and is one control here.
        if (known.Count > 0)
        {
            zone = MainWindow.ZoneOf(known[0]);
            var z = zone;
            parts.Add(string.Join(Strings.Sheet_ThenJoiner, known.Select(i => MainWindow.FriendlyInput(i, z))));
        }
        parts.AddRange(unknown.Select(i =>
            string.Format(CultureInfo.CurrentCulture, Strings.Sheet_UnknownInput, i)));

        int rank = known.Count > 0 ? MainWindow.InputSuggestions.IndexOf(known[0]) : int.MaxValue;
        return ("input\n" + string.Join("\n", known) + "\n\n" + string.Join("\n", unknown),
            zone, string.Join(", ", parts), rank, known.Count > 1);
    }

    // Already encoded: this goes into a cell as markup, for the bold name.
    static string Line(Binding b, bool xboxNames)
    {
        if (Vocab.IsPreferenceOverride(b.Output, b.Function))
            return Enc(b.Inputs.Count > 0 ? b.Inputs[0] : Strings.Main_NoValue);

        var line = new StringBuilder();
        if (b.ActionName.Length > 0)
            line.Append("<strong>").Append(Enc(b.ActionName)).Append("</strong>: ");
        line.Append(Enc(OutputName(b.Output, xboxNames)));
        if (FunctionWords(b.Function) is { Length: > 0 } words) line.Append(", ").Append(Enc(words));
        return line.ToString();
    }

    // The words the mapping cards use, through the resolver they use, which is
    // what turns "Kb w" into the name on the key. A token the vocabulary does not
    // have has no friendly name to give, and tidying it would make up a name the
    // file does not hold, so it shows as written.
    static string OutputName(string token, bool xboxNames) =>
        token.Length == 0 ? Strings.Main_NothingYet
        : Vocab.AllOutputs.Contains(token) || Vocab.IsPreferenceOverride(token, "")
            ? OutputVisuals.For(token, t => MainWindow.FriendlyToken(t, xboxNames),
                xboxNames ? ControllerPromptStyle.Xbox : null).FriendlyLabel
            : token;

    static string FunctionName(string function) =>
        function.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    // Empty for plain held-down behaviour, which is what nobody needs telling.
    static string FunctionWords(string function)
    {
        var f = function.Trim();
        if (f.Length == 0 || f == "normal") return "";
        return MainWindow.FunctionExplain(f).Length > 0 ? f.Replace('_', ' ') : f;
    }

    // Each behaviour this profile uses, in the sentence the editor explains it
    // with, so "delayed latch" on paper is not left for somebody to guess at.
    static void Legend(StringBuilder page, List<ModeSheet> modes)
    {
        var used = modes.SelectMany(m => m.Bindings)
            .Where(b => !Vocab.IsPreferenceOverride(b.Output, b.Function))
            .Select(b => FunctionName(b.Function))
            .Where(n => n.Length > 0 && n != "normal" && MainWindow.FunctionExplain(n).Length > 0)
            .Distinct(StringComparer.Ordinal).ToList();
        if (used.Count == 0) return;
        page.Append("<h2>").Append(Enc(Strings.Sheet_WhatTheWordsMean)).Append("</h2>\n<dl>\n");
        foreach (var name in used)
            page.Append("<dt>").Append(Enc(name.Replace('_', ' '))).Append("</dt><dd>")
                .Append(Enc(MainWindow.FunctionExplain(name))).Append("</dd>\n");
        page.Append("</dl>\n");
    }
}
