using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using QuadStick.Format;

namespace QuadStick.App;

// Following a Google Sheets link. A profile imported from a link remembers it
// once it is saved, and "Get the latest from the link" asks that sheet again.
//
// Three rules hold the whole feature:
//   - the automatic check (Settings, on by default) only ever says the sheet
//     changed; it never replaces, and with it off nothing reaches the network
//     until the user presses the button;
//   - the download goes through the same import path and the same validation
//     as a pasted link, so a sheet that would not import cannot replace;
//   - nothing replaces the local profile until the user has seen what changed
//     and said yes, and their old copy goes to the rescue folder first.
public partial class MainWindow
{
    /// <summary>The link the profile open right now was imported from, until
    /// it is saved and the link can be kept against its file.</summary>
    string? _pendingFollowLink;
    string _pendingFollowHash = "";

    bool _gettingLatest;

    /// <summary>Test seam: answers Save As instead of the system picker, which
    /// a headless run cannot drive. Null for the real picker.</summary>
    internal Func<string?>? PickSavePathForTest;

    /// <summary>Test seam: the client the import paths use, in place of the
    /// app's own, so a test can count every request the app makes.</summary>
    internal HttpClient? HttpOverrideForTest;

    // Home has its own message line, and the card menu is pressed from Home,
    // where the editor's status line is not on screen. Said in both, so the
    // answer is wherever the user is looking.
    void Say(string text, StatusKind kind)
    {
        Status(text, kind);
        if (HomeView.IsVisible)
        {
            HomeStatusText.Text = text;
            HomeStatusText.IsVisible = true;
        }
    }

    /// <summary>The Sheets link a saved profile follows, or null.</summary>
    internal string? FollowedLink(string path) =>
        _settings.Follows.TryGetValue(path, out var follow) ? follow.Link : null;

    void RememberFollowLink(string? path)
    {
        if (path is null || _pendingFollowLink is not string link) return;
        _pendingFollowLink = null;
        SheetsUrl.TryGetId(link, out var id);
        // The import's own download is the first thing seen, so the first
        // check does not call an unchanged sheet a change.
        _settings.Follows[path] = new FollowLink { Link = link, SheetId = id ?? "", LastHash = _pendingFollowHash };
        _pendingFollowHash = "";
        PersistSettings();
    }

    void ForgetFollowLink(string path)
    {
        if (_settings.Follows.Remove(path)) PersistSettings();
        if (path == _savePath) ShowFollowBanner(false);
    }

    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Stop following. The sheet itself is not touched: it is
    /// somebody else's, and only the link on this computer is forgotten.</summary>
    internal void StopFollowing(string? path)
    {
        path ??= _savePath;
        if (path is null || FollowedLink(path) is null) return;
        ForgetFollowLink(path);
        if (HomeView.IsVisible) RefreshHomeCards();
        Say(Strings.Follow_Forgotten, StatusKind.Ready);
    }

    // The two follow actions, added to the share menu everywhere it appears.
    // A card's menu is built per card and knows its file; the editor's is
    // built once, so it decides on opening whether the open file follows.
    void AddFollowItems(MenuFlyout menu, string? path)
    {
        if (!NetworkFeature.Enabled) return;
        var latest = new MenuItem { Header = Strings.Follow_GetLatest };
        AutomationProperties.SetName(latest, Strings.Follow_GetLatestHelp);
        latest.Click += async (_, _) => await GetLatestAsync(path);
        var forget = new MenuItem { Header = Strings.Follow_StopFollowing };
        AutomationProperties.SetName(forget, Strings.Follow_StopFollowingHelp);
        forget.Click += (_, _) => StopFollowing(path);

        void Show()
        {
            var target = path ?? _savePath;
            var follows = target is not null && FollowedLink(target) is not null;
            latest.IsVisible = follows;
            forget.IsVisible = follows;
        }
        Show();
        menu.Opening += (_, _) => Show();
        menu.Items.Add(latest);
        menu.Items.Add(forget);
    }

    /// <summary>What a Sheets download came back as, before anything is done
    /// with it. File is null when Google answered with a web page.</summary>
    internal sealed record SheetDownload(
        string Text, ProfileFile? File, IReadOnlyList<SkippedTab> Skipped,
        IReadOnlyList<TabRename> Renamed, string? Limitation, bool Ours);

