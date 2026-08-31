using System.IO.Compression;
using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(QuadStick.App.Tests.TestAppBuilder))]

namespace QuadStick.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

// UI smoke tests: build the REAL window headlessly and drive its public
// seams. Every screen and every model must construct without throwing.
// A crash here is a crash a disabled user would have hit; these run in CI
// on every push, so a crash-on-click can never ship unnoticed again.
public class SmokeTests
{
    // The Drive button label lives on its Content StackPanel [dot, label], set
    // in RefreshDriveButton. Read it there, not the visual tree: when Google is
    // not configured the button is hidden, so no visual descendants are realized.
    static string DriveButtonWord(Button b) =>
        ((StackPanel)b.Content!).Children.OfType<TextBlock>().First(t => t.Text != "●").Text!;

    static MainWindow NewWindow()
    {
        // A fresh CI machine has no settings file, which would auto-start
        // the tutorial overlay and wall off the UI mid-test. Pre-mark it seen.
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        var w = new MainWindow();
        // Every import ends in a modal review, and nothing here would close it.
        // What the import HANDS the review is still recorded, in LastImportReview.
        w.ShowImportReview = (_, _) => Task.CompletedTask;
        w.Show();
        return w;
    }

    [AvaloniaFact]
    public void Window_opens_on_home_without_throwing()
    {
        var w = NewWindow();
        Assert.Contains("Quadstick: Config Manager", w.Title);
        w.Close();
    }

