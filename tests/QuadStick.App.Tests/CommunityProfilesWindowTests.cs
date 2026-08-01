using System.Net;
using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// The community profiles browser. It is the only place in the app that asks
// quadstick.com for anything, it never touches a QuadStick, and it has to be
// usable by keyboard and screen reader alone, so all three are pinned here.
public sealed class CommunityProfilesWindowTests : IDisposable
{
    const string IdCyber = "1AbCdEfGhIjKlMnOpQrStUvWxYz012345678";
    const string IdDoom = "2ZyXwVuTsRqPoNmLkJiHgFeDcBa987654321";

    // Two good rows and one that names no sheet, so the skipped count is real.
    const string GoodBody = """
        [[["Cyberpunk 2077","1AbCdEfGhIjKlMnOpQrStUvWxYz012345678","Cyberpunk.csv","https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345678/edit","PS5 USB","Sip to toggle aim","mouse"],
        ["Doom Eternal","2ZyXwVuTsRqPoNmLkJiHgFeDcBa987654321","DoomEternal.csv","","Xbox","","joystick"],
        ["Broken row","not a sheet id","Broken.csv"]],
        [["Voice pack","voices.vch","9ZzZzZzZzZzZzZzZzZzZzZzZzZzZzZzZ"]]]
        """;

    // The smallest thing ProfileFile.Load calls a profile.
    const string ProfileCsv =
        "Profile Name,,Walking\r\ncatalog.csv\r\nPlayStation Outputs,Function,usb\r\ndpad_N,normal,right_sip\r\n";

    readonly string _dir = Path.Combine(Path.GetTempPath(), $"qcm-community-{Guid.NewGuid():N}");

    string CachePath => Path.Combine(_dir, "community-catalog.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    void SeedCache(string body)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(CachePath, body);
    }

    // ---- fakes ----