    /// <summary>The one Sheets download, shared by Import and by Get the
    /// latest. Throws HttpRequestException, TaskCanceledException and
    /// InvalidDataException, which each caller words for its own screen.
    /// The link must already have passed SheetsUrl.</summary>
    async Task<SheetDownload> FetchSheetAsync(string link, HttpClient client)
    {
        SheetsUrl.TryGetXlsxExportUrl(link, out var workbookUrl);
        SheetsUrl.TryGetCsvExportUrl(link, out var csvUrl);

        // A sheet this app made is read with the app's own token. The link
        // the user pastes is most often the one they copied from this very
        // profile a second ago, and the anonymous export below only answers
        // for a sheet whose link sharing is on.
        string? ours = null;
        if (SheetsUrl.TryGetId(link, out var sheetId)
            && Backup() is DriveBackup backup && backup.Knows(sheetId))
            try { ours = await backup.ReadProfileAsync(sheetId); }
            catch (Exception ex) when (ex is DriveApiException or GoogleAuthRevokedException
                or HttpRequestException or TaskCanceledException or InvalidDataException)
            { ours = null; } // fall through to the public export

        string text;
        bool wholeWorkbook = true;
        IReadOnlyList<SkippedTab> skipped = Array.Empty<SkippedTab>();
        IReadOnlyList<TabRename> renamed = Array.Empty<TabRename>();
        string? tooLarge = null;
        if (ours is not null) text = ours;
        else
        {
            // Ask for the whole workbook first, so a profile split across
            // mode tabs arrives whole. Published links can only give one tab
            // as CSV; they answer with something that is not a workbook, so
            // fall back.
            var bytes = await GetSheetAsync(client, workbookUrl!, (c, ct) => c.ReadAsByteArrayAsync(ct));
            wholeWorkbook = Xlsx.LooksLikeXlsx(bytes);
            if (wholeWorkbook)
            {
                using var stream = new MemoryStream(bytes);
                var read = Xlsx.Import(stream);
                (text, skipped, tooLarge, renamed) = (read.Csv, read.Skipped, read.Limitation, read.Renamed);
            }
            else text = await GetSheetAsync(client, csvUrl!, (c, ct) => c.ReadAsStringAsync(ct));
        }

        var file = text.TrimStart().StartsWith('<') ? null : ProfileFile.Load(text);
        return new SheetDownload(text, file, skipped, renamed,
            wholeWorkbook ? tooLarge : Strings.Main_ThisLinkIsAPublished, ours is not null);
    }

    /// <summary>Test seam: how a retry waits. Task.Delay for real.</summary>
    internal static Func<TimeSpan, CancellationToken, Task> RetryDelay = Task.Delay;

