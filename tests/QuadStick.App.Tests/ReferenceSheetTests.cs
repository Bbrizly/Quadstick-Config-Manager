using System.Text.RegularExpressions;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// The page that goes on paper by the chair. A wrong column here is somebody
// reaching for a control that does something else in that mode.
public class ReferenceSheetTests
{
    static readonly DateTime Day = new(2026, 10, 30);

    static ModeSheet Mode(string name, params Binding[] bindings)
    {
        var sheet = new ModeSheet { Type = SheetType.ProfileName, ModeName = name, CsvFileName = "game.csv" };
        sheet.Bindings.AddRange(bindings);
        return sheet;
    }

    static Binding Bind(string output, string function, params string[] inputs) =>
        new(0, output, function, inputs, inputs.Select((_, i) => i + 2).ToArray());

    static ProfileDocument Doc(params ModeSheet[] sheets)
    {
        var doc = new ProfileDocument();
        doc.Sheets.AddRange(sheets);
        return doc;
    }

    static string Build(params ModeSheet[] sheets) => ReferenceSheet.Build(Doc(sheets), Day);

    static List<string> Tables(string html) =>
        Regex.Matches(html, "<table>.*?</table>", RegexOptions.Singleline).Select(m => m.Value).ToList();

    static List<string> Headers(string html) =>
        Regex.Matches(html, "<th scope=col>(.*?)</th>").Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();

    // The cells of the row with this label, in column order, with the label left out.
    static List<string> Cells(string html, string label)
    {
        var row = Regex.Match(html, "<tr><th scope=row>" + Regex.Escape(System.Net.WebUtility.HtmlEncode(label)) + "</th>(.*?)</tr>");
        Assert.True(row.Success, "no row called " + label + " in:\n" + html);
        return Regex.Matches(row.Groups[1].Value, "<td>(.*?)</td>").Select(m => m.Groups[1].Value).ToList();
    }

    const string Script = "<script>alert(1)</script>";
    const string Breakout = "\"><img onerror=alert(1) src=x>";