    sealed class Recording : HttpMessageHandler
    {
        public readonly List<string> Urls = new();
        readonly Func<string, HttpResponseMessage> _reply;
        public Recording(Func<string, HttpResponseMessage> reply) => _reply = reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(_reply(request.RequestUri!.ToString()));
        }
    }

    static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    static Recording Serving(string body) => new(_ => Ok(body));

    static Recording Failing() => new(_ => throw new HttpRequestException("no network"));

    // The workbook request answers with something that is not a workbook, so the
    // import falls back to the CSV for the linked tab, the same as a published
    // sheet does. One fake covers both requests.
    static Recording Sheets() => new(url => Ok(url.Contains("format=csv") ? ProfileCsv : "not a workbook"));

    // ---- window helpers ----

    static MainWindow NewWindow()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    async Task<CommunityProfilesWindow> OpenAsync(
        MainWindow owner, HttpMessageHandler catalog, HttpMessageHandler? sheets = null)
    {
        var win = new CommunityProfilesWindow(
            owner,
            new CommunityCatalogClient(catalog, CachePath),
            sheets is null ? null : new HttpClient(sheets));
        win.OpenUri = _ => Task.CompletedTask; // never open a real browser in a test
        _ = win.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();
        await win.CatalogLoaded;
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();
        return win;
    }

    static Button Button(Window w, string automationName) =>
        w.GetVisualDescendants().OfType<Button>()
            .First(b => AutomationProperties.GetName(b) == automationName);

    static TextBox Search(Window w) => w.GetVisualDescendants().OfType<TextBox>().First();

    static ListBox Results(Window w) => w.GetVisualDescendants().OfType<ListBox>().First();

    static string[] Rows(Window w)
    {
        w.UpdateLayout();
        return Results(w).ItemsSource!.Cast<ListBoxItem>()
            .Select(i => ((TextBlock)((StackPanel)i.Content!).Children[0]).Text!)
            .ToArray();
    }

    static string[] AllText(Window w)
    {
        w.UpdateLayout();
        return w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
    }

    static bool Says(Window w, string fragment) => AllText(w).Any(t => t.Contains(fragment));

    static void Type(Window w, string text)
    {
        Search(w).Text = text;
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
    }

    static void Press(Window w, Control target, Key key)
    {
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = target });
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
    }

    static void Tap(Window w, string automationName)
    {
        Button(w, automationName).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
    }

    // ---- the home card ----

    // CAT-03: the way in is a Start card, not a hidden menu.
    [AvaloniaFact]
    public void Home_offers_a_community_profiles_card()
    {
        var w = NewWindow();
        var card = w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "HomeCommunityButton");
        Assert.Equal(
            "Browse the community list of shared game profiles and import one",
            AutomationProperties.GetName(card));
        Assert.True(card.IsVisible);
        w.Close();
    }

    // ---- lazy network ----

    // CAT-06: nothing asks for the catalog until this window opens. The handler
    // sees its first request only after the window is up.
    [AvaloniaFact]
    public async Task Opening_the_window_is_the_first_catalog_request()
    {
        var catalog = Serving(GoodBody);
        var w = NewWindow();
        w.UpdateLayout();
        Assert.Empty(catalog.Urls);

        var win = await OpenAsync(w, catalog);

        Assert.Equal(new[] { CatalogUrl }, catalog.Urls);
        win.Close();
        w.Close();
    }

    const string CatalogUrl = "https://bvhbml89uymwxubx.quadstick.com/";

    // The only catalog client in the app is the one this window makes, so no
    // other screen can start a fetch by accident.
    [Fact]
    public void Only_the_community_window_builds_a_catalog_client()
    {
        var files = Directory.GetFiles(SourceDir, "*.cs")
            .Where(f => File.ReadAllText(f).Contains("new CommunityCatalogClient"))
            .Select(Path.GetFileName)
            .ToArray();
        Assert.Equal(new[] { "CommunityProfilesWindow.cs" }, files);
    }

    // CAT-05: a catalog pick reaches the device only through the editor and the
    // normal Install button. This window cannot shortcut that.
    [Fact]
    public void The_window_never_touches_the_installer()
    {
        var source = File.ReadAllText(Path.Combine(SourceDir, "CommunityProfilesWindow.cs"));
        Assert.DoesNotContain("Device.Install", source);
        Assert.DoesNotContain("InstallFlow", source);
    }

    static string SourceDir => Path.Combine(RepoRoot, "src", "QuadStick.App");

    static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "QuadStick.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }

    // ---- honest state ----

    // A saved copy is never announced as new, and dropped rows are counted out
    // loud instead of silently shrinking the list.
    [AvaloniaFact]
    public async Task A_saved_copy_says_so_and_names_the_skipped_rows()
    {
        SeedCache(GoodBody);
        var catalog = Serving(GoodBody);
        var w = NewWindow();

        var win = await OpenAsync(w, catalog);

        Assert.Empty(catalog.Urls); // a usable cache costs no request
        Assert.True(Says(win, "2 profiles from the copy saved on this computer"));
        Assert.True(Says(win, "1 row was skipped"));
        Assert.False(Says(win, "downloaded just now"));
        win.Close();
        w.Close();
    }

    // Refresh is the second and last way to reach the network, and only then may
    // the window call the list new.
    [AvaloniaFact]
    public async Task Refresh_downloads_again_and_says_the_list_is_new()
    {
        SeedCache(GoodBody);
        var catalog = Serving(GoodBody);
        var w = NewWindow();
        var win = await OpenAsync(w, catalog);

        Tap(win, "Download the community list again");
        await win.CatalogLoaded;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { CatalogUrl }, catalog.Urls);
        Assert.True(Says(win, "2 profiles, downloaded just now"));
        win.Close();
        w.Close();
    }

    // CROSS-03: no network and no saved copy is a plain sentence and a working
    // window, not an empty screen or a crash.
    [AvaloniaFact]
    public async Task With_no_network_and_no_cache_the_window_still_works()
    {
        var w = NewWindow();

        var win = await OpenAsync(w, Failing());

        Assert.Empty(Rows(win));
        Assert.True(Says(win, "could not be downloaded"));
        Assert.True(Says(win, "press Refresh"));
        Assert.True(Button(win, "Download the community list again").IsEnabled);
        Assert.True(Button(win, "Close this window").IsEnabled);
        win.Close();
        w.Close();
    }

    // A dead network with a saved copy shows the saved copy, still labelled as
    // saved, because the refresh did not work.
    [AvaloniaFact]
    public async Task A_failed_refresh_keeps_the_saved_copy_and_does_not_call_it_new()
    {
        SeedCache(GoodBody);
        var w = NewWindow();
        var win = await OpenAsync(w, Failing());

        Tap(win, "Download the community list again");
        await win.CatalogLoaded;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "Cyberpunk 2077", "Doom Eternal" }, Rows(win));
        Assert.True(Says(win, "from the copy saved on this computer"));
        Assert.False(Says(win, "downloaded just now"));
        win.Close();
        w.Close();
    }

    // ---- search ----

    // CAT-03: search covers the name and the optional fields, so "xbox" and
    // "joystick" find a game whose name has neither word in it.
    [AvaloniaFact]
    public async Task Search_matches_name_file_name_connection_and_pointer()
    {
        SeedCache(GoodBody);
        var w = NewWindow();
        var win = await OpenAsync(w, Serving(GoodBody));

        Assert.Equal(new[] { "Cyberpunk 2077", "Doom Eternal" }, Rows(win));

        Type(win, "cyber");
        Assert.Equal(new[] { "Cyberpunk 2077" }, Rows(win));

        Type(win, "DoomEternal.csv");
        Assert.Equal(new[] { "Doom Eternal" }, Rows(win));

        Type(win, "xbox");
        Assert.Equal(new[] { "Doom Eternal" }, Rows(win));

        Type(win, "mouse");
        Assert.Equal(new[] { "Cyberpunk 2077" }, Rows(win));

        Type(win, "nothing like this");
        Assert.Empty(Rows(win));
        Assert.True(Says(win, "No profiles match"));

        Type(win, "");
        Assert.Equal(2, Rows(win).Length);
        win.Close();
        w.Close();
    }

    // ---- accessibility ----

    // CROSS-02: every control and every result row announces itself.
    [AvaloniaFact]
    public async Task Every_control_and_result_has_an_automation_name()
    {
        SeedCache(GoodBody);
        var w = NewWindow();
        var win = await OpenAsync(w, Serving(GoodBody));

        foreach (var wanted in new[]
                 {
                     "Import the selected profile into the editor",
                     "Open the selected profile's Google Sheet in your browser",
                     "Download the community list again",
                     "Close this window",
                 })
            Assert.Contains(win.GetVisualDescendants().OfType<Button>(),
                b => AutomationProperties.GetName(b) == wanted);

        Assert.Equal("Search the community profiles", AutomationProperties.GetName(Search(win)));
        Assert.Equal("Community profiles, use the arrow keys", AutomationProperties.GetName(Results(win)));

        var names = Results(win).ItemsSource!.Cast<ListBoxItem>()
            .Select(AutomationProperties.GetName).ToArray();
        Assert.Equal(
            new[]
            {
                "Cyberpunk 2077. Cyberpunk.csv, PS5 USB, mouse, Sip to toggle aim",
                "Doom Eternal. DoomEternal.csv, Xbox, joystick",
            },
            names);
        win.Close();
        w.Close();
    }

    // Which row is picked is said in words, so it does not depend on seeing the
    // highlight colour.
    [AvaloniaFact]
    public async Task The_selected_row_is_named_in_text()
    {
        SeedCache(GoodBody);
        var w = NewWindow();
        var win = await OpenAsync(w, Serving(GoodBody));

        Assert.True(Says(win, "Cyberpunk 2077 is selected."));

        Results(win).SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.True(Says(win, "Doom Eternal is selected."));
        win.Close();
        w.Close();
    }

    // ---- keyboard ----

    // CAT-03: search, move into the results, and import, without a mouse.
    [AvaloniaFact]
    public async Task The_keyboard_alone_searches_and_imports()
    {
        SeedCache(GoodBody);
        var sheets = Sheets();
        var w = NewWindow();
        var win = await OpenAsync(w, Serving(GoodBody), sheets);

        Type(win, "doom");
        Press(win, Search(win), Key.Down);
        var row = Assert.IsType<ListBoxItem>(win.FocusManager?.GetFocusedElement());

        Press(win, row, Key.Enter);
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(w.OpenFile);
        Assert.Equal("Walking", w.OpenFile!.Document.Sheets[0].ModeName);
        Assert.Contains($"/d/{IdDoom}/export?format=xlsx", sheets.Urls[0]);
        w.OpenFile.Dirty = false;
        w.Close();
    }

    // ---- import ----

    // CAT-04: a pick becomes the standard Sheets link and goes through the one
    // import, so it lands on the same document a pasted link produces.
    [AvaloniaFact]
    public async Task A_pick_imports_the_same_document_a_pasted_link_would()
    {
        SeedCache(GoodBody);
        var sheets = Sheets();
        var w = NewWindow();
        var win = await OpenAsync(w, Serving(GoodBody), sheets);

        Tap(win, "Import the selected profile into the editor");
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(w.OpenFile);
        var fromCatalog = w.OpenFile!.ToCsvText();
        Assert.Equal(
            new[]
            {
                $"https://docs.google.com/spreadsheets/d/{IdCyber}/export?format=xlsx",
                $"https://docs.google.com/spreadsheets/d/{IdCyber}/export?format=csv",
            },
            sheets.Urls);

        w.OpenFile.Dirty = false;
        var pasted = Sheets();
        await w.ImportSheetsAsync(
            $"https://docs.google.com/spreadsheets/d/{IdCyber}/edit", new HttpClient(pasted));

        Assert.Equal(fromCatalog, w.OpenFile!.ToCsvText());
        w.OpenFile.Dirty = false;
        w.Close();
    }

    // Importing gets out of the way afterwards: the profile is open in the
    // editor behind, so the window closes rather than covering it.
    [AvaloniaFact]
    public async Task A_finished_import_closes_the_window()
    {
        SeedCache(GoodBody);
        var w = NewWindow();
        var win = await OpenAsync(w, Serving(GoodBody), Sheets());

        Tap(win, "Import the selected profile into the editor");
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        Assert.False(win.IsVisible);
        w.OpenFile!.Dirty = false;
        w.Close();
    }

    // An import failure has to land where the user is looking. The home screen
    // is behind this window, so its error line must stay empty and the words
    // must appear here.
    [AvaloniaFact]
    public async Task An_import_failure_is_shown_in_this_window_not_on_home()
    {
        SeedCache(GoodBody);
        var w = NewWindow();
        var win = await OpenAsync(w, Serving(GoodBody), new Recording(_ => Ok("<html>sign in</html>")));

        Tap(win, "Import the selected profile into the editor");
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();

        Assert.Null(w.OpenFile);
        Assert.True(win.IsVisible); // still open, so the message can be read
        Assert.True(Says(win, "not shared publicly"));
        var home = w.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "HomeStatusText");
        Assert.False(home.IsVisible);
        win.Close();
        w.Close();
    }

    // Open in Sheets builds the link from the ID, never from a URL the catalog
    // handed over.
    [AvaloniaFact]
    public async Task Open_in_sheets_uses_the_link_built_from_the_id()
    {
        SeedCache(GoodBody);
        var w = NewWindow();
        var win = await OpenAsync(w, Serving(GoodBody));
        Uri? opened = null;
        win.OpenUri = uri => { opened = uri; return Task.CompletedTask; };

        Tap(win, "Open the selected profile's Google Sheet in your browser");

        Assert.Equal($"https://docs.google.com/spreadsheets/d/{IdCyber}/edit", opened?.ToString());
        Assert.True(Says(win, "Opened the Cyberpunk 2077 sheet in your browser."));
        win.Close();
        w.Close();
    }

    // ---- the promise in writing ----

    // CAT-06: the privacy text and the in-app guide both have to name the
    // catalog fetch, or the app is quietly doing more than it says.
    [Fact]
    public void The_privacy_and_help_text_name_the_catalog_fetch()
    {
        var help = string.Join("\n", MainWindow.HelpSections().Select(s => s.Title + "\n" + s.Body));
        Assert.Contains("Community profiles", help);
        Assert.Contains("quadstick.com", help);

        foreach (var file in new[] { "PRIVACY.md", Path.Combine("docs", "privacy.html") })
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot, file));
            Assert.Contains("community profile list", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("quadstick.com", text);
        }
    }
}
