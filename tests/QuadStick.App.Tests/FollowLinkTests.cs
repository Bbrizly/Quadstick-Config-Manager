using System.Net;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// A profile imported from a Google Sheets link follows it: once saved, "Get
// the latest from the link" asks the same sheet again, shows what changed, and
// replaces the local file only after a yes. A clinician edits the sheet; the
// person at home presses the button. Nothing here may reach out on its own.
public class FollowLinkTests
{
    const string Link = "https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/edit";

    static string Profile(params string[] modes) => string.Join("\r\n", modes.Select(m =>
        $"Profile Name,,{m}\r\ncatalog.csv\r\nPlayStation Outputs,Function,usb\r\ndpad_N,normal,right_sip\r\n"));

    // Answers like a sheet whose workbook export is unavailable, so the import
    // takes its CSV fallback: the same path a real link can take, and one the
    // body of the answer can be swapped on between presses.
    sealed class Sheet : HttpMessageHandler
    {
        public string Csv;
        public int Requests;
        public Sheet(string csv) => Csv = csv;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            var body = request.RequestUri!.ToString().Contains("format=csv") ? Csv : "not a workbook";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) });
        }
    }

    sealed class Library : IDisposable
    {
        readonly string _libWas = MainWindow.LibraryDir;
        readonly string? _rescueWas = CrashGuard.RescueDirOverride;
        public string Dir { get; } = Directory.CreateTempSubdirectory("qcm-follow-").FullName;
        public string Path => System.IO.Path.Combine(Dir, "game.csv");
        public string Rescue => System.IO.Path.Combine(Dir, "rescue");

        public Library(string? local, bool follow)
        {
            MainWindow.LibraryDir = Dir;
            CrashGuard.RescueDirOverride = Rescue;
            if (local is not null) File.WriteAllText(Path, local);
            var s = Settings.Load();
            s.TutorialSeen = true;
            s.RememberWindow = false;
            s.FollowLinks.Clear();
            if (follow) s.FollowLinks[Path] = Link;
            Settings.Save(s);
        }

        public void Dispose()
        {
            MainWindow.LibraryDir = _libWas;
            CrashGuard.RescueDirOverride = _rescueWas;
            var s = Settings.Load();
            s.FollowLinks.Clear();
            Settings.Save(s);
            try { Directory.Delete(Dir, true); } catch (IOException) { }
        }
    }

    static MainWindow NewWindow()
    {
        var w = new MainWindow();
        w.ShowImportReview = (_, _) => Task.CompletedTask;
        w.Show();
        return w;
    }

    static void Close(MainWindow w)
    {
        if (w.OpenFile is { } f) f.Dirty = false;
        w.Close();
    }

    // The question comes up after a download and a file read, both of which
    // finish off the UI thread, so it is waited for rather than assumed.
    static async Task<Window> DialogAsync(MainWindow w)
    {
        for (int i = 0; i < 500; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (w.OwnedWindows.Count > 0) return w.OwnedWindows[0];
            await Task.Delay(10);
        }
        throw new TimeoutException("no dialog");
    }

    static void Press(Window dialog, string label) => Ui.Click(
        dialog.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == label));

    static string Said(Window dialog) => string.Join("\n",
        dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

    static bool ShowsStatus(MainWindow w, string fragment)
    {
        w.UpdateLayout();
        return w.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains(fragment) == true);
    }

    [AvaloniaFact]
    public async Task An_imported_profile_remembers_its_link_once_saved_and_after_a_restart()
    {
        using var lib = new Library(local: null, follow: false);
        using var http = new HttpClient(new Sheet(Profile("Walking")));
        var w = NewWindow();
        try
        {
            await w.ImportSheetsAsync(Link, http);
            Assert.Null(w.FollowedLink(lib.Path)); // not until there is a file to keep it against
            w.PickSavePathForTest = () => lib.Path;
            Assert.True(await w.SaveProfileAsync());

            Assert.Equal(Link, w.FollowedLink(lib.Path));
            Assert.Equal(Link, Settings.Load().FollowLinks[lib.Path]);
        }
        finally { Close(w); }

        var again = NewWindow();
        try { Assert.Equal(Link, again.FollowedLink(lib.Path)); }
        finally { Close(again); }
    }

    // Opening some other profile between the import and the save is not that
    // import being saved, and must not hand it the link.
    [AvaloniaFact]
    public async Task A_profile_that_was_not_imported_does_not_pick_up_a_link()
    {
        using var lib = new Library(Profile("Walking"), follow: false);
        using var http = new HttpClient(new Sheet(Profile("Walking")));
        var w = NewWindow();
        try
        {
            await w.ImportSheetsAsync(Link, http);
            w.OpenFile!.Dirty = false;
            w.OpenPath(lib.Path);
            w.OpenFile!.Dirty = true;
            Assert.True(await w.SaveProfileAsync());
            Assert.Null(w.FollowedLink(lib.Path));
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public async Task The_latest_replaces_the_profile_only_after_a_yes()
    {
        using var lib = new Library(Profile("Walking"), follow: true);
        var sheet = new Sheet(Profile("Walking", "Driving"));
        using var http = new HttpClient(sheet);
        var w = NewWindow();
        try
        {
            var getting = w.GetLatestAsync(lib.Path, http);
            var dialog = await DialogAsync(w);
            Assert.Contains("Driving", Said(dialog));     // what changed, before the question
            Assert.Contains("Walking", File.ReadAllText(lib.Path));
            Assert.DoesNotContain("Driving", File.ReadAllText(lib.Path));

            Press(dialog, Strings.Follow_Replace);
            await getting;

            Assert.Contains("Driving", File.ReadAllText(lib.Path));
            var kept = Assert.Single(Directory.GetFiles(lib.Rescue, "game-before-latest-*.csv"));
            Assert.DoesNotContain("Driving", File.ReadAllText(kept));
            Assert.True(ShowsStatus(w, Strings.Follow_Replaced));

            // A second replace in the same second keeps both rescue copies.
            sheet.Csv = Profile("Walking", "Driving", "Flying");
            getting = w.GetLatestAsync(lib.Path, http);
            Press(await DialogAsync(w), Strings.Follow_Replace);
            await getting;
            var both = Directory.GetFiles(lib.Rescue, "game-before-latest-*.csv");
            Assert.Equal(2, both.Length);
            Assert.Contains(both, f => !File.ReadAllText(f).Contains("Driving"));
            Assert.Contains(both, f => File.ReadAllText(f).Contains("Driving"));
        }
        finally { Close(w); }
    }

    // The whole hand-off, as the client at home meets it: the exact link a
    // host's share hands out, pasted into Import, saved, then the button.
    [AvaloniaFact]
    public async Task The_link_a_clinic_shares_is_one_the_home_app_imports_and_follows()
    {
        var link = DriveBackup.Url("1AbCdEfGhIjKlMnOpQrStUvWxYz012345");
        using var lib = new Library(local: null, follow: false);
        var sheet = new Sheet(Profile("Walking"));
        using var http = new HttpClient(sheet);
        var w = NewWindow();
        try
        {
            await w.ImportSheetsAsync(link, http);
            Assert.Contains(w.OpenFile!.Document.Sheets, m => m.ModeName == "Walking");
            w.PickSavePathForTest = () => lib.Path;
            Assert.True(await w.SaveProfileAsync());
            Assert.Equal(link, w.FollowedLink(lib.Path));

            sheet.Csv = Profile("Walking", "Driving");   // the clinician saved a change
            var getting = w.GetLatestAsync(lib.Path, http);
            var dialog = await DialogAsync(w);
            Assert.Contains("Driving", Said(dialog));
            Press(dialog, Strings.Follow_Replace);
            await getting;
            Assert.Contains("Driving", File.ReadAllText(lib.Path));
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public async Task Saying_keep_mine_changes_nothing()
    {
        var local = Profile("Walking");
        using var lib = new Library(local, follow: true);
        using var http = new HttpClient(new Sheet(Profile("Walking", "Driving")));
        var w = NewWindow();
        try
        {
            w.OpenPath(lib.Path);
            var before = File.ReadAllText(lib.Path);
            var getting = w.GetLatestAsync(null, http);  // the open profile
            Press(await DialogAsync(w), Strings.Follow_Keep);
            await getting;

            Assert.Equal(before, File.ReadAllText(lib.Path));
            Assert.DoesNotContain(w.OpenFile!.Document.Sheets, s => s.ModeName == "Driving");
            Assert.False(Directory.Exists(lib.Rescue) && Directory.GetFiles(lib.Rescue).Length > 0);
            Assert.True(ShowsStatus(w, Strings.Follow_Kept));
        }
        finally { Close(w); }
    }

    // The same validation as Import: a sign-in page or a sheet with no profile
    // tab in it never gets as far as the question, let alone the file.
    [AvaloniaTheory]
    [InlineData("<html>sign in</html>")]
    [InlineData("Shopping,list\r\nmilk,eggs\r\n")]
    public async Task A_sheet_that_would_not_import_is_refused(string answer)
    {
        var local = Profile("Walking");
        using var lib = new Library(local, follow: true);
        var sheet = new Sheet(answer);
        using var http = new HttpClient(sheet);
        var w = NewWindow();
        try
        {
            await w.GetLatestAsync(lib.Path, http);
            Assert.True(sheet.Requests > 0);
            Assert.Empty(w.OwnedWindows);
            Assert.Equal(local, File.ReadAllText(lib.Path));
            Assert.True(ShowsStatus(w, "Could not get the latest"));
        }
        finally { Close(w); }
    }

    // Unsaved work in the editor is asked about before anything else, and a
    // no stops the whole thing before a single request goes out.
    [AvaloniaFact]
    public async Task An_unsaved_edit_is_never_thrown_away()
    {
        var local = Profile("Walking");
        using var lib = new Library(local, follow: true);
        var sheet = new Sheet(Profile("Walking", "Driving"));
        using var http = new HttpClient(sheet);
        var w = NewWindow();
        try
        {
            w.OpenPath(lib.Path);
            w.OpenFile!.Dirty = true;
            var getting = w.GetLatestAsync(null, http);
            var dialog = await DialogAsync(w);
            Assert.Contains(Strings.Follow_SaveFirstTitle, Said(dialog));
            Press(dialog, Strings.Main_Cancel);
            await getting;

            Assert.Equal(0, sheet.Requests);
            Assert.True(w.OpenFile!.Dirty);
            Assert.Equal(local, File.ReadAllText(lib.Path));
        }
        finally { Close(w); }
    }

    // The editor is not locked while the question is up. An edit made then
    // was never in the list the user said yes to, so a yes does not cover it.
    [AvaloniaFact]
    public async Task An_edit_made_while_the_question_is_up_is_not_replaced()
    {
        var local = Profile("Walking");
        using var lib = new Library(local, follow: true);
        using var http = new HttpClient(new Sheet(Profile("Walking", "Driving")));
        var w = NewWindow();
        try
        {
            w.OpenPath(lib.Path);
            var getting = w.GetLatestAsync(null, http);
            var dialog = await DialogAsync(w);
            w.OpenFile!.Dirty = true;          // typed into the editor behind it
            Press(dialog, Strings.Follow_Replace);
            await getting;

            Assert.Equal(local, File.ReadAllText(lib.Path));
            Assert.True(w.OpenFile!.Dirty);
            Assert.DoesNotContain(w.OpenFile.Document.Sheets, s => s.ModeName == "Driving");
            Assert.True(ShowsStatus(w, Strings.Follow_ChangedMeanwhile));
        }
        finally { Close(w); }
    }

    // Opening Home, opening the profile, and going back makes no request. The
    // only thing that does is the press, and the press is on the card.
    [AvaloniaFact]
    public async Task Nothing_is_asked_for_until_the_button_is_pressed()
    {
        using var lib = new Library(Profile("Walking"), follow: true);
        var sheet = new Sheet(Profile("Walking", "Driving"));
        var w = new MainWindow { HttpOverrideForTest = new HttpClient(sheet) };
        w.ShowImportReview = (_, _) => Task.CompletedTask;
        w.Show();
        try
        {
            w.ShowHomeForPreview();
            Dispatcher.UIThread.RunJobs();
            Button Card() => w.GetVisualDescendants().OfType<WrapPanel>()
                .First(p => p.Name == "LibraryCards").GetVisualDescendants().OfType<Button>().First();
            Assert.Contains(Card().GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "CardFollows");

            Ui.Click(Card());                  // open it in the editor
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(lib.Path, w.CurrentProfilePath);
            w.ShowHomeForPreview();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(200);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, sheet.Requests);

            var latest = ((MenuFlyout)Card().ContextFlyout!).Items.OfType<MenuItem>()
                .Single(i => (i.Header as string) == Strings.Follow_GetLatest);
            Assert.True(latest.IsVisible);
            latest.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var dialog = await DialogAsync(w);
            Assert.True(sheet.Requests > 0);
            Press(dialog, Strings.Follow_Keep);
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void Deleting_a_profile_forgets_its_link()
    {
        using var lib = new Library(Profile("Walking"), follow: true);
        var w = NewWindow();
        try
        {
            w.ShowHomeForPreview();
            Dispatcher.UIThread.RunJobs();
            var card = w.GetVisualDescendants().OfType<WrapPanel>()
                .First(p => p.Name == "LibraryCards").GetVisualDescendants().OfType<Button>().First();
            var del = ((MenuFlyout)card.ContextFlyout!).Items.OfType<MenuItem>()
                .Single(i => (i.Header as string) == Strings.Main_DeleteProfile);
            del.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Press(Assert.Single(w.OwnedWindows), Strings.Main_YesContinue);
            Dispatcher.UIThread.RunJobs();

            Assert.False(File.Exists(lib.Path));
            Assert.Null(w.FollowedLink(lib.Path));
            Assert.False(Settings.Load().FollowLinks.ContainsKey(lib.Path));
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void Forgetting_the_link_stops_following_and_leaves_the_file()
    {
        var local = Profile("Walking");
        using var lib = new Library(local, follow: true);
        var w = NewWindow();
        try
        {
            w.StopFollowing(lib.Path);
            Assert.Null(w.FollowedLink(lib.Path));
            Assert.False(Settings.Load().FollowLinks.ContainsKey(lib.Path));
            Assert.Equal(local, File.ReadAllText(lib.Path));
        }
        finally { Close(w); }
    }

    // ---- what changed ----

    [AvaloniaFact]
    public void The_same_profile_has_no_changes_even_with_a_different_backup_id()
    {
        var a = ProfileFile.Load("QuadStick Configuration,Version 1.5,aaaaaaaaaaaaaaaaaaaaaaaaa,Walk\r\n" + Profile("Walking"));
        var b = ProfileFile.Load("QuadStick Configuration,Version 1.5,bbbbbbbbbbbbbbbbbbbbbbbbb,Walk\r\n" + Profile("Walking"));
        Assert.Empty(ProfileChanges.Between(a, b));
    }

    [AvaloniaFact]
    public void An_edited_mapping_reads_as_one_added_and_one_removed()
    {
        var a = ProfileFile.Load(Profile("Walking"));
        var b = ProfileFile.Load(Profile("Walking").Replace("right_sip", "left_puff"));
        var line = Assert.Single(ProfileChanges.Between(a, b));
        Assert.Equal("Walking: 1 added, 1 removed", line);
    }

    [AvaloniaFact]
    public void Modes_that_come_and_go_are_named()
    {
        var a = ProfileFile.Load(Profile("Walking", "Flying"));
        var b = ProfileFile.Load(Profile("Walking", "Driving"));
        var lines = ProfileChanges.Between(a, b);
        Assert.Contains("New in the sheet: Driving", lines);
        Assert.Contains("No longer in the sheet: Flying", lines);
        Assert.DoesNotContain(Strings.Follow_OtherChanged, lines);
    }

    // A note changed beside a changed mapping is still said: the question
    // must not hide part of what a yes replaces.
    [AvaloniaFact]
    public void A_note_changed_beside_a_mapping_is_still_listed()
    {
        var two = Profile("Walking").Replace("right_sip\r\n", "right_sip\r\ndpad_S,normal,left_sip\r\n");
        var a = ProfileFile.Load(two);
        var b = ProfileFile.Load(two.Replace("right_sip", "left_puff").Replace("left_sip\r\n", "left_sip,,,,,,,,call Anna\r\n"));
        var lines = ProfileChanges.Between(a, b);
        Assert.Contains("Walking: 1 added, 1 removed", lines);
        Assert.Contains(Strings.Follow_OtherChanged, lines);
    }
}
