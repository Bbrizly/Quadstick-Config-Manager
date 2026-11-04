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

        public Library(string? local, bool follow, bool autoCheck = false)
        {
            MainWindow.LibraryDir = Dir;
            CrashGuard.RescueDirOverride = Rescue;
            if (local is not null) File.WriteAllText(Path, local);
            var s = Settings.Load();
            s.TutorialSeen = true;
            s.RememberWindow = false;
            s.Follows.Clear();
            s.FollowAutoCheck = autoCheck; // off unless a test is about it, so no other test reaches out
            if (follow) s.Follows[Path] = new FollowLink { Link = Link };
            Settings.Save(s);
        }

        public void Dispose()
        {
            MainWindow.LibraryDir = _libWas;
            CrashGuard.RescueDirOverride = _rescueWas;
            var s = Settings.Load();
            s.Follows.Clear();
            s.FollowAutoCheck = true;
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
            var kept = Settings.Load().Follows[lib.Path];
            Assert.Equal(Link, kept.Link);
            Assert.Equal("1AbCdEfGhIjKlMnOpQrStUvWxYz012345", kept.SheetId);
            Assert.NotEqual("", kept.LastHash); // the import's download is the first one seen
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

    // A big sheet edit lists every change, not the first dozen and a "...":
    // the list scrolls, the question stays on screen.
    [AvaloniaFact]
    public async Task A_long_list_of_changes_is_shown_in_full_and_scrolls()
    {
        using var lib = new Library(Profile("Walking"), follow: true);
        var modes = new[] { "Walking" }.Concat(Enumerable.Range(1, 30).Select(i => $"Mode{i:00}")).ToArray();
        using var http = new HttpClient(new Sheet(Profile(modes)));
        var w = NewWindow();
        try
        {
            var getting = w.GetLatestAsync(lib.Path, http);
            var dialog = await DialogAsync(w);
            var said = Said(dialog);
            for (int i = 1; i <= 30; i++) Assert.Contains($"Mode{i:00}", said);
            Assert.DoesNotContain("\u2022 \u2026", said);
            var scroll = Assert.Single(dialog.GetVisualDescendants().OfType<ScrollViewer>(),
                s => s.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("Mode30") == true));
            Assert.True(scroll.MaxHeight < 1000);
            Press(dialog, Strings.Follow_Keep);
            await getting;
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
            Assert.False(Settings.Load().Follows.ContainsKey(lib.Path));
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
            Assert.False(Settings.Load().Follows.ContainsKey(lib.Path));
            Assert.Equal(local, File.ReadAllText(lib.Path));
        }
        finally { Close(w); }
    }

    // ---- what changed ----

    // Answers each request with the next status in line, then the sheet.
    sealed class Flaky : HttpMessageHandler
    {
        readonly Queue<HttpResponseMessage> _first;
        readonly string _csv;
        public int Requests;
        public Flaky(string csv, params HttpResponseMessage[] first) { _csv = csv; _first = new(first); }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            if (_first.Count > 0) return Task.FromResult(_first.Dequeue());
            var body = request.RequestUri!.ToString().Contains("format=csv") ? _csv : "not a workbook";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) });
        }
    }

    static HttpResponseMessage Status(HttpStatusCode code, int? retryAfter = null)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent("") };
        if (retryAfter is int s) r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
        return r;
    }

    // Opening a followed profile starts a check of its own. Let it finish, so
    // the one the test asks for is not turned away as a second at once.
    static async Task Check(MainWindow w, HttpClient http)
    {
        for (int i = 0; i < 500 && w.FollowChecking; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        await w.CheckFollowAsync(http);
    }

    sealed class Waits : IDisposable
    {
        readonly Func<TimeSpan, CancellationToken, Task> _was = MainWindow.RetryDelay;
        public List<TimeSpan> Seen { get; } = new();
        public Waits() => MainWindow.RetryDelay = (t, _) => { lock (Seen) Seen.Add(t); return Task.CompletedTask; };
        public void Dispose() => MainWindow.RetryDelay = _was;
    }

    // A sheet with an error in it cannot replace the file. The error is said
    // in the question, and the replace button is there but cannot be pressed.
    [AvaloniaFact]
    public async Task A_sheet_with_errors_is_shown_but_cannot_replace()
    {
        var local = Profile("Walking");
        var broken = Profile("Walking") + "joystick_deflection_maximum,,abc\r\n";
        Assert.True(ProfileFile.Load(broken).HasErrors);
        using var lib = new Library(local, follow: true);
        using var http = new HttpClient(new Sheet(broken));
        var w = NewWindow();
        try
        {
            var getting = w.GetLatestAsync(lib.Path, http);
            var dialog = await DialogAsync(w);
            Assert.Contains(Strings.Follow_HasErrors, Said(dialog));
            var replace = dialog.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == Strings.Follow_Replace);
            Assert.False(replace.IsEnabled);
            Press(dialog, Strings.Follow_Keep);
            await getting;
            Assert.Equal(local, File.ReadAllText(lib.Path));
        }
        finally { Close(w); }
    }

    // A published link can hand back one tab of a five-mode profile. Using it
    // would delete the rest, so the question says so in its own line.
    [AvaloniaFact]
    public async Task A_sheet_with_fewer_modes_says_so()
    {
        using var lib = new Library(Profile("Walking", "Driving", "Flying"), follow: true);
        using var http = new HttpClient(new Sheet(Profile("Walking")));
        var w = NewWindow();
        try
        {
            var getting = w.GetLatestAsync(lib.Path, http);
            var dialog = await DialogAsync(w);
            Assert.Contains(string.Format(Strings.Follow_FewerModes, 1, 3), Said(dialog));
            Press(dialog, Strings.Follow_Keep);
            await getting;
        }
        finally { Close(w); }
    }

    // A sheet that is not shared answers 403, or sends the request to Google's
    // sign-in page. Either way the answer is how to share it.
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_sheet_that_is_not_shared_says_how_to_share_it(bool signInRedirect)
    {
        var local = Profile("Walking");
        using var lib = new Library(local, follow: true);
        var answer = signInRedirect
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>sign in</html>"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://accounts.google.com/ServiceLogin?continue=x"),
            }
            : Status(HttpStatusCode.Forbidden);
        using var http = new HttpClient(new Flaky(Profile("Walking", "Driving"), answer));
        var w = NewWindow();
        try
        {
            await w.GetLatestAsync(lib.Path, http);
            Assert.Empty(w.OwnedWindows);
            Assert.Equal(local, File.ReadAllText(lib.Path));
            Assert.True(ShowsStatus(w, Strings.Main_GoogleReturnedAWebPage));
        }
        finally { Close(w); }
    }

    // Google's "slow down" and its server errors are asked again twice, after
    // 1 s and 3 s, or after Retry-After capped at 10 s. Then it gives up.
    [AvaloniaFact]
    public async Task A_busy_export_is_retried_twice_with_backoff()
    {
        using var waits = new Waits();
        var ok = new Flaky("a,b\r\n", Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.TooManyRequests, retryAfter: 60));
        using (var http = new HttpClient(ok))
            Assert.Equal("a,b\r\n", await MainWindow.GetSheetAsync(http, "https://x.test/?format=csv", (c, ct) => c.ReadAsStringAsync(ct)));
        Assert.Equal(3, ok.Requests);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10) }, waits.Seen);

        waits.Seen.Clear();
        var down = new Flaky("", Status(HttpStatusCode.TooManyRequests, retryAfter: 2), Status(HttpStatusCode.BadGateway), Status(HttpStatusCode.BadGateway));
        using (var http = new HttpClient(down))
        {
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
                MainWindow.GetSheetAsync(http, "https://x.test/", (c, ct) => c.ReadAsStringAsync(ct)));
            Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
        }
        Assert.Equal(3, down.Requests);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3) }, waits.Seen);
    }

    // With the automatic check on, a changed sheet puts up a banner and
    // nothing else: no dialog, no replace. Not now hides it until the sheet
    // changes again, and Review opens the same question as the button.
    [AvaloniaFact]
    public async Task A_changed_sheet_is_offered_never_replaced()
    {
        var local = Profile("Walking");
        using var lib = new Library(local, follow: true, autoCheck: true);
        var sheet = new Sheet(Profile("Walking", "Driving"));
        using var http = new HttpClient(sheet);
        var w = new MainWindow { HttpOverrideForTest = http };
        w.ShowImportReview = (_, _) => Task.CompletedTask;
        w.Show();
        try
        {
            w.OpenPath(lib.Path);
            await Check(w, http);
            Assert.True(sheet.Requests > 0);
            Assert.True(w.FollowBannerShown);
            Assert.Empty(w.OwnedWindows);
            Assert.Equal(local, File.ReadAllText(lib.Path));

            Ui.Click(w.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == Strings.Follow_BannerDismiss));
            await Check(w, http);
            Assert.False(w.FollowBannerShown); // the same change, already said no to

            sheet.Csv = Profile("Walking", "Swimming");
            await Check(w, http);
            Assert.True(w.FollowBannerShown); // a new change is news again

            Ui.Click(w.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == Strings.Follow_BannerReview));
            var dialog = await DialogAsync(w);
            Press(dialog, Strings.Follow_Keep);
            for (int i = 0; i < 50 && w.FollowBannerShown; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
            Assert.False(w.FollowBannerShown);
            Assert.Equal(local, File.ReadAllText(lib.Path));
        }
        finally { Close(w); }
    }

    // A sheet that matches the file is not news, and with the check off the
    // app asks for nothing at all.
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_unchanged_sheet_or_the_check_off_shows_nothing(bool autoCheck)
    {
        var local = Profile("Walking");
        using var lib = new Library(local, follow: true, autoCheck: autoCheck);
        var sheet = new Sheet(autoCheck ? local : Profile("Walking", "Driving"));
        using var http = new HttpClient(sheet);
        var w = new MainWindow { HttpOverrideForTest = http };
        w.ShowImportReview = (_, _) => Task.CompletedTask;
        w.Show();
        try
        {
            w.OpenPath(lib.Path);
            await Check(w, http);
            Assert.False(w.FollowBannerShown);
            Assert.Equal(autoCheck, sheet.Requests > 0);
        }
        finally { Close(w); }
    }

    // Google saying slow down to the automatic check stops it asking again
    // until the doubled wait has passed.
    [AvaloniaFact]
    public async Task The_automatic_check_backs_off_when_google_is_busy()
    {
        using var waits = new Waits();
        using var lib = new Library(Profile("Walking"), follow: true, autoCheck: true);
        var busy = new Flaky("", Enumerable.Range(0, 3).Select(_ => Status(HttpStatusCode.TooManyRequests)).ToArray());
        using var http = new HttpClient(busy);
        var now = DateTime.UtcNow;
        var w = new MainWindow { HttpOverrideForTest = http, FollowClock = () => now };
        w.ShowImportReview = (_, _) => Task.CompletedTask;
        w.Show();
        try
        {
            w.OpenPath(lib.Path);
            await Check(w, http);
            var asked = busy.Requests;
            Assert.Equal(3, asked);
            now += TimeSpan.FromMinutes(9);
            await Check(w, http);
            Assert.Equal(asked, busy.Requests); // still inside the 10 minute wait
            now += TimeSpan.FromMinutes(2);
            await Check(w, http);
            Assert.True(busy.Requests > asked);
        }
        finally { Close(w); }
    }

    // Drive: the modified time, and a failing export so the read falls
    // through to the public link, as for a sheet this app cannot export.
    sealed class Drive : HttpMessageHandler
    {
        public string Modified = "t1";
        public int MetadataRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Query.Contains("fields=modifiedTime"))
            {
                Interlocked.Increment(ref MetadataRequests);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent($"{{\"modifiedTime\":\"{Modified}\"}}") });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("") });
        }
    }

    // A sheet this app made is asked for its modified time first. A new time
    // whose download then fails is not taken as seen, so the next check
    // downloads again instead of calling the sheet unchanged.
    [AvaloniaFact]
    public async Task A_failed_download_after_a_new_drive_time_is_tried_again()
    {
        using var waits = new Waits();
        var local = Profile("Walking");
        using var lib = new Library(local, follow: true, autoCheck: true);
        const string id = "1AbCdEfGhIjKlMnOpQrStUvWxYz012345";
        var s = Settings.Load();
        s.Follows[lib.Path] = new FollowLink { Link = Link, SheetId = id, LastModifiedTime = "t0" };
        s.DriveLinks["/elsewhere/mine.csv"] = new DriveLink { SpreadsheetId = id };
        Settings.Save(s);

        var sheet = new Flaky(Profile("Walking", "Driving"), Status(HttpStatusCode.Forbidden));
        using var http = new HttpClient(sheet);
        var w = new MainWindow { HttpOverrideForTest = http };
        w.ShowImportReview = (_, _) => Task.CompletedTask;
        var drive = new Drive();
        w.BackupForTest = new DriveBackup(new DriveClient(drive, _ => Task.FromResult("token")),
            () => w.CurrentSettings, () => true,
            (_, _) => Task.FromResult(ConflictChoice.KeepOnline), (_, _) => Task.FromResult(false),
            (_, _) => { }, () => Task.FromResult(false));
        w.Show();
        try
        {
            w.OpenPath(lib.Path);              // the first check: new time, export refused
            await Check(w, http);              // the second: same time, so it must download
            Assert.True(drive.MetadataRequests >= 2);
            Assert.True(w.FollowBannerShown);
            Assert.Equal("t1", w.CurrentSettings.Follows[lib.Path].LastModifiedTime);
            Assert.Equal(local, File.ReadAllText(lib.Path));
        }
        finally
        {
            var t = Settings.Load(); t.DriveLinks.Remove("/elsewhere/mine.csv"); Settings.Save(t);
            Close(w);
        }
    }

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