    [Theory]
    [InlineData(Script)]
    [InlineData(Breakout)]
    public void Text_from_the_file_cannot_become_markup(string evil)
    {
        var sheet = new ModeSheet { Type = SheetType.ProfileName, ModeName = evil, CsvFileName = evil };
        sheet.Bindings.Add(new Binding(0, "x", "normal", new[] { "lip" }, new[] { 2 }, evil));
        sheet.Bindings.Add(Bind(evil, evil, evil));
        var doc = Doc(sheet);
        doc.HeaderName = evil;

        var html = ReferenceSheet.Build(doc, Day);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(evil), html);
    }

    [Fact]
    public void The_page_loads_nothing_and_says_it_will_not()
    {
        var html = Build(Mode("A", Bind("x", "normal", "lip")));

        Assert.Contains("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<link", html);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
        Assert.DoesNotContain("url(", html);
        Assert.Matches("<html lang=[A-Za-z-]+ dir=(ltr|rtl)>", html);
    }

    [Fact]
    public void Printing_settings_are_there()
    {
        var html = Build(Mode("A", Bind("x", "normal", "lip")));

        Assert.Contains("size: landscape", html);
        Assert.Contains("thead { display: table-header-group; }", html);
        Assert.Contains("tr { break-inside: avoid; }", html);
        Assert.Contains("@media print", html);
        Assert.Contains("class=hint", html);
        Assert.Contains("<thead>", html);
        Assert.Contains("<th scope=row>", html);
    }

    [Fact]
    public void Two_modes_with_one_name_stay_two_columns()
    {
        var html = Build(Mode("Drive", Bind("x", "normal", "lip")), Mode("Drive", Bind("circle", "normal", "lip")));

        Assert.Equal(new[] { "Control", "1 Drive", "2 Drive" }, Headers(html));
        var cells = Cells(html, "Lip");
        Assert.Equal("X", Assert.IsType<string>(Strip(cells[0])));
        Assert.Equal("Circle", Strip(cells[1]));
    }

    static string Strip(string cell) => Regex.Replace(cell, "<[^>]+>", " ").Trim();

    [Fact]
    public void A_row_bound_only_in_mode_3_lands_in_column_3()
    {
        var html = Build(Mode("A", Bind("x", "normal", "up")), Mode("B"), Mode("C", Bind("circle", "normal", "lip")));

        Assert.Equal(new[] { "-", "-", "<div>Circle</div>" }, Cells(html, "Lip"));
    }

    [Fact]
    public void A_mode_with_nothing_in_it_reads_as_dashes_not_as_a_mistake()
    {
        var html = Build(Mode("A", Bind("x", "normal", "up"), Bind("circle", "normal", "lip")), Mode("Empty"));

        Assert.Equal("-", Cells(html, "Up")[1]);
        Assert.Equal("-", Cells(html, "Lip")[1]);
    }

    // Configuration.c lists a row's inputs newest last and DataFlow.c matches
    // them newest first, so file order is the order they are done in.
    [Fact]
    public void A_sequence_is_its_own_ordered_row_and_no_part_of_it_fires_alone()
    {
        var html = Build(Mode("A",
            Bind("x", "normal", "mp_left_sip", "mp_left_puff"),
            Bind("circle", "normal", "lip")));

        Assert.Equal(new[] { "<div>X</div>" }, Cells(html, "Sip, then Puff"));
        Assert.DoesNotContain("<th scope=row>Sip</th>", html);
        Assert.DoesNotContain("<th scope=row>Puff</th>", html);
        Assert.Contains("one after the other", html);
    }

    [Fact]
    public void A_sequence_across_parts_names_the_other_part()
    {
        var html = Build(Mode("A", Bind("x", "normal", "lip", "mp_center_sip")));

        Assert.Equal(new[] { "<div>X</div>" }, Cells(html, "Lip, then Center · sip"));
    }

    [Fact]
    public void A_sequence_and_the_same_input_alone_are_two_rows()
    {
        var html = Build(Mode("A",
            Bind("x", "normal", "mp_left_sip"),
            Bind("circle", "normal", "mp_left_sip", "mp_left_puff")));

        Assert.Equal(new[] { "<div>X</div>" }, Cells(html, "Sip"));
        Assert.Equal(new[] { "<div>Circle</div>" }, Cells(html, "Sip, then Puff"));
    }

    // Configuration.c turns none, and any word it does not know, into _NONE and
    // shifts those out, so what is left is what the device runs.
    [Fact]
    public void A_none_cell_does_not_make_a_sequence()
    {
        var html = Build(Mode("A", Bind("x", "normal", "lip", "none")));

        Assert.Equal(new[] { "<div>X</div>" }, Cells(html, "Lip"));
        Assert.DoesNotContain("then", Regex.Match(html, "<tbody>.*</tbody>", RegexOptions.Singleline).Value);
    }

    [Fact]
    public void An_input_name_the_device_does_not_know_shows_as_written_and_says_so()
    {
        var html = Build(Mode("A", Bind("x", "normal", "lip", "Not_A_Real_Input")));

        Assert.Contains("Not_A_Real_Input", html);
        Assert.Contains("skips it", html);
        Assert.DoesNotContain("then Not_A_Real_Input", html);
    }

    [Fact]
    public void Twenty_modes_make_four_tables_each_with_the_control_column()
    {
        var modes = Enumerable.Range(1, 20).Select(i => Mode("M" + i, Bind("x", "normal", "lip"))).ToArray();
        var tables = Tables(Build(modes));

        Assert.Equal(4, tables.Count);
        Assert.All(tables, t => Assert.Equal("Control", Headers(t)[0]));
        Assert.Equal(new[] { 7, 7, 7, 3 }, tables.Select(t => Headers(t).Count));
        Assert.Equal("7 M7", Headers(tables[1])[1]);
        Assert.Equal(string.Format(Strings.Sheet_ModePastLimit, "20 M20"), Headers(tables[3])[2]);
    }

    [Fact]
    public void Preferences_and_infrared_are_not_columns_and_the_page_says_they_are_left_out()
    {
        var prefs = new ModeSheet { Type = SheetType.Preferences, ModeName = "PrefsTab" };
        prefs.Bindings.Add(Bind("x", "normal", "up"));
        var ir = new ModeSheet { Type = SheetType.Infrared, ModeName = "IrTab" };
        var html = Build(prefs, Mode("Only", Bind("circle", "normal", "lip")), ir);

        Assert.Equal(new[] { "Control", "1 Only" }, Headers(html));
        Assert.DoesNotContain("PrefsTab", html);
        Assert.DoesNotContain("IrTab", html);
        Assert.DoesNotContain("<th scope=row>Up</th>", html);
        Assert.Contains("not listed here", html);
    }

    [Fact]
    public void An_output_with_no_friendly_name_shows_as_written()
    {
        var html = Build(Mode("A", Bind("not_a_real_output", "normal", "lip")));

        Assert.Equal(new[] { "<div>not_a_real_output</div>" }, Cells(html, "Lip"));
    }

    [Fact]
    public void A_file_token_with_a_friendly_name_is_never_shown_raw()
    {
        var html = Build(Mode("A", Bind("increment_mode", "normal", "lip"), Bind("left_1", "normal", "up")));

        Assert.DoesNotContain("increment_mode", html);
        Assert.DoesNotContain("left_1", html);
        Assert.Contains("Increment mode", html);
    }

    [Fact]
    public void A_key_reads_by_the_name_on_the_key()
    {
        var html = Build(Mode("A", Bind("kb_w", "normal", "lip")));

        Assert.Equal(new[] { "<div>" + OutputVisuals.For("kb_w").KeycapText + "</div>" }, Cells(html, "Lip"));
        Assert.DoesNotContain("Kb w", html);
    }

    [Fact]
    public void Xbox_style_names_the_buttons_the_xbox_way()
    {
        var html = ReferenceSheet.Build(Doc(Mode("A", Bind("x", "normal", "lip"))), Day, xboxNames: true);

        Assert.Equal(new[] { "<div>" + Strings.Main_AButton + "</div>" }, Cells(html, "Lip"));
    }

    [Fact]
    public void A_cell_has_the_output_the_name_and_the_function_in_words()
    {
        var html = Build(Mode("A", new Binding(0, "x", "delayed_latch", new[] { "lip" }, new[] { 2 }, "Jump")));

        Assert.Equal(new[] { "<div><strong>Jump</strong>: X, delayed latch</div>" }, Cells(html, "Lip"));
        // And the words are explained: nothing is left for somebody to guess at.
        Assert.Contains("<dt>delayed latch</dt>", html);
    }

    [Fact]
    public void Normal_is_not_written_out_and_two_bindings_get_two_lines()
    {
        var html = Build(Mode("A", Bind("x", "normal", "lip"), Bind("circle", "", "lip")));

        Assert.Equal(new[] { "<div>X</div><div>Circle</div>" }, Cells(html, "Lip"));
        Assert.DoesNotContain("normal", Regex.Match(html, "<body>.*", RegexOptions.Singleline).Value);
    }

    [Fact]
    public void A_binding_with_no_input_goes_under_its_own_heading()
    {
        var html = Build(Mode("A", Bind("x", "normal"), Bind("circle", "normal", "none")));

        Assert.Contains(">" + Strings.Main_NoInputYet + "</th>", html);
        Assert.Equal(new[] { "<div>X</div><div>Circle</div>" }, Cells(html, Strings.Main_NoInput));
    }

    [Fact]
    public void A_mode_setting_shows_its_value_under_settings_and_is_not_read_as_an_input()
    {
        var html = Build(Mode("A", Bind("mouse_speed", "", "50")));

        Assert.Contains(">" + Strings.Main_ModeSettings + "</th>", html);
        Assert.Equal(new[] { "<div>50</div>" }, Cells(html, "Mouse speed"));
    }

    [Fact]
    public void Groups_run_in_device_order_with_their_translated_titles()
    {
        var html = Build(Mode("A", Bind("x", "normal", "lip"), Bind("circle", "normal", "up"), Bind("square", "normal", "mp_left_sip")));

        int stick = html.IndexOf(">" + Strings.Main_Joystick + "</th>", StringComparison.Ordinal);
        int hole = html.IndexOf(">" + Strings.Main_LeftMouthpieceHole + "</th>", StringComparison.Ordinal);
        int lip = html.IndexOf(">" + Strings.Main_LipSwitch + "</th>", StringComparison.Ordinal);
        Assert.True(stick > 0 && stick < hole && hole < lip, html);
    }

    [Fact]
    public void A_profile_with_no_modes_says_so()
    {
        var prefs = new ModeSheet { Type = SheetType.Preferences, ModeName = "P" };
        Assert.Contains(Strings.Sheet_NoModes, System.Net.WebUtility.HtmlDecode(Build(prefs)));
        Assert.Empty(Tables(Build(prefs)));
    }

    [Fact]
    public void Nothing_is_signalled_by_colour()
    {
        var html = Build(Mode("A", Bind("x", "toggle", "lip")));
        var colours = Regex.Matches(Regex.Match(html, "<style>.*</style>", RegexOptions.Singleline).Value, @"#[0-9a-fA-F]{3,6}\b").Select(m => m.Value).Distinct().OrderBy(x => x);

        Assert.Equal(new[] { "#000", "#fff" }, colours);
    }

    static MainWindow Open(ProfileFile? file)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        if (file is not null) { file.Dirty = false; w.LoadProfile(file); }
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    static Button Button(MainWindow w) => w.FindControl<Button>("PrintSheetButton")!;

    static string StatusLine(MainWindow w) =>
        string.Join(" ", w.GetVisualDescendants().OfType<ContentControl>()
            .Where(c => c.Name == "StatusHost")
            .SelectMany(c => c.GetVisualDescendants().OfType<TextBlock>())
            .Select(t => t.Text ?? ""));

    [AvaloniaFact]
    public void The_button_says_what_it_is()
    {
        var file = ProfileFile.NewFromTemplate("mygame.csv");
        var w = Open(file);
        var b = Button(w);

        Assert.Equal(Strings.Shell_MakeAPrintableSheet, AutomationProperties.GetName(b));
        Assert.Equal(Strings.Shell_PrintAReferenceSheet, ToolTip.GetTip(b));
        Assert.Single(b.GetVisualDescendants().OfType<PathIcon>());
        w.Close();
    }

    [AvaloniaFact]
    public void With_no_file_open_the_button_only_says_to_open_one()
    {
        var w = Open(null);

        Ui.ClickEvenIfDisabled(Button(w));
        Dispatcher.UIThread.RunJobs();

        // Home has no status bar on screen, so read what was put in it.
        var chip = (Control)w.FindControl<ContentControl>("StatusHost")!.Content!;
        Assert.Contains(chip.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == Strings.Main_OpenOrCreateAProfile);
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_sheet_reads_the_live_document_and_a_launch_failure_is_said_with_the_path()
    {
        var dir = Directory.CreateTempSubdirectory("qcm-sheet-").FullName;
        var path = Path.Combine(dir, "sheet.html");
        var file = ProfileFile.Load("Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\nx,normal,lip\n");
        var w = Open(file);
        // Edited and not saved. The headless launcher opens nothing and says so.
        file.SetCell(file.Document.Sheets[0].Bindings[0].Row, 0, "circle");

        await w.WriteAndOpenReferenceSheetAsync(path);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("Circle", File.ReadAllText(path));
        Assert.Contains(path, StatusLine(w));
        Assert.Contains("could not open", StatusLine(w));
        file.Dirty = false;
        w.Close();
        Directory.Delete(dir, true);
    }

    [AvaloniaFact]
    public async Task A_write_failure_is_said_with_the_path()
    {
        var path = Path.Combine(Path.GetTempPath(), "qcm-no-such-folder-" + Guid.NewGuid().ToString("N"), "sheet.html");
        var file = ProfileFile.NewFromTemplate("mygame.csv");
        var w = Open(file);

        await w.WriteAndOpenReferenceSheetAsync(path);
        Dispatcher.UIThread.RunJobs();

        Assert.False(File.Exists(path));
        Assert.Contains(path, StatusLine(w));
        file.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void A_mode_past_the_limit_stays_a_column_and_says_its_controls_are_not_read()
    {
        var modes = Enumerable.Range(1, 17)
            .Select(i => Mode("M" + i, Bind("x", "normal", "lip"))).ToArray();

        var headers = Headers(Build(modes)).Where(h => h != Strings.Sheet_Control).ToList();

        Assert.Equal(17, headers.Count);
        Assert.Equal("16 M16", headers[15]);
        Assert.Equal(string.Format(Strings.Sheet_ModePastLimit, "17 M17"), headers[16]);
    }
}