    /// <summary>One export request. A 429 or a 5xx is asked again twice, after
    /// 1 s and then 3 s, or after Retry-After when Google gives one (never
    /// more than 10 s). A sheet that is not shared answers 401 or 403, or
    /// sends the request to Google's sign-in page; both come back as a 403
    /// HttpRequestException, which callers word as "not shared".</summary>
    internal static async Task<T> GetSheetAsync<T>(HttpClient client, string url,
        Func<HttpContent, CancellationToken, Task<T>> read, CancellationToken ct = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var response = await client.GetAsync(url, ct);
            var code = (int)response.StatusCode;
            if (code is 401 or 403 || IsSignInPage(response.RequestMessage?.RequestUri))
                throw new HttpRequestException(Strings.Main_GoogleReturnedAWebPage, null, HttpStatusCode.Forbidden);
            if ((code == 429 || code >= 500) && attempt < 2)
            {
                await RetryDelay(RetryWait(response, attempt), ct);
                continue;
            }
            response.EnsureSuccessStatusCode();
            return await read(response.Content, ct);
        }
    }

    static bool IsSignInPage(Uri? uri) =>
        uri is { IsAbsoluteUri: true } && string.Equals(uri.Host, "accounts.google.com", StringComparison.OrdinalIgnoreCase);

    internal static TimeSpan RetryWait(HttpResponseMessage response, int attempt)
    {
        var wait = TimeSpan.FromSeconds(attempt == 0 ? 1 : 3);
        if (response.Headers.RetryAfter is { } after
            && (after.Delta ?? (after.Date - DateTimeOffset.UtcNow)) is { } asked && asked > TimeSpan.Zero)
            wait = asked;
        return wait > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : wait;
    }

    /// <summary>The download error, in words. A sheet that is not shared says
    /// how to share it rather than quoting an HTTP status.</summary>
    static string Failure(Exception ex) =>
        ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized }
            ? Strings.Main_GoogleReturnedAWebPage
            : string.Format(CultureInfo.CurrentCulture, Strings.Main_CouldNotDownloadTheSheet,
                ex is TaskCanceledException ? Strings.Main_ConnectionTimedOut : ex.Message);

    /// <summary>Why a download cannot be used as a profile, or null when it
    /// can. The same refusals for Import and for Get the latest.</summary>
    static string? Refusal(SheetDownload d) =>
        d.File is null ? Strings.Main_GoogleReturnedAWebPage
        : d.File.Document.Sheets.Count == 0 ? NoProfileTab(d.Text, d.Skipped)
        : null;

    /// <summary>"Get the latest from the link". Path null means the profile
    /// open in the editor.</summary>
    internal async Task GetLatestAsync(string? path, HttpClient? http = null)
    {
        if (!NetworkFeature.Enabled) return;
        // A mouth stick can double-fire one press. One download at a time.
        if (_gettingLatest) return;
        _gettingLatest = true;
        try { await GetLatestCoreAsync(path ?? _savePath, http); }
        finally { _gettingLatest = false; }
    }

    async Task GetLatestCoreAsync(string? path, HttpClient? http)
    {
        if (path is null || FollowedLink(path) is not string link)
        { Say(Strings.Follow_NotFollowed, StatusKind.Info); return; }

        // Unsaved work is never thrown away to make room for the sheet. It is
        // saved first, so it is what gets compared, and what the rescue folder
        // keeps if the sheet replaces it. Saying no stops here, before any
        // request is made.
        if (_savePath == path && _file is { Dirty: true })
        {
            if (!await ConfirmAsync(Strings.Follow_SaveFirstTitle, Strings.Follow_SaveFirstBody)) return;
            if (!await SaveAsync()) return;
        }

        Say(Strings.Main_DownloadingTheSpreadsheet, StatusKind.Info);
        SheetDownload got;
        try { got = await FetchSheetAsync(link, http ?? HttpClient); }
        catch (InvalidDataException)
        { Refused(Strings.Main_CouldNotReadThatSpreadsheet); return; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { Refused(Failure(ex)); return; }
        if (Refusal(got) is string refused) { Refused(refused); return; }
        var seen = Hash(got.Text);

        var incoming = got.File!;
        incoming.NormalizeForDeviceCsv(); // what lands on disk matches a save byte for byte
        ProfileFile? local = null;
        string? onDisk = null;
        // A copy that cannot be read is still a copy: it is compared as
        // unreadable and still goes to the rescue folder before a replace.
        try { onDisk = await File.ReadAllTextAsync(path); local = ProfileFile.Load(onDisk); }
        catch { local = null; }

        var changes = ProfileChanges.Between(local, incoming);
        if (changes.Count == 0)
        {
            Seen(path, seen, changed: false);
            Say(Strings.Follow_UpToDate, StatusKind.Ready);
            return;
        }

        // The same validation an import gets. An error blocks the replace, a
        // warning is shown beside it, and a sheet with fewer modes than the
        // file says so in the line nobody skims past.
        int ModeCount(ProfileFile? f) => f?.Document.Sheets.Count(s => s.Type == SheetType.ProfileName) ?? 0;
        int had = ModeCount(local), gets = ModeCount(incoming);
        var fewer = local is not null && gets < had
            ? string.Format(CultureInfo.CurrentCulture, Strings.Follow_FewerModes, gets, had) : null;
        if (!await ConfirmLatestAsync(changes, got.Limitation, incoming.Issues, fewer))
        {
            Seen(path, seen, changed: true, dismissed: true);
            Say(Strings.Follow_Kept, StatusKind.Info);
            return;
        }

        // The download and the question both take time, and the editor is not
        // locked while they do. What the user said yes to is the list above,
        // compared against the copy as it was; an edit made since, or a file
        // changed on disk since, was never shown in it and is not replaced.
        if ((_savePath == path && _file is { Dirty: true }) || !SameOnDisk(path, onDisk))
        { Say(Strings.Follow_ChangedMeanwhile, StatusKind.Warning); return; }

        var text = incoming.ToCsvText();
        try
        {
            if (File.Exists(path))
            {
                Directory.CreateDirectory(CrashGuard.RescueDir);
                var name = Path.GetFileNameWithoutExtension(path);
                // A fresh name every time and never overwrite: a rescue copy
                // is the only copy of what was replaced.
                File.Copy(path, Path.Combine(CrashGuard.RescueDir,
                    $"{name}-before-latest-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.csv"),
                    overwrite: false);
            }
            ProfileFile.WriteAtomic(path, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say(string.Format(CultureInfo.CurrentCulture, Strings.Main_CouldNotSaveExMessage, ex.Message), StatusKind.Error);
            return;
        }

        if (_savePath == path) OpenInEditor(ProfileFile.Load(text), path, ProfileSource.Sheets);
        else if (HomeView.IsVisible) RefreshHomeCards();
        // The user's own backup, if they have one, takes the new bytes the
        // same way a save would hand them over.
        FireBackupPush(path, text);
        Seen(path, seen, changed: false);
        Say(Strings.Follow_Replaced, StatusKind.Ready);

        void Refused(string why) => Say(
            string.Format(CultureInfo.CurrentCulture, Strings.Follow_CouldNotGet, why), StatusKind.Warning);
    }

    // What the last look at the sheet found, so the automatic check knows
    // whether a download is news.
    void Seen(string path, string hash, bool changed, bool dismissed = false)
    {
        if (!_settings.Follows.TryGetValue(path, out var follow)) return;
        follow.LastHash = hash;
        follow.Changed = changed;
        if (dismissed) follow.DismissedHash = hash;
        PersistSettings();
        if (path == _savePath) ShowFollowBanner(changed && !dismissed);
    }

    static bool SameOnDisk(string path, string? before)
    {
        try { return File.ReadAllText(path) == before; }
        catch { return before is null && !File.Exists(path); }
    }

    /// <summary>What the sheet would change, and the one question: replace?
    /// The safe answer is the default, so a stray Enter keeps the user's copy.</summary>
    async Task<bool> ConfirmLatestAsync(IReadOnlyList<string> changes, string? limitation,
        IReadOnlyList<Issue> issues, string? fewerModes)
    {
        var errors = issues.Where(i => i.Severity == Severity.Error).ToList();
        var warnings = issues.Where(i => i.Severity == Severity.Warning).ToList();
        // An error blocks the replace. The button stays, greyed, so a screen
        // reader still finds it and the reason above it.
        var replace = new Button { Content = Strings.Follow_Replace, MinWidth = 140, IsEnabled = errors.Count == 0 };
        var keep = new Button { Content = Strings.Follow_Keep, MinWidth = 140, IsDefault = true, IsCancel = true };
        var list = new StackPanel { Spacing = 4 };
        // Every change is listed; a long one scrolls so the question stays on
        // screen (the same 260 cap as the agent's write review).
        foreach (var line in changes)
            list.Children.Add(new TextBlock { Text = "• " + line, TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize") });
        var panel = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 16,
            MaxWidth = 520,
            Children =
            {
                new TextBlock { Text = Strings.Follow_ReviewTitle, FontWeight = FontWeight.Bold, FontSize = Size("SubheadSize"), TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = Strings.Follow_ReviewBody, TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize") },
                new ScrollViewer { MaxHeight = 260, Content = list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
            },
        };
        // A published link sees one tab. Replacing a five-mode profile with it
        // would drop four modes, so the reason is said beside the list that
        // shows them going.
        if (limitation is not null)
            panel.Children.Add(new TextBlock { Text = limitation, TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize") });
        if (fewerModes is not null)
            panel.Children.Add(Flagged(fewerModes, "WarningBrush"));
        if (errors.Count > 0) panel.Children.Add(IssueList(Strings.Follow_HasErrors, errors, "ErrorBrush"));
        if (warnings.Count > 0) panel.Children.Add(IssueList(Strings.Follow_HasWarnings, warnings, "WarningBrush"));
        panel.Children.Add(new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Children = { replace, keep } });

        var dialog = new Window
        {
            Title = Strings.Follow_ReviewTitle,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = ZoomWrap(panel, _uiScale),
        };
        var result = false;
        replace.Click += (_, _) => { result = true; dialog.Close(); };
        keep.Click += (_, _) => dialog.Close();
        await ShowDialogInShellAsync(dialog);
        return result;
    }

    // Bold and coloured, and the words carry it on their own.
    TextBlock Flagged(string text, string brush)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize"), FontWeight = FontWeight.Bold };
        t[!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(brush);
        return t;
    }

    Control IssueList(string heading, IReadOnlyList<Issue> issues, string brush)
    {
        var list = new StackPanel { Spacing = 4 };
        list.Children.Add(Flagged(heading, brush));
        foreach (var i in issues)
            list.Children.Add(new TextBlock
            { Text = "• " + i.Cell + ": " + i.Message + " " + i.Fix, TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize") });
        return new ScrollViewer { MaxHeight = 160, Content = list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    // ---- The automatic check ----------------------------------------------
    // One timer. A followed profile is looked at when it is opened, when the
    // window comes back to the front (at most once a minute), and every five
    // minutes while it stays there. A sheet this app made is asked for its
    // modified time first, which is one small request; anything else is
    // downloaded and its hash compared with the last one seen. Google saying
    // "slow down" or failing doubles the wait, up to 30 minutes.
    static readonly TimeSpan FollowEvery = TimeSpan.FromMinutes(5);
    static readonly TimeSpan FollowFocusGap = TimeSpan.FromSeconds(60);
    static readonly TimeSpan FollowMostWait = TimeSpan.FromMinutes(30);
    DispatcherTimer? _followTimer;
    TimeSpan _followWait = FollowEvery;
    DateTime _followLast = DateTime.MinValue, _followNotBefore = DateTime.MinValue;
    bool _followChecking;

    /// <summary>Test seam: a Drive engine with a fake client, in place of one
    /// built from a stored Google token.</summary>
    internal DriveBackup BackupForTest { set => _driveBackup = value; }

    /// <summary>Test seam: the clock the check reads.</summary>
    internal Func<DateTime> FollowClock = () => DateTime.UtcNow;

    void StartFollowWatch()
    {
        FollowBannerReview.Click += async (_, _) => await GetLatestAsync(null);
        FollowBannerDismiss.Click += (_, _) =>
        {
            if (_savePath is string p && _settings.Follows.TryGetValue(p, out var f))
            {
                f.DismissedHash = f.LastHash;
                PersistSettings();
            }
            ShowFollowBanner(false);
        };
        _followTimer = new DispatcherTimer { Interval = FollowEvery };
        _followTimer.Tick += (_, _) => { if (IsActive) _ = CheckFollowAsync(); };
        _followTimer.Start();
        Activated += (_, _) => { if (FollowClock() - _followLast >= FollowFocusGap) _ = CheckFollowAsync(); };
        Closed += (_, _) => _followTimer.Stop();
    }

    /// <summary>Called when a profile opens: the banner shows what the last
    /// check found, and a followed profile is looked at again.</summary>
    void FollowOpened()
    {
        ShowFollowBanner(_savePath is string p && _settings.Follows.TryGetValue(p, out var f) && Pending(f));
        if (_savePath is string q && _settings.Follows.ContainsKey(q)) _ = CheckFollowAsync();
    }

    static bool Pending(FollowLink f) => f.Changed && f.LastHash != f.DismissedHash;

    void ShowFollowBanner(bool show) => FollowBannerBar.IsVisible = show;

    internal bool FollowBannerShown => FollowBannerBar.IsVisible;
    internal bool FollowChecking => _followChecking;

    internal async Task CheckFollowAsync(HttpClient? http = null)
    {
        if (!NetworkFeature.Enabled || !_settings.FollowAutoCheck || _followChecking || _gettingLatest) return;
        if (_savePath is not string path || !_settings.Follows.TryGetValue(path, out var follow)) return;
        if (FollowClock() < _followNotBefore || !NetworkInterface.GetIsNetworkAvailable()) return;
        _followChecking = true;
        _followLast = FollowClock();
        try
        {
            string? modified = null;
            if (follow.SheetId.Length > 0 && Backup() is DriveBackup backup && backup.Knows(follow.SheetId))
            {
                try { modified = await backup.ModifiedTimeAsync(follow.SheetId); }
                catch (Exception ex) when (ex is DriveApiException or GoogleAuthRevokedException
                    or HttpRequestException or TaskCanceledException or InvalidOperationException) { }
                if (modified is not null && modified == follow.LastModifiedTime) { Settled(); return; }
            }

            var got = await FetchSheetAsync(follow.Link, http ?? HttpOverrideForTest ?? HttpClient);
            if (Refusal(got) is not null) return; // looked at again next time, modified time not taken
            // Kept only once the download it stands for has been compared, so a
            // failed download is tried again rather than taken as seen.
            if (modified is not null) follow.LastModifiedTime = modified;
            if (Hash(got.Text) is var hash && hash != follow.LastHash)
            {
                ProfileFile? local = null;
                try { local = ProfileFile.Load(await File.ReadAllTextAsync(path)); } catch { local = null; }
                var incoming = got.File!;
                incoming.NormalizeForDeviceCsv();
                follow.LastHash = hash;
                follow.Changed = ProfileChanges.Between(local, incoming).Count > 0;
            }
            PersistSettings();
            Settled();
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.TooManyRequests
            || (int?)ex.StatusCode >= 500)
        {
            _followWait = _followWait * 2 > FollowMostWait ? FollowMostWait : _followWait * 2;
            _followNotBefore = FollowClock() + _followWait;
            if (_followTimer is not null) _followTimer.Interval = _followWait;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException) { }
        finally { _followChecking = false; }

        void Settled()
        {
            _followWait = FollowEvery;
            if (_followTimer is not null) _followTimer.Interval = FollowEvery;
            if (_savePath == path) ShowFollowBanner(Pending(follow));
        }
    }
}
