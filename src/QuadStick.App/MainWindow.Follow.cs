using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using QuadStick.Format;

namespace QuadStick.App;

// Following a Google Sheets link. A profile imported from a link remembers it
// once it is saved, and "Get the latest from the link" asks that sheet again.
//
// Three rules hold the whole feature:
//   - nothing reaches the network until the user presses the button: there is
//     no check on open, on launch, or on a timer;
//   - the download goes through the same import path and the same validation
//     as a pasted link, so a sheet that would not import cannot replace;
//   - nothing replaces the local profile until the user has seen what changed
//     and said yes, and their old copy goes to the rescue folder first.
public partial class MainWindow
{
    /// <summary>The link the profile open right now was imported from, until
    /// it is saved and the link can be kept against its file.</summary>
    string? _pendingFollowLink;

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
        _settings.FollowLinks.TryGetValue(path, out var link) ? link : null;

    void RememberFollowLink(string? path)
    {
        if (path is null || _pendingFollowLink is not string link) return;
        _pendingFollowLink = null;
        _settings.FollowLinks[path] = link;
        PersistSettings();
    }

    void ForgetFollowLink(string path)
    {
        if (_settings.FollowLinks.Remove(path)) PersistSettings();
    }

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
            var bytes = await client.GetByteArrayAsync(workbookUrl);
            wholeWorkbook = Xlsx.LooksLikeXlsx(bytes);
            if (wholeWorkbook)
            {
                using var stream = new MemoryStream(bytes);
                var read = Xlsx.Import(stream);
                (text, skipped, tooLarge, renamed) = (read.Csv, read.Skipped, read.Limitation, read.Renamed);
            }
            else text = await client.GetStringAsync(csvUrl);
        }

        var file = text.TrimStart().StartsWith('<') ? null : ProfileFile.Load(text);
        return new SheetDownload(text, file, skipped, renamed,
            wholeWorkbook ? tooLarge : Strings.Main_ThisLinkIsAPublished, ours is not null);
    }

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
        {
            Refused(string.Format(CultureInfo.CurrentCulture, Strings.Main_CouldNotDownloadTheSheet,
                ex is TaskCanceledException ? Strings.Main_ConnectionTimedOut : ex.Message));
            return;
        }
        if (Refusal(got) is string refused) { Refused(refused); return; }

        var incoming = got.File!;
        incoming.NormalizeForDeviceCsv(); // what lands on disk matches a save byte for byte
        ProfileFile? local = null;
        string? onDisk = null;
        // A copy that cannot be read is still a copy: it is compared as
        // unreadable and still goes to the rescue folder before a replace.
        try { onDisk = await File.ReadAllTextAsync(path); local = ProfileFile.Load(onDisk); }
        catch { local = null; }

        var changes = ProfileChanges.Between(local, incoming);
        if (changes.Count == 0) { Say(Strings.Follow_UpToDate, StatusKind.Ready); return; }

        if (!await ConfirmLatestAsync(changes, got.Limitation))
        { Say(Strings.Follow_Kept, StatusKind.Info); return; }

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
                File.Copy(path, Path.Combine(CrashGuard.RescueDir,
                    $"{name}-before-latest-{DateTime.Now:yyyyMMdd-HHmmss}-{DateTime.Now.Ticks % 10000}.csv"),
                    overwrite: true);
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
        Say(Strings.Follow_Replaced, StatusKind.Ready);

        void Refused(string why) => Say(
            string.Format(CultureInfo.CurrentCulture, Strings.Follow_CouldNotGet, why), StatusKind.Warning);
    }

    static bool SameOnDisk(string path, string? before)
    {
        try { return File.ReadAllText(path) == before; }
        catch { return before is null && !File.Exists(path); }
    }

    /// <summary>What the sheet would change, and the one question: replace?
    /// The safe answer is the default, so a stray Enter keeps the user's copy.</summary>
    async Task<bool> ConfirmLatestAsync(IReadOnlyList<string> changes, string? limitation)
    {
        var replace = new Button { Content = Strings.Follow_Replace, MinWidth = 140 };
        var keep = new Button { Content = Strings.Follow_Keep, MinWidth = 140, IsDefault = true, IsCancel = true };
        var list = new StackPanel { Spacing = 4 };
        foreach (var line in changes.Take(12))
            list.Children.Add(new TextBlock { Text = "• " + line, TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize") });
        if (changes.Count > 12)
            list.Children.Add(new TextBlock { Text = "• …", FontSize = Size("BodySize") });
        var panel = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 16,
            MaxWidth = 520,
            Children =
            {
                new TextBlock { Text = Strings.Follow_ReviewTitle, FontWeight = FontWeight.Bold, FontSize = Size("SubheadSize"), TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = Strings.Follow_ReviewBody, TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize") },
                list,
            },
        };
        // A published link sees one tab. Replacing a five-mode profile with it
        // would drop four modes, so the reason is said beside the list that
        // shows them going.
        if (limitation is not null)
            panel.Children.Add(new TextBlock { Text = limitation, TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize") });
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
}