    // No .csv anywhere the user reads: home cards and the window title show
    // the bare profile name.
    [AvaloniaFact]
    public void Home_cards_show_names_without_csv()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qcm-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "mygame.csv"),
            ProfileFile.NewFromTemplate("mygame.csv").ToCsvText());
        var old = MainWindow.LibraryDir;
        MainWindow.LibraryDir = dir;
        try
        {
            var w = NewWindow();
            var texts = w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("mygame", texts);
            Assert.DoesNotContain("mygame.csv", texts);
            w.Close();
        }
        finally { MainWindow.LibraryDir = old; Directory.Delete(dir, recursive: true); }
    }

    // The profile name box shows just the name; the .csv extension is ours
    // to add. Typing it anyway must not double it up.
    [AvaloniaFact]
    public void Profile_name_shows_without_csv_and_gets_it_back_on_commit()
    {
        var w = NewWindow();
        var file = ProfileFile.NewFromTemplate("smoke.csv");
        w.LoadProfile(file);
        var box = w.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "FileNameBox");
        var park = w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SaveButton");
        Assert.Equal("smoke", box.Text);

        box.Text = "racing";
        box.Focus();
        park.Focus(); // commit fires when focus leaves the box
        Assert.Equal("racing.csv", file.Document.CsvFileName);
        Assert.Equal("racing", box.Text);

        box.Text = "gta.csv";
        box.Focus();
        park.Focus();
        Assert.Equal("gta.csv", file.Document.CsvFileName);
        Assert.Equal("gta", box.Text);

        file.Dirty = false;
        w.Close();
    }

    // A greyed ".csv" sits inside the right edge of the name box so users
    // can see the extension is added for them.
    [AvaloniaFact]
    public void Profile_name_box_shows_csv_suffix_hint()
    {
        var w = NewWindow();
        w.LoadProfile(ProfileFile.NewFromTemplate("smoke.csv"));
        var box = w.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "FileNameBox");
        var hint = Assert.IsType<TextBlock>(box.InnerRightContent);
        Assert.Equal(".csv", hint.Text);
        w.Close();
    }

    // The editor Share button opens a flyout with the two sharing actions.
    // Presence only; we never trigger a network call.
    [AvaloniaFact]
    public void Editor_share_button_flyout_has_both_actions()
    {
        var w = NewWindow();
        var file = ProfileFile.NewFromTemplate("smoke.csv");
        w.LoadProfile(file);
        var share = w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "ShareButton");
        var flyout = Assert.IsType<MenuFlyout>(share.Flyout);
        var headers = flyout.Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
        Assert.Contains("Copy share link", headers);
        Assert.Contains("Open in Google Sheets", headers);
        file.Dirty = false;
        w.Close();
    }

    // The home Drive button is the always-present status light: shown on any
    // build that can reach Google (a real client shipped), hidden only on a
    // placeholder build where backup could never work.
    [AvaloniaFact]
    public void Home_drive_button_shows_when_google_is_configured()
    {
        var w = NewWindow();
        var drive = w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "HomeDriveButton");
        Assert.Equal(GoogleAuth.IsConfigured, drive.IsVisible);
        w.Close();
    }

    // The status word matches the real state: off (no client), green (connected),
    // or yellow (needs sign-in). The coloured dot is the other TextBlock.
    [AvaloniaFact]
    public void Home_drive_button_word_matches_the_state()
    {
        var w = NewWindow();
        var drive = w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "HomeDriveButton");
        var word = DriveButtonWord(drive);
        var expected = !GoogleAuth.IsConfigured ? "Backup off"
            : w.DriveConnected ? "Backing up to Drive" : "Sign in to back up";
        Assert.Equal(expected, word);
        w.Close();
    }

    // In the yellow (needs sign-in) state one press only arms; the browser opens
    // on the second press, so a stray click never launches sign-in.
    [AvaloniaFact]
    public void Home_drive_button_arms_before_it_signs_in()
    {
        var w = NewWindow();
        if (!GoogleAuth.IsConfigured || w.DriveConnected) { w.Close(); return; } // only the yellow state arms
        var drive = w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "HomeDriveButton");
        Ui.Click(drive);
        var word = DriveButtonWord(drive);
        Assert.Equal("Press again to sign in", word);
        w.Close();
    }

    [AvaloniaFact]
    public void New_profile_opens_the_editor_and_every_zone_builds()
    {
        var w = NewWindow();
        w.LoadProfile(ProfileFile.NewFromTemplate("smoke.csv"));
        Assert.Contains("smoke", w.Title);
        Assert.DoesNotContain(".csv", w.Title); // the extension stays out of sight

        // Selecting every part of the device must never throw, mapped or not.
        foreach (var zone in new[]
                 { "joystick", "mp_left", "mp_center", "mp_right", "combo", "side", "lip", "jacks", "other", "unset" })
            w.SelectZoneForPreview(zone);
        w.Close();
    }

    [AvaloniaFact]
    public void Every_model_rebuilds_the_device_view_without_throwing()
    {
        var w = NewWindow();
        w.LoadProfile(ProfileFile.NewFromTemplate("smoke.csv"));
        for (int model = 0; model < 3; model++)
        {
            w.SetModelForPreview(model);
            w.SelectZoneForPreview("mp_center");
        }
        w.Close();
    }

    [AvaloniaFact]
    public void Label_style_cycles_through_all_three_without_throwing()
    {
        var w = NewWindow();
        w.LoadProfile(ProfileFile.NewFromTemplate("smoke.csv"));
        w.SelectZoneForPreview("mp_center");
        // plain English -> Xbox style -> raw list names -> back to plain
        for (int k = 0; k < 3; k++) w.CycleLabelStyleForPreview();
        w.Close();
    }

    [AvaloniaFact]
    public void Save_as_template_then_use_template_round_trips()
    {
        MainWindow.LibraryDir = Path.Combine(Path.GetTempPath(), "qs-tpl-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(MainWindow.TemplatesDir);
            var f = ProfileFile.NewFromTemplate("shooter.csv");
            File.WriteAllText(Path.Combine(MainWindow.TemplatesDir, "My FPS.csv"), f.ToCsvText());

            // A saved template must load back into the editor as a fresh copy.
            var w = NewWindow();
            var loaded = ProfileFile.Load(File.ReadAllText(Path.Combine(MainWindow.TemplatesDir, "My FPS.csv")));
            w.LoadProfile(loaded);
            Assert.NotEmpty(loaded.Document.Sheets);
            loaded.Dirty = false;
            w.Close();
        }
        finally
        { if (Directory.Exists(MainWindow.LibraryDir)) Directory.Delete(MainWindow.LibraryDir, recursive: true); }
    }

    [Theory]
    [InlineData("My FPS", "My FPS.csv")]
    [InlineData("shooter.csv", "shooter.csv")]
    [InlineData("bad/name:v2", "bad_name_v2.csv")]
    [InlineData("   ", "")]
    public void Template_names_are_made_safe(string input, string expected)
        => Assert.Equal(expected, MainWindow.SafeTemplateName(input));

    [AvaloniaFact]
    public void Editing_through_the_file_keeps_the_window_alive()
    {
        var w = NewWindow();
        var f = ProfileFile.NewFromTemplate("smoke.csv");
        w.LoadProfile(f);
        var b = f.Document.Sheets[0].Bindings[0];
        f.SetCell(b.Row, 0, "circle");          // edit an output
        f.NormalizeForDeviceCsv();               // the P0 case: rows shift
        w.LoadProfile(f);                        // window must rebind cleanly
        w.SelectZoneForPreview("mp_left");
        Assert.True(f.Document.HasVersionHeader);
        // Closing dirty pops the "save your changes?" dialog, which has no
        // user to answer it headlessly (that guard doing its job is exactly
        // why it hangs). Mark saved so Close proceeds.
        f.Dirty = false;
        w.Close();
    }

    // ---- The one Sheets import ----
    //
    // ImportSheetsAsync is the single workbook conversion in the app: the
    // pasted link on Home and a pick from the community catalog both call it.
    // These drive it directly with a fake HttpClient, so the real behaviour
    // (workbook first, CSV fallback, each scoped error, the multi-tab status)
    // is pinned without ever touching the network.

    // Answers from a script keyed by URL and records what was asked for.
    sealed class FakeSheets : HttpMessageHandler
    {
        public readonly List<string> Urls = new();
        readonly Func<string, HttpResponseMessage> _reply;
        public FakeSheets(Func<string, HttpResponseMessage> reply) => _reply = reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            return Task.FromResult(_reply(url));
        }
    }

    static HttpResponseMessage Body(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    static HttpResponseMessage Body(string text) => Body(Encoding.UTF8.GetBytes(text));

    const string SheetLink = "https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/edit#gid=7";

    // One mode tab, the smallest thing ProfileFile.Load calls a profile.
    static string[][] ModeTab(string name) => new[]
    {
        new[] { "Profile Name", "", name },
        new[] { "catalog.csv" },
        new[] { "PlayStation Outputs", "Function", "usb" },
        new[] { "dpad_N", "normal", "right_sip" },
    };

    // A real .xlsx: a zip of the three parts Xlsx.cs reads. Built here rather
    // than copied from a corpus so the App tests stay free of fixture files.
    static byte[] Workbook(params string[] tabNames)
    {
        static string Xml(string raw) => raw.Replace("&", "&amp;").Replace("<", "&lt;");
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Put(string path, string content)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }

            var sheets = new StringBuilder();
            var rels = new StringBuilder();
            for (int i = 0; i < tabNames.Length; i++)
            {
                sheets.Append($"<sheet name=\"{Xml(tabNames[i])}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
                rels.Append($"<Relationship Id=\"rId{i + 1}\" Target=\"worksheets/sheet{i + 1}.xml\"/>");

                var data = new StringBuilder();
                var rows = ModeTab(tabNames[i]);
                for (int r = 0; r < rows.Length; r++)
                {
                    data.Append($"<row r=\"{r + 1}\">");
                    for (int c = 0; c < rows[r].Length; c++)
                    {
                        if (rows[r][c].Length == 0) continue; // empty cells are absent in a real workbook
                        data.Append($"<c r=\"{(char)('A' + c)}{r + 1}\" t=\"inlineStr\"><is><t>{Xml(rows[r][c])}</t></is></c>");
                    }
                    data.Append("</row>");
                }
                Put($"xl/worksheets/sheet{i + 1}.xml",
                    "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>"
                    + data + "</sheetData></worksheet>");
            }

            Put("xl/workbook.xml",
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\""
                + " xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>"
                + sheets + "</sheets></workbook>");
            Put("xl/_rels/workbook.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + rels + "</Relationships>");
        }
        return buffer.ToArray();
    }

    static string HomeError(MainWindow w) =>
        w.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "HomeStatusText").Text ?? "";

    static bool ShowsStatus(MainWindow w, string fragment)
    {
        w.UpdateLayout();
        return w.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains(fragment) == true);
    }

    // The workbook comes first so a profile split across mode tabs arrives
    // whole. Every tab becomes a mode and the status says how many.
    [AvaloniaFact]
    public async Task Import_takes_the_whole_workbook_and_reports_every_mode()
    {
        var handler = new FakeSheets(_ => Body(Workbook("Walking", "Driving")));
        using var http = new HttpClient(handler);
        var w = NewWindow();

        await w.ImportSheetsAsync(SheetLink, http);

        Assert.Equal(2, w.OpenFile!.Document.Sheets.Count);
        Assert.Equal("Walking", w.OpenFile.Document.Sheets[0].ModeName);
        Assert.Equal("Driving", w.OpenFile.Document.Sheets[1].ModeName);
        // One request, for the whole workbook: the gid names one tab, so it goes.
        Assert.Equal(
            new[] { "https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/export?format=xlsx" },
            handler.Urls);
        Assert.True(ShowsStatus(w, "Imported 2 modes"));
        w.OpenFile.Dirty = false;
        w.Close();
    }

    // Importing opens the profile and stops there. Nothing is written to the
    // library and nothing is installed, so a catalog pick can never reach the
    // device by itself.
    [AvaloniaFact]
    public async Task Import_opens_the_profile_without_saving_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qcm-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var old = MainWindow.LibraryDir;
        MainWindow.LibraryDir = dir;
        try
        {
            var handler = new FakeSheets(_ => Body(Workbook("Walking")));
            using var http = new HttpClient(handler);
            var w = NewWindow();

            await w.ImportSheetsAsync(SheetLink, http);

            Assert.NotNull(w.OpenFile);
            Assert.Empty(Directory.GetFiles(dir, "*.csv", SearchOption.AllDirectories));
            w.OpenFile!.Dirty = false;
            w.Close();
        }
        finally { MainWindow.LibraryDir = old; Directory.Delete(dir, recursive: true); }
    }

    // A published link cannot serve a workbook, so the import falls back to the
    // single linked tab as CSV and says so.
    [AvaloniaFact]
    public async Task Import_falls_back_to_csv_when_the_answer_is_not_a_workbook()
    {
        const string csv = "Profile Name,,Mouse Mode\r\ncatalog.csv\r\nPlayStation Outputs,Function,usb\r\ndpad_N,normal,right_sip\r\n";
        var handler = new FakeSheets(url => Body(url.Contains("format=csv") ? csv : "not a workbook"));
        using var http = new HttpClient(handler);
        var w = NewWindow();

        await w.ImportSheetsAsync(SheetLink, http);

        Assert.Single(w.OpenFile!.Document.Sheets);
        Assert.Equal("Mouse Mode", w.OpenFile.Document.Sheets[0].ModeName);
        // Workbook first, then the CSV for the linked tab.
        Assert.Equal(2, handler.Urls.Count);
        Assert.Contains("format=xlsx", handler.Urls[0]);
        Assert.Contains("format=csv&gid=7", handler.Urls[1]);
        // The one thing this window could get most wrong. Only one tab of the
        // spreadsheet was ever sent, so the review has to be told that before
        // it counts anything: without it a profile missing four of its five
        // modes would be reported as a clean import.
        var review = w.LastImportReview;
        Assert.NotNull(review);
        Assert.NotNull(review!.Value.Limitation);
        Assert.Contains("only ever gives a single tab", review.Value.Limitation);
        w.OpenFile.Dirty = false;
        w.Close();
    }

    // The other half of that: a real workbook came in whole, so the review is
    // told there was no limitation and is free to call the import clean.
    [AvaloniaFact]
    public async Task A_whole_workbook_import_tells_the_review_it_saw_everything()
    {
        using var http = new HttpClient(new FakeSheets(_ => Body(Workbook("Walking"))));
        var w = NewWindow();

        await w.ImportSheetsAsync(SheetLink, http);

        var review = w.LastImportReview;
        Assert.NotNull(review);
        Assert.Null(review!.Value.Limitation);
        w.OpenFile!.Dirty = false;
        w.Close();
    }

    // Google answers an unshared link with its sign-in page. The error names
    // the fix instead of showing HTML.
    [AvaloniaFact]
    public async Task Import_of_an_unshared_sheet_names_the_sharing_fix()
    {
        var handler = new FakeSheets(_ => Body("<html>sign in</html>"));
        using var http = new HttpClient(handler);
        var w = NewWindow();

        await w.ImportSheetsAsync(SheetLink, http);

        Assert.Null(w.OpenFile);
        Assert.Contains("not shared publicly", HomeError(w));
        w.Close();
    }

    // A workbook with no profile tab is not an error worth a stack trace.
    [AvaloniaFact]
    public async Task Import_of_a_sheet_with_no_profile_tab_says_so()
    {
        var handler = new FakeSheets(_ => Body("Shopping list\r\nmilk,eggs\r\n"));
        using var http = new HttpClient(handler);
        var w = NewWindow();

        await w.ImportSheetsAsync(SheetLink, http);

        Assert.Null(w.OpenFile);
        Assert.Contains("no profile tab", HomeError(w));
        w.Close();
    }

    // A link that is not a Sheets link is caught before any request goes out.
    [AvaloniaFact]
    public async Task Import_of_a_link_that_is_not_a_sheet_asks_nothing()
    {
        var handler = new FakeSheets(_ => Body("never asked"));
        using var http = new HttpClient(handler);
        var w = NewWindow();

        await w.ImportSheetsAsync("my spreadsheet", http);

        Assert.Null(w.OpenFile);
        Assert.Empty(handler.Urls);
        Assert.Contains("does not look like a Google Sheets link", HomeError(w));
        w.Close();
    }

    // A dead connection is reported in the user's words, not the exception's.
    [AvaloniaFact]
    public async Task Import_reports_a_download_failure_without_throwing()
    {
        var handler = new FakeSheets(_ => throw new HttpRequestException("no network"));
        using var http = new HttpClient(handler);
        var w = NewWindow();

        await w.ImportSheetsAsync(SheetLink, http);

        Assert.Null(w.OpenFile);
        Assert.Contains("Could not download the sheet", HomeError(w));
        w.Close();
    }
}
