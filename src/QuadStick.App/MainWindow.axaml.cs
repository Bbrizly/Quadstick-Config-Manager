using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.Format;

namespace QuadStick.App;

public partial class MainWindow : Window
{
    // Bind any brush property to a theme token so it repaints on theme change.
    // Never resolve+assign a concrete brush for a themed color: that freezes it.
    static void BindBrush(Control target, AvaloniaProperty property, string tokenKey) =>
        target[!property] = new DynamicResourceExtension(tokenKey + "Brush");

    /// <summary>The same binding, for the gallery, which is not a MainWindow.</summary>
    internal static void BindBrushTo(Control target, AvaloniaProperty property, string tokenKey) =>
        BindBrush(target, property, tokenKey);

    // Type scale doesn't change with theme, so a one-time resource read is fine
    // here (same reasoning as the icon Data lookup below).
    static double Size(string tokenKey) => (double)Application.Current!.FindResource(tokenKey)!;

    enum StatusKind { Ready, Info, Warning, Error }

    static Control StatusChip(StatusKind kind, string text, bool plainDot = false)
    {
        var (iconKey, tokenKey) = kind switch
        {
            StatusKind.Ready   => ("IconCheck",   "Success"),
            StatusKind.Warning => ("IconWarning", "Warning"),
            StatusKind.Error   => ("IconError",   "Error"),
            _                  => ("IconChevron", "TextSecondary"),
        };
        // A neutral "not connected" state reads better as a simple hollow dot
        // than a chevron glyph (which looked like a stray ">").
        Control icon;
        if (plainDot)
        {
            var dot = new Border { Width = 12, Height = 12, CornerRadius = new Avalonia.CornerRadius(6),
                BorderThickness = new Avalonia.Thickness(2), Background = Brushes.Transparent };
            BindBrush(dot, Border.BorderBrushProperty, tokenKey);
            icon = dot;
        }
        else icon = Glyph(iconKey, tokenKey);
        var label = new TextBlock { Text = text, FontSize = Size("BodySize"),
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        BindBrush(label, TextBlock.ForegroundProperty, tokenKey);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { icon, label } };
    }

    ProfileFile? _file;
    string? _savePath;          // where Save writes; null until saved or opened from a path
    int _sheetIndex;
    bool _deviceView = true;    // true = the split editor (diagram OR rail); false = the raw List View
    bool _railView;             // when in the split editor, show the parts as a list instead of the diagram
    string? _selectedZone;
    // Device View shows friendly words ("soft sip") by default; the Words
    // button cycles plain English -> Xbox-style button names -> the raw token
    // the List View and the CSV use ("mp_left_sip_soft"), so the views speak
    // whichever vocabulary the user thinks in.
    int _labelStyle = 1; // 0 = raw list names, 1 = plain English, 2 = Xbox style
    bool _friendlyLabels => _labelStyle != 0;
    QsModel _model;
    AppSettings _settings = Settings.Load();
    double _uiScale = 1.0;
    bool _reduceMotion;

    static readonly string[] ModelNames = { "QuadStick FPS", "QuadStick Original", "QuadStick Singleton" };

    void SaveModel() { _settings.Model = _model.ToString(); Settings.Save(_settings); }

    // Google Sheets backup engine, built lazily. Null (backup does nothing)
    // unless backup is on, a real client id shipped, and a token is stored.
    // Settings default off, so headless tests never touch the network.
    DriveBackup? _driveBackup;
    DriveBackup? Backup()
    {
        if (_driveBackup != null) return _driveBackup;
        if (!_settings.DriveBackup || !GoogleAuth.IsConfigured) return null;
        var store = TokenStore.Create();
        if (store.Load() is null) return null;
        var auth = new GoogleAuth(store);
        var client = new DriveClient(new HttpClientHandler(), auth.GetAccessTokenAsync);
        // Buttons are fixed Yes/Cancel, so the choice lives in the body text
        // (Yes = replace with mine / recreate). The engine runs off the UI
        // thread and dialogs build only on it, so prompts marshal through it.
        _driveBackup = new DriveBackup(client, () => _settings, () => Settings.TrySave(_settings),
            conflictPrompt: async (title, body) =>
                await Dispatcher.UIThread.InvokeAsync(() => ConfirmAsync(title, body))
                    ? ConflictChoice.ReplaceWithMine : ConflictChoice.KeepOnline,
            recreatePrompt: (title, body) => Dispatcher.UIThread.InvokeAsync(() => ConfirmAsync(title, body)),
            status: (msg, warn) => Dispatcher.UIThread.Post(() =>
                Status(msg, warn ? StatusKind.Warning : StatusKind.Info)),
            shareConfirm: () => Dispatcher.UIThread.InvokeAsync(() => ConfirmAsync(
                Strings.Main_ShareThisSheet,
                Strings.Main_AnyoneWithThisLinkCan)));
        return _driveBackup;
    }

    // True only when backup is fully live (on, real client, token stored).
    // Settings and home cards read this so all three agree on "Connected".
    public bool DriveConnected => Backup() is not null;

    // Fire-and-forget the push after a save. Never awaited on the save path.
    void FireBackupPush(string path, string text) =>
        RunBackup(path, async b => (PushResult?)await b.PushAsync(path, text));

    // Fire-and-forget the retry when a linked profile opens.
    // Cell C1 of the file is the sheet it is backed up to. Called before any
    // write and when a profile opens, so the id travels with the file instead
    // of only living in settings under a path that a rename invalidates.
    //
    // Recovery runs first: a profile that moved carries its id but has no
    // settings entry, and TryRecoverLink decides whether that id is really
    // this file's to claim.
    void SyncSheetIdentity(string? path)
    {
        if (_file is null || path is null) return;
        var backup = Backup();
        if (backup is null) return;
        if (SheetsUrl.TryGetEditUrlFromHeader(_file.Document.HeaderVersion, _file.Document.HeaderSource, out _)
            && _file.Document.HeaderSource is { Length: > 0 } carried)
            backup.TryRecoverLink(path, IdOnly(carried));
        _file.HeaderSheetId = backup.LinkedSheetId(path);
    }

    // A 1.4 header carries a whole URL where 1.5 carries the bare id. Settings
    // only ever hold the id.
    static string IdOnly(string source) =>
        SheetsUrl.TryGetId(source, out var id) ? id : source;

    void FireBackupRetry(string path, string text) =>
        RunBackup(path, b => b.RetryIfDirtyAsync(path, text));

    // The last background backup task. Copy share link awaits it so the
    // just-saved push settles before the share flow reads link state,
    // instead of racing it for the engine gate.
    Task? _backupInFlight;

    // One wrapper for both: run off the UI thread, swallow everything (backup
    // must never crash the app), apply a KeptOnline result on the UI thread.
    // A null backup means backup is off; do nothing.
    void RunBackup(string path, Func<DriveBackup, Task<PushResult?>> op)
    {
        var backup = Backup();
        if (backup is null) return;
        _backupInFlight = Task.Run(async () =>
        {
            try
            {
                var result = await op(backup);
                if (result?.Kind == PushResultKind.KeptOnline && result.DownloadedCsv is string online)
                    await Dispatcher.UIThread.InvokeAsync(() => ApplyKeptOnline(path, online));
                else if (result?.Kind == PushResultKind.Pushed)
                    Dispatcher.UIThread.Post(() =>
                    {
                        // Tracked on a real push, not on entry. FireBackupPush
                        // runs after every save, so counting attempts here
                        // would report backup as used by anyone who saved once
                        // with Drive connected, working or not.
                        Telemetry.Track(TelemetryEvent.FeatureUsed, AppFeature.DriveBackup);
                        Status(Strings.Main_BackedUpToGoogleDrive, StatusKind.Ready);
                        // The push writes the Drive link that draws a card's
                        // "on Google Drive" line. Home may already be on screen
                        // by the time it lands, so redraw it.
                        if (HomeView.IsVisible) RefreshHomeCards();
                    });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_BackupErrorExMessage, ex.Message), StatusKind.Warning));
            }
        });
    }

    // Keep online: the local file is never lost. Copy it to the rescue folder
    // first, then overwrite with the sheet and reload if still on this file.
    void ApplyKeptOnline(string path, string onlineCsv)
    {
        // Read the sheet BEFORE touching the local file. Someone can empty or
        // wreck a sheet in the browser, and that must never be able to replace
        // a working profile. Same validity check restore uses.
        ProfileFile? online = null;
        try { online = ProfileFile.Load(onlineCsv); } catch { }
        if (online is null || online.Document.Sheets.Count == 0)
        {
            Status(Strings.Main_TheOnlineCopyOfThis, StatusKind.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(CrashGuard.RescueDir);
            if (File.Exists(path))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var dest = Path.Combine(CrashGuard.RescueDir,
                    $"{name}-replaced-{DateTime.Now:yyyyMMdd-HHmmss}-{DateTime.Now.Ticks % 10000}.csv");
                File.Copy(path, dest, overwrite: true);
            }
            ProfileFile.WriteAtomic(path, onlineCsv);
            if (_savePath == path)
                OpenInEditor(online, path, ProfileSource.Drive);
            Status(Strings.Main_LoadedTheOnlineVersionOf, StatusKind.Warning);
        }
        catch (Exception ex)
        {
            Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_CouldNotLoadTheOnline, ex.Message), StatusKind.Error);
        }
    }

    // Runs OAuth sign-in and turns backup on when it succeeds. Shared by the
    // Settings Backup checkbox and Reconnect button: one place that flips the
    // setting and rebuilds the engine.
    public async Task<bool> ConnectGoogleAsync(CancellationToken ct = default)
    {
        if (!GoogleAuth.IsConfigured) return false;
        try
        {
            var auth = new GoogleAuth(TokenStore.Create());
            await auth.SignInAsync(uri => Launcher.LaunchUriAsync(uri), ct);
            _settings.DriveBackup = true;
            PersistSettings();
            _driveBackup = null; // rebuild next use, now that a token is stored
        }
        // Sign-in runs a browser, a socket, and a keychain write, and any of
        // them can throw something we did not name. Failing to connect means
        // "not connected", never a crash on top of it.
        catch (Exception) { return false; }

        // Reconnect means catch up: the open profile's failed backup goes now
        // rather than waiting for the next save.
        if (_savePath is not null && _file is not null)
            FireBackupRetry(_savePath, _file.ToCsvText());
        return true;
    }

    // Turns backup off and forgets the Google account. Off has to mean off: a
    // token left in the keychain is a connection the user thinks they ended,
    // and it is also the only way to sign in as somebody else.
    public void DisableDriveBackup()
    {
        _settings.DriveBackup = false;
        PersistSettings();
        _driveBackup = null;
        try { TokenStore.Create().Delete(); } catch { /* nothing left to do about it */ }
    }

    // The home Drive button is a status light and the main way to turn backup on.
    //   green  = connected (click opens Import)
    //   yellow = not signed in, or sign-in broke (click signs in)
    //   red    = this build has no Google connection
    // Yellow needs two presses so a stray click never launches a browser.
    bool _driveArmed;
    DispatcherTimer? _driveArmTimer;

    void RefreshDriveButton()
    {
        _driveArmed = false;
        _driveArmTimer?.Stop();

        HomeDriveButton.IsVisible = GoogleAuth.IsConfigured;
        if (!GoogleAuth.IsConfigured)
        {
            SetDriveButton(Strings.Main_BackupOff, "Error", enabled: false,
                Strings.Main_GoogleDriveBackupIsNot);
            return;
        }
        if (DriveConnected)
            SetDriveButton(Strings.Main_BackingUpToDrive, "Success", enabled: true,
                Strings.Main_BackingUpToGoogleDrive);
        else
            SetDriveButton(Strings.Main_SignInToBackUp, "Warning", enabled: true,
                Strings.Main_ClickToSignInAnd);
    }

    void SetDriveButton(string text, string colorToken, bool enabled, string help)
    {
        HomeDriveButton.IsEnabled = enabled;
        var dot = new TextBlock { Text = "●", VerticalAlignment = VerticalAlignment.Center };
        BindBrush(dot, TextBlock.ForegroundProperty, colorToken);
        var label = new TextBlock
        { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = Size("SmallSize") };
        BindBrush(label, TextBlock.ForegroundProperty, colorToken);
        HomeDriveButton.Content = new StackPanel
        { Orientation = Orientation.Horizontal, Spacing = 6, Children = { dot, label } };
        AutomationProperties.SetName(HomeDriveButton, help);
        ToolTip.SetTip(HomeDriveButton, help);
    }

    async void OnDriveButtonClick()
    {
        if (!GoogleAuth.IsConfigured) return;

        // Green, connected: the click brings other profiles over.
        if (DriveConnected) { await ShowDrivePickerAsync(preCheck: false); return; }

        // Yellow, first press: arm and wait for a second press.
        if (!_driveArmed)
        {
            _driveArmed = true;
            SetDriveButton(Strings.Main_PressAgainToSignIn, "Warning", enabled: true,
                Strings.Main_PressAgainToOpenGoogle);
            _driveArmTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _driveArmTimer.Tick -= DisarmDrive;
            _driveArmTimer.Tick += DisarmDrive;
            _driveArmTimer.Start();
            return;
        }

        // Yellow, second press: sign in, then offer to pull profiles down.
        _driveArmed = false;
        _driveArmTimer?.Stop();
        bool ok = await ConnectGoogleAsync();
        RefreshDriveButton();
        if (ok && await ConfirmRestoreAfterConnectAsync())
            await ShowDrivePickerAsync(preCheck: true);
    }

    void DisarmDrive(object? sender, EventArgs e)
    {
        _driveArmTimer?.Stop();
        if (_driveArmed) { _driveArmed = false; RefreshDriveButton(); }
    }

    // Open the Drive restore picker. Same guard as the share actions: when
    // backup is off, explain and route to Settings. preCheck true pre-checks
    // everything (restore-all from onboarding); false starts empty (cherry-pick).
    public async Task ShowDrivePickerAsync(bool preCheck)
    {
        if (!await ReadyForDriveAsync(Strings.Main_ImportFromGoogleDrive)) return;
        await new DrivePickerWindow(this, preCheck).ShowDialog(this);
    }

    // Naming a game and having a profile built for it. Not modal: the agent can
    // ask a question that takes real thought, and a window that pins the whole
    // app while somebody decides what crouch should be is the wrong shape.
    //
    // Off for this release. The guard is here as well as on the buttons so no
    // other path can reach the window while AgentFeature.Enabled is false.
    public void ShowAgent(bool changing = false)
    {
        if (!AgentFeature.Enabled) return;
        new AgentWindow(this, changing).Show(this);
    }

    // The community list is fetched from here and nowhere else. Startup and a
    // home refresh never touch it, so the app opens with no network at all.
    // Built once and kept. The catalog it downloaded is the reason: coming back
    // to the page should not be another wait on quadstick.com.
    CommunityProfilesView? _community;

    internal CommunityProfilesView CommunityView =>
        _community ??= new CommunityProfilesView(this);

    /// <summary>Test seam: host a view built with fake HTTP handlers on the
    /// Community page and show it, so a test drives the page the shell shows
    /// rather than a control floating outside the window.</summary>
    internal void HostCommunityViewForPreview(CommunityProfilesView view)
    {
        _community = view;
        CommunityPageBody.Children.Clear();
        ShowCommunityPage();
    }

    public void ShowCommunityPage()
    {
        _file = null; // no profile is open on a page; a stale dirty file re-asks "leave?" on the next action
        if (CommunityPageBody.Children.Count == 0) CommunityPageBody.Children.Add(CommunityView);
        ShowPage(CommunityPage, ShellCommunityButton);
        CommunityView.Start();
    }

    // Managing what is already on the QuadStick. Everything it does is file
    // work on a mounted drive; nothing here talks to the device any other way.
    public async Task ShowDeviceFilesAsync()
    {
        await new DeviceFilesWindow(this).ShowDialog(this);
        // Copying or deleting on the device changes what Home shows.
        if (HomeView.IsVisible) RefreshHomeCards();
    }

    // The device manager opens a file the same way a home card does: a working
    // copy with no save path, so Save goes to the library and only Install,
    // with its backup and verification, ever writes back to the QuadStick.
    internal void OpenDeviceProfile(ProfileFile file)
    {
        OpenInEditor(file, null, ProfileSource.Device);
        Status(Strings.Main_OpenedFromYourQuadStickSave, StatusKind.Warning);
    }

    // The picker reaches back through these. Backup() is non-null here:
    // ShowDrivePickerAsync gated on it.
    internal Task<List<DriveSheetInfo>> ListDriveSheetsAsync() => Backup()!.ListForPickerAsync();

    internal async Task<RestoreSummary> RestoreFromDriveAsync(IReadOnlyList<(string Id, string Name)> picks)
    {
        var summary = await Backup()!.RestoreAsync(picks, LibraryDir);
        // Opening the picker is not using restore. Someone who browses it and
        // closes it, or picks sheets that all fail, has restored nothing.
        if (summary.Imported.Count > 0)
            Telemetry.Track(TelemetryEvent.FeatureUsed, AppFeature.DriveRestore);
        return summary;
    }
    // Called by the Manage files window after every copy and delete. That
    // window is careful to keep its own file work off the UI thread, and this
    // undid it: RefreshHomeCards rescans the drives, lists the device, and
    // parses every profile on it to build each card's subtitle, all inline. On a
    // spun down stick that is seconds of frozen window, and it ran even with the
    // editor on screen and the cards it was rebuilding not visible at all.
    // ShowDeviceFilesAsync has always had this guard; the way back in did not.
    internal void RefreshHomeAfterRestore()
    {
        if (HomeView.IsVisible) RefreshHomeCards();
    }

    // Offered right after a connect, the new-machine moment. Public wrapper
    // because ConfirmAsync is private (like ConfirmResetAsync).
    public Task<bool> ConfirmRestoreAfterConnectAsync() => ConfirmAsync(
        Strings.Main_RestoreYourProfiles,
        Strings.Main_CopyYourBackedUpProfiles);

    // The two sharing actions, one pair everywhere: the editor's Share flyout
    // and each home card's context menu. path is null for the open editor,
    // a real path for a home card.
    MenuFlyout ShareMenu(string? path)
    {
        var copy = new MenuItem { Header = Strings.Main_CopyShareLink };
        AutomationProperties.SetName(copy, Strings.Main_CopyALinkToThis);
        copy.Click += async (_, _) => await CopyShareLinkAsync(path);
        var open = new MenuItem { Header = Strings.Main_OpenInGoogleSheets };
        AutomationProperties.SetName(open, Strings.Main_OpenThisProfileSGoogle);
        open.Click += async (_, _) => await OpenInSheetsAsync(path);
        return new MenuFlyout { Items = { copy, open } };
    }

    // Sharing needs backup on and connected. When off, the actions still show,
    // and choosing one opens the setup wizard rather than dropping the user in
    // Settings with a one line status behind it. True when the caller may go
    // ahead: either backup was already live, or the user walked the wizard to
    // its last step.
    //
    // The wizard never shares anything itself. Its last button closes it and
    // the caller runs the action the user originally asked for, so there is
    // only ever one copy of what share does.
    internal async Task<bool> ReadyForDriveAsync(string finishLabel, bool needsSave = false)
    {
        if (Backup() is not null) return true;
        var wizard = new ShareSetupWindow(this, finishLabel, needsSave);
        await wizard.ShowDialog(this);
        return wizard.Completed && Backup() is not null;
    }

    /// <summary>Whether the open profile has somewhere on disk to be saved to.
    /// The share wizard asks, because a sheet is named after the file and an
    /// unsaved profile has no name yet.</summary>
    internal bool ProfileIsSaved => _savePath is not null;

    /// <summary>Save the open profile, for the share wizard's second step.
    /// </summary>
    internal Task<bool> SaveProfileAsync() => SaveAsync();

    // Copy a profile's share link. path null means the open editor: save first
    // so a never-saved file is named and on disk. A home card passes its path,
    // already saved. Awaited (not fire-and-forget): async HTTP yields, so the
    // UI stays responsive without a spinner.
    internal async Task CopyShareLinkAsync(string? path)
    {
        Telemetry.Track(TelemetryEvent.FeatureUsed, AppFeature.ShareLink);
        if (!await ReadyForDriveAsync(Strings.Main_CopyShareLink, needsSave: path is null && _savePath is null)) return;

        string csvText;
        if (path is null)
        {
            if (!await SaveAsync()) return; // save names the file and gives a path
            path = _savePath;
            if (path is null || _file is null) return;
            csvText = _file.ToCsvText();
        }
        else
        {
            try { csvText = File.ReadAllText(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_CouldNotReadBareNamePath, BareName(Path.GetFileName(path)), ex.Message), StatusKind.Error);
                return;
            }
        }

        // Let the save's background push finish first, so the share flow reads
        // settled link state instead of racing it for the engine gate.
        if (_backupInFlight is Task inFlight)
            try { await inFlight; } catch { /* RunBackup already reported it */ }

        // A share now writes one tab per mode and colours the new ones, so it
        // is several requests, and the window looked idle for all of them.
        Status(Strings.Main_PuttingThisProfileInGoogle, StatusKind.Info);
        ShareLinkResult result;
        try { result = await Backup()!.GetShareLinkAsync(path, csvText); }
        catch (Exception ex)
        {
            // Google answering in a shape the client did not expect escaped to
            // the crash guard, which swallows it. The status was left reading
            // "Putting this profile in Google Sheets..." for good.
            Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_CouldNotShareThisProfile, ex.Message), StatusKind.Error);
            return;
        }

        // The dirty push here can hit the conflict prompt. Keep online still
        // rescues and replaces the local file, share or no share.
        if (result.DownloadedCsv is string kept) ApplyKeptOnline(path, kept);

        switch (result.Kind)
        {
            case ShareLinkKind.Copied:
            case ShareLinkKind.CopiedStale:
                if (result.Url is string url && Clipboard is { } cb)
                {
                    await cb.SetTextAsync(url);
                    // On the clipboard is the only point where the user got
                    // what they asked for. Cancelled and Failed both land in
                    // the arms below and must not count as a share.
                    Telemetry.Track(TelemetryEvent.FeatureUsed, AppFeature.ShareLink);
                    Status(result.Message, result.Kind == ShareLinkKind.CopiedStale ? StatusKind.Warning : StatusKind.Ready);
                }
                break;
            case ShareLinkKind.Failed:
                Status(result.Message, StatusKind.Warning);
                break;
            case ShareLinkKind.Cancelled:
                break; // the user backed out; say nothing
        }
    }

    // Open a profile's sheet in the browser. No sheet yet means copy a share
    // link first, which creates it.
    async Task OpenInSheetsAsync(string? path)
    {
        if (!await ReadyForDriveAsync(Strings.Main_OpenInGoogleSheets,
            needsSave: path is null && _savePath is null)) return;

        if (path is null)
        {
            if (_savePath is null)
            {
                Status(Strings.Main_SaveThisProfileFirstThen, StatusKind.Info);
                return;
            }
            path = _savePath;
        }

        var url = Backup()!.LinkedSheetUrl(path);
        if (url is null)
        {
            Status(Strings.Main_ThisProfileHasNoGoogle, StatusKind.Info);
            return;
        }
        await Launcher.LaunchUriAsync(new Uri(url));
    }

    readonly Dictionary<string, Border> _cellBorders = new();
    readonly Dictionary<string, Button> _zoneButtons = new(); // Device View zone id -> its button, for focus management
    // A workbook is a spreadsheet of a few hundred rows. Sheets will happily
    // export a ten million cell document, and this had no size cap at all while
    // the catalog client next to it did, so a community row could name a sheet
    // big enough to run the app out of memory on Import. Going over fails the
    // download, which the import path already reports.
    internal const int MaxWorkbookBytes = 32 * 1024 * 1024;

    static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = MaxWorkbookBytes,
    };

    /// <summary>The app's one HTTP client, for windows that need it. Settings
    /// uses it for the update check.</summary>
    internal HttpClient HttpClient => Http;
    const string DefaultNewName = "mygame.csv";

    // The same page the store listings declare. bbrizly.github.io still
    // redirects here, so old links keep working.
    internal const string PrivacyPolicyUrl =
        "https://bassamkamal.dev/Quadstick-Config-Manager/privacy.html";

    public static string LibraryDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "QuadStick Profiles");

    // Templates are just saved profile CSVs kept in a subfolder of the library.
    // The library card list reads LibraryDir non-recursively, so this subfolder
    // never leaks into "Your profiles". A template opens as a fresh local copy
    // (savePath null), so editing and installing never touches the template.
    public static string TemplatesDir => Path.Combine(LibraryDir, "Templates");

    // Home already lists the library folder, so a recent card there would be a
    // duplicate. Compared on the folder, not the path, so a file moved into the
    // library stops being "recent" without any bookkeeping.
    static bool InLibrary(string path) => string.Equals(
        Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(LibraryDir),
        StringComparison.OrdinalIgnoreCase);

    const int MaxRecents = 8;

    // Newest first, no duplicates. Called on every open and save that has a
    // real path behind it, which is what makes a file findable again later.
    void RememberRecent(string path)
    {
        var full = Path.GetFullPath(path);
        _settings.Recents.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        _settings.Recents.Insert(0, full);
        if (_settings.Recents.Count > MaxRecents)
            _settings.Recents.RemoveRange(MaxRecents, _settings.Recents.Count - MaxRecents);
        Settings.TrySave(_settings);
    }

    static readonly HashSet<string> JoystickDirs = new(StringComparer.OrdinalIgnoreCase)
    { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    static readonly List<string> OutputSuggestions = WithModeOverrides(Vocab.AllOutputs);
    static readonly List<string> OutputSuggestionsPs = WithModeOverrides(
        Vocab.OutputsPs3.Concat(Vocab.LegacyOutputs).ToHashSet(StringComparer.Ordinal));
    static readonly List<string> OutputSuggestionsXbox = WithModeOverrides(
        Vocab.OutputsXbox.Concat(Vocab.LegacyOutputs).ToHashSet(StringComparer.Ordinal));
    static readonly List<string> FunctionSuggestions = Vocab.FunctionArity.Keys.OrderBy(x => x).ToList();
    // A stick is a shape, not a word list. Alphabetical opened the joystick
    // with any_direction and then scattered the compass (E, E_inner, N, NE,
    // NE_inner, N_inner, NW...), so finding "down" meant reading all 22. This
    // is the order the hardware is described in: the four directions, then the
    // outer eight-way ring, then the inner one. A tester asked for exactly
    // this, in the one menu, without another level to click through.
    static readonly string[] JoystickOrder =
    {
        "up", "down", "left", "right", "center", "any_direction",
        "N", "NE", "E", "SE", "S", "SW", "W", "NW",
        "N_inner", "NE_inner", "E_inner", "SE_inner", "S_inner", "SW_inner", "W_inner", "NW_inner",
    };

    // Anything not listed sorts after the lot and keeps its alphabetical place,
    // so a token added to the firmware still appears instead of vanishing.
    static int JoystickRank(string input)
    {
        int i = Array.IndexOf(JoystickOrder, input);
        return i < 0 ? JoystickOrder.Length : i;
    }

    static readonly List<string> InputSuggestions =
        Vocab.AllInputs.OrderBy(GroupRank).ThenBy(JoystickRank).ThenBy(x => x, StringComparer.Ordinal).ToList();
    static readonly List<string> NoSuggestions = new();

    // A3 of each sheet names the output convention ("PlayStation Outputs" /
    // "XBox Outputs"). Suggest the matching set; union when the label is generic.
    static List<string> OutputSuggestionsFor(ModeSheet s)
    {
        var label = s.HeaderLabel;
        if (label.Contains("xbox", StringComparison.OrdinalIgnoreCase)) return OutputSuggestionsXbox;
        if (label.Contains("playstation", StringComparison.OrdinalIgnoreCase)) return OutputSuggestionsPs;
        return OutputSuggestions;
    }

    // The output picker for the open profile: its own names under "Custom"
    // first, then that sheet's tokens. Rebuilt per call because editing the
    // names table changes the list.
    OutputCatalog.ProfileOutputs OutputsFor(ModeSheet s) =>
        OutputCatalog.ForProfile(CustomNameRows(), OutputSuggestionsFor(s));

    // What a row's output field shows and commits. A named row reads by its
    // name; picking one writes the token and the name together.
    static string OutputFieldValue(Binding b) => b.ActionName.Length > 0 ? b.ActionName : b.Output;

    void CommitOutput(int row, OutputCatalog.ProfileOutputs outputs, string picked)
    {
        if (_file is null) return;
        var (token, name) = outputs.Resolve(picked);
        if (name.Length > 0 && token.Length == 0)
        {
            // A name with no button behind it yet takes the one this row is
            // already on, instead of emptying column A and leaving the row
            // doing nothing. Rows that carry the name are blank too, so they
            // move with it, or SetOutput refuses the pick as one name meaning
            // two outputs.
            token = _file.GetCell(row, 0);
            if (token.Length > 0) _file.RetargetAction(name, token);
        }
        _file.SetOutput(row, token, name);
        // The name now lives on the row and travels with the file, so the copy
        // waiting in settings is not needed.
        if (token.Length > 0 && _drafts.Remove(name)) PersistDrafts();
    }

    // List View builds each row's picker once, so a new name has to reach the
    // other rows' "Game" lists somehow: rebuild when the naming changed.
    void CommitOutputFromList(Binding b, OutputCatalog.ProfileOutputs outputs, string picked)
    {
        if (_file is null) return;
        var oldOutput = _file.GetCell(b.Row, 0);
        var oldName = _file.GetCell(b.Row, ProfileFile.ActionColumn);
        CommitOutput(b.Row, outputs, picked);
        // A different output can change every duplicate badge in this mode.
        // List View used to rebuild only when an action name changed, leaving
        // the count stale until the person left and came back to Rows.
        if (_file.GetCell(b.Row, 0) == oldOutput
            && _file.GetCell(b.Row, ProfileFile.ActionColumn) == oldName) return;
        var off = GridScroll.Offset;
        Dispatcher.UIThread.Post(() => { RebuildRows(); RestoreListScroll(off, () => { }); });
    }

    // The sheet's color language, familiar to every QuadStick user:
    // yellow = outputs, pink = function, blue = inputs. Keys into the theme's
    // <Key>Brush DynamicResource (Palette.cs / Theme.cs), not fixed colors.
    const string OutputTint = "OutputTint";
    const string FunctionTint = "FunctionTint";
    const string InputTint = "InputTint";

    // Group inputs the way users think of the hardware, not alphabetically.
    static int GroupRank(string input) => input switch
    {
        _ when input.StartsWith("mp_") => 0,                                         // mouthpiece
        "push" or "lip" or "lip_soft" => 1,                                         // mouthpiece switch
        _ when input.StartsWith("right_") => 2,                                      // side tube
        "left" or "right" or "up" or "down" or "any_direction" or "center" => 3,     // joystick
        _ when JoystickDirs.Contains(input) || JoystickDirs.Contains(input.Replace("_inner", "")) => 3,
        _ when input.StartsWith("usb_") => 5,
        _ when input.StartsWith("digital_") => 6,
        _ => 4,
    };

    public MainWindow()
    {
        FlowDirection = Localization.Direction;
        InitializeComponent();
        var v = typeof(MainWindow).Assembly.GetName().Version;
        HomeVersionText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Main_VVMajorVMinor, v?.Major, v?.Minor, v?.Build);

        if (_settings.RememberWindow)
        {
            if (_settings.WinW is { } winW && _settings.WinH is { } winH)
            {
                Width = Math.Max(winW, MinWidth);
                Height = Math.Max(winH, MinHeight);
            }
            if (_settings.WinX is { } winX && _settings.WinY is { } winY)
                Position = new PixelPoint((int)winX, (int)winY);
        }
        RootHost.PropertyChanged += (_, e) => { if (e.Property == Visual.BoundsProperty) UpdateScaleSize(); };
        RootPanel.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty) UpdateShellDensity();
        };
        // Sentence cards lay out one way with room and another without it, so
        // the panel has to say which it has. Only a crossing rebuilds, and a
        // rebuild cannot change this width, so this settles in one pass.
        ZoneDetailPanel.SizeChanged += (_, e) =>
        {
            bool narrow = NarrowCards(e.NewSize.Width);
            if (narrow == _narrowCards) return;
            _narrowCards = narrow;
            if (_settings.DeviceCards) BuildZoneDetail();
        };
        Opened += (_, _) => ClampToScreen();

        HomeNewButton.Click += async (_, _) => { if (await ConfirmLeaveAsync()) NewFromTemplate(); };
        HomeTemplateButton.Click += async (_, _) => await UseTemplateAsync();
        HomeOpenButton.Click += async (_, _) => await GuardedAsync(OpenAsync);
        HomeAgentButton.Click += (_, _) => ShowAgent();
        AgentButton.Click += (_, _) => ShowAgent(changing: true);
        HomeCommunityButton.Click += (_, _) => ShowCommunityPage();
        HomeDeviceFilesButton.Click += async (_, _) => await ShowDeviceFilesAsync();
        HomeHelpButton.Click += (_, _) => ShowHelp();
        ImportButton.Click += async (_, _) => await ImportAsync();
        HomeDriveButton.Click += (_, _) => OnDriveButtonClick();

        // Persistent shell commands mirror the page actions. Keeping these
        // delegates here means the shell is navigation, not a second set of
        // business logic that can drift from the accessible Home controls.
        ShellHomeButton.Click += async (_, _) => { if (await ConfirmLeaveAsync()) ShowHome(); };
        // The mark goes home too. Same guard, so unsaved work still asks first.
        ShellBrandButton.Click += async (_, _) => { if (await ConfirmLeaveAsync()) ShowHome(); };
        // Both leave the editor, so both ask about unsaved work first.
        ShellDeviceButton.Click += async (_, _)
            => { if (await ConfirmLeaveAsync()) await ShowDevicePageAsync(); };
        ShellCommunityButton.Click += async (_, _)
            => { if (await ConfirmLeaveAsync()) ShowCommunityPage(); };

        // Empty-library state offers the same three actions as the Start
        // cards above, so an empty library is never a dead end.
        LibraryEmptyNewButton.Click += (_, _) => NewFromTemplate();
        LibraryEmptyOpenButton.Click += async (_, _) => await GuardedAsync(OpenAsync);
        LibraryEmptyImportButton.Click += async (_, _) => await ImportAsync();

        Closing += async (_, e) =>
        {
            if (_file is not { Dirty: true } || _closeConfirmed) return;
            e.Cancel = true;
            if (await ConfirmLeaveAsync()) { _closeConfirmed = true; Close(); }
        };
        Closing += (_, _) =>
        {
            if (!_settings.RememberWindow) return;
            _settings.WinW = Width;
            _settings.WinH = Height;
            _settings.WinX = Position.X;
            _settings.WinY = Position.Y;
            Settings.Save(_settings);
        };
        FileNameBox.LostFocus += (_, _) => CommitFileName();
        FileNameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitFileName(); };
        SaveButton.Click += async (_, _) => await SaveAsync();
        UndoButton.Click += (_, _) => UndoEdit();
        SaveTemplateButton.Click += async (_, _) => await SaveAsTemplateAsync();
        InstallButton.Click += async (_, _) => await RunInstallFlowAsync();
        HelpButton.Click += (_, _) => ShowHelp();
        AddRowButton.Click += (_, _) => AddRow();
        SelectionModeButton.Click += (_, _) => ToggleSelectionMode();
        ModesButton.Click += async (_, _) => await ShowModesAsync();
        ShareButton.Flyout = ShareMenu(null); // null = the open editor's profile
        // A click that lands on nothing selectable drops the row selection,
        // exactly like a file explorer. Row-number presses mark themselves
        // Handled, so they never reach this.
        GridScroll.AddHandler(PointerPressedEvent, (_, _) => ClearSelection());
        WireDragScroll(GridScroll);
        WireDragScroll(DeviceStageScroll);
        SelectionDeleteButton.Click += (_, _) => DeleteSelectedRows();
        SelectionClearButton.Click += (_, _) => ClearSelection();
        SelectionMoveButton.Flyout = MoveMenu();

        // Device view mappings read as plain sentences by default; this flips
        // to the detailed editor for users who want every field on screen.
        // Rows view has the same pair the other way round, and its own setting.
        CardViewButton.Click += (_, _) =>
        {
            if (_deviceView) _settings.DeviceCards = !_settings.DeviceCards;
            else _settings.RowCards = !_settings.RowCards;
            Settings.Save(_settings);
            UpdateCardViewButton();
            if (_deviceView) BuildZoneDetail(); else RebuildRows();
        };
        UpdateCardViewButton();

        UnusedButton.Click += (_, _) => ShowUnusedInputs();

        AddModeButton.Click += (_, _) => AddModeAndOpen();

        // Plain-language explainers, shown as dismissable popups so the answer
        // is one click away and never clutters the editing surface.
        ModeHelpButton.Click += (_, _) => ShowInfoFlyout(ModeHelpButton, Strings.Main_WhatIsAMode,
            Strings.Main_AModeIsOneFull);
        DeviceHelpButton.Click += (_, _) => ShowInfoFlyout(DeviceHelpButton, Strings.Main_UsingDeviceView,
            Strings.Main_ClickAnyPartOfThe + ModelDescription);

        ProblemsToggle.Click += (_, _) => ToggleProblems();

        // Selecting a problem copies it, so users can paste it into a bug
        // report or a forum post without retyping. It also jumps focus to
        // the offending cell so the user can fix it right away.
        IssuesList.SelectionChanged += async (_, _) =>
        {
            // An item is a bare TextBlock, or a StackPanel wrapping the text
            // plus a quick-fix button (unknown-input errors).
            var tb = IssuesList.SelectedItem as TextBlock
                ?? (IssuesList.SelectedItem as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault();
            if (tb is { Text.Length: > 0 } && Clipboard is { } cb)
            {
                await cb.SetTextAsync(tb.Text);
                Status(Strings.Main_ProblemCopiedToTheClipboard, StatusKind.Info);
                if (tb.Tag is Issue issue) FocusIssueCell(issue);
                IssuesList.SelectedIndex = -1; // allow copying the same one again
            }
        };
        FixFirstButton.Click += (_, _) =>
        {
            var firstError = _file?.Issues.FirstOrDefault(i => i.Severity == Severity.Error);
            if (firstError is null) { Status(Strings.Main_NoErrorsToFix, StatusKind.Ready); return; }
            FocusIssueCell(firstError);
        };

        // Refit the picture to whatever height the panel has now. Cheap: it
        // sets one MaxHeight, it does not rebuild the diagram.
        DeviceStageScroll.SizeChanged += (_, _) => FitDeviceStage();

        DeviceViewButton.Click += (_, _) => SetDeviceView(true, rail: false);
        RailViewButton.Click += (_, _) => SetDeviceView(true, rail: true);
        ListViewButton.Click += (_, _) => SetDeviceView(false);
        LabelStyleButton.Click += (_, _) => ToggleLabelStyle();
        UpdateLabelStyleButton();
        _model = Enum.TryParse<QsModel>(_settings.Model, out var savedModel) ? savedModel : QsModel.FPS;
        ModelPicker.ItemsSource = ModelNames;
        ModelPicker.SelectedIndex = (int)_model;
        ModelPicker.SelectionChanged += (_, _) =>
        {
            if (_pickerSyncing) return;
            if (ModelPicker.SelectedIndex < 0) return;
            _model = (QsModel)ModelPicker.SelectedIndex;
            SaveModel();
            if (_deviceView) { _selectedZone = null; RefreshEditor(); }
        };

        var savedTheme = _settings.Theme;
        AppearancePicker.ItemsSource = QuadStick.App.Theme.Choices;
        AppearancePicker.SelectedIndex = savedTheme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        // ApplyTheme sets SelectedIndex back to the same value on the way out,
        // which does not re-fire SelectionChanged, so this can't loop.
        // By position, not by the word on screen: the word is translated and
        // the value saved to settings.json is not.
        AppearancePicker.SelectionChanged += (_, _) => ApplyTheme(QuadStick.App.Theme.ChoiceAt(AppearancePicker.SelectedIndex));

        // Settings can connect or disconnect Drive, and the Home button reads
        // that state. Without the refresh it keeps the old label until the user
        // navigates away from Home and back.
        SettingsButton.Click += (_, _) => ShowSettingsPage();

        // Ctrl (Windows/Linux) or Cmd (macOS) shortcuts, plus the bare F1 help
        // key. Ctrl-combos are safe to fire even while a field has focus
        // (that's how Ctrl+S already behaved, and how every other app
        // treats Ctrl+shortcuts); a *bare* letter key is not, since it would
        // steal a keystroke out of whatever the user is typing (e.g. an
        // un-modified "i" over RunInstallFlowAsync mid-edit). Only the modifier-free
        // case needs the typing guard.
        KeyDown += (_, e) =>
        {
            // The tutorial overlay owns the keyboard while it's up: its Next/Skip
            // (Enter/Esc) still work, but app shortcuts like Ctrl+O must not fire
            // behind it. Ctrl+O would swap in a real profile that teardown then
            // discards. Returning without Handled leaves Enter/Esc to the callout.
            if (_tourOverlay?.IsVisible == true) return;
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            {
                if (e.Key == Key.F1 && e.Source is not (TextBox or AutoCompleteBox))
                { ShowHelp(); e.Handled = true; }
                else if (e.Key == Key.Escape && _selectedRows.Count > 0)
                { ClearSelection(); e.Handled = true; }
                else if (e.Key == Key.Escape && SettingsPage.IsVisible)
                { LeaveSettingsPage(); e.Handled = true; }
                else if (e.Key == Key.Escape && _expandedMapping >= 0 && DeviceContainer.IsVisible)
                { _expandedMapping = -1; BuildZoneDetail(); e.Handled = true; }
                else if (e.Key == Key.Escape && _expandedMapping >= 0 && _settings.RowCards)
                { _expandedMapping = -1; RebuildRows(); e.Handled = true; }
                else if (e.Key == Key.Delete && _selectedRows.Count > 0
                         && e.Source is not (TextBox or AutoCompleteBox))
                { DeleteSelectedRows(); e.Handled = true; }
                return;
            }
            switch (e.Key)
            {
                // Open and New both discard the current profile, so they get
                // the same unsaved-changes gate as Home and window close.
                case Key.O: _ = GuardedAsync(OpenAsync); e.Handled = true; break;
                case Key.S: _ = SaveAsync(); e.Handled = true; break;
                case Key.N: _ = GuardedAsync(async () => { if (await ConfirmLeaveAsync()) NewFromTemplate(); }); e.Handled = true; break;
                case Key.Z: UndoEdit(); e.Handled = true; break;
                case Key.I: _ = RunInstallFlowAsync(); e.Handled = true; break;
                case Key.D: SetDeviceView(!_deviceView, _railView); e.Handled = true; break; // keep Parts List sub-mode
                case Key.H: ShowHelp(); e.Handled = true; break;
            }
        };

        _reduceMotion = _settings.ReduceMotion;
        ApplyInterfaceScale(_settings.InterfaceScalePercent);

        // The crash safety net needs to know what to rescue, always.
        CrashGuard.CurrentFile = () => _file;

        // Autosave: every 30 seconds, unsaved work is copied to a draft file.
        // A crash, a dead battery, or a force-quit can then cost at most 30
        // seconds of edits, never an afternoon.
        var autosave = new Avalonia.Threading.DispatcherTimer
        { Interval = TimeSpan.FromSeconds(30) };
        autosave.Tick += (_, _) => WriteDraft();
        autosave.Start();

        ShowHome();
        OfferRescueIfAny();
        if (!_settings.TutorialSeen) Opened += StartTutorialOnce;
        Opened += TelemetryOnceOnOpen;

        // Only on a close that is really happening. The unsaved-work handler
        // above cancels the close to ask, and every Closing handler still runs
        // when it does, so shutting down here unconditionally would leave a
        // window that stays open with telemetry dead for the rest of the
        // session. Shutdown pushes the queue first and waits at most two
        // seconds for it.
        Closing += (_, e) => { if (!e.Cancel) Telemetry.Shutdown(); };
    }

    // ---------- telemetry consent ----------

    // Both dialogs live behind one Opened handler so they cannot overlap, and
    // so the crash prompt is never shown to someone who has not yet been told
    // what this app sends.
    async void TelemetryOnceOnOpen(object? sender, EventArgs e)
    {
        Opened -= TelemetryOnceOnOpen;
        // Nothing can be sent, so there is nothing to ask about. This is also
        // what keeps a headless test run from opening a modal it cannot close.
        if (Telemetry.DisabledByEnvironment) return;
        try
        {
            if (_settings.TelemetryNoticeVersion < Telemetry.NoticeVersion)
                await ShowTelemetryNoticeAsync();
            else
                ApplyStoredConsent();

            await OfferPendingCrashReportAsync();
        }
        catch { /* a consent dialog that fails must not take the app down */ }
    }

    static bool _launchTracked;   // static: one launch per process, not per window

    /// <summary>Push the saved answer into Telemetry. The only place standing consent starts a client.</summary>
    void ApplyStoredConsent()
    {
        Telemetry.ApplyConsent(_settings.TelemetryNoticeVersion, _settings.UsageAnalytics);
        if (_settings.UsageAnalytics)
        {
            // Minted here rather than at the notice: someone who said no never
            // gets an identifier at all.
            Telemetry.SetInstallId(Telemetry.InstallId(_settings));

            // Once per process. ApplyStoredConsent also runs whenever the
            // Settings toggle is saved, so without this a user who flips it
            // off and on three times reports four launches and every
            // per-launch rate is wrong.
            if (!_launchTracked)
            {
                _launchTracked = true;
                Telemetry.Track(TelemetryEvent.AppLaunched);
            }
        }
        else Telemetry.SetInstallId("");
    }

    internal async Task ShowTelemetryNoticeAsync()
    {
        var yes = new Button { Content = Strings.Main_YesShareUsageData, MinWidth = 180 };
        var no = new Button { Content = Strings.Main_NoThanks, MinWidth = 140, IsDefault = true, IsCancel = true };
        AutomationProperties.SetName(yes, Strings.Main_YesShareAnonymousUsageData);
        AutomationProperties.SetName(no, Strings.Main_NoThanksDoNotShare);

        // Deliberately not "nothing is ever sent automatically", which stops
        // being true the moment the toggle is on. Say what each answer does.
        string body =
            Strings.Main_ThisAppCanSendAnonymous;

        // Someone deciding here has to be able to read the whole policy here,
        // not hunt for it after the fact. The short version above is the only
        // thing most people will read, so the link is what carries the rest:
        // retention, the processor, and how to have the data deleted.
        var policy = new Button
        {
            Content = Strings.Main_ReadThePrivacyPolicy,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(policy, Strings.Main_ReadThePrivacyPolicyOpens);
        policy.Click += async (_, _) =>
        {
            try { await Launcher.LaunchUriAsync(new Uri(PrivacyPolicyUrl)); }
            catch { /* best effort: the notice already says what matters */ }
        };

        var dialog = new Window
        {
            Title = Strings.Main_HelpImproveQuadStickConfigManager,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = ZoomWrap(new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 16,
                MaxWidth = 520,
                Children =
                {
                    new TextBlock
                    {
                        Text = Strings.Main_HelpImproveQuadStickConfigManager,
                        FontWeight = FontWeight.Bold, FontSize = Size("SubheadSize"), TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize"), LineHeight = 22 },
                    policy,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { yes, no } },
                },
            }, _uiScale),
        };

        var said = false;
        yes.Click += (_, _) => { said = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        await ShowDialogInShellAsync(dialog);

        ApplyTelemetryAnswer(said);
    }

    /// <summary>Fail closed: an answer that could not be written to disk is treated as no.</summary>
    /// <returns>True when the answer reached disk. False means nothing is sent now, but the file is unchanged.</returns>
    internal bool ApplyTelemetryAnswer(bool usage)
    {
        _settings.UsageAnalytics = usage;
        _settings.TelemetryNoticeVersion = Telemetry.NoticeVersion;

        if (!Settings.TrySave(_settings))
        {
            _settings.UsageAnalytics = false;
            _settings.TelemetryNoticeVersion = 0;
            Telemetry.ApplyConsent(0, usage: false);
            Telemetry.SetInstallId("");
            // "Stays off" was only true of this session. The file still holds
            // whatever it held, so a failed turn-off comes back on next launch
            // unless the caller shows that.
            Status(Strings.Main_CouldNotSaveThatPreference,
                   StatusKind.Warning);
            return false;
        }

        ApplyStoredConsent();
        return true;
    }

    // Asked once per launch, about the newest report only. Pressing Send is
    // the consent for that crash: there is no standing setting that sends one,
    // which is why nothing was sent at crash time in the first place.
    internal async Task OfferPendingCrashReportAsync()
    {
        if (!_settings.AskAboutCrashes) return;

        // Newest first, skipping anything that will not parse. A file that
        // cannot be read is a file that cannot be sent, and keeping it would
        // make it the newest forever and bury every good report behind it.
        // It is also not something to show a user, so it goes.
        string? details = null, newest = null;
        foreach (var path in CrashReport.Pending().Reverse())
        {
            string text;
            try { text = File.ReadAllText(path); }
            catch { continue; }   // locked right now: leave it for next time

            if (CrashReport.FromJson(text) is { Chain.Count: > 0 })
            {
                details = text;
                newest = path;
                break;
            }
            CrashReport.Discard(path);
        }
        if (details is null || newest is null) return;

        var send = new Button { Content = Strings.Main_SendReport, MinWidth = 140 };
        var later = new Button { Content = Strings.Main_NotNow, MinWidth = 140, IsDefault = true, IsCancel = true };
        var never = new Button { Content = Strings.Main_StopAsking, MinWidth = 140 };
        AutomationProperties.SetName(send, Strings.Main_SendThisCrashReport);
        AutomationProperties.SetName(later, Strings.Main_NotNowKeepTheReport);
        AutomationProperties.SetName(never, Strings.Main_StopAskingAboutCrashReports);

        var box = new TextBox
        {
            Text = details,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("monospace"),
            FontSize = Size("SmallSize"),
            Height = 220,
        };
        AutomationProperties.SetName(box, Strings.Main_CrashDetailsUsedToBuild);

        // Not "these are the exact bytes". Sending converts this into
        // PostHog's error format and the SDK adds more than the install ID:
        // its own name and version go in the payload, and the request's
        // User-Agent carries the .NET version, the full OS description
        // including the kernel build, and whether the CPU is Intel or ARM.
        // Verified against 2.12.0 by capturing a real send. "Nothing else is
        // added" was here before and was simply false.
        string note =
            Strings.Main_TheAppCrashedLastTime;

        var dialog = new Window
        {
            Title = Strings.Main_SendACrashReport,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = ZoomWrap(new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 16,
                MaxWidth = 620,
                Children =
                {
                    new TextBlock
                    {
                        Text = Strings.Main_SendACrashReport,
                        FontWeight = FontWeight.Bold, FontSize = Size("SubheadSize"), TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize"), LineHeight = 22 },
                    box,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { send, later, never } },
                },
            }, _uiScale),
        };

        var choice = CrashChoice.Later;
        send.Click += (_, _) => { choice = CrashChoice.Send; dialog.Close(); };
        later.Click += (_, _) => dialog.Close();
        never.Click += (_, _) => { choice = CrashChoice.Never; dialog.Close(); };
        await ShowDialogInShellAsync(dialog);

        switch (choice)
        {
            case CrashChoice.Send:
                // Minted only now for someone who never turned usage data on:
                // pressing Send is the first act that needs an identity.
                if (Telemetry.InstallId(_settings) is { Length: > 0 } id)
                {
                    Telemetry.SetInstallId(id);
                    // Only the one file the user was shown and agreed to. The
                    // older reports were never on screen, so consent to this
                    // one is not consent to delete those: they stay, and the
                    // next launch offers the next one down.
                    if (await Telemetry.SendCrashReportAsync(details))
                    {
                        CrashReport.Discard(newest);
                        Status(Strings.Main_CrashReportSentThankYou, StatusKind.Info);
                    }
                    else Status(Strings.Main_CouldNotSendTheCrash, StatusKind.Warning);
                }
                else Status(Strings.Main_CouldNotSendTheCrash, StatusKind.Warning);
                break;

            case CrashChoice.Never:
                _settings.AskAboutCrashes = false;
                CrashReport.Discard();
                if (!Settings.TrySave(_settings))
                    Status(Strings.Main_CouldNotSaveThatYou, StatusKind.Warning);
                break;

            case CrashChoice.Later:
            default:
                break;   // the files stay, and the next launch asks again
        }
    }

    enum CrashChoice { Send, Later, Never }

    // ---------- autosave drafts and crash recovery ----------

    static string DraftPath => Path.Combine(CrashGuard.RescueDir, "autosave-draft.csv");
    int _draftedRevision = -1; // last _file.Revision written to the draft; -1 = none

    void WriteDraft()
    {
        try
        {
            if (_file is { Dirty: true })
            {
                // Nothing edited since the last draft: don't re-serialize the whole
                // grid and rewrite the file every 30s for no reason.
                if (_file.Revision == _draftedRevision) return;
                Directory.CreateDirectory(CrashGuard.RescueDir);
                ProfileFile.WriteAtomic(DraftPath, _file.ToCsvText());
                _draftedRevision = _file.Revision;
            }
            else if (_file is not null && File.Exists(DraftPath))
            {
                // A file is open and clean (just saved): its draft is stale, drop it.
                // When NO file is open we must NOT delete. On startup after a crash
                // that draft is the unopened recovery still offered on the Home screen,
                // and the 30s timer would otherwise erase it out from under the user.
                File.Delete(DraftPath);
                _draftedRevision = -1;
            }
        }
        catch { /* autosave must never interrupt the user */ }
    }

    void OfferRescueIfAny()
    {
        var rescues = CrashGuard.PendingRescues();
        if (rescues.Count == 0) return;
        var newest = rescues[0];
        HomeStatusText.Text =
            string.Format(CultureInfo.CurrentCulture, Strings.Main_UnsavedWorkFromLastTime, Path.GetFileNameWithoutExtension(newest));
        HomeStatusText.IsVisible = true;
        RescuePanel.IsVisible = true;
        RescueOpenButton.Click += (_, _) =>
        {
            try
            {
                OpenInEditor(ProfileFile.Load(File.ReadAllText(newest)), savePath: null, ProfileSource.Rescue);
                if (_file is not null) _file.Dirty = true; // unsaved recovery: leaving must warn, not silently drop it
                CrashGuard.DiscardRescues(); // now in the editor: the rescue files on disk are spent, don't re-offer them forever
                Status(Strings.Main_RecoveredProfileOpenedSaveIt, StatusKind.Warning);
                RescuePanel.IsVisible = false;
                HomeStatusText.IsVisible = false; // the offer is spent; coming back Home must not still announce it
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { HomeStatusText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Main_CouldNotOpenTheRecovered, ex.Message); }
        };
        RescueDismissButton.Click += (_, _) =>
        {
            CrashGuard.DiscardRescues();
            RescuePanel.IsVisible = false;
            HomeStatusText.IsVisible = false;
        };
    }

    internal static readonly int[] ValidScalePercents = { 60, 70, 80, 90, 100, 125, 150, 200 };

    public void ApplyInterfaceScale(int pct)
    {
        if (Array.IndexOf(ValidScalePercents, pct) < 0) pct = 100;
        _uiScale = pct / 100.0;
        ZoomHost.LayoutTransform = _uiScale == 1.0 ? null : new ScaleTransform(_uiScale, _uiScale);
        ApplyScaledMinimums();
        EnsureWindowFitsScale();
        UpdateScaleSize();
    }

    // Min size grows with scale so toolbar rows do not clip. Capped to screen.
    void ApplyScaledMinimums()
    {
        double capW = double.PositiveInfinity, capH = double.PositiveInfinity;
        if ((Screens?.ScreenFromWindow(this) ?? Screens?.Primary) is { } screen)
        {
            var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
            capW = screen.WorkingArea.Width / scaling;
            capH = screen.WorkingArea.Height / scaling;
        }
        // 760 is what the editor itself needs; the panel down its left side is
        // permanent chrome, so the window has to be that much wider or every
        // layout inside gets the sidebar width less than it was measured for.
        // Capped to the screen either way.
        MinWidth = Math.Min((760 + Size("SidebarWidth")) * _uiScale, capW);
        MinHeight = Math.Min(560 * _uiScale, capH);
    }

    // Bigger scale needs a bigger window or the fixed-height Problems dock
    // crowds the editor. Grow (never shrink) toward a comfortable size, capped
    // to the screen. Skipped until the window is on screen, so the saved-size
    // restore in the constructor wins at startup.
    void EnsureWindowFitsScale()
    {
        if (!IsVisible || WindowState != WindowState.Normal) return;
        var screen = Screens?.ScreenFromWindow(this) ?? Screens?.Primary;
        if (screen is null) return;
        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var wantW = Math.Min(1000 * _uiScale, screen.WorkingArea.Width / scaling);
        var wantH = Math.Min(720 * _uiScale, screen.WorkingArea.Height / scaling);
        if (Width < wantW) Width = wantW;
        if (Height < wantH) Height = wantH;
    }

    // A remembered position can land off-screen after a monitor is unplugged.
    // For a mouth-operated app a lost window is very hard to recover, so pull
    // it back onto a real screen once we know the actual monitor layout.
    void ClampToScreen()
    {
        if (Screens is not { } screens) return;
        if (screens.All.Any(s => s.WorkingArea.Contains(Position))) return;
        var wa = (screens.ScreenFromWindow(this) ?? screens.Primary)?.WorkingArea;
        if (wa is { } r) Position = new PixelPoint(r.X + 40, r.Y + 40);
    }

    void UpdateScaleSize()
    {
        if (RootHost.Bounds is { Width: > 0, Height: > 0 } b)
        {
            RootPanel.Width = b.Width / _uiScale;
            RootPanel.Height = b.Height / _uiScale;
        }
    }

    void UpdateShellDensity()
    {
        var compact = RootPanel.Bounds.Width is > 0 and < 930;
        ShellBrandCaption.IsVisible = !compact;
    }

    // ---- Settings page API: SettingsView.cs calls these so every
    // setting applies live and persists through the same single source of
    // truth (_settings) the rest of the window already uses. ----
    public AppSettings CurrentSettings => _settings;
    public void PersistSettings() => Settings.Save(_settings);
    public static IReadOnlyList<string> ModelDisplayNames => ModelNames;
    public double UiScale => _uiScale;

    // Wrap a window's content so it scales with the app's interface-size setting.
    public static Control ZoomWrap(Control content, double scale) =>
        scale == 1.0 ? content
        : new LayoutTransformControl { LayoutTransform = new ScaleTransform(scale, scale), Child = content };

    // Every secondary workflow gets the same branded page frame. Native chrome
    // still provides reliable platform move/resize controls; this inner frame
    // supplies the application identity and hierarchy that Fluent's generic
    // blank window cannot.
    public static Control DialogShell(Window window, Control content)
    {
        window.FlowDirection = Localization.Direction;
        var windowTitle = string.IsNullOrWhiteSpace(window.Title) ? "window" : window.Title;

        // No close button of its own. The window already has the operating
        // system's, and two of them a few pixels apart is how a port looks:
        // on macOS the red dot sits top left and this one sat top right, so
        // every window in the app asked to be closed twice. Escape and the
        // window's own Done/Cancel are the other two ways out and both stay.
        var shell = new Border { Classes = { "dialogshell" } };

        // Focus has to land inside the window or Escape never reaches it, and
        // it must not land on something that means cancel: a prompt that opens
        // on Cancel answers Cancel to Enter, so "Save your changes?" threw the
        // work away and the Home click that raised it looked like it did
        // nothing. The shell itself is the last resort, for a window with
        // nothing in it to focus at all.
        window.Opened += (_, _) =>
        {
            var inside = content.GetSelfAndVisualDescendants().OfType<Control>()
                .Where(c => c.Focusable && c.IsEffectivelyVisible && c.IsEnabled)
                .ToList();
            var first = inside.FirstOrDefault(c => c is TextBox)
                     ?? inside.FirstOrDefault(c => c is Button { IsDefault: true })
                     ?? inside.FirstOrDefault();
            (first ?? shell).Focus();
        };

        var title = new TextBlock
        {
            Text = windowTitle,
            Classes = { "dialogtitle" },
            VerticalAlignment = VerticalAlignment.Center,
        };
        var statusDot = new Border
        { Width = 8, Height = 8, CornerRadius = new CornerRadius(2) };
        BindBrush(statusDot, Border.BackgroundProperty, "Accent");
        var header = new Border
        {
            Classes = { "dialogheader" },
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children = { statusDot, title },
            },
        };
        DockPanel.SetDock(header, Dock.Top);
        shell.Focusable = true;
        shell.Child = new DockPanel { Children = { header, content } };
        return shell;
    }

    async Task ShowDialogInShellAsync(Window dialog)
    {
        dialog.Classes.Add("dialog");
        if (dialog.Content is Control content
            && !(content is Border border && border.Classes.Contains("dialogshell")))
        {
            // Let go of the old content before the frame adopts it. Handing a
            // control to a new parent while the window still owns it throws,
            // and every prompt in the app comes through here: with unsaved
            // work open, Home and the rest died on this line and did nothing.
            dialog.Content = null;
            dialog.Content = DialogShell(dialog, content);
        }
        await dialog.ShowDialog(this);
    }

    // ---- Modes window API: ModesWindow.cs owns adding, renaming, reordering
    // and deleting modes, and edits the same open profile the editor shows. ----
    public ProfileFile? OpenFile => _file;

    // Called after any change made in the Modes window. The editor may be
    // showing a mode that just moved or vanished, so it is rebuilt around
    // whichever sheet should be selected now.
    public void ModesChanged(int selectSheetIndex, string status)
    {
        if (_file is null) return;
        // A profile can have no modes at all: the advanced grid can type over
        // the keyword that opens the last one. Clamp would throw on an empty
        // range, and index 0 lands on the custom names table, which is the only
        // thing left to show.
        _sheetIndex = _file.Document.Sheets.Count == 0
            ? 0
            : Math.Clamp(selectSheetIndex, 0, _file.Document.Sheets.Count - 1);
        _selectedZone = null;
        RefreshEditor(); // rebuilds the modes list from the file it just changed
        if (status.Length > 0) Status(status, StatusKind.Ready);
    }

    bool _pickerSyncing; // stops the header/settings pickers re-triggering each other

    public void ApplyTheme(string choice)
    {
        if (_pickerSyncing) return;
        _pickerSyncing = true;
        try
        {
            QuadStick.App.Theme.Apply(choice);
            _settings.Theme = choice;
            Settings.Save(_settings);
            AppearancePicker.SelectedIndex = choice switch { "Light" => 1, "Dark" => 2, _ => 0 };
        }
        finally { _pickerSyncing = false; }
    }

    // Applied by rebuilding: a window bakes its text while it is being built,
    // so the honest way to change its language is to build it again. The open
    // profile is handed to the new window as the same object, edits and dirty
    // flag intact; nothing is saved, discarded or asked about on the way.
    public MainWindow SetLanguage(string tag)
    {
        bool onSettings = SettingsPage.IsVisible;
        if (_settings.Language == tag) return this;
        _settings.Language = tag;
        Settings.Save(_settings);
        Localization.Apply(tag);
        Localization.Relocalize();
        var next = new MainWindow
        {
            Position = Position,
            Width = Width,
            Height = Height,
        };
        if (_file is not null)
        {
            next.OpenInEditor(_file, _savePath, ProfileSource.File, track: false);
            next.SelectSheet(_sheetIndex); // same mode open as before
        }
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = next;
        next.Show();
        if (onSettings) next.ShowSettingsPage();
        _closeConfirmed = true; // the profile moved, it was not discarded
        Close();
        return next;
    }

    public void SetInterfaceScale(int pct)
    {
        _settings.InterfaceScalePercent = pct;
        Settings.Save(_settings);
        ApplyInterfaceScale(pct);
    }

    public void SetReduceMotion(bool v)
    {
        _reduceMotion = v;
        _settings.ReduceMotion = v;
        Settings.Save(_settings);
        RefreshTourMotion();
    }

    /// <summary>How deep the output and input pickers file their choices.
    /// Detailed drills a category and then a group inside it; Wide stops at
    /// the category; Flat is one searchable list.</summary>
    public static readonly string[] PickerGroupings = { "Detailed", "Wide", "Flat" };

    public void SetPickerGrouping(string choice)
    {
        if (!PickerGroupings.Contains(choice) || _settings.PickerGrouping == choice) return;
        _settings.PickerGrouping = choice;
        Settings.Save(_settings);
        // The pickers are built per row, so the open editor has to be rebuilt
        // before the setting is anything the user can see.
        RefreshEditor();
    }

    /// <summary>Which order the compact mapping cards use for their plain
    /// English sentence. This is a presentation preference only: the profile
    /// file and the detailed editor are unchanged.</summary>
    public static readonly string[] CardSentenceStyles = { "PressWhen", "InputToOutput" };

    public void SetCardSentenceStyle(string choice)
    {
        if (!CardSentenceStyles.Contains(choice) || _settings.CardSentenceStyle == choice) return;
        _settings.CardSentenceStyle = choice;
        Settings.Save(_settings);
        if (_deviceView && CurrentSheet?.Type == SheetType.ProfileName)
        { BuildDeviceView(); BuildZoneDetail(); }
    }

    public void SetDefaultModel(int index)
    {
        if (_pickerSyncing || index < 0 || index >= ModelNames.Length) return;
        _pickerSyncing = true;
        try
        {
            _model = (QsModel)index;
            ModelPicker.SelectedIndex = index;
            SaveModel();
            if (_deviceView) { _selectedZone = null; RefreshEditor(); }
        }
        finally { _pickerSyncing = false; }
    }

    public void ResetSettings()
    {
        // Silence first, persist second. If the write fails the old settings
        // come back next launch, but this session is already quiet, and the
        // user is told rather than left believing the reset took.
        Telemetry.ApplyConsent(0, usage: false);
        Telemetry.SetInstallId("");
        CrashReport.Discard();

        _settings = new AppSettings();
        if (!Settings.TrySave(_settings))
            Status(Strings.Main_CouldNotSaveTheReset, StatusKind.Warning);
        QuadStick.App.Theme.Apply(_settings.Theme);
        AppearancePicker.SelectedIndex = 0;
        _reduceMotion = _settings.ReduceMotion;
        RefreshTourMotion();
        _model = QsModel.FPS;
        ModelPicker.SelectedIndex = 0;
        ApplyInterfaceScale(_settings.InterfaceScalePercent);
    }

    // Small public wrapper: ConfirmAsync itself is private, and the Settings
    // window's Reset button needs the same confirm-dialog idiom.
    public Task<bool> ConfirmResetAsync() => ConfirmAsync(Strings.Main_ResetAllSettings,
        Strings.Main_AppearanceInterfaceSizeAndThe);

    // One page at a time, and the tab that named it is the one marked. Marking
    // is a class and not a colour: "active" also changes the weight and the
    // underline, so the current page is readable without seeing hue.
    void ShowPage(Control page, Button? tab)
    {
        if (SettingsPage.IsVisible && !ReferenceEquals(page, SettingsPage))
            SettingsView.OnLeaving();
        foreach (var p in new Control[] { HomeView, EditorView, DevicePage, CommunityPage, SettingsPage })
            p.IsVisible = ReferenceEquals(p, page);
        foreach (var t in new[] { ShellHomeButton, ShellDeviceButton, ShellCommunityButton })
            if (ReferenceEquals(t, tab)) t.Classes.Add("active"); else t.Classes.Remove("active");
    }

    SettingsView? _settingsView;
    Control? _settingsReturnPage;

    internal SettingsView SettingsView =>
        _settingsView ??= new SettingsView(this);

    /// <summary>Test seam: show the settings page inside the shell.</summary>
    internal void ShowSettingsPageForPreview() => ShowSettingsPage();

    public void ShowSettingsPage()
    {
        _settingsReturnPage = CurrentVisiblePage();
        if (SettingsPageBody.Children.Count == 0)
            SettingsPageBody.Children.Add(SettingsView);
        ShowPage(SettingsPage, null);
        SettingsView.FocusBack();
        UpdateLayout();
    }

    public void LeaveSettingsPage() => ReturnFromSettingsPage();

    void ReturnFromSettingsPage()
    {
        var page = _settingsReturnPage ?? HomeView;
        _settingsReturnPage = null;
        if (ReferenceEquals(page, HomeView)) ShowHome();
        else if (ReferenceEquals(page, EditorView)) ShowPage(EditorView, null);
        else if (ReferenceEquals(page, DevicePage)) ShowPage(DevicePage, ShellDeviceButton);
        else if (ReferenceEquals(page, CommunityPage)) ShowCommunityPage();
        else ShowHome();
        if (HomeView.IsVisible) RefreshHomeCards();
    }

    Control CurrentVisiblePage()
    {
        if (HomeView.IsVisible) return HomeView;
        if (EditorView.IsVisible) return EditorView;
        if (DevicePage.IsVisible) return DevicePage;
        if (CommunityPage.IsVisible) return CommunityPage;
        return HomeView;
    }

    void ShowHome()
    {
        _file = null; // Home has no profile open; a leftover dirty file would re-prompt "leave?" on the next action
        // The banner describes something that just happened. Arriving on Home
        // again is not that moment, and a screen reader reads it out as if it
        // were. The startup rescue offer is written after this runs.
        HomeStatusText.IsVisible = false;
        ShowPage(HomeView, ShellHomeButton);
        Title = Strings.Main_QuadstickConfigManagerUnofficial; // no profile is open on Home
        RefreshHomeCards();
        HomeNewButton.Focus();
    }

    void ShowEditor()
    {
        ShowPage(EditorView, null);
        // The mode the profile opens on, not the name box: that would put a
        // caret on the filename.
        (_modeRows.GetValueOrDefault(_sheetIndex) as Control ?? EditorSidebar).Focus();

    }

    // Switches between Device View and List View, keeping keyboard focus on
    // the new view's first interactive control instead of dropping it.
    void SetDeviceView(bool device, bool rail = false)
    {
        _deviceView = device;
        _railView = device && rail;
        // A selection made in the other view would be invisible here, and an
        // invisible selection must never feed the Delete button.
        _selectedRows.Clear(); _selAnchor = -1;
        RefreshEditor();
        if (device && _railView)
        {
            // Land on the selected row, or the first part, so arrow keys work at once.
            (_zoneButtons.GetValueOrDefault(_selectedZone ?? "") ?? _zoneButtons.Values.FirstOrDefault())?.Focus();
        }
        else if (device)
        {
            if (_zoneButtons.TryGetValue("joystick", out var joystickBtn)) joystickBtn.Focus();
        }
        else if (CurrentSheet?.Bindings.FirstOrDefault()?.Row is int firstRow
                 && _cellBorders.TryGetValue($"A{firstRow}", out var border))
        { border.BringIntoView(); (border.Child as Control)?.Focus(); }
        else AddRowButton.Focus();
    }

    // Cycle the editor between plain-English words, Xbox-style button names,
    // and the raw CSV token names, and rebuild so every dropdown label follows
    // suit. Rows View honours the same switch: raw names get the file's own
    // spelling and no button art, the other two get the words and the art.
    void ToggleLabelStyle()
    {
        _labelStyle = (_labelStyle + 1) % 3;
        UpdateLabelStyleButton();
        if (CurrentSheet?.Type != SheetType.ProfileName) return;
        if (_deviceView) { BuildDeviceView(); BuildZoneDetail(); }
        else RebuildRows();
    }

    void UpdateLabelStyleButton()
    {
        LabelStyleButton.Content = _labelStyle switch
        {
            0 => Strings.Main_WordsListNames,
            1 => Strings.Main_WordsPlainEnglish,
            _ => Strings.Main_WordsXboxStyle,
        };
        AutomationProperties.SetName(LabelStyleButton, _labelStyle switch
        {
            0 => Strings.Main_WordsAreShownAsRaw,
            1 => Strings.Main_WordsAreShownInPlain,
            _ => Strings.Main_WordsAreShownAsXbox,
        });
    }

    // Test seam, same shape as the one in DeviceFilesWindow. A test cannot mount
    // two QuadSticks, and grouping is only visible when it can.
    internal Func<IReadOnlyList<string>> FindDeviceRoots { get; set; } = () => Device.FindCandidatesCached();

    void RefreshHomeCards()
    {
        // The Drive button is a live status light, refreshed on every home load.
        // The only Drive work here: no files.list runs. Home stays a local view;
        // the sheet list is fetched only when the picker opens.
        RefreshDriveButton();

        LibraryCards.Children.Clear();
        var libraryFiles = Directory.Exists(LibraryDir)
            ? Directory.GetFiles(LibraryDir, "*.csv").OrderBy(Path.GetFileName).ToArray()
            : Array.Empty<string>();
        LibraryEmptyPanel.IsVisible = libraryFiles.Length == 0;
        foreach (var path in libraryFiles)
            LibraryCards.Children.Add(ProfileCard(path, onDevice: false));

        // A recent that has since been moved into the library, deleted, or
        // saved to a temp folder the system wiped just drops off the list.
        RecentCards.Children.Clear();
        var recents = _settings.Recents
            .Where(p => !InLibrary(p) && File.Exists(p) && !IsAgentScratchFile(p))
            .ToList();
        RecentSection.IsVisible = recents.Count > 0;
        foreach (var path in recents)
            RecentCards.Children.Add(ProfileCard(path, onDevice: false,
                note: string.Format(CultureInfo.CurrentCulture, Strings.Main_InFolder,
                    Path.GetFileName(Path.GetDirectoryName(path)))));

        DeviceCards.Children.Clear();
        var drives = FindDeviceRoots()
            .Select(root => (Root: root, Files: ProfileFilesOn(root)))
            .Where(d => d.Files.Length > 0)
            .OrderBy(d => d.Root, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        DeviceEmptyText.IsVisible = drives.Length == 0;

        foreach (var (root, files) in drives)
        {
            // One QuadStick is the normal case and a heading over the only group
            // is just noise. With two plugged in the files interleave by name, so
            // without a heading there is nothing to say whose profiles are whose.
            if (drives.Length > 1) DeviceCards.Children.Add(DriveHeading(root));

            // The number the profile switch counts to reach this file, from the
            // same order the selection guide draws. prefs.csv is not selectable
            // and gets no number.
            var order = Device.SelectionOrder(files.Select(Path.GetFileName)!).ToList();
            var cards = new WrapPanel();
            // Laid out in that same order, not by file name. Numbers that jump
            // about the screen read as a bug, and default.csv is always number
            // one however late its name sorts. prefs.csv has no number, so it
            // goes last rather than in the middle of the count.
            foreach (var (path, position) in files
                .Select(p => (Path: p, Position: order.IndexOf(Path.GetFileName(p)) + 1))
                .OrderBy(x => x.Position == 0 ? int.MaxValue : x.Position))
                cards.Children.Add(ProfileCard(path, onDevice: true, position: position));
            DeviceCards.Children.Add(cards);
        }
    }

    // The agent writes its working copies as qcm-agent-<guid>.csv in the temp
    // folder. They are scratch, not something a person opened, and one turning
    // up under "Opened from your computer" is noise at best and a dead link
    // once the system clears temp. This hides the artefact; the agent code
    // itself is untouched and still builds (see AgentFeature).
    static bool IsAgentScratchFile(string path) =>
        Path.GetFileName(path).StartsWith("qcm-agent-", StringComparison.OrdinalIgnoreCase);

    // A yanked USB stick between FindCandidates and GetFiles is routine for this
    // hardware; it must never crash the home screen. That drive drops off the
    // list and the others still show.
    static string[] ProfileFilesOn(string root)
    {
        try
        {
            return Directory.GetFiles(root, "*.csv")
                .Where(p => Device.IsProfileFileName(Path.GetFileName(p)))
                .OrderBy(Path.GetFileName)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }
    }

    // The drive's own name, with the path beside it, matching how the Manage
    // files window names a drive. The path is what tells two identically
    // labelled QuadSticks apart.
    static TextBlock DriveHeading(string root) => new()
    {
        Text = string.Format(CultureInfo.CurrentCulture, Strings.Main_DeviceFilesWindowLabelForRootRoot, DeviceFilesWindow.LabelFor(root), root),
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 4, 0, 2),
    };

    // ShowHome re-reads and re-parses every library + device file on each visit
    // just to show "N sheets, M bindings". Cache it by path + last-write time so
    // an unchanged file is parsed once, not on every navigation back to Home.
    // A slow USB still costs one read the first time it's seen; that's inherent.
    /// <summary>Rebuild or empty every static that baked text in the old
    /// language, so the next window built reads entirely in the new one.</summary>
    internal static void RelocalizeStatics()
    {
        AllZones = BuildZones();
        _inputCatalog = null; // lazy; next read rebuilds with the new zone titles
        _cardCache.Clear();
        _factsCache.Clear();
    }

    static readonly Dictionary<string, (long Stamp, string Sub)> _cardCache = new();

    // File names on the QuadStick decide the order the profile switch steps
    // through them, so people keep them short and numbered and then cannot tell
    // one from another. The name inside the file is free of that job, so every
    // list that names a file leads with it. Skipped when it only repeats the
    // file name, and never in place of the file name: that is what gets typed,
    // renamed and installed.
    internal static string TitleNote(ProfileDocument doc, string path)
    {
        var title = doc.Title;
        return title.Length > 0
            && !title.Equals(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)
            ? $"{title} · "
            : "";
    }

    // What a profile card says about itself beyond its name. Everything here is
    // read out of the file we already parsed: no lookup, no network, no guess.
    internal readonly record struct CardFacts(string Meta, string Modes, string Edited);

    static readonly Dictionary<string, (long Stamp, CardFacts Facts)> _factsCache = new(StringComparer.Ordinal);

    static CardFacts FactsFor(string path)
    {
        long stamp;
        try { stamp = File.GetLastWriteTimeUtc(path).Ticks; }
        catch { stamp = 0; }
        if (_factsCache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Facts;

        CardFacts facts;
        try
        {
            var doc = Parser.Parse(File.ReadAllText(path)).Doc;
            var modes = doc.Sheets.Where(s => s.Type == SheetType.ProfileName).ToList();
            var bindings = modes.Sum(s => s.Bindings.Count);
            var title = doc.Title;
            var meta = (title.Length > 0 && !title.Equals(Path.GetFileNameWithoutExtension(path),
                            StringComparison.OrdinalIgnoreCase) ? $"{title} · " : "")
                + $"{Plural.Of(modes.Count, "Count_Mode")} · {Plural.Of(bindings, "Count_Binding")}";

            // The mode names are the one thing that says what a profile is for.
            // "Movement, Building, Driving" tells you it is a game setup; the
            // binding count never did. With a single mode its name is already
            // the profile's title, and printing it twice is how the card looked
            // padded rather than informative.
            var names = modes.Select(m => m.ModeName.Trim())
                             .Where(n => n.Length > 0)
                             .ToList();
            var shown = "";
            if (names.Count > 1)
            {
                shown = string.Join(", ", names.Take(3));
                if (names.Count > 3) shown += string.Format(CultureInfo.CurrentCulture, Strings.Main_PlusMore, names.Count - 3);
            }
            facts = new CardFacts(meta, shown, EditedNote(path));
        }
        catch
        {
            facts = new CardFacts(Strings.Main_CouldNotReadThisFile, "", "");
        }
        _factsCache[path] = (stamp, facts);
        return facts;
    }

    // Rough on purpose. An exact timestamp on a card is noise; "yesterday" is
    // what people actually sort by in their head.
    static string EditedNote(string path)
    {
        try
        {
            var age = DateTime.Now - File.GetLastWriteTime(path);
            if (age < TimeSpan.FromMinutes(2)) return Strings.Main_JustNow;
            if (age < TimeSpan.FromHours(1)) return string.Format(CultureInfo.CurrentCulture, Strings.Main_IntAgeTotalMinutesMinAgo, (int)age.TotalMinutes);
            if (age < TimeSpan.FromHours(24)) return string.Format(CultureInfo.CurrentCulture, Strings.Main_IntAgeTotalHoursHAgo, (int)age.TotalHours);
            if (age < TimeSpan.FromDays(2)) return "yesterday";
            if (age < TimeSpan.FromDays(30)) return string.Format(CultureInfo.CurrentCulture, Strings.Main_IntAgeTotalDaysDaysAgo, (int)age.TotalDays);
            return File.GetLastWriteTime(path).ToString(Strings.Main_DMMMYyyy);
        }
        catch { return ""; }
    }

    // Twelve grounds dark enough to carry white text, so the initials always
    // clear 4.5:1 whatever the profile is called. CardTileTests pins that.
    // Eight collided too often to be worth having: "gta" and "rocket-league",
    // the two profiles in the sample library, landed on the same red.
    internal static readonly Color[] TileColors =
    {
        Color.FromRgb(0x1F, 0x4E, 0x79), Color.FromRgb(0x6B, 0x2D, 0x5C),
        Color.FromRgb(0x1B, 0x5E, 0x4A), Color.FromRgb(0x8A, 0x3A, 0x1E),
        Color.FromRgb(0x3B, 0x35, 0x77), Color.FromRgb(0x7A, 0x2E, 0x2E),
        Color.FromRgb(0x24, 0x55, 0x63), Color.FromRgb(0x5A, 0x44, 0x14),
        Color.FromRgb(0x4A, 0x2E, 0x6B), Color.FromRgb(0x0F, 0x51, 0x32),
        Color.FromRgb(0x8A, 0x2B, 0x4A), Color.FromRgb(0x34, 0x49, 0x5E),
    };

    // Same name, same colour, on every machine and every run: a card people
    // recognise by its shape has to keep that shape. String.GetHashCode is
    // randomised per process, so this does its own.
    internal static Color TileColorFor(string name)
    {
        uint h = 2166136261;
        foreach (var c in name.ToLowerInvariant()) { h ^= c; h *= 16777619; }
        return TileColors[h % (uint)TileColors.Length];
    }

    // "rocket-league" reads as RL, "gta" as GT. Two characters is what fits and
    // what people actually recognise at card size.
    internal static string InitialsFor(string name)
    {
        var words = name.Split(new[] { ' ', '-', '_', '.', '+' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(w => char.IsLetterOrDigit(w[0])).ToList();
        if (words.Count == 0) return "?";
        if (words.Count == 1)
            return words[0].Length >= 2
                ? words[0][..2].ToUpperInvariant()
                : words[0][..1].ToUpperInvariant();
        return $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}";
    }

    static string CardSubtitle(string path)
    {
        long stamp;
        try { stamp = File.GetLastWriteTimeUtc(path).Ticks; }
        catch { stamp = 0; }
        if (_cardCache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Sub;
        string sub;
        try
        {
            var doc = Parser.Parse(File.ReadAllText(path)).Doc;
            // Modes, not sheets. A preferences or infrared sheet is neither a
            // mode nor a set of bindings, and counting them here said a profile
            // had one more mode than the device would ever run.
            var modes = doc.Sheets.Where(s => s.Type == SheetType.ProfileName).ToList();
            sub = TitleNote(doc, path)
                + $"{Plural.Of(modes.Count, "Count_ModeSheet")}, {Plural.Of(modes.Sum(s => s.Bindings.Count), "Count_Binding")}";
        }
        catch { sub = Strings.Main_CouldNotReadThisFile; }
        _cardCache[path] = (stamp, sub);
        return sub;
    }

    Control ProfileCard(string path, bool onDevice, string note = "", int position = 0)
    {
        var name = Path.GetFileName(path);
        var bare = BareName(name); // the user never reads ".csv"
        // "3." the way the selection guide writes it: the number of pushes of
        // the profile switch that lands on this file. Only files on a QuadStick
        // have one, and prefs.csv is not in the count.
        var heading = position > 0 ? $"{position}. {bare}" : bare;
        var subtitle = CardSubtitle(path) + note;
        if (onDevice && name.Equals("default.csv", StringComparison.OrdinalIgnoreCase))
            subtitle += Strings.Main_TheDeviceSFallbackFile;
        // Show that this profile has a copy on Drive. Kept out of CardSubtitle's
        // cache since link state changes on its own (connect, restore, turn off).
        // A dirty link means the last backup never landed: say so, or the card
        // tells someone their profile is safe when it is not.
        if (!onDevice && _settings.DriveLinks.TryGetValue(path, out var driveLink))
            subtitle += driveLink.BackupDirty ? Strings.Main_BackupPending : " · on Google Drive";

        // Stretch, so cards in one row end level. A profile whose facts wrap to
        // an extra line used to stand taller than the two beside it.
        var card = new Button { Classes = { "card" }, VerticalAlignment = VerticalAlignment.Stretch };
        AutomationProperties.SetName(card,
            string.Format(CultureInfo.CurrentCulture,
                onDevice ? Strings.Main_OpenOnDevice : Strings.Main_OpenInLibrary, bare, subtitle)
            + (position > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.Main_NumberPositionInTheProfile, position) : ""));
        // A tile, then the name, then what the profile actually is. The tile is
        // the recognition: same name always gets the same colour and initials,
        // so "Rocket League" and "GTA" are told apart across the room without
        // reading either of them. It is derived from the name, never fetched,
        // so it works offline and can never label a profile with the wrong art.
        var facts = FactsFor(path);
        var tile = new Border
        {
            Width = 46, Height = 46,
            CornerRadius = new Avalonia.CornerRadius(11),
            Background = new SolidColorBrush(TileColorFor(bare)),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = InitialsFor(bare),
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                FontSize = Size("SubheadSize"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        AutomationProperties.SetAccessibilityView(tile, AccessibilityView.Raw);

        var lines = new StackPanel { Spacing = 3 };
        lines.Children.Add(new TextBlock
        {
            // Named so a test can ask for the card's name without sweeping up
            // every other line on it.
            Name = "CardHeading",
            Text = heading, FontSize = Size("SectionSize"), FontWeight = FontWeight.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        lines.Children.Add(new TextBlock { Text = facts.Meta + note, Classes = { "cardsub" } });
        if (facts.Modes.Length > 0)
            lines.Children.Add(new TextBlock
            {
                Text = facts.Modes, Classes = { "cardsub" },
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        if (facts.Edited.Length > 0)
            lines.Children.Add(new TextBlock
            {
                Text = Strings.Main_Edited + facts.Edited, Classes = { "cardsub" },
                FontSize = Size("SmallSize"), Opacity = 0.75,
            });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 13 };
        row.Children.Add(tile);
        row.Children.Add(lines);
        card.Content = row;
        card.Click += async (_, _) =>
        {
            if (onDevice && name.Equals("prefs.csv", StringComparison.OrdinalIgnoreCase)
                && !await ConfirmAsync(Strings.Main_EditDevicePreferences,
                    Strings.Main_PrefsCsvHoldsTheQuadStick))
                return;
            try
            {
                // Device files open as a working copy (no save path): Save
                // routes to the library, and only Install, with its backup and
                // verification, ever writes back to the QuadStick.
                OpenInEditor(ProfileFile.Load(File.ReadAllText(path)), onDevice ? null : path,
                    onDevice ? ProfileSource.Device : ProfileSource.Library);
                if (onDevice)
                    Status(Strings.Main_OpenedFromYourQuadStickSave, StatusKind.Warning);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Stay on Home: showing an empty editor to display an error
                // strands the user in a dead view.
                HomeStatusText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Main_CouldNotOpenBareEx, bare, ex.Message);
                HomeStatusText.IsVisible = true;
            }
        };
        // Library cards get the same two sharing actions as the editor (right
        // click or long-press opens the menu), plus Delete. Device cards do not:
        // an on-device file has no library path to key a sheet by, and the only
        // safe way to change what is on the QuadStick is Install.
        if (!onDevice)
        {
            var menu = ShareMenu(path);
            var del = new MenuItem { Header = Strings.Main_DeleteProfile };
            AutomationProperties.SetName(del, string.Format(CultureInfo.CurrentCulture, Strings.Main_DeleteBareFromYourProfile, bare));
            del.Click += async (_, _) => await DeleteProfileAsync(path, bare);
            menu.Items.Add(new Separator());
            menu.Items.Add(del);
            card.ContextFlyout = menu;
        }
        return card;
    }

    // The Google Sheet copy is deliberately left alone. It may be shared with
    // someone else by now, and nothing here should reach into another person's
    // Drive.
    async Task DeleteProfileAsync(string path, string bare)
    {
        if (!await ConfirmAsync(string.Format(CultureInfo.CurrentCulture, Strings.Main_DeleteBare, bare),
            string.Format(CultureInfo.CurrentCulture, Strings.Main_PathGetFileNamePathIsRemoved, Path.GetFileName(path))))
            return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HomeStatusText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Main_CouldNotDeleteBareEx, bare, ex.Message);
            HomeStatusText.IsVisible = true;
            return;
        }
        RefreshHomeCards();
        HomeStatusText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Main_DeletedBare, bare);
        HomeStatusText.IsVisible = true;
    }

    void NewFromTemplate() => OpenInEditor(ProfileFile.NewFromTemplate(DefaultNewName), savePath: null, ProfileSource.New);

    /// <summary>First empty input cell (column C..J) on a binding row; 9 when the row is full.</summary>
    static int FirstFreeInputColumn(Binding b) =>
        Enumerable.Range(2, 8).FirstOrDefault(c => !b.InputCols.Contains(c), 9);

    // source defaults to File because that is what an unlabelled open is: a
    // profile that came off disk. The sites that know better say so.
    // No default on source. It used to be File, which silently mislabelled the
    // Sheets import for anyone who forgot to pass one, and a wrong source is
    // worse than no source: it reads as a real measurement.
    // track is false only when a profile moves between windows it is already
    // open in (the language rebuild): the person did not open anything.
    void OpenInEditor(ProfileFile file, string? savePath, ProfileSource source, bool track = true)
    {
        if (track) Telemetry.Track(TelemetryEvent.ProfileOpened, source);
        _file = file;
        _savePath = savePath;
        LoadDrafts(savePath);
        _draftedRevision = -1; // new file: its Revision counter is unrelated to the last one's
        _sheetIndex = 0;
        FileNameBox.Text = BareName(file.Document.CsvFileName);
        var headerName = file.Document.HeaderName;
        var bareTitle = BareName(file.Document.CsvFileName);
        if (bareTitle.Length == 0) bareTitle = "untitled";
        Title = Strings.Main_QuadstickConfigManagerUnofficial2
            + (headerName.Length > 0 ? $"{headerName} ({bareTitle})" : bareTitle);
        _selectedZone = null;
        ShowEditor();
        RefreshEditor(); // RefreshIssues inside sets the status line
        // A profile opened from a path retries its backup if it was left dirty.
        if (savePath is not null)
        {
            SyncSheetIdentity(savePath); // a moved profile finds its sheet again on open
            RememberRecent(savePath);
            FireBackupRetry(savePath, file.ToCsvText());
        }
    }

    /// <summary>Open a sheet in the editor. Everything on screen follows from
    /// which one is open, so nothing sets _sheetIndex on its own except
    /// ModesChanged, which knows the file changed shape underneath it.</summary>
    void SelectSheet(int index)
    {
        if (_file is null) return;
        // One past the last sheet is the names table: a view onto column L, not
        // a sheet in the file. See CustomNames.cs.
        index = Math.Clamp(index, 0, _file.Document.Sheets.Count);
        if (index == _sheetIndex) return;
        _sheetIndex = index;
        _selectedZone = null;
        RefreshEditor();
    }

    // The rows of the modes list by sheet index, so the one that is open can
    // take focus back after the list is rebuilt under it.
    readonly Dictionary<int, ToggleButton> _modeRows = new();

    // The modes list down the left side. Numbered the way the device numbers
    // them, and the way the modes window and the import review both do: only a
    // mode takes a number. Numbering the rows instead put "2: Preferences"
    // above "3: Drive" while every other screen called that same sheet mode 2.
    void BuildModeList()
    {
        ModeList.Children.Clear();
        _modeRows.Clear();
        if (_file is null) return;
        int mode = 0;
        var labels = _file.Document.Sheets.Select(sheet => sheet.Type switch
        {
            SheetType.Preferences => "Preferences",
            SheetType.Infrared => "Infrared",
            _ => string.Format(CultureInfo.CurrentCulture, Strings.Main_ModeNumberAndName, ++mode,
                    sheet.ModeName.Length > 0 ? sheet.ModeName : Strings.Main_UnnamedMode),
        }).ToList();
        labels.Add(CustomNamesLabel);
        for (int i = 0; i < labels.Count; i++) ModeList.Children.Add(ModeRow(i, labels[i]));
    }

    // The same selectable row a part gets, for the same reason: one list of
    // things where one of them is open. See RailRow.
    Control ModeRow(int index, string label)
    {
        var row = new ToggleButton
        {
            Classes = { "zone", "modeRow" },
            Content = new TextBlock
            { Text = label, TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize") },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            // Navigation rows are deliberately denser than editor controls:
            // three choices can stay visible before the list needs scrolling.
            Padding = new Avalonia.Thickness(8, 6),
            IsChecked = index == _sheetIndex,
        };
        AutomationProperties.SetName(row, label);
        row.Click += (_, _) =>
        {
            SelectSheet(index);
            // Pressing the open mode again must not leave the list with nothing
            // checked, and the rebuild above threw this control away, so focus
            // goes to whatever replaced it.
            row.IsChecked = index == _sheetIndex;
            _modeRows.GetValueOrDefault(index)?.Focus();
        };
        _modeRows[index] = row;
        return row;
    }

    /// <summary>Add a mode and open it, the way the modes window adds one:
    /// named past any "Mode N" already taken, so the plus never stops to ask.
    /// Renaming it is one button below the list.</summary>
    public int AddModeAndOpen()
    {
        if (_file is null) return -1;
        var sheets = _file.Document.Sheets;
        // Two modes with the same name are legal but unreadable in a list, so
        // count past any name already taken.
        var taken = sheets.Where(s => s.Type == SheetType.ProfileName)
            .Select(s => s.ModeName).ToHashSet();
        int n = sheets.Count(s => s.Type == SheetType.ProfileName) + 1;
        while (taken.Contains($"Mode {n}")) n++;
        // Under the mode you are looking at, not at the bottom of the file. A
        // new mode belongs beside the one it was made from, and the only way
        // back up the list is one press per place.
        int after = _sheetIndex >= 0 && _sheetIndex < sheets.Count
            && sheets[_sheetIndex].Type == SheetType.ProfileName ? _sheetIndex : -1;
        int idx = _file.AddModeSheet($"Mode {n}", after);
        ModesChanged(idx, Strings.Modes_ModeAdded);
        return idx;
    }

    // One dialog serves every "name a mode" prompt (add, rename, duplicate);
    // extraAboveBox slots caller-specific controls between the title and the
    // name box. Returns the trimmed name, or null on cancel or an empty name.
    async Task<string?> AskNameAsync(string title, string initialText, string confirmLabel,
        string boxAccessibleName, params Control[] extraAboveBox)
    {
        var box = new TextBox
        {
            Text = initialText,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // A tester renamed a mode to a whole paragraph; nothing past this
            // fits the mode picker or the side tube's speech anyway.
            MaxLength = 40,
        };
        AutomationProperties.SetName(box, boxAccessibleName);
        var confirm = new Button { Content = confirmLabel, MinWidth = 140, IsDefault = true };
        var cancel = new Button { Content = Strings.Main_Cancel, MinWidth = 140, IsCancel = true };
        var panel = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 16,
            MaxWidth = 480,
        };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.Bold, FontSize = Size("SubheadSize"), TextWrapping = TextWrapping.Wrap });
        foreach (var extra in extraAboveBox) panel.Children.Add(extra);
        panel.Children.Add(box);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { confirm, cancel } });
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = ZoomWrap(panel, _uiScale),
        };
        var confirmed = false;
        void Confirm() { confirmed = true; dialog.Close(); }
        confirm.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => dialog.Close();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Confirm(); };
        await ShowDialogInShellAsync(dialog);

        if (!confirmed) return null;
        var name = (box.Text ?? "").Trim();
        return name.Length == 0 ? null : name;
    }

    // Modes are managed in one window (ModesWindow.cs), not through a menu:
    // add, rename, reorder and delete all live next to the mode they act on.
    async Task ShowModesAsync()
    {
        if (_file is null) { Status(Strings.Main_OpenOrCreateAProfile); return; }
        await new ModesWindow(this).ShowDialog(this);
    }

    public void AddPreferencesSheetToFile()
    {
        if (_file is null) return;
        int idx = _file.AddPreferencesSheet();
        if (idx < 0) return;
        ModesChanged(idx, Strings.Main_PreferencesSheetAdded);
    }

    internal sealed record Zone(string Id, string Title, string Display, string DefaultInput, string Blurb);

    // Shared with the agent window, which draws the same device from the same
    // list. Two tables of the parts of a QuadStick would drift, and the one that
    // drifted would be teaching somebody their device wrong.
    internal static Zone[] AllZones { get; private set; } = BuildZones();

    static Zone[] BuildZones() => new Zone[]
    {
        new("joystick", Strings.Main_Joystick, Strings.Main_Joystick, "up",
            Strings.Main_MovingTheWholeMouthpieceWith),
        new("mp_left", Strings.Main_LeftMouthpieceHole, Strings.Main_Left, "mp_left_sip",
            Strings.Main_SipOrPuffOnThe),
        new("mp_center", Strings.Main_CenterMouthpieceHole, Strings.Main_Center, "mp_center_sip",
            Strings.Main_SipOrPuffOnThe2),
        new("mp_right", Strings.Main_RightMouthpieceHole, Strings.Main_Right, "mp_right_sip",
            Strings.Main_SipOrPuffOnThe3),
        new("combo", Strings.Main_HoleCombos, Strings.Main_Combos, "mp_left_center_sip",
            Strings.Main_TwoOrMoreHolesUsed),
        new("side", Strings.Main_SideTube, Strings.Main_SideTube, "right_sip",
            Strings.Main_SipOrPuffOnThe4),
        new("lip", Strings.Main_LipSwitch, Strings.Main_LipSwitch, "lip",
            Strings.Main_PressTheLipSwitchOr),
        // Default input is digital_in_8: a switch plugged into the top jack
        // with no splitter lands there, so it is the first thing a new mapping
        // on this zone should be.
        new("jacks", Strings.Main_SwitchJacks, Strings.Main_SwitchJacks, "digital_in_8",
            Strings.Main_AdaptiveSwitchesPluggedIntoThe),
        new("other", Strings.Main_USBDevices, Strings.Main_USBDevices, "usb_1_button_1",
            Strings.Main_AJoystickOrControllerPlugged),
        new("settings", Strings.Main_ModeSettings, "Settings", "",
            Strings.Main_DeviceSettingsThisModeChanges),
        new("unset", Strings.Main_NoInputYet, Strings.Main_NoInputYet, "",
            Strings.Main_RowsThatPressAGame),
    };

    // mp_right_mode_* is the right hole and the side tube together: the firmware
    // calls the side tube the "mode" tube, and its sensor map (DataFlow.c) gives
    // the pairing both bits. It is a combo, not something on the Right hole, so
    // it has to be caught before the mp_right arm below.
    internal static string ZoneOf(string input) => input switch
    {
        "" => "unset",
        _ when input.StartsWith("mp_left_center") || input.StartsWith("mp_right_center")
            || input.StartsWith("mp_left_right") || input.StartsWith("mp_triple")
            || input.StartsWith("mp_right_mode") => "combo",
        _ when input.StartsWith("mp_left") => "mp_left",
        _ when input.StartsWith("mp_center") => "mp_center",
        _ when input.StartsWith("mp_right") => "mp_right",
        "right_sip" or "right_puff" or "right_sip_soft" or "right_puff_soft"
            or "right_sip_long" or "right_puff_long" => "side",
        "push" or "lip" or "lip_soft" => "lip",
        _ when input.StartsWith("digital_in") => "jacks",
        "left" or "right" or "up" or "down" or "any_direction" or "center" => "joystick",
        _ when JoystickDirs.Contains(input) || JoystickDirs.Contains(input.Replace("_inner", "")) => "joystick",
        _ => "other",
    };

    // What each model actually has, per quadstick.com. FPS and Original share
    // the same inputs; the FPS difference is joystick precision, not mapping.
    string ModelDescription => _model switch
    {
        QsModel.Singleton => Strings.Main_SingletonASingleSipPuff,
        QsModel.Original => Strings.Main_Original3HoleMouthpieceSide,
        _ => Strings.Main_FPS3HoleMouthpieceSide,
    };

    // "mp_left_puff_soft" reads as "soft puff" on the Left hole's own card.
    static string ShortInput(Zone z, Binding b)
    {
        var input = b.Inputs.Count > 0 ? b.Inputs[0] : "";
        if (input.Length == 0) return Strings.Main_NoInput;
        var extra = b.Inputs.Count > 1 ? $" +{b.Inputs.Count - 1}" : "";
        return StripInput(input, z.Id) + extra;
    }

    // The friendly short form of one input token, scoped to the part it lives
    // on: on the Left hole "mp_left_puff_soft" becomes "soft puff". Shared by
    // ShortInput and the Device View dropdown labels.
    internal static string StripInput(string input, string zoneId)
    {
        // Avalonia calls item templates with a null item during measure, so a
        // null token can reach here before any real value is bound.
        if (string.IsNullOrEmpty(input)) return Strings.Main_NoInput;
        var s = input;
        // Longest first: "mp_right_mode_" has to be tried before "mp_right_",
        // or the loop breaks on the short one and leaves the word "mode".
        foreach (var prefix in new[] { "mp_left_center_", "mp_right_center_", "mp_left_right_", "mp_triple_", "mp_right_mode_", "mp_left_", "mp_center_", "mp_right_", "right_" })
            if (zoneId is not ("joystick" or "other") && s.StartsWith(prefix)) { s = s[prefix.Length..]; break; }
        if (s.EndsWith("_soft")) s = "soft " + s[..^5];
        return s.Replace('_', ' ');
    }

    // Turn a raw token into plain words: "mouse_left_button" -> "Mouse left
    // button". Used for outputs and functions in friendly mode.
    static string Humanize(string token)
    {
        var s = (token ?? "").Trim();
        if (s.Length == 0) return s;
        s = s.Replace('_', ' ');
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    // PlayStation output tokens shown with their Xbox equivalents, for users
    // who think in Xbox terms. Only game buttons differ; everything else
    // (keyboard, mouse, dpad, sticks) reads the same on both and falls back
    // to plain English.
    internal static readonly Dictionary<string, string> XboxStyle = new(StringComparer.Ordinal)
    {
        ["x"] = Strings.Main_AButton, ["circle"] = Strings.Main_BButton, ["square"] = Strings.Main_XButton,
        ["triangle"] = Strings.Main_YButton, ["left_1"] = Strings.Main_LeftBumper, ["right_1"] = Strings.Main_RightBumper,
        ["left_2"] = Strings.Main_LeftTrigger, ["right_2"] = Strings.Main_RightTrigger,
        ["left_3"] = Strings.Main_LeftStickClick, ["right_3"] = Strings.Main_RightStickClick,
        ["select"] = Strings.Main_ViewButton, ["start"] = Strings.Main_MenuButton, ["ps3"] = Strings.Main_XboxButton,
    };

    // How an output/function token is shown in Device View: friendly words,
    // Xbox-style names, or the raw token exactly as List View and the CSV
    // spell it.
    // The six tokens whose plain English name is the Xbox one. The firmware
    // sets ps3.L1 from left_1, ps3.R2 from right_2 and so on, and comments them
    // "shoulder", "trigger" and "joystick push button" (DataFlow.c). L1 and LB
    // are one button under two names, so both styles say what it is; Humanize
    // was calling it "Right 1", which names nothing on any controller.
    static readonly HashSet<string> SharedWithXbox = new(StringComparer.Ordinal)
    { "left_1", "left_2", "left_3", "right_1", "right_2", "right_3" };

    string TokenLabel(string token)
    {
        // Avalonia templates a null item during measure before any value binds.
        var t = token ?? "";
        if (_labelStyle == 0) return t;
        // "Ps3" and "Xac" are what Humanize makes of two names that are printed
        // on hardware. Neither is a word, so neither gets sentence case.
        if (t == "ps3") return _labelStyle == 2 ? Strings.Main_XboxButton : "PS";
        if ((_labelStyle == 2 || SharedWithXbox.Contains(t))
            && XboxStyle.TryGetValue(t, out var xbox)) return xbox;
        var plain = Humanize(t);
        return plain.StartsWith("Xac ", StringComparison.Ordinal) ? "XAC" + plain[3..] : plain;
    }

    // Keep the existing Words switch as the single source of truth for both
    // labels and controller prompt art. This affects presentation only; the
    // resolver still carries the raw token unchanged.
    OutputVisual VisualFor(string token, Func<string, string>? label = null) =>
        // List names is the file as the device reads it. A picture of a
        // controller button is the one thing that view is not for, so this
        // hands back a wordless kind and every caller draws the token.
        _labelStyle == 0
            ? new(token ?? "", OutputVisualKind.Generic, (label ?? TokenLabel)(token ?? ""))
            : OutputVisuals.For(token, label ?? TokenLabel,
                _labelStyle == 2 ? ControllerPromptStyle.Xbox : null);

    static string OutputDisplayLabel(Binding binding, Func<string, string> tokenLabel,
                                     int labelStyle)
    {
        return binding.ActionName.Length > 0 && labelStyle != 0
            ? binding.ActionName
            : tokenLabel(binding.Output);
    }

    // Label for an input token in a dropdown that can list inputs from more than
    // one part. Same-part tokens read bare ("Puff"); tokens from another part are
    // qualified ("Left · puff") so three parts' "puff" don't collapse into three
    // identical-looking rows.
    string InputOptionLabel(string token, string cardZone)
    {
        // Avalonia templates a null item during measure before any value binds.
        if (string.IsNullOrEmpty(token) || !_friendlyLabels) return token ?? "";
        // "Digital in 8" is the token with the underscores taken out; it says
        // nothing about where to plug the switch. The socket does.
        if (SwitchJacks.For(token) is { } jack) return jack.Label;
        if (SwitchJacks.RearJoystick.Contains(token, StringComparer.Ordinal))
            return string.Format(CultureInfo.CurrentCulture, Strings.Main_RearJoystickDirection, token["usb_1_".Length..]);
        var tz = ZoneOf(token);
        var bare = Humanize(StripInput(token, tz));
        if (bare.Length == 0) return bare;
        // A compass point is a name, not the start of a sentence: lowering
        // only its first letter turned NE into nE and NW_inner into nW inner.
        var low = bare.Split(' ')[0].All(char.IsUpper)
            ? bare
            : $"{char.ToLowerInvariant(bare[0])}{bare[1..]}";
        // Every pairing strips to the same word ("sip"), so a combo token must
        // always name its pairing or the list reads as duplicates.
        if (tz == "combo") return $"{ComboPair(token)} · {low}";
        if (tz == cardZone || tz is "other" or "unset") return bare;
        var disp = AllZones.FirstOrDefault(z => z.Id == tz)?.Display ?? tz;
        return $"{disp} · {low}";
    }

    // The back of the QuadStick, written out. Three switch jacks in the order
    // they sit on the case, then the USB-A port, each row saying what a plug in
    // it actually arrives as.
    //
    // Drawn as text and one glyph per port rather than as vector art: it has to
    // stay legible at every interface size, read aloud in order, and survive a
    // theme change. A picture of the panel would be nicer to look at and would
    // say none of this.
    // The back panel photo and its labels are laid out at one fixed size and
    // scaled as a whole, so a label can never drift off the socket it names.
    // Every number below is measured off Assets/QuadStickBack.png at 588x319.
    // BackPanelTests pins that size, so swapping the photo fails loudly.
    const double BackStageW = 720, BackStageH = 285;
    const double BackPhotoX = 150, BackPhotoY = 27, BackPhotoW = 420, BackPhotoH = 228;
    const double BackPillW = 145, BackPillH = 54;

    // Each socket: where its label sits, and the point on the photo it names.
    // The points are fractions of the photo, taken off the centre of each
    // socket's opening. The three jacks run down the left edge of the case and
    // the two USB ports down the right, so the labels sit outside the photo on
    // the side their socket is on.
    static readonly (string Name, string Detail, bool Left, double LabelY, double PointX, double PointY)[] BackSockets =
    {
        (SwitchJacks.PortLabel(SwitchJacks.TopPort), Strings.Main_OneSwitchIn8, true, 60, 0.1114, 0.2963),
        (SwitchJacks.PortLabel(SwitchJacks.LipPort), Strings.Main_OneSwitchIn5, true, 122, 0.1114, 0.5000),
        (SwitchJacks.PortLabel(SwitchJacks.BottomPort), Strings.Main_OneSwitchIn1, true, 184, 0.1114, 0.7147),
        (Strings.Main_USBBPort, Strings.Main_ToTheComputer, false, 70, 0.9005, 0.3354),
        (Strings.Main_USBAPort, Strings.Main_JoystickOrIn34, false, 150, 0.9107, 0.6254),
    };

    // Loaded once, the way the front photo is: this runs on every zone change.
    static Avalonia.Media.Imaging.Bitmap? _backPhoto;

    static Avalonia.Media.Imaging.Bitmap BackPhoto() =>
        _backPhoto ??= new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(
            new Uri("avares://QuadStickConfigManager/Assets/QuadStickBack.png")));

    // The picture Drew asked for: the back of the case with each socket named
    // and the number a plug lands on written beside it. The case's own green
    // silkscreen says "In 7-8", which is the pair, never which of the two a
    // single switch with no splitter actually gets.
    Control BackPanelPicture()
    {
        var stage = new Canvas { Width = BackStageW, Height = BackStageH, FlowDirection = Avalonia.Media.FlowDirection.LeftToRight };
        var photo = new Image
        {
            Source = BackPhoto(), Width = BackPhotoW, Height = BackPhotoH,
            Stretch = Stretch.Uniform, IsHitTestVisible = false,
        };
        Canvas.SetLeft(photo, BackPhotoX);
        Canvas.SetTop(photo, BackPhotoY);
        stage.Children.Add(photo);

        foreach (var (name, detail, left, labelY, fx, fy) in BackSockets)
        {
            double px = BackPhotoX + fx * BackPhotoW, py = BackPhotoY + fy * BackPhotoH;
            double lx = left ? 0 : BackStageW - BackPillW;
            double ax = left ? lx + BackPillW : lx;
            foreach (var line in Leader(ax, labelY + BackPillH / 2, px, py)) stage.Children.Add(line);
            stage.Children.Add(Marker(px, py));

            var pill = new Border
            {
                Width = BackPillW, Height = BackPillH,
                CornerRadius = new Avalonia.CornerRadius(5),
                Padding = new Avalonia.Thickness(8, 5),
                BorderThickness = new Avalonia.Thickness(1),
                Child = new StackPanel
                {
                    Spacing = 1,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = name, FontSize = Size("SmallSize"),
                            FontWeight = FontWeight.SemiBold,
                        },
                        new TextBlock
                        {
                            Text = detail, FontSize = Size("SmallSize"),
                            Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
                        },
                    },
                },
            };
            BindBrush(pill, Border.BackgroundProperty, "Surface");
            BindBrush(pill, Border.BorderBrushProperty, "Divider");
            // The two lines are one fact, so a screen reader reads them as one
            // sentence instead of announcing a fragment and then a number.
            AutomationProperties.SetName(pill, string.Format(CultureInfo.CurrentCulture, Strings.Main_NameDetail, name, detail));
            Canvas.SetLeft(pill, lx);
            Canvas.SetTop(pill, labelY);
            stage.Children.Add(pill);
        }
        // Shrinks to fit a narrow panel rather than clipping a label off the
        // edge, and is never blown up past the photo's own size.
        return new Viewbox
        {
            Child = stage, Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 2, 0, 4),
        };
    }

    Control BackPanelGuide()
    {
        var rows = new StackPanel { Spacing = 6 };
        foreach (var (port, channels) in SwitchJacks.Ports)
        {
            // The USB-A data pins are not a socket anybody plugs a switch into.
            // They are listed for completeness at the end, not as a port.
            if (port == SwitchJacks.UsbDataPort) continue;
            rows.Children.Add(PortRow("◎", port, SwitchJacks.Explain(port)));
        }
        rows.Children.Add(PortRow("▭", Strings.Main_USBAPort,
            Strings.Main_AJoystickPluggedInHere + string.Join(", ", SwitchJacks.RearJoystick)
            + Strings.Main_ItsButtonsAreUsb1));

        var title = new TextBlock
        {
            Text = Strings.Main_BackOfTheQuadStick,
            FontSize = Size("SmallSize"), FontWeight = FontWeight.Bold,
        };
        var box = new Border
        {
            CornerRadius = new Avalonia.CornerRadius(5),
            Padding = new Avalonia.Thickness(10, 8),
            Margin = new Avalonia.Thickness(0, 6, 0, 2),
            MaxWidth = 860,
            Child = new StackPanel { Spacing = 6, Children = { title, BackPanelPicture(), rows } },
        };
        BindBrush(box, Border.BackgroundProperty, "SurfaceSubtle");
        return box;
    }

    Control PortRow(string glyph, string port, string explain)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        var mark = new TextBlock
        {
            Text = glyph, FontSize = Size("BodySize"),
            Margin = new Avalonia.Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        var name = new TextBlock
        {
            Text = port, FontSize = Size("SmallSize"), FontWeight = FontWeight.SemiBold,
            Width = 96, VerticalAlignment = VerticalAlignment.Top,
        };
        var body = new TextBlock
        {
            Text = explain, FontSize = Size("SmallSize"),
            Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(body, 2);
        grid.Children.Add(mark);
        grid.Children.Add(name);
        grid.Children.Add(body);
        // The glyph is decoration; a screen reader reads the port and its
        // sentence as one line instead of announcing a shape.
        AutomationProperties.SetName(grid, string.Format(CultureInfo.CurrentCulture, Strings.Main_PortExplain, port, explain));
        AutomationProperties.SetName(mark, "");
        return grid;
    }

    Dictionary<string, List<Binding>> BindingsByZone()
    {
        var map = new Dictionary<string, List<Binding>>();
        foreach (var b in CurrentSheet?.Bindings ?? [])
        {
            // A settings row's column C is its value, not an input, so filing it
            // by ZoneOf put every per mode setting under a piece of hardware it
            // has nothing to do with: "50" matches no input, so it fell through
            // to "other", which is titled USB devices and described as extra
            // controllers plugged into the USB-A port. Its own zone says what it
            // is, and stays hidden in the profiles that have none.
            IEnumerable<string> zones =
                IsModePreferenceOverride(b) ? new[] { "settings" }
                : b.Inputs.Count > 0 ? b.Inputs.Select(ZoneOf).Distinct()
                : new[] { "unset" };
            foreach (var z in zones)
            {
                if (!map.TryGetValue(z, out var list))
                    map[z] = list = new();
                list.Add(b);
            }
        }
        return map;
    }

    IEnumerable<Zone> VisibleZones(Dictionary<string, List<Binding>> byZone, bool withUsbPort = false) =>
        AllZones.Where(z => z.Id switch
        {
            // The rear USB-A port is a real socket on the case, so the device
            // view always offers its card: reaching a joystick plugged in there
            // needs somewhere to click. The toolbar's unused list leaves it out
            // until the profile uses one, because twenty usb_1_* names in front
            // of the mouthpiece is not what that list is for.
            "other" => withUsbPort || byZone.ContainsKey(z.Id),
            // Not parts of the QuadStick, so they appear only when the profile
            // actually has rows in them.
            "unset" or "settings" => byZone.ContainsKey(z.Id),
            // A part the model does not have is still shown when the profile
            // maps it, marked, so nothing is ever hidden-but-live. A Singleton
            // has no left or right hole, no combos, no side tube, no lip switch
            // and no switch jacks, so an unmapped one of those is not offered.
            _ => ModelHasZone(z.Id) || byZone.ContainsKey(z.Id),
        });

    // A function cell holds its parameters too ("force_off 500", "delay 250"),
    // so the name is the first word. Matching on a prefix instead would let a
    // typo like "force_offf" pass for the real thing.
    static string FunctionName(string function)
    {
        var f = (function ?? "").Trim();
        int sp = f.IndexOf(' ');
        return sp < 0 ? f : f[..sp];
    }

    // Inputs this mode has nothing mapped to. A force_off row does not count as
    // using its input: it only turns off an output that toggle or delayed_latch
    // left on, so the input is still free for a real mapping.
    /// <param name="alsoZone">One zone to answer for whatever the toolbar list
    /// leaves out. Standing on a part is a narrower question than "what is free
    /// anywhere", and the rear USB-A port had no answer to it at all.</param>
    IEnumerable<string> UnusedInputs(string? alsoZone = null)
    {
        var used = (CurrentSheet?.Bindings ?? [])
            .Where(b => FunctionName(b.Function) != "force_off")
            .SelectMany(b => b.Inputs)
            .ToHashSet(StringComparer.Ordinal);
        // Same model filter as the device view, so a Singleton is not told its
        // missing holes are free. The unused picker does include the rear
        // USB-A port even before it has a mapping: its joystick directions are
        // precisely the inputs a person needs to discover and choose here.
        var zones = VisibleZones(BindingsByZone(), withUsbPort: true)
            .Select(z => z.Id).ToHashSet(StringComparer.Ordinal);
        if (alsoZone is not null) zones.Add(alsoZone);
        // validation.json does not list the four rear-joystick directions,
        // although the firmware accepts them and the device view already
        // knows how to name them. Include them here so “Joystick / North” is
        // actually a selectable unused input, not just something shown after
        // a person has somehow typed it first.
        return Vocab.AllInputs.Concat(SwitchJacks.RearJoystick)
            .Distinct(StringComparer.Ordinal)
            .Where(i => !used.Contains(i) && zones.Contains(ZoneOf(i)))
            .OrderBy(GroupRank).ThenBy(x => x, StringComparer.Ordinal);
    }

    // Which parts a combo token needs at once, in the words the zone cards use.
    // "Side tube" because mp_right_mode_* is the right hole plus the side tube,
    // which the firmware names the mode tube.
    internal static string ComboPair(string token) =>
        token.StartsWith("mp_triple_", StringComparison.Ordinal) ? Strings.Main_AllThree
        : token.StartsWith("mp_left_center_", StringComparison.Ordinal) ? "Left + Center"
        : token.StartsWith("mp_right_center_", StringComparison.Ordinal) ? "Right + Center"
        : token.StartsWith("mp_right_mode_", StringComparison.Ordinal) ? Strings.Main_RightSideTube
        : token.StartsWith("mp_left_right_", StringComparison.Ordinal) ? "Left + Right" : "Combo";

    // Chip text: the short form, since the zone heading above it already says
    // which part it is on. Combos are the exception. Every pairing strips to
    // the same word, so a combo chip has to name its pairing.
    // A chip on a card and a line in the unused list read the same way as the
    // picker: "digital in 1" is the token with its underscores taken out and
    // tells nobody which hole to plug into.
    internal static string ChipLabel(string token, string zoneId) =>
        SwitchJacks.For(token) is { } jack ? jack.Label
        : SwitchJacks.RearJoystick.Contains(token, StringComparer.Ordinal)
            ? string.Format(CultureInfo.CurrentCulture, Strings.Main_RearJoystickDirection, token["usb_1_".Length..])
        : zoneId != "combo"
        ? StripInput(token, zoneId)
        : (token.StartsWith("mp_triple_", StringComparison.Ordinal) ? "all 3 "
            : token.StartsWith("mp_left_center_", StringComparison.Ordinal) ? "L+C "
            : token.StartsWith("mp_right_center_", StringComparison.Ordinal) ? "R+C "
            : token.StartsWith("mp_right_mode_", StringComparison.Ordinal) ? "R+S " : "L+R ")
          + StripInput(token, zoneId);

    // Where a jack sorts: its socket's place on the case, then the lone
    // channel before the splitter's second one. Anything that is not a jack
    // keeps the order it arrived in.
    static int JackRank(string token)
    {
        if (SwitchJacks.For(token) is { } jack)
        {
            int port = Array.FindIndex(SwitchJacks.Ports, p => p.Port == jack.Port);
            return (port * 2) + (jack.Lone ? 0 : 1);
        }
        // On the USB card the four joystick directions lead. Alphabetical put
        // them after sixteen button numbers, so the thing somebody plugging a
        // joystick in is looking for was the last thing offered.
        int dir = Array.IndexOf(SwitchJacks.RearJoystick, token);
        return dir < 0 ? 0 : dir - SwitchJacks.RearJoystick.Length;
    }

    // Flyout list grouped by part. It is an input picker, not a detour through
    // Device View: each input itself starts a mapping in the view already open.
    void ShowUnusedInputs()
    {
        var free = UnusedInputs().ToList();
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var body = new StackPanel
        {
            Spacing = 4, MaxWidth = 360, Margin = new Avalonia.Thickness(4),
            Focusable = true, // focus lands here so a screen reader reads the list, not silence
        };
        body.Children.Add(new TextBlock
        {
            Text = free.Count == 0
                ? Strings.Main_EveryInputOnYourQuadStick
                : Plural.Of(free.Count, "Count_UnusedInput"),
            FontWeight = FontWeight.Bold, FontSize = Size("SubheadSize"), TextWrapping = TextWrapping.Wrap,
        });
        foreach (var zone in AllZones)
        {
            var inZone = free.Where(i => ZoneOf(i) == zone.Id).ToList();
            if (inZone.Count == 0) continue;
            body.Children.Add(new TextBlock
            {
                Text = zone.Title, Classes = { "secondary" },
                FontWeight = FontWeight.Bold, FontSize = Size("SmallSize"),
                Margin = new Avalonia.Thickness(0, 8, 0, 2), TextWrapping = TextWrapping.Wrap,
            });
            var chips = new WrapPanel();
            foreach (var token in inZone)
            {
                var input = new Button
                {
                    Content = new TextBlock { Text = ChipLabel(token, zone.Id), FontSize = Size("SmallSize") },
                    Classes = { "quiet" }, Padding = new Thickness(8, 3),
                    Margin = new Thickness(0, 0, 4, 4),
                };
                var t = token;
                AutomationProperties.SetName(input,
                    string.Format(CultureInfo.CurrentCulture, Strings.Main_MapTokenToANew, t, zone.Title));
                ToolTip.SetTip(input, t);
                input.Click += (_, _) =>
                {
                    flyout.Hide();
                    AddMappingWithInput(t, inputWasChosen: true);
                };
                chips.Children.Add(input);
            }
            body.Children.Add(chips);
        }
        flyout.Content = new ScrollViewer { Content = body, MaxHeight = 420 };
        flyout.Opened += (_, _) => body.Focus();
        flyout.ShowAt(UnusedButton);
    }

    void RefreshEditor()
    {
        bool device = _deviceView && CurrentSheet?.Type == SheetType.ProfileName;
        BuildModeList();
        // The list is rebuilt whenever a sheet changes. After it is measured,
        // reveal the fresh selected row in Modes' own viewport.
        AfterLayout(ScrollSelectedModeIntoView);
        GridContainer.IsVisible = !device;
        DeviceContainer.IsVisible = device;
        DeviceViewButton.Classes.Set("primary", device && !_railView);
        RailViewButton.Classes.Set("primary", device && _railView);
        ListViewButton.Classes.Set("primary", !device);
        // The names table has no mappings to draw on the device, so the three
        // view keys would be dead there. Disabled beats silently doing nothing.
        DeviceViewButton.IsEnabled = RailViewButton.IsEnabled = ListViewButton.IsEnabled = !OnCustomNames;
        AddRowButton.Content = AddRowContent(OnCustomNames ? Strings.Main_AddName : Strings.Main_AddRow);
        AutomationProperties.SetName(AddRowButton, OnCustomNames
            ? Strings.Main_AddARowToThe
            : Strings.Main_AddANewBindingRow);
        // The words toggle and the card style follow the mappings into Rows
        // view: the same Words switch, the same sentences. Neither has anything
        // to say on a preferences sheet or on the names table.
        LabelStyleButton.IsVisible = CardViewButton.IsVisible =
            CurrentSheet?.Type == SheetType.ProfileName && !OnCustomNames;
        // Each view carries its own card setting, so the word on the button
        // changes when the view does.
        UpdateCardViewButton();
        // Device View adds a mapping from the part you are looking at, which
        // says where the row landed. In the band it made a row appear somewhere
        // off screen, so the band belongs to Rows view only.
        AddRowButton.IsVisible = !device;
        // Only modes have inputs. The picker is available in every mode view:
        // it now adds the exact input the user chose without changing views.
        bool mode = CurrentSheet?.Type == SheetType.ProfileName;
        UnusedButton.IsVisible = mode;
        // Which QuadStick and whether one is plugged in are facts about the
        // machine in front of you, so the panel says them whichever view is on.
        var connected = Device.FindCandidatesCached().Count > 0;
        DeviceHeaderStatus.Content = StatusChip(connected ? StatusKind.Ready : StatusKind.Info,
            connected ? Strings.Main_QuadStickConnected : Strings.Main_NoQuadStickDetected, plainDot: !connected);
        if (device) { BuildDeviceView(); BuildZoneDetail(); }
        else RebuildRows();
        // The parts with nowhere to sit on the photo. The parts list already
        // names every one of them, and Rows view shows every row there is, so
        // the list in the panel is the diagram's own half of that job.
        ZoneList.IsVisible = device && !_railView;
        // The count rides on the label, so the number is there at a glance
        // without opening anything. Refreshed here, the one place every edit
        // already funnels through, so it is never stale.
        if (mode)
        {
            int free = UnusedInputs().Count();
            UnusedButton.Content = string.Format(CultureInfo.CurrentCulture, Strings.Main_UnusedFree, free);
            // Mirror the live count into the name, so a screen reader never
            // reads a stale label while the eye sees the number.
            AutomationProperties.SetName(UnusedButton,
                Plural.Of(free, "Count_UnusedInputOpens"));
        }
        RefreshIssues();
        RepaintSelection();
    }

    void ScrollSelectedModeIntoView()
    {
        if (_modeRows.GetValueOrDefault(_sheetIndex) is { } selected)
            selected.BringIntoView();
    }

    // The "refactor": NOT an incremental diff engine. Device View still rebuilds
    // from truth on every edit (small profiles, blur-triggered commits, and a diff
    // would only add stale-UI and focus bugs). What was missing is focus: after
    // a rebuild the control the user just used is gone, so keyboard/switch users
    // were dropped. Rebuild, then refocus the same cell's replacement control.
    // Editing an input can move its mapping to another zone, so follow it there.
    void RebuildDeviceAfterEdit(int row, int col)
    {
        if (col >= 2 && _file is not null) // an input cell: its zone follows the new value
            _selectedZone = ZoneOf(_file.GetCell(row, col));
        BuildDeviceView();
        BuildZoneDetail();
        RefreshIssues();
        AfterLayout(() =>
        {
            if (_cellBorders.TryGetValue($"{(char)('A' + col)}{row}", out var border))
            { border.BringIntoView(); (border.Child as Control)?.Focus(); }
        });
    }

    void BuildDeviceView()
    {
        DeviceCanvas.Children.Clear();
        ZoneList.Children.Clear();
        _stageBox = null;
        _zoneButtons.Clear();
        _cellBorders.Clear(); // stale entries from other zones/profiles would get issue-highlighted
        var byZone = BindingsByZone();

        // Parts List: a plain vertical list of part rows instead of the diagram,
        // for users who'd rather arrow through a list than read a picture.
        if (_railView)
        {
            var rail = new StackPanel { Spacing = 6 };
            rail.Children.Add(new TextBlock
            { Text = Strings.Main_Parts, FontSize = Size("SmallSize"), FontWeight = FontWeight.Bold, Classes = { "muted" }, Margin = new Avalonia.Thickness(2, 0, 0, 4) });
            foreach (var z in VisibleZones(byZone, withUsbPort: true))
                rail.Children.Add(RailRow(z, byZone));
            DeviceCanvas.Children.Add(rail);
            return;
        }

        var visible = VisibleZones(byZone, withUsbPort: true).ToList();

        // Say it, do not just mark it. A profile written for an FPS opened on a
        // Singleton keeps every row and every card, but the diagram cannot draw
        // a hole the device has not got, so the difference is named in words
        // above the picture instead of being left to a dimmed card.
        var foreign = ForeignMappedZones(byZone).ToList();
        if (foreign.Count > 0)
        {
            var warning = new TextBlock
            {
                Name = "ModelMismatchWarning",
                Text = string.Format(CultureInfo.CurrentCulture, Strings.Main_ThisProfileMapsPartsYour,
                    Plural.Of(foreign.Count, "Count_Part"), ModelNames[(int)_model]),
                FontSize = Size("SmallSize"), TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(2, 0, 2, 10),
            };
            BindBrush(warning, TextBlock.ForegroundProperty, "Warning");
            DeviceCanvas.Children.Add(warning);
        }

        // ---- Main diagram: the photo of the device with each part pinned
        // where it physically sits, so a part is found by looking at the thing
        // in your mouth instead of matching a name to a box. ----
        var photoY = DevicePhotoY;
        var d = Diagram;
        var stage = new Canvas
        {
            Name = "DeviceStage", Width = StageW, Height = DeviceStageHeight,
            FlowDirection = Avalonia.Media.FlowDirection.LeftToRight,
        };

        // The catalog photos are square with wide transparent margins, so the
        // device is drawn full size inside a window clipped to the part worth
        // showing. Cropping here rather than in the file keeps the supplied
        // asset untouched.
        var frame = new Canvas
        {
            Name = "DevicePhotoFrame",
            Width = d.PhotoW, Height = d.PhotoH, ClipToBounds = true,
            IsHitTestVisible = false, // the labels are the controls, not the picture
        };
        var photo = new Image
        {
            Source = DevicePhoto(_model),
            Width = d.FullSize.Width, Height = d.FullSize.Height,
            Stretch = Stretch.Fill, IsHitTestVisible = false,
        };
        Canvas.SetLeft(photo, d.FullOffset.X);
        Canvas.SetTop(photo, d.FullOffset.Y);
        frame.Children.Add(photo);
        Canvas.SetLeft(frame, d.PhotoX);
        Canvas.SetTop(frame, photoY);
        stage.Children.Add(frame);

        // The device's own mode lights, lit the way the firmware lights them.
        // The header says the same thing in words, because a colour on its own
        // is not a cue every reader gets.
        int mode = CurrentModeNumber();
        var lights = ModeLights.For(mode);
        for (int i = 0; d.Lights is { } row && lights is not null && i < lights.Length; i++)
            if (lights[i] != ModeLight.Off)
            {
                var at = d.OnPhoto(row.X + i * row.Gap, row.Y);
                foreach (var dot in Led(lights[i], d.PhotoX + at.X, photoY + at.Y))
                    stage.Children.Add(dot);
            }

        foreach (var spot in d.Hotspots)
        {
            var z = visible.FirstOrDefault(v => v.Id == spot.Zone);
            if (z is null) continue;
            var at = d.OnPhoto(spot.PointX, spot.PointY);
            double pointX = d.PhotoX + at.X, pointY = photoY + at.Y;
            double calloutHeight = spot.Bottom ? SmallPillH : PillH;
            // A top callout hangs from a fixed bottom edge just above the photo
            // and grows upward into the room above, so a card with a two line
            // row can never reach down onto the device. A bottom one still
            // starts at the band under the photo.
            double calloutBottom = photoY - TopCalloutGap;
            // The leader line leaves the edge of the label facing the part.
            double ax = spot.LabelX + PillW / 2, ay = spot.Bottom ? DeviceBottomLabelY : calloutBottom;
            foreach (var line in Leader(ax, ay, pointX, pointY)) stage.Children.Add(line);
            stage.Children.Add(Marker(pointX, pointY));

            var label = ZoneButton(z, byZone, PillW, minHeight: calloutHeight, shortName: true);
            label.Width = PillW;
            Canvas.SetLeft(label, spot.LabelX);
            if (spot.Bottom) Canvas.SetTop(label, DeviceBottomLabelY);
            else Canvas.SetBottom(label, DeviceStageHeight - calloutBottom);
            stage.Children.Add(label);
        }
        // Shrinks to fit a narrow panel instead of clipping a hotspot off the
        // edge, and is never blown up past the photo's own size.
        _stageBox = new Viewbox
        {
            Child = stage, Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
        FitDeviceStage();
        DeviceCanvas.Children.Add(_stageBox);

        // ---- Secondary parts, in the panel down the left side: hole combos,
        // switch jacks, USB, per-mode settings, then unmapped rows last so "No
        // input yet" reads as the end of the list. They have nowhere to sit on
        // the photo, and under it they pushed the QuadStick itself off the top
        // of the panel it is the whole point of. ----
        // Anything the diagram has no marker for goes here too. Parts the model
        // does not have are collected behind one card rather than given a row
        // each: on a Singleton that was five extra rows shoving the modes list
        // and the view keys off the panel, for parts that device has not got.
        var pinned = d.Hotspots.Select(h => h.Zone).ToHashSet(StringComparer.Ordinal);
        var railed = visible.Where(z => z.Id is not "unset" && !pinned.Contains(z.Id)).ToList();
        foreach (var z in railed.Where(z => ModelHasZone(z.Id))
                                .Concat(visible.Where(z => z.Id is "unset")))
            ZoneList.Children.Add(RailRow(z, byZone, compact: true));
        var offModel = railed.Where(z => !ModelHasZone(z.Id)).ToList();
        if (offModel.Count > 0) ZoneList.Children.Add(OffModelCard(offModel, byZone));
    }

    // The scaled photo, kept so a resize can refit it without a full rebuild.
    Viewbox? _stageBox;

    // Under this the labels on the picture stop being readable, so the panel
    // scrolls instead of shrinking further.
    const double StageFloorH = 280;

    // A Viewbox inside a ScrollViewer is measured against infinite height, so
    // it only ever scales to the width and then runs off the bottom. Capping it
    // to the height actually on screen makes it fit both ways, and the floor
    // below is where scrolling takes over.
    void FitDeviceStage()
    {
        if (_stageBox is null) return;
        double room = DeviceStageScroll.Bounds.Height;
        _stageBox.MaxHeight = room > 0 ? Math.Max(StageFloorH, room) : DeviceStageHeight;
    }

    // The photo and its labels are laid out at one fixed size and scaled as a
    // whole, so a label can never drift off the part it names. The photo, the
    // parts on it and their measured positions live in DeviceDiagram, one entry
    // per model, so an Original owner is never shown an FPS.
    DeviceDiagram Diagram => DeviceDiagram.For(_model);

    const double StageW = 700;
    const double PillW = 160, PillH = 116, SmallPillH = 100;

    // Room kept above the photo for the top callouts, over what the diagram
    // itself asks for. A callout is as tall as the words in it: "Increment
    // mode" and "Right trigger" take two lines each, and at 116px the card ran
    // down onto the device. The whole stage is inside a Viewbox, so paying for
    // the room costs a little scale and nothing else.
    const double TopCalloutRoom = 48, TopCalloutGap = 10;

    // Larger type makes the top callouts taller. Move the photo down by the
    // same amount and move the lower band with it, so the controls never sit
    // on top of the device at an accessibility scale.
    double DevicePhotoY => Diagram.PhotoY + TopCalloutRoom + Math.Max(0, (_uiScale - 1.0) * 98);
    // The lower band clears the bottom of the photo wherever the photo ends up,
    // which is a different place on each of the three models.
    double DeviceBottomLabelY => DevicePhotoY + Diagram.PhotoH + 13;
    // A model with nothing pinned below gives that band back to the photo.
    double DeviceStageHeight => Diagram.Hotspots.Any(h => h.Bottom)
        ? DeviceBottomLabelY + SmallPillH + 20
        : DevicePhotoY + Diagram.PhotoH + 20;

    // Leader lines and markers are drawn twice: a thick line in the surface
    // colour under a thin one in the text colour, so they stay visible over
    // the black device and over the panel behind it, in either theme.
    IEnumerable<Control> Leader(double x1, double y1, double x2, double y2)
    {
        foreach (var (thickness, brush) in new[] { (5.0, "Surface"), (2.0, "TextSecondary") })
        {
            var line = new Avalonia.Controls.Shapes.Line
            {
                StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2),
                StrokeThickness = thickness, IsHitTestVisible = false,
            };
            BindBrush(line, Avalonia.Controls.Shapes.Shape.StrokeProperty, brush);
            yield return line;
        }
    }

    // A lit LED: the lens colour with a wider, dimmer bloom around it, which is
    // what a lit one looks like against the black case.
    static IEnumerable<Control> Led(ModeLight light, double x, double y)
    {
        var colour = light switch
        {
            ModeLight.Purple => Color.FromRgb(0xC2, 0x5C, 0xFF),
            ModeLight.Blue => Color.FromRgb(0x4C, 0x8D, 0xFF),
            _ => Color.FromRgb(0xFF, 0x4D, 0x45),
        };
        foreach (var (size, opacity) in new[] { (26.0, 0.3), (13.0, 1.0) })
        {
            var dot = new Avalonia.Controls.Shapes.Ellipse
            {
                Width = size, Height = size, Fill = new SolidColorBrush(colour),
                Opacity = opacity, IsHitTestVisible = false,
            };
            Canvas.SetLeft(dot, x - size / 2);
            Canvas.SetTop(dot, y - size / 2);
            yield return dot;
        }
    }

    Control Marker(double x, double y)
    {
        var dot = new Avalonia.Controls.Shapes.Ellipse
        { Width = 14, Height = 14, StrokeThickness = 3, IsHitTestVisible = false };
        BindBrush(dot, Avalonia.Controls.Shapes.Shape.StrokeProperty, "TextSecondary");
        BindBrush(dot, Avalonia.Controls.Shapes.Shape.FillProperty, "Surface");
        Canvas.SetLeft(dot, x - 7);
        Canvas.SetTop(dot, y - 7);
        return dot;
    }

    // Decoded once per model: BuildDeviceView runs on every edit and decoding
    // the PNG each time showed up as a stutter on the mapping panel.
    static readonly Dictionary<QsModel, Avalonia.Media.Imaging.Bitmap> _devicePhotos = new();

    static Avalonia.Media.Imaging.Bitmap DevicePhoto(QsModel model)
    {
        if (_devicePhotos.TryGetValue(model, out var cached)) return cached;
        var bmp = new Avalonia.Media.Imaging.Bitmap(
            Avalonia.Platform.AssetLoader.Open(new Uri(DeviceDiagram.For(model).Asset)));
        _devicePhotos[model] = bmp;
        return bmp;
    }

    // The device numbers modes by counting Profile Name segments as it reads
    // the file, so a mode's number is its position among those, not its row in
    // the sheet picker (Preferences and Infrared take no number).
    int CurrentModeNumber() =>
        _file is null ? 0
        : _file.Document.Sheets.Take(_sheetIndex + 1).Count(s => s.Type == SheetType.ProfileName);

    // Which parts the selected model physically has. Zones the model lacks
    // still show when a profile maps them, but marked, so a profile made for
    // an FPS is never silently broken on a Singleton. "settings" and "unset"
    // are rows in the file rather than hardware, so no model owns them and
    // they are never called foreign.
    bool ModelHasZone(string zoneId) =>
        zoneId is "settings" or "unset" || Diagram.HasZone(zoneId);

    // Parts this profile maps that the selected model does not have. The rows
    // stay editable and the zone cards stay reachable; this is what the banner
    // over the diagram names, because a marked card somebody has to scroll to
    // is not the same as being told.
    IEnumerable<Zone> ForeignMappedZones(Dictionary<string, List<Binding>> byZone) =>
        AllZones.Where(z => byZone.ContainsKey(z.Id) && !ModelHasZone(z.Id));

    string SummaryActionText(GestureSummary summary)
    {
        var names = summary.Actions
            .Where(a => !a.IsSupport && a.FriendlyOutput.Length > 0)
            .Select(a => OutputDisplayLabel(a.Binding, TokenLabel, _labelStyle))
            .Distinct(StringComparer.CurrentCulture)
            .ToList();
        if (names.Count == 0) return "—";
        if (summary.HasComplexBehavior)
        {
            if (summary.SequenceUses.Count > 0 && summary.Actions.Count == 1)
                return $"{names[0]} · {Strings.Main_Sequence}";
            return $"{names[0]} · {Plural.Of(summary.Actions.Count, "Count_Action")}";
        }
        if (names.Count <= 4) return string.Join(" · ", names);
        return $"{string.Join(" · ", names.Take(3))} · +{names.Count - 3}";
    }

    Control SummaryActionVisuals(GestureSummary summary)
    {
        var spoken = SummaryActionText(summary);
        TextBlock Words(string t)
        {
            var block = new TextBlock
            {
                Text = t,
                FontSize = Size("SmallSize"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (!summary.IsMapped) block.Classes.Add("muted");
            return block;
        }

        var named = summary.Actions
            .Where(a => !a.IsSupport && a.FriendlyOutput.Length > 0)
            .Select(a => (a.Binding, Label: OutputDisplayLabel(a.Binding, TokenLabel, _labelStyle)))
            .DistinctBy(x => x.Label, StringComparer.CurrentCulture)
            .ToList();

        // "· 3 actions" and "· +2" count things rather than name them, and no
        // picture says a count. Those summaries stay the sentence they were.
        if (named.Count == 0 || summary.HasComplexBehavior || named.Count > 4)
            return Words(spoken);

        // A B button drawn beside the word "B button" says it twice. The
        // prompt stands alone; anything the device only does as an idea, and
        // anything the user has given a name of their own, stays as words.
        //
        // WrapPanel, not StackPanel: a horizontal StackPanel hands its children
        // unlimited width, so "Decrement mode" never wrapped and the callout
        // card cut it off mid-word.
        var row = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var (binding, label) in named)
        {
            if (row.Children.Count > 0)
            {
                var dot = Words("\u00b7");
                dot.Margin = new Thickness(4, 0);
                row.Children.Add(dot);
            }
            var visual = VisualFor(binding.Output);
            // RequiresTextLabel: a moving stick and a mouse direction draw the
            // control but not which way it goes, so those keep their words.
            row.Children.Add(visual.IsSelfDescribing && !visual.RequiresTextLabel
                                 && binding.ActionName.Length == 0
                ? OutputVisuals.Render(visual, includeLabel: false, compact: true)
                : Words(label));
        }
        AutomationProperties.SetName(row, spoken);
        return row;
    }

    Control MouthpieceSummary(string zone)
    {
        var rows = DeviceSummary.Mouthpiece(CurrentSheet, zone, TokenLabel);
        // One grid for the whole card, not one per row: an Auto first column
        // sizes to the widest gesture name here and hands every pixel it does
        // not need to the action, which is the column that runs out of room.
        // Per-row grids had to guess that width, and 70px was both too much for
        // "Sip" and too little for a language that spells "Soft puff" longer.
        var panel = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch,
                               ColumnDefinitions = new ColumnDefinitions("Auto,4,*") };
        foreach (var summary in rows)
        {
            panel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            // The gesture reads back to the gutter and the action reads out
            // from it, so the eye has one edge to follow down each side and the
            // gap in the middle says which column is which.
            var gesture = new TextBlock
            {
                Text = summary.FriendlyGestureName,
                FontSize = Size("SmallSize"),
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.NoWrap,
                TextAlignment = TextAlignment.Right,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var action = SummaryActionVisuals(summary);
            action.VerticalAlignment = VerticalAlignment.Center;
            action.HorizontalAlignment = HorizontalAlignment.Left;
            // Room for the rules to sit in without touching the words.
            gesture.Margin = action.Margin = new Thickness(0, 2);
            int r = panel.RowDefinitions.Count - 1;
            Grid.SetRow(gesture, r);
            Grid.SetRow(action, r);
            Grid.SetColumn(action, 2);
            panel.Children.Add(gesture);
            panel.Children.Add(action);
        }

        // Rules, so a callout reads as a small table instead of four pairs of
        // words floating in a box: the middle gutter carries the line between
        // the columns, and every row after the first draws the line above it.
        int rowCount = panel.RowDefinitions.Count;
        for (int r = 1; r < rowCount; r++)
        {
            var across = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top };
            BindBrush(across, Border.BackgroundProperty, "SurfaceSubtle");
            Grid.SetRow(across, r);
            Grid.SetColumnSpan(across, 3);
            panel.Children.Add(across);
        }
        if (rowCount == 0) return panel;
        var down = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Center };
        BindBrush(down, Border.BackgroundProperty, "SurfaceSubtle");
        Grid.SetColumn(down, 1);
        Grid.SetRowSpan(down, rowCount);
        panel.Children.Add(down);
        // The last rule separates the part's name from its table.
        var ruled = new Border
        { Child = panel, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 2, 0, 0) };
        BindBrush(ruled, Border.BorderBrushProperty, "SurfaceSubtle");
        return ruled;
    }

    Control JoystickSummaryContent()
    {
        var summary = DeviceSummary.Joystick(CurrentSheet);
        var panel = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center };
        if (summary.IsRecognized)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Strings.Main_Movement, FontSize = Size("SmallSize"),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            // The stick has its own controller prompt, so draw it. The name
            // stays on the control for anyone reading the screen aloud. An
            // output with no prompt of its own falls to the words below.
            if (summary.RoleToken.Length > 0
                && VisualFor(summary.RoleToken) is { IsSelfDescribing: true } roleVisual)
            {
                var art = OutputVisuals.Render(roleVisual, includeLabel: false, compact: true);
                art.HorizontalAlignment = HorizontalAlignment.Center;
                AutomationProperties.SetName(art, summary.Role);
                panel.Children.Add(art);
            }
            else panel.Children.Add(new TextBlock
            {
                Text = summary.Role, FontSize = Size("SmallSize"),
                FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            });
            if (summary.ExtraActionCount > 0)
                panel.Children.Add(new TextBlock
                {
                    Text = string.Format(CultureInfo.CurrentCulture, Strings.Main_ExtraActions, summary.ExtraActionCount),
                    FontSize = Size("SmallSize"), Classes = { "muted" },
                    TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = summary.ActionCount == 0
                    ? Strings.Main_NotMapped
                    : Plural.Of(summary.ActionCount, "Count_Action"),
                FontSize = Size("SmallSize"),
                Classes = { "muted" }, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            if (summary.ActionCount > 0)
                panel.Children.Add(new TextBlock
                {
                    Text = Strings.Main_ViewDetails, FontSize = Size("SmallSize"),
                    TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                });
        }
        return panel;
    }

    string SummaryAccessibleText(Zone z)
    {
        if (z.Id is "mp_left" or "mp_center" or "mp_right" or "side" or "lip")
            return string.Join(", ", DeviceSummary.Mouthpiece(CurrentSheet, z.Id, TokenLabel)
                .Select(s => $"{s.FriendlyGestureName}: {SummaryActionText(s)}"));
        if (z.Id == "joystick")
        {
            var summary = DeviceSummary.Joystick(CurrentSheet);
            return summary.IsRecognized
                ? $"{Strings.Main_Movement}: {summary.Role}"
                : summary.ActionCount == 0 ? Strings.Main_NotMapped : Plural.Of(summary.ActionCount, "Count_Action");
        }
        return "";
    }

    Control ZoneButton(Zone z, Dictionary<string, List<Binding>> byZone, double minWidth,
                       double minHeight = 84, bool circle = false, bool shortName = false)
    {
        byZone.TryGetValue(z.Id, out var bindings);
        int count = bindings?.Count ?? 0;
        bool foreign = !ModelHasZone(z.Id);
        bool selected = _selectedZone == z.Id;

        var content = new StackPanel { Spacing = 3 };
        content.Children.Add(new TextBlock
        {
            // Round holes and photo callouts use the short name: the full one
            // wraps to three lines and pushes the mapping count out of the box.
            Text = circle || shortName ? z.Display : z.Title, FontWeight = FontWeight.Bold,
            FontSize = Size(circle ? "SmallSize" : "BodySize"),
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        if (z.Id is "mp_left" or "mp_center" or "mp_right" or "side" or "lip")
            content.Children.Add(MouthpieceSummary(z.Id));
        else if (z.Id == "joystick")
            content.Children.Add(JoystickSummaryContent());
        else
        {
            var countLabel = new TextBlock
            {
                Text = count == 0 ? Strings.Main_NotMapped : Plural.Of(count, "Count_Mapping"),
                FontSize = Size("SmallSize"), TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            if (count == 0) countLabel.Classes.Add("muted");
            else BindBrush(countLabel, TextBlock.ForegroundProperty, "AccentText");
            content.Children.Add(countLabel);
        }

        // Every foreign part says so in TEXT, circles included: dimming alone
        // is a contrast-only cue that low-vision users cannot rely on.
        if (foreign)
            content.Children.Add(new TextBlock
            {
                Text = circle || shortName ? Strings.Main_NotOnModel : Strings.Main_NotOnYourModel,
                FontSize = Size("SmallSize"), Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            });

        var btn = new ToggleButton
        {
            Classes = { "zone" }, MinWidth = minWidth, MinHeight = minHeight,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = content,
            IsChecked = selected,
        };
        if (circle)
        {
            btn.Width = minWidth; btn.Height = minWidth;
            btn.CornerRadius = new Avalonia.CornerRadius(minWidth / 2);
            btn.Padding = new Avalonia.Thickness(2);
        }
        // Parts the model doesn't physically have are greyed out but still
        // reachable, so a profile built for another model can be cleaned up.
        if (foreign) btn.Opacity = 0.5;

        SetZoneAccessibleName(btn, z, bindings, count, foreign, selected);
        WireZoneSelect(btn, z.Id);
        return btn;
    }

    // A part row for the Parts List view: the same selectable control as a
    // diagram tile, laid out as a wide row (name + mapping count) so the left
    // side becomes a plain list to arrow through. Feeds the same editor.
    // The parts this model has not got, behind one card. A profile written for
    // an FPS opened on a Singleton maps five of them, and five dimmed rows in
    // the side panel is five rows of somebody else's device in front of the
    // list of modes.
    //
    // The card opens a flyout to the right holding the same rows the panel
    // would have shown, so nothing is lost: the mappings are still there, still
    // named, still one click from the editor. They are just not in the way of
    // the parts the device actually has.
    Control OffModelCard(List<Zone> zones, Dictionary<string, List<Binding>> byZone)
    {
        int total = zones.Sum(z => byZone.GetValueOrDefault(z.Id)?.Count ?? 0);
        string title = string.Format(CultureInfo.CurrentCulture,
            Strings.Main_NotOnYourModelName, ModelNames[(int)_model]);

        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeight.Bold,
            FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
        });
        name.Children.Add(new TextBlock
        {
            Text = Plural.Of(zones.Count, "Count_Part"),
            FontSize = Size("SmallSize"), Classes = { "muted" },
        });

        var cnt = new TextBlock
        {
            Text = Plural.Of(total, "Count_Mapping"),
            FontSize = Size("SmallSize"), VerticalAlignment = VerticalAlignment.Center,
        };
        BindBrush(cnt, TextBlock.ForegroundProperty, "AccentText");

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(name);
        Grid.SetColumn(cnt, 1);
        row.Children.Add(cnt);

        var body = new StackPanel
        {
            Spacing = 4, MinWidth = 240, MaxWidth = 320, Margin = new Avalonia.Thickness(4),
            Focusable = true, // focus lands here so a reader reads the list, not silence
        };
        body.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeight.Bold, FontSize = Size("SubheadSize"),
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = Strings.Main_TheseRowsAreKeptInThe, FontSize = Size("SmallSize"),
            Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 4),
        });

        var flyout = new Flyout { Content = body, Placement = PlacementMode.RightEdgeAlignedTop };
        foreach (var z in zones)
        {
            // The same row the panel would have drawn, so a part is named,
            // counted and selected here exactly as it is anywhere else.
            var item = (ToggleButton)RailRow(z, byZone, compact: true);
            item.Click += (_, _) => flyout.Hide();
            body.Children.Add(item);
        }

        var card = new Button
        {
            Classes = { "zone" }, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Avalonia.Thickness(10, 8),
            Content = row, Flyout = flyout,
        };
        AutomationProperties.SetName(card, string.Format(CultureInfo.CurrentCulture,
            Strings.Main_NotOnYourModelNameParts, title,
            Plural.Of(zones.Count, "Count_Part"), Plural.Of(total, "Count_Mapping")));
        // Selecting one of these rebuilds the panel, and the row that was
        // clicked no longer exists. Point every off-model part's focus at the
        // card, which does.
        foreach (var z in zones) _zoneButtons[z.Id] = card;
        return card;
    }

    Control RailRow(Zone z, Dictionary<string, List<Binding>> byZone, bool compact = false)
    {
        byZone.TryGetValue(z.Id, out var bindings);
        int count = bindings?.Count ?? 0;
        bool foreign = !ModelHasZone(z.Id);
        bool selected = _selectedZone == z.Id;

        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = z.Title, FontWeight = FontWeight.Bold, FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap });
        if (foreign)
            name.Children.Add(new TextBlock { Text = Strings.Main_NotOnYourModel, FontSize = Size("SmallSize"), Classes = { "muted" } });

        var cnt = new TextBlock
        {
            Text = count == 0 ? Strings.Main_NotMapped : Plural.Of(count, "Count_Mapping"),
            FontSize = Size("SmallSize"), VerticalAlignment = VerticalAlignment.Center,
        };
        if (count == 0) cnt.Classes.Add("muted"); else BindBrush(cnt, TextBlock.ForegroundProperty, "AccentText");

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(name);
        Grid.SetColumn(cnt, 1);
        row.Children.Add(cnt);

        var btn = new ToggleButton
        {
            Classes = { "zone" }, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            // Tighter in the side panel: four of these plus the modes list
            // and the view keys have to share one fixed-width column.
            Padding = new Avalonia.Thickness(compact ? 10 : 14, compact ? 8 : 12),
            Content = row, IsChecked = selected,
        };
        if (foreign) btn.Opacity = 0.5;
        SetZoneAccessibleName(btn, z, bindings, count, foreign, selected);
        WireZoneSelect(btn, z.Id);
        return btn;
    }

    void SetZoneAccessibleName(Control btn, Zone z, List<Binding>? bindings, int count, bool foreign, bool selected)
    {
        var summary = SummaryAccessibleText(z);
        var spoken = summary.Length > 0
            ? summary
            : count == 0
                ? Strings.Main_NothingMappedYet
                : string.Join(", ", (bindings ?? new()).Take(4).Select(b => string.Format(CultureInfo.CurrentCulture,
                    Strings.Main_InputPressesOutput, ShortInput(z, b), OutputFieldValue(b))));
        var warning = foreign ? string.Format(CultureInfo.CurrentCulture, Strings.Main_NotAvailableOnYourModelNames, ModelNames[(int)_model]) : "";
        AutomationProperties.SetName(btn,
            string.Format(CultureInfo.CurrentCulture,
                selected ? Strings.Main_ZoneSelected : Strings.Main_Zone,
                z.Title, Plural.Of(count, "Count_Mapping"), spoken, warning));
    }

    void WireZoneSelect(ToggleButton btn, string zoneId)
    {
        btn.Click += (_, _) =>
        {
            _selectedZone = zoneId;
            _selectedRows.Clear(); _selAnchor = -1; // cards of another part leave the screen
            BuildDeviceView(); BuildZoneDetail();
            // The click target no longer exists after the rebuild above; refocus
            // its replacement so keyboard/switch users aren't dropped. IsChecked
            // is re-derived from _selectedZone on rebuild, so re-clicking the
            // selected part can't leave it unchecked with no selection.
            _zoneButtons.GetValueOrDefault(zoneId)?.Focus();
        };
        _zoneButtons[zoneId] = btn;
    }

    // Card view state: the one mapping open for editing; -1 = all cards closed.
    int _expandedMapping = -1;

    // Below this the sentence needs two lines. Picked from the tightest real
    // case: the smallest window the app allows, in raw token style, where the
    // one-line version left the names nothing but an ellipsis.
    const double NarrowCardWidth = 470;
    bool _narrowCards;

    // Width 0 is "not laid out yet", which reads as roomy: the first build
    // happens before the panel has a size, and the resize below corrects it.
    // No hysteresis needed, the scroll bar here floats over the content, so a
    // rebuild cannot change the width that decided it.
    bool NarrowCards(double width) => width > 0 && width < NarrowCardWidth;

    // Cards are per view: Device View opens as sentences, Rows View opens as
    // the sheet it is.
    bool CardsHere => _deviceView ? _settings.DeviceCards : _settings.RowCards;

    void UpdateCardViewButton()
    {
        CardViewButton.Content = CardsHere ? Strings.Main_DetailedEditor : Strings.Main_SimpleCards;
        AutomationProperties.SetName(CardViewButton, CardsHere
            ? Strings.Main_MappingsReadAsSimpleSentence
            : Strings.Main_MappingsShowTheDetailedEditor);
    }

    // One mapping as a plain sentence: "Press X when you lip, as normal." or
    // "Lip to X as normal." The output and inputs wear their column colors,
    // the note reads as a muted second line, and a click opens the detailed
    // editor for just this mapping. The handle on the left selects and drags;
    // there is no second reorder control competing with the sentence.
    // Hairline rules between the sentence cells, so a column of cards reads
    // as a table you scan down instead of phrases floating in a box. The last
    // filled column and row skip theirs: the card's own outline is that edge,
    // and a rule past the last cell would hang in space on a mapping with no
    // "as" behavior.
    static Control RuleGrid(Grid g)
    {
        int lastCol = 0, lastRow = 0;
        foreach (var c in g.Children)
        {
            lastCol = Math.Max(lastCol, Grid.GetColumn(c) + Grid.GetColumnSpan(c) - 1);
            lastRow = Math.Max(lastRow, Grid.GetRow(c) + Grid.GetRowSpan(c) - 1);
        }
        foreach (var child in g.Children.ToList())
        {
            int col = Grid.GetColumn(child), row = Grid.GetRow(child);
            int colSpan = Grid.GetColumnSpan(child), rowSpan = Grid.GetRowSpan(child);
            g.Children.Remove(child);
            var cell = new Border
            {
                Child = child,
                Padding = new Avalonia.Thickness(3, 2),
                BorderThickness = new Avalonia.Thickness(0, 0,
                    col + colSpan - 1 < lastCol ? 1 : 0,
                    row + rowSpan - 1 < lastRow ? 1 : 0),
            };
            BindBrush(cell, Border.BorderBrushProperty, "SurfaceSubtle");
            Grid.SetColumn(cell, col); Grid.SetRow(cell, row);
            Grid.SetColumnSpan(cell, colSpan); Grid.SetRowSpan(cell, rowSpan);
            g.Children.Add(cell);
        }
        var box = new Border
        {
            Child = g,
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(4),
        };
        BindBrush(box, Border.BorderBrushProperty, "SurfaceSubtle");
        return box;
    }

    Control SentenceCard(Zone zone, Binding b, int n)
    {
        Control Pill(string text, string tint, OutputVisual? visual = null, bool showText = true,
                     int duplicateCount = 0)
        {
            var pillContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = visual is { IsSelfDescribing: true } ? 5 : 0,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (visual is { IsSelfDescribing: true })
                pillContent.Children.Add(OutputVisuals.Render(visual, includeLabel: false, compact: true));
            if (showText)
                pillContent.Children.Add(new TextBlock
                {
                    Text = text, FontSize = Size("BodySize"), FontWeight = FontWeight.SemiBold,
                    // Wrap, not trim: trimming does not make a control ask for
                    // less room, so the pill kept its full width, pushed the
                    // card past the 30% detail panel and the edge cut it off.
                    // Wrapping makes the card taller, which the panel scrolls.
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            // Keep the count inside the sentence pill. Wrapping the pill in a
            // second grid put its text to the left of the count and broke the
            // centred columns that make narrow cards readable.
            if (DuplicateChip(duplicateCount) is { } duplicate)
                pillContent.Children.Add(duplicate);
            var bd = new Border
            {
                CornerRadius = new Avalonia.CornerRadius(4),
                Padding = new Avalonia.Thickness(7, 2),
                Margin = new Avalonia.Thickness(0, 2), VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = pillContent,
            };
            BindBrush(bd, Border.BackgroundProperty, tint);
            ToolTip.SetTip(bd, text);
            AutomationProperties.SetName(bd, text);
            return bd;
        }
        // The joining words are grey and small on purpose: they are the same on
        // every card, so they should read as grammar and leave the width to the
        // names, which are the part you are actually scanning for.
        Control Word(string text, bool left = false) => new TextBlock
        {
            Text = text, FontSize = Size("SmallSize"), Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(5, 2),
            // Stacked lines read down a left edge; a single line does not care.
            HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Stretch,
        };

        // The same Words button styles as everywhere else in device view.
        // A named row reads by its name, except in the raw token style, where
        // the point is to see exactly what the file holds.
        string output = b.ActionName.Length > 0 && _labelStyle != 0 ? b.ActionName
            : b.Output.Length > 0 ? TokenLabel(b.Output)
            : Strings.Main_NothingYet;
        // A settings row has no inputs: its column C is the value. Reading it
        // as "50 presses mouse_speed" was backwards, and it is the sentence a
        // screen reader user hears before deciding whether to open the row.
        bool setting = IsModePreferenceOverride(b);
        var inputs = b.Inputs.Count > 0
            ? b.Inputs.Select(i => _labelStyle == 0 ? i : StripInput(i, zone.Id)).ToList()
            : new List<string> { setting ? Strings.Main_NoValue : Strings.Main_NoInput };
        // The words the pills sit between. The spoken sentence was fixed to say
        // "set X to 50" and the visible one was left saying "Press mouse_speed
        // when you 50", so the card on screen and the card in a screen reader
        // disagreed about the same row. Same fix, other direction.
        string verb = setting ? Strings.Main_SetVerb : Strings.Main_PressVerb;
        string joiner = setting ? Strings.Main_ToJoiner : Strings.Main_WhenYou;
        string func = _labelStyle == 0 ? b.Function : b.Function.Replace('_', ' ');
        bool inputFirst = !setting && _settings.CardSentenceStyle == "InputToOutput";

        // Every card uses the same column widths, so the outputs line up under
        // each other and so do the inputs: reading a list of cards is then
        // reading down two columns, not chasing text across ragged lines. The
        // shares are fixed (not Auto) precisely so one long name in one card
        // cannot shift the card below it. In a narrow panel the sentence takes
        // two lines instead of squeezing every name down to an ellipsis.
        var line = _narrowCards
            ? inputFirst
                ? new Grid
                {
                    // Keep the requested sentence together: input, "to",
                    // output. The behavior drops to a quiet second line.
                    ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
                    RowDefinitions = new RowDefinitions("Auto,Auto"),
                }
                : new Grid
                {
                    // Both shares, not Auto for the words: an Auto column is
                    // measured against infinity, so in a language whose "when
                    // you" is half again as long the words took the width and
                    // the pill beside them hung off the edge of the card.
                    ColumnDefinitions = new ColumnDefinitions("*,*"),
                    RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
                }
            : inputFirst
                ? new Grid { ColumnDefinitions = new ColumnDefinitions("3*,Auto,3*,Auto,3*") }
                : new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,4*,Auto,3*,4*") };
        void Cell(Control c, int col, int row = 0)
        {
            Grid.SetColumn(c, col); Grid.SetRow(c, row);
            line.Children.Add(c);
        }

        // Centered in their columns, across and down: the pills read as tidy
        // stacks instead of ragged edges, and a card with two inputs keeps them
        // on its middle line rather than hanging from the top.
        var inputPills = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        for (int i = 0; i < inputs.Count; i++)
        {
            // "then", not "and": several inputs on one row are done one after
            // the other, and the card's spoken sentence says so. Leaving the
            // pills saying "and" only moved the contradiction from the screen
            // reader to the screen.
            if (i > 0) inputPills.Children.Add(Word("then"));
            var token = i < b.Inputs.Count ? b.Inputs[i] : "";
            inputPills.Children.Add(Pill(inputs[i], InputTint,
                duplicateCount: _dupes.Input(token)));
        }

        // Outputs use the same generated visual language in the concise card
        // as they do in the editor: controller face buttons, D-pad directions,
        // and keyboard keycaps remain recognizable at a glance.
        var outputVisual = b.Output.Length > 0
            ? VisualFor(b.Output)
            : null;
        // RequiresTextLabel is the third case: a mouse silhouette and a moving
        // thumb stick draw the control but not which way it goes, so those keep
        // their words here too. Without it left_joy_left and left_joy_right
        // were the same picture with nothing beside it.
        var showOutputText = outputVisual is null
            || !outputVisual.IsSelfDescribing
            || outputVisual.RequiresTextLabel
            || b.ActionName.Length > 0;
        Control OutputPill() => Pill(output, OutputTint, outputVisual, showOutputText,
            _dupes.Output(b.Output));

        if (_narrowCards && inputFirst)
        {
            inputPills.HorizontalAlignment = HorizontalAlignment.Left;
            Cell(inputPills, 0, row: 0);
            Cell(Word(Strings.Main_ToJoiner), 1, row: 0);
            var outputPill = OutputPill();
            outputPill.HorizontalAlignment = HorizontalAlignment.Left;
            Cell(outputPill, 2, row: 0);
            if (func.Length > 0)
            {
                // Keep "as normal" as one little phrase. Putting the pill in
                // the grid's second column made it float halfway across the
                // card when the output column had spare room.
                var asLine = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 0,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { Word(Strings.Main_AsJoiner, left: true) },
                };
                var funcPill = Pill(func, FunctionTint);
                funcPill.HorizontalAlignment = HorizontalAlignment.Left;
                asLine.Children.Add(funcPill);
                Cell(asLine, 0, row: 1);
                Grid.SetColumnSpan(asLine, 3);
            }
        }
        else if (_narrowCards)
        {
            // One phrase per line, words left, pills sharing one centered
            // column. Everything still lines up, and a long name has the whole
            // card to itself instead of a quarter of it.
            Cell(Word(verb), 0); Cell(OutputPill(), 1);
            Cell(Word(joiner, left: true), 0, row: 1); Cell(inputPills, 1, row: 1);
            if (func.Length > 0)
            { Cell(Word(Strings.Main_AsJoiner, left: true), 0, row: 2); Cell(Pill(func, FunctionTint), 1, row: 2); }
        }
        else if (inputFirst)
        {
            Cell(inputPills, 0);
            Cell(Word(Strings.Main_ToJoiner), 1);
            Cell(OutputPill(), 2);
            if (func.Length > 0)
            {
                Cell(Word(Strings.Main_AsJoiner), 3);
                Cell(Pill(func, FunctionTint), 4);
            }
        }
        else
        {
            Cell(Word(verb), 0);
            Cell(OutputPill(), 1);
            Cell(Word(joiner), 2);
            Cell(inputPills, 3);
            if (func.Length > 0)
            {
                // "as" keeps its own width so it lands in the same place on
                // every card; the pill takes the rest and trims if it must.
                var asFunc = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                asFunc.Children.Add(Word(Strings.Main_AsJoiner));
                var funcPill = Pill(func, FunctionTint);
                funcPill.HorizontalAlignment = HorizontalAlignment.Left; // sits against "as", not adrift
                Grid.SetColumn(funcPill, 1);
                asFunc.Children.Add(funcPill);
                Cell(asFunc, 4);
            }
        }

        var body = new StackPanel { Spacing = 4, Children = { RuleGrid(line) } };
        var note = _file!.GetCell(b.Row, NoteColumn);
        if (note.Length > 0)
            body.Children.Add(new TextBlock
            {
                Text = string.Format(CultureInfo.CurrentCulture, Strings.Main_NoteSummary,
                    Strings.Main_NoteLabel, note),
                FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
            });

        var open = new Button
        {
            Content = body, Classes = { "quiet" },
            Padding = new Avalonia.Thickness(10, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // Stretch, not Left: the sentence grid needs the card's full width
            // for its column shares to come out the same on every card.
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        ToolTip.SetTip(open, Strings.Main_EditMappingOrAddNote);
        // "and" would say a chord, and several inputs on one row are a sequence
        // in time: the device matches them against the last inputs used, newest
        // first, so they have to be done one after the other. The tooltip and
        // the help already say that. A screen reader user was still being told
        // the opposite, which is the one place it costs the most.
        AutomationProperties.SetName(open, setting
            ? string.Format(CultureInfo.CurrentCulture, Strings.Main_SettingRowSpoken, n, b.Output,
                _file!.GetCell(b.Row, 2).Trim() is { Length: > 0 } v ? v : Strings.Main_NothingYet)
            : inputFirst
                ? string.Format(CultureInfo.CurrentCulture,
                    Strings.Main_MappingInputFirstSpoken,
                    n, string.Join(Strings.Main_ThenJoin, inputs), output,
                    func.Length > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.Main_AsFunction, func) : "")
                : string.Format(CultureInfo.CurrentCulture,
                    inputs.Count > 1 ? Strings.Main_MappingRowSpokenInOrder : Strings.Main_MappingRowSpoken,
                    n, output, string.Join(Strings.Main_ThenJoin, inputs),
                    func.Length > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.Main_AsFunction, func) : ""));
        open.Click += (_, _) =>
        {
            _expandedMapping = b.Row;
            if (_deviceView) BuildZoneDetail(); else RebuildRows();
            AfterLayout(() =>
            {
                if (_cellBorders.TryGetValue($"C{b.Row}", out var border))
                { border.BringIntoView(); (border.Child as Control)?.Focus(); }
            });
        };

        // 40x40 is the floor for a click target here (see Button.icon); the
        // tester found the old 24px-wide strip too small to hit.
        var dragIcon = Glyph("IconDrag", "TextSecondary");
        dragIcon.Width = dragIcon.Height = 24;
        var handleBox = new Border
        { Child = dragIcon, Padding = new Avalonia.Thickness(10), BorderThickness = new Avalonia.Thickness(0, 0, 1, 0) };
        BindBrush(handleBox, Border.BorderBrushProperty, "SurfaceSubtle");
        var handle = WireDragHandle(handleBox, b, string.Format(CultureInfo.CurrentCulture, Strings.Main_MappingNumber, n));

        // Keep the sentence's full-width grid. The reorder pair overlays the
        // quiet right edge of the card instead of stealing a whole layout
        // column and making long sequences wrap earlier than they should.
        var p = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        p.Tag = "mapping-card";
        BindBrush(p, Panel.BackgroundProperty, "Surface");
        p.Children.Add(handle);
        Grid.SetColumn(open, 1);
        p.Children.Add(open);

        WireRowDrop(p, b);
        // Same menu as a list row: copy, move and delete already know which
        // view they are in, so a mapping card gets the gesture for free.
        WireRowMenu(p, b);
        _rowPanels[b.Row] = p;
        PaintRow(b.Row);
        return p;
    }

    void BuildZoneDetail()
    {
        // Read the width now rather than trust the last resize: the first build
        // can land before the panel has ever been measured.
        _narrowCards = NarrowCards(ZoneDetailPanel.Bounds.Width);
        ZoneDetailPanel.Children.Clear();
        _rowPanels.Clear(); // device view owns the selection targets while visible
        _dupes = DuplicateUses.In(CurrentSheet?.Bindings);
        var zone = AllZones.FirstOrDefault(z => z.Id == _selectedZone);
        if (zone is null)
        {
            ZoneDetailPanel.Children.Add(new TextBlock
            {
                Text = Strings.Main_NothingSelectedNNPickA,
                FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
            });
            RepaintSelection();
            return;
        }

        var byZone = BindingsByZone();
        byZone.TryGetValue(zone.Id, out var bindings);

        int mappingCount = bindings?.Count ?? 0;
        var zoneTitle = new TextBlock
        {
            Text = zone.Title,
            FontSize = Size("SectionSize"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetLiveSetting(zoneTitle, AutomationLiveSetting.Polite);

        // Keep the part name as the heading. The count is supporting context,
        // not part of the title, and the one-click explanation belongs behind
        // the same question-mark pattern used for modes.
        var help = new Button { Classes = { "icon", "quiet" }, Content = "?" };
        ToolTip.SetTip(help, zone.Title);
        AutomationProperties.SetName(help, zone.Title);
        help.Click += (_, _) => ShowInfoFlyout(help, zone.Title, zone.Blurb);

        var count = new TextBlock
        {
            Text = mappingCount.ToString(CultureInfo.CurrentCulture),
            FontSize = Size("SmallSize"), VerticalAlignment = VerticalAlignment.Center,
        };
        if (mappingCount == 0) count.Classes.Add("muted");
        else BindBrush(count, TextBlock.ForegroundProperty, "AccentText");

        var meta = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { help, count },
        };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(zoneTitle);
        Grid.SetColumn(meta, 1);
        heading.Children.Add(meta);
        ZoneDetailPanel.Children.Add(heading);

        // The two zones that live on the back of the case get the panel drawn
        // out, because the numbering is the whole problem: nothing on the
        // hardware says which jack is digital_in_8.
        if (zone.Id is "jacks" or "other") ZoneDetailPanel.Children.Add(BackPanelGuide());

        if (bindings is { Count: > 0 })
        {
            var zoneInputs = Vocab.AllInputs.Where(i => ZoneOf(i) == zone.Id).OrderBy(GroupRank).ThenBy(x => x).ToList();
            bool cards = _settings.DeviceCards;
            int n = 0;
            foreach (var b in bindings)
            {
                n++;
                // Card mode: a closed mapping is one readable sentence. Only
                // the expanded one (at most one, accordion style) gets the
                // full editor below.
                if (cards && b.Row != _expandedMapping)
                { ZoneDetailPanel.Children.Add(SentenceCard(zone, b, n)); continue; }
                // One compact card per mapping. A header line carries the number
                // and a small remove button; the body is three aligned label|field
                // rows ("When you / Press / As") so a mapping reads like a short
                // sentence instead of a tall stack of separate labelled boxes.
                var body = new StackPanel { Spacing = 6 };

                // The header is just the actions, right-aligned: Done (card
                // mode only) and a big trash icon. The card already says
                // which mapping this is, a "Mapping N" label repeated it.
                var header = new StackPanel
                { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
                var delIcon = Glyph("IconDelete", "Error");
                delIcon.Width = delIcon.Height = 32; // double the usual 16, per the tester
                var del = new Button { Classes = { "danger", "quiet" }, Padding = new Avalonia.Thickness(8, 2), Content = delIcon };
                ToolTip.SetTip(del, Strings.Main_RemoveThisMapping);
                // ShortInput reads column C, which on a settings row is the
                // value, so the destructive control in the card announced
                // "Remove the 50 mapping" while the card above it had already
                // been fixed to call the row a setting.
                AutomationProperties.SetName(del, IsModePreferenceOverride(b)
                    ? string.Format(CultureInfo.CurrentCulture, Strings.Main_RemoveTheBOutputSetting, b.Output)
                    : string.Format(CultureInfo.CurrentCulture, Strings.Main_RemoveTheShortInputZoneB, ShortInput(zone, b)));
                del.Click += (_, _) =>
                {
                    int deletedIndex = bindings!.IndexOf(b);
                    // The card this trash lives in, measured before the rebuild
                    // destroys it, so the cards below can settle into its place.
                    var card = del.FindAncestorOfType<Border>();
                    double gap = card is not null ? card.Bounds.Height + 14 : 0;
                    if (card is not null) GhostRowAway(card, ZoneOverlay); // snapshot while still attached
                    _file!.DeleteRow(b.Row);
                    BuildDeviceView(); BuildZoneDetail(); RefreshIssues();
                    // The heading stays above the cards; its explanation is in
                    // the help popup rather than taking a permanent second row.
                    int firstMapping = zone.Id is "jacks" or "other" ? 2 : 1;
                    AnimateGapClose(ZoneDetailPanel, firstMapping + deletedIndex, gap);
                    FocusZoneDetailSibling(zone.Id, deletedIndex);
                };
                if (cards)
                {
                    // The way back to the sentence card, next to the trash.
                    var done = new Button { Content = Strings.Main_Done, Classes = { "quiet" }, Padding = new Avalonia.Thickness(12, 2) };
                    AutomationProperties.SetName(done, string.Format(CultureInfo.CurrentCulture, Strings.Main_CloseTheEditorForMapping, n));
                    done.Click += (_, _) => { _expandedMapping = -1; BuildZoneDetail(); };
                    header.Children.Add(done);
                }
                header.Children.Add(del);
                body.Children.Add(header);

                // ---- "When you": one aligned row per input, each removable ----
                // A mode row whose output is a setting name has no inputs at
                // all: the device skips column B and reads column C as the
                // value with a bare atoi. List View learned this first, and
                // this is the view the app actually opens in.
                if (IsModePreferenceOverride(b))
                {
                    var prefDef = Definition(b.Output);
                    var prefValue = _file!.GetCell(b.Row, 2);
                    bool prefTyped = prefDef is not null && CanRepresent(prefDef, prefValue, 2);
                    body.Children.Add(Labeled(Strings.Main_SettingLabel, OutputPicker(b, OutputsFor(CurrentSheet!),
                        Strings.Main_SettingChangedByThisRow, OutputTint)));
                    body.Children.Add(Labeled(Strings.Main_SetItTo, PrefsValueCell(b, prefTyped ? prefDef : null, 2)));
                    if (prefDef is not null
                        && PreferenceInfoLine(b, prefDef, prefTyped ? _cellBorders.GetValueOrDefault($"C{b.Row}") : null, 2) is { } prefInfo)
                        body.Children.Add(prefInfo);
                    body.Children.Add(Labeled(Strings.Main_NoteLabel, NoteBox(b.Row, NoteColumn,
                        Strings.Main_NoteForThisRowSaved)));
                    body.Children.Add(ScopeBanner(ModeScope,
                        Strings.Main_ThisRowSetsAQuadStick));
                    ZoneDetailPanel.Children.Add(MappingCard(body));
                    continue;
                }

                var inputsBox = new StackPanel { Spacing = 6 };
                int inputCount = Math.Max(1, b.Inputs.Count);
                Grid? lastInputRow = null;
                for (int i = 0; i < inputCount && i < 8; i++)
                {
                    // Third column holds the add button on the last row, so the
                    // trash icons stay in one line down the card.
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
                    // Inputs can sit in any of columns C..J with gaps; the
                    // editor must write to each input's REAL column, or an
                    // edit lands on a blank cell and duplicates the input.
                    int col = i < b.InputCols.Count ? b.InputCols[i] : FirstFreeInputColumn(b);
                    var value = i < b.Inputs.Count ? b.Inputs[i] : "";
                    var name = string.Format(CultureInfo.CurrentCulture, Strings.Main_InputI1ForThis, i + 1, zone.Display);
                    // The first input belongs to the part you are looking at, so
                    // a short dropdown covers it. Inputs after it can be any
                    // token on the device, which needs the searchable picker.
                    var inputBox = i == 0 && zoneInputs.Count > 0
                        ? TokenField(b.Row, col, value, zoneInputs,
                            t => InputOptionLabel(t, zone.Id), name, InputTint)
                        : DeviceInputPicker(b.Row, col, value, name, zone.Id);
                    var markedInput = WithDuplicateMark(inputBox, _dupes.Input(value));
                    Grid.SetColumn(markedInput, 0);
                    row.Children.Add(markedInput);
                    // Every committed input gets a trash. Removing the last one
                    // leaves an empty box on purpose. That IS the "no input" state.
                    if (i < b.Inputs.Count)
                    {
                        int idx = i;
                        var rmv = IconButton("IconDelete", string.Format(CultureInfo.CurrentCulture, Strings.Main_RemoveThisInputFromMapping, n));
                        rmv.Margin = new Avalonia.Thickness(8, 0, 0, 0);
                        rmv.Click += (_, _) =>
                        {
                            _file!.RemoveInput(b.Row, idx);
                            BuildDeviceView(); BuildZoneDetail(); RefreshIssues();
                            SayIfNothingFiresIt(b.Row);
                            FocusZoneDetailSibling(zone.Id, bindings!.IndexOf(b));
                        };
                        Grid.SetColumn(rmv, 2);
                        row.Children.Add(rmv);
                    }
                    inputsBox.Children.Add(row);
                    lastInputRow = row;
                }
                if (inputCount < 8)
                {
                    var addInput = IconButton("IconAdd", string.Format(CultureInfo.CurrentCulture, Strings.Main_AddAnotherInputToMapping, n));
                    addInput.Margin = new Avalonia.Thickness(8, 0, 0, 0);
                    ToolTip.SetTip(addInput, Strings.Main_AddAnotherInput);
                    int nextCol = FirstFreeInputColumn(b);
                    // The add button rides in the last input row, left of that
                    // row's trash, instead of hanging under the rows on its own.
                    void MoveAddTo(Grid row)
                    {
                        (addInput.Parent as Grid)?.Children.Remove(addInput);
                        Grid.SetColumn(addInput, 1);
                        row.Children.Add(addInput);
                    }
                    addInput.Click += (_, _) =>
                    {
                        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
                        var newBox = DeviceInputPicker(b.Row, nextCol, "",
                            string.Format(CultureInfo.CurrentCulture, Strings.Main_ExtraInputForMappingN, n), zone.Id);
                        Grid.SetColumn(newBox, 0);
                        row.Children.Add(newBox);
                        // The new row isn't committed until a value is picked, so its
                        // trash just drops the row instead of editing the file.
                        var rmv = IconButton("IconDelete", string.Format(CultureInfo.CurrentCulture, Strings.Main_RemoveThisEmptyInputFrom, n));
                        rmv.Margin = new Avalonia.Thickness(8, 0, 0, 0);
                        rmv.Click += (_, _) =>
                        {
                            // This row carries the add button, so hand it back to
                            // the row above before the row goes away.
                            int at = inputsBox.Children.IndexOf(row);
                            if (at > 0 && inputsBox.Children[at - 1] is Grid prev) MoveAddTo(prev);
                            inputsBox.Children.Remove(row);
                        };
                        Grid.SetColumn(rmv, 2);
                        row.Children.Add(rmv);
                        inputsBox.Children.Add(row);
                        MoveAddTo(row);
                        AnimateIn(row);
                        nextCol++;
                        if (nextCol >= 2 + 8) addInput.IsVisible = false;
                        (newBox as Border)?.Child?.Focus();
                    };
                    MoveAddTo(lastInputRow!);
                }
                body.Children.Add(Labeled(Strings.Main_WhenYou2, inputsBox));

                // ---- "Press" (game button) and "As" (how it presses) ----
                body.Children.Add(Labeled(Strings.Main_PressVerb, WithDuplicateMark(
                    OutputPicker(b, OutputsFor(CurrentSheet!),
                        string.Format(CultureInfo.CurrentCulture, Strings.Main_GameButtonPressedByShortInput, ShortInput(zone, b)), OutputTint),
                    _dupes.Output(b.Output))));
                body.Children.Add(Labeled(Strings.Main_AsLabel, FunctionCombo(b, zone)));
                body.Children.Add(Labeled(Strings.Main_NoteLabel, NoteBox(b.Row, NoteColumn, Strings.Main_NoteForThisMappingSaved)));

                ZoneDetailPanel.Children.Add(MappingCard(body));
            }
        }
        else
            ZoneDetailPanel.Children.Add(new TextBlock
            { Text = Strings.Main_NothingMappedHereYet, FontSize = Size("BodySize"), Classes = { "muted" } });

        // What this part can still do. The toolbar list answers "what is free
        // anywhere"; this answers "what is free right here", which is the
        // question you actually have while looking at one part. Each one is a
        // full-width button rather than a chip because these are aim targets,
        // and this app gets aimed at with a mouth stick.
        if (zone.Id != "unset")
        {
            // Ordered by socket, top of the case down, so the first thing
            // offered on the jacks is the top jack rather than digital_in_1.
            // The USB-A data pins sort last: nothing plugs into them.
            var freeHere = UnusedInputs(zone.Id).Where(i => ZoneOf(i) == zone.Id)
                .OrderBy(JackRank).ToList();
            if (freeHere.Count > 0)
            {
                ZoneDetailPanel.Children.Add(new TextBlock
                {
                    Text = string.Format(CultureInfo.CurrentCulture, Strings.Main_NotUsedYetOnThis, freeHere.Count),
                    FontSize = Size("SmallSize"), Classes = { "secondary" },
                    Margin = new Avalonia.Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
                });
                foreach (var token in freeHere)
                {
                    var line = new DockPanel();
                    var raw = new TextBlock
                    {
                        Text = token, Classes = { "muted" }, FontSize = Size("SmallSize"),
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(8, 0, 0, 0),
                    };
                    DockPanel.SetDock(raw, Dock.Right);
                    line.Children.Add(raw);
                    line.Children.Add(new TextBlock
                    {
                        Text = ChipLabel(token, zone.Id), FontSize = Size("BodySize"),
                        VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
                    });
                    var free = new Button
                    {
                        Content = line, Classes = { "quiet" },
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    };
                    AutomationProperties.SetName(free, string.Format(CultureInfo.CurrentCulture, Strings.Main_MapTokenToANew, token, zone.Title));
                    ToolTip.SetTip(free, string.Format(CultureInfo.CurrentCulture, Strings.Main_StartANewMappingOn, token));
                    var t = token;
                    free.Click += (_, _) => AddMappingWithInput(t, inputWasChosen: true);
                    ZoneDetailPanel.Children.Add(free);
                }
            }
            // A plus, not a sentence. This is the one thing you press on a part
            // you are looking at, and the words competed with the list of what
            // is free above it. The sentence stays on the name a screen reader
            // reads and in the tooltip, so nothing is lost by dropping it.
            var add = new Button
            {
                Content = Glyph("IconAdd", "OnAccent"),
                Classes = { "primary", "command" },
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
            };
            var addName = string.Format(CultureInfo.CurrentCulture, Strings.Main_AddANewMappingFor, zone.Title);
            AutomationProperties.SetName(add, addName);
            ToolTip.SetTip(add, addName);
            add.Click += (_, _) => AddMappingWithInput(zone.DefaultInput);
            ZoneDetailPanel.Children.Add(add);
        }
        RepaintSelection(); // the bars follow whatever rebuilt here
    }

    // Column A is the output, C is the input. A chosen input needs an output
    // picked, so focus A. A guessed input needs changing, so focus C. This is
    // shared by a part's free-input list and the global unused-input picker;
    // the latter must preserve Rows View instead of sending someone to the
    // device diagram merely for choosing "North".
    void AddMappingWithInput(string input, bool inputWasChosen = false)
    {
        if (_file is null || CurrentSheet is null) return;
        int newRow = _file.AddBindingRow(CurrentSheet);
        _file.SetCell(newRow, 2, input);
        _expandedMapping = newRow; // a brand new mapping opens ready to edit
        if (!DeviceContainer.IsVisible)
        {
            RebuildRows();
            RefreshIssues();
            AfterLayout(() => FocusNewMappingCell(newRow, inputWasChosen));
            return;
        }
        // Follow the input to its own part, so seeding a combo token from the
        // combo card does not leave the detail panel showing a different one.
        _selectedZone = ZoneOf(input);
        BuildDeviceView(); BuildZoneDetail(); RefreshIssues();
        // Take the user to the mapping they just created (mirrors AddRow in List View).
        // The cells are ComboBox-backed (TokenField), so focus them as Controls.
        AfterLayout(() => FocusNewMappingCell(newRow, inputWasChosen));
    }

    void FocusNewMappingCell(int row, bool inputWasChosen)
    {
        if (!_cellBorders.TryGetValue($"{(inputWasChosen ? 'A' : 'C')}{row}", out var newBorder)) return;
        newBorder.BringIntoView();
        (newBorder.Child as Control)?.Focus();
        // Rise the whole new mapping card in, not just the cell. Rows view
        // deliberately has no card container to animate.
        var card = newBorder.GetVisualAncestors().OfType<Border>()
            .FirstOrDefault(x => x.Parent == ZoneDetailPanel);
        if (card is not null) AnimateIn(card);
    }

    public void LoadProfile(ProfileFile file) => OpenInEditor(file, savePath: null, ProfileSource.File);

    /// <summary>Open a profile from a path, in the editor, exactly as opening a
    /// file does. The agent window hands its result back through here rather
    /// than through anything of its own, so what it wrote is checked, validated
    /// and installed by the same screens as any other profile.</summary>
    public void OpenPath(string path) =>
        OpenInEditor(ProfileFile.Load(File.ReadAllText(path)), path, ProfileSource.File);

    /// <summary>Open what the agent wrote and go straight into installing it.
    ///
    /// Straight into the real install flow, not past it: it re-reads the file,
    /// refuses to install one with errors, asks which drive, and asks again
    /// before replacing the device's default profile. Skipping any of that to
    /// save a click would be skipping it on a device somebody depends on.</summary>
    public async Task OpenPathAndInstallAsync(string path)
    {
        OpenPath(path);
        await RunInstallFlowAsync();
    }

    /// <summary>Whether a QuadStick is plugged in right now. The agent asks so
    /// it only offers to install when there is something to install onto.</summary>
    public static bool DeviceIsConnected() => Device.FindCandidates().Count > 0;

    /// <summary>The file the editor is on, or null on the home screen. The
    /// agent asks so that "make sprint a hard puff" changes the profile in front
    /// of them instead of building a new one.</summary>
    public string? CurrentProfilePath => _savePath;

    // Opening a real path is what feeds the recents list, so a test needs the
    // path seam, not just LoadProfile's in-memory one.
    public void OpenPathForPreview(string path) => OpenPath(path);

    public void ShowHomeForPreview() => ShowHome();

    public void SelectZoneForPreview(string zoneId)
    { _selectedZone = zoneId; BuildDeviceView(); BuildZoneDetail(); }

    internal string? SelectedZoneForPreview => _selectedZone;

    public void SetModelForPreview(int index)
    { ModelPicker.SelectedIndex = index; }

    public void SetDeviceViewForPreview(bool device) => SetDeviceView(device);

    public void CycleLabelStyleForPreview() => ToggleLabelStyle();

    public void ShowUnusedForPreview() => ShowUnusedInputs();

    public void AddRowForPreview() => AddRow();

    public void SelectSheetForPreview(int index) => SelectSheet(index);

    public ModeSheet? CurrentSheetForPreview => CurrentSheet;

    public void ShowProblemsForPreview()
    { if (!_problemsExpanded) ToggleProblems(); }

    // Async click/shortcut handlers are fire-and-forget; an unhandled disk
    // error would otherwise tear down the whole app.
    async Task GuardedAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Status(ex.Message, StatusKind.Error); }
    }

    async Task OpenAsync()
    {
        if (!await ConfirmLeaveAsync()) return; // opening discards unsaved work
        var picks = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Main_OpenQuadStickProfile,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(Strings.Main_QuadStickProfile) { Patterns = new[] { "*.csv", "*.xlsx" } },
            },
        });
        if (picks.Count == 0) return;
        var path = picks[0].Path.LocalPath;
        try
        {
            // A workbook is an import, not a file we own: it opens unsaved so
            // the first save writes a .csv instead of overwriting the .xlsx.
            if (path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                XlsxImport read;
                using (var stream = File.OpenRead(path)) read = Xlsx.Import(stream);
                var imported = ProfileFile.Load(read.Csv);
                if (imported.Document.Sheets.Count == 0)
                {
                    Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_Picks0NameNoProfileTabRead, picks[0].Name, NoProfileTab(read.Csv, read.Skipped)), StatusKind.Error);
                    return;
                }
                OpenInEditor(imported, savePath: null, ProfileSource.File);
                Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_ImportedModesImportedFromPicks, Modes(imported), picks[0].Name),
                    StatusKind.Ready);
                await ShowImportReviewAsync(imported, picks[0].Name, read.Skipped, read.Limitation,
                    renamed: read.Renamed);
                return;
            }
            OpenInEditor(ProfileFile.Load(await File.ReadAllTextAsync(path)), path, ProfileSource.File);
        }
        catch (InvalidDataException)
        { Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_CouldNotReadPicks0, picks[0].Name), StatusKind.Error); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_CouldNotOpenPicks0, picks[0].Name, ex.Message), StatusKind.Error); }
    }

    // Returns true only once the file has actually reached disk, so
    // ConfirmLeaveAsync can tell a real save from a cancelled picker or a
    // failed write and keep the user's work on screen either way.
    async Task<bool> SaveAsync()
    {
        if (_file is null) return false;

        // Save never writes to the QuadStick itself. Only Install does: it is
        // the one path with validate, backup, readback, and the default.csv
        // confirmation. Without this gate, opening default.csv from its own
        // device card and pressing Ctrl+S would overwrite the fallback file raw.
        if (_savePath is not null
            && Path.GetDirectoryName(_savePath) is string dir
            && Device.IsInstallTarget(dir))
        {
            Status(Strings.Main_ThisProfileLivesOnThe, StatusKind.Warning);
            _savePath = null; // fall through to Save As on the next save
            return false;
        }

        if (_savePath is null)
        {
            Directory.CreateDirectory(LibraryDir);
            var start = await StorageProvider.TryGetFolderFromPathAsync(LibraryDir);
            var pick = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Strings.Main_SaveProfileCSV,
                SuggestedFileName = BareName(_file.Document.CsvFileName) is { Length: > 0 } sug ? sug : "profile",
                SuggestedStartLocation = start,
                DefaultExtension = "csv",
            });
            if (pick is null) return false;
            // Type a bare name in the macOS save panel and it hands the path
            // back without the extension, DefaultExtension or not. The device
            // only reads .csv and home only lists .csv, so a profile saved as
            // "mygame" would look like it vanished.
            var picked = pick.Path.LocalPath;
            if (!picked.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) picked += ".csv";
            var pickedDir = Path.GetDirectoryName(picked);
            if (pickedDir is not null && Device.IsInstallTarget(pickedDir))
            {
                Status(Strings.Main_ThatFolderIsAQuadStick, StatusKind.Warning);
                return false;
            }
            _savePath = picked;
        }

        string text;
        try
        {
            _file.NormalizeForDeviceCsv(); // saved files match installed files byte for byte
            SyncSheetIdentity(_savePath);  // stamp C1 before the bytes are written
            text = _file.ToCsvText();
            await Task.Run(() => ProfileFile.WriteAtomic(_savePath, text));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_CouldNotSaveExMessage, ex.Message), StatusKind.Error); return false; }
        _file.Dirty = false;
        RememberRecent(_savePath); // Save As invents a path that no open ever saw
        PersistDrafts();           // and gives an untitled profile's names somewhere to live
        RefreshEditor(); // header insertion shifted every row; BOTH views must rebind
        Telemetry.Track(TelemetryEvent.ProfileSaved);
        Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_SavedToSavePath, _savePath), StatusKind.Ready);
        // Local save is done. Push the exact bytes just written to the sheet in
        // the background; the save path never waits on the network.
        FireBackupPush(_savePath, text);
        return true;
    }

    Task ImportAsync() => ImportSheetsAsync(SheetsUrlBox.Text ?? "");

    // Sheets, not modes: preferences and infrared are sheets the device reads
    // on their own keywords, and counting them made an import of one mode plus
    // a preferences sheet announce itself as two modes.
    static string Modes(ProfileFile f)
    {
        int n = f.Document.Sheets.Count(s => s.Type == SheetType.ProfileName);
        return Plural.Of(n, "Count_Mode");
    }

    /// <summary>The import review. Every import ends here, clean or not: the
    /// user handed us a spreadsheet that already works on their QuadStick, and
    /// the app has to show what it made of it rather than leave a correct
    /// import looking like a broken one.
    /// <paramref name="limitation"/> is set when the import could not see the
    /// whole workbook, so a partial read is never called a clean one.
    /// <paramref name="dialogOwner"/> is whichever window is actually on top;
    /// a dialog owned by a MainWindow that a modal is already covering would
    /// open behind it.</summary>
    internal async Task ShowImportReviewAsync(ProfileFile file, string source,
        IReadOnlyList<SkippedTab> skipped, string? limitation = null, Window? dialogOwner = null,
        IReadOnlyList<TabRename>? renamed = null)
    {
        LastImportReview = (source, skipped, limitation, renamed ?? Array.Empty<TabRename>());
        await ShowImportReview(new ImportReviewWindow(this, file, source, skipped, limitation, renamed),
            dialogOwner ?? this);
    }

    /// <summary>How the review gets on screen. Tests swap it out, the same way
    /// the community window swaps out OpenUri: a modal dialog with nothing to
    /// close it would hang a headless run forever. The window's own behaviour
    /// is covered directly in ImportReviewWindowTests.</summary>
    internal Func<ImportReviewWindow, Window, Task> ShowImportReview =
        (dialog, owner) => dialog.ShowDialog(owner);

    /// <summary>What the last import handed the review, so a test can check the
    /// thing the review cannot check about itself: whether the import path told
    /// it the truth about how much of the workbook it actually saw.</summary>
    internal (string Source, IReadOnlyList<SkippedTab> Skipped, string? Limitation,
        IReadOnlyList<TabRename> Renamed)? LastImportReview;

    /// <summary>The one Sheets import. The pasted link on Home and a pick from
    /// the community catalog both land here, so the app has a single workbook
    /// conversion. Returns once the profile is open in the editor or the error
    /// is on screen. It never saves and never installs.
    /// <paramref name="onError"/> takes the failure message instead of Home when
    /// the caller is another window, so the words land where the user is
    /// looking.</summary>
    internal async Task ImportSheetsAsync(string pasted, HttpClient? http = null, Action<string>? onError = null,
        Window? dialogOwner = null)
    {
        var client = http ?? Http;
        void HomeError(string message)
        {
            if (onError is not null) { onError(message); return; }
            HomeStatusText.Text = message;
            HomeStatusText.IsVisible = true;
            SheetsUrlBox.Focus();
        }
        HomeStatusText.IsVisible = false;

        void Progress(string message)
        {
            if (onError is not null) return; // another window owns its own line
            HomeStatusText.Text = message;
            HomeStatusText.IsVisible = true;
        }

        if (!SheetsUrl.TryGetXlsxExportUrl(pasted, out var workbookUrl)
            || !SheetsUrl.TryGetCsvExportUrl(pasted, out var csvUrl))
        { HomeError(Strings.Main_ThatDoesNotLookLike); return; }
        try
        {
            Progress(Strings.Main_DownloadingTheSpreadsheet);
            // A sheet this app made is read with the app's own token. The link
            // the user pastes is most often the one they copied from this very
            // profile a second ago, and the anonymous export below only answers
            // for a sheet whose link sharing is on.
            string? ours = null;
            if (SheetsUrl.TryGetId(pasted, out var sheetId)
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

            if (text.TrimStart().StartsWith('<'))
            { HomeError(Strings.Main_GoogleReturnedAWebPage); return; }
            var imported = ProfileFile.Load(text);
            if (imported.Document.Sheets.Count == 0)
            { HomeError(NoProfileTab(text, skipped)); return; }
            // Tracked here, not on entry: a bad URL, an unshared sheet, and a
            // workbook with no profile tab all return above, and counting those
            // as "used the Sheets import" would make the feature look healthy
            // exactly when it is failing.
            Telemetry.Track(TelemetryEvent.FeatureUsed, AppFeature.SheetsImport);
            HomeStatusText.IsVisible = false; // the progress line has done its job
            OpenInEditor(imported, savePath: null, ProfileSource.Sheets);
            Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_ImportedModesImportedFromThe, Modes(imported)), StatusKind.Ready);
            // A published link hands back one tab and no way to ask for the
            // rest, so the review has to say that before it counts anything.
            // Without it, a profile missing four of its five modes would be
            // reported as a clean import, which is the worst thing this window
            // could ever say.
            await ShowImportReviewAsync(imported, Strings.Main_ThisSpreadsheet, skipped,
                wholeWorkbook ? tooLarge
                    : Strings.Main_ThisLinkIsAPublished,
                dialogOwner, renamed);
        }
        catch (InvalidDataException)
        { HomeError(Strings.Main_CouldNotReadThatSpreadsheet); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { HomeError(string.Format(CultureInfo.CurrentCulture, Strings.Main_CouldNotDownloadTheSheet,
                ex is TaskCanceledException ? Strings.Main_ConnectionTimedOut : ex.Message)); }
    }

    /// <summary>Why an import found no profile, in the words of what was
    /// actually there. "That spreadsheet has no profile tab" was the same
    /// sentence for an empty download, a sheet of somebody's notes, and this
    /// app's own share link, and the user could not tell which they had.</summary>
    internal static string NoProfileTab(string text, IReadOnlyList<SkippedTab> skipped)
    {
        var start = Strings.Main_AProfileTabStartsWith;
        // Asked before the empty check, because a workbook whose every tab was
        // passed over converts to nothing, and "came back empty" would send the
        // user looking at their connection instead of at cell A1.
        if (skipped.Count > 0)
            return string.Format(CultureInfo.CurrentCulture, Strings.Main_NoTabInThatSpreadsheet, Naming(skipped), start);

        if (text.Trim().Length == 0)
            return Strings.Main_ThatSpreadsheetCameBackEmpty;

        var a1 = Csv.Parse(text) is { Count: > 0 } grid && grid[0].Length > 0 ? grid[0][0].Trim() : "";
        return a1.Length == 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.Main_ThatSpreadsheetHasNoProfile, start)
            : string.Format(CultureInfo.CurrentCulture, Strings.Main_ThatSpreadsheetHasNoProfile2, Shortened(a1), start);
    }

    static string Naming(IReadOnlyList<SkippedTab> skipped) =>
        skipped.Count == 1
            ? string.Format(CultureInfo.CurrentCulture, Strings.Main_TheTabSkipped0Name, skipped[0].Name)
            : Strings.Main_TheseTabsWerePassedOver + string.Join(", ", skipped.Select(t => $"\"{t.Name}\"")) + ".";

    // A1 can hold a paragraph somebody pasted. Enough of it to recognise.
    static string Shortened(string value) => value.Length <= 60 ? value : value[..57] + "...";

    // Strip characters that are illegal in a file name and force a .csv
    // extension, so a template named "My FPS / v2" cannot escape TemplatesDir
    // or land without an extension the loader looks for.
    public static string SafeTemplateName(string name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return "";
        // Strip this platform's invalid chars, plus the path separators and
        // drive colon that are legal on macOS but break a synced file on
        // Windows. This app runs on both, so a template name must be safe on
        // both.
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':' }).ToHashSet();
        var cleaned = string.Concat(trimmed.Select(c => invalid.Contains(c) ? '_' : c));
        if (cleaned.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[..^4];
        return cleaned + ".csv";
    }

    async Task SaveAsTemplateAsync()
    {
        if (_file is null) { Status(Strings.Main_OpenOrCreateAProfile); return; }

        var suggested = Path.GetFileNameWithoutExtension(_file.Document.CsvFileName ?? Strings.Main_MyTemplate);
        var box = new TextBox { Text = suggested, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(box, Strings.Main_NameForThisTemplate);
        var save = new Button { Content = Strings.Main_SaveTemplate, MinWidth = 140, IsDefault = true };
        var cancel = new Button { Content = Strings.Main_Cancel, MinWidth = 140, IsCancel = true };
        var dialog = new Window
        {
            Title = Strings.Main_SaveAsTemplate,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = ZoomWrap(new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 16,
                MaxWidth = 480,
                Children =
                {
                    new TextBlock { Text = Strings.Main_SaveAsTemplate, FontWeight = FontWeight.Bold, FontSize = Size("SubheadSize"), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = Strings.Main_KeepsACopyYouCan, TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize") },
                    box,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { save, cancel } },
                },
            }, _uiScale),
        };
        var confirmed = false;
        void Confirm() { confirmed = true; dialog.Close(); }
        save.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => dialog.Close();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Confirm(); };
        await ShowDialogInShellAsync(dialog);
        if (!confirmed) return;

        var fileName = SafeTemplateName(box.Text ?? "");
        if (fileName.Length == 0) { Status(Strings.Main_ATemplateNeedsAName, StatusKind.Warning); return; }
        try
        {
            Directory.CreateDirectory(TemplatesDir);
            _file.NormalizeForDeviceCsv(); // templates match installed files byte for byte
            ProfileFile.WriteAtomic(Path.Combine(TemplatesDir, fileName), _file.ToCsvText());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_CouldNotSaveTheTemplate, ex.Message), StatusKind.Error); return; }
        RefreshEditor(); // NormalizeForDeviceCsv may have shifted rows
        Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_SavedTemplateFileNameFindIt, fileName), StatusKind.Ready);
    }

    async Task UseTemplateAsync()
    {
        void HomeError(string message)
        { HomeStatusText.Text = message; HomeStatusText.IsVisible = true; }
        HomeStatusText.IsVisible = false;

        var templates = Directory.Exists(TemplatesDir)
            ? Directory.GetFiles(TemplatesDir, "*.csv").OrderBy(Path.GetFileName).ToArray()
            : Array.Empty<string>();
        if (templates.Length == 0)
        { HomeError(Strings.Main_YouHaveNotSavedAny); return; }

        if (!await ConfirmLeaveAsync()) return; // opening discards unsaved work

        // Mutable so Rename/Delete can update the picker in place without
        // closing and reopening the dialog.
        var templatePaths = templates.ToList();
        var list = new ListBox
        {
            ItemsSource = templatePaths.Select(Path.GetFileNameWithoutExtension).ToList(),
            SelectedIndex = 0,
            MaxHeight = 320,
        };
        AutomationProperties.SetName(list, Strings.Main_YourSavedTemplates);
        void RefreshList(int selectIndex)
        {
            list.ItemsSource = templatePaths.Select(Path.GetFileNameWithoutExtension).ToList();
            list.SelectedIndex = selectIndex;
        }

        var open = new Button { Content = Strings.Main_UseTemplate, MinWidth = 140, IsDefault = true };
        var cancel = new Button { Content = Strings.Main_Cancel, MinWidth = 140, IsCancel = true };
        var rename = new Button { Content = Strings.Main_Rename, Classes = { "quiet" } };
        var delete = new Button { Content = Strings.Main_Delete, Classes = { "danger", "quiet" } };
        AutomationProperties.SetName(rename, Strings.Main_RenameSelectedTemplate);
        AutomationProperties.SetName(delete, Strings.Main_DeleteSelectedTemplate);
        var dialog = new Window
        {
            Title = Strings.Main_UseTemplate,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = ZoomWrap(new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 16,
                MaxWidth = 480,
                Children =
                {
                    new TextBlock { Text = Strings.Main_StartFromATemplate, FontWeight = FontWeight.Bold, FontSize = Size("SubheadSize"), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = Strings.Main_OpensAFreshCopyYou, TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize") },
                    list,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { rename, delete } },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { open, cancel } },
                },
            }, _uiScale),
        };
        var confirmed = false;
        void Confirm() { confirmed = true; dialog.Close(); }
        open.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => dialog.Close();
        list.DoubleTapped += (_, _) => Confirm();

        rename.Click += async (_, _) =>
        {
            var idx = list.SelectedIndex;
            if (idx < 0) { Status(Strings.Main_SelectATemplateToRename, StatusKind.Warning); return; }
            var oldPath = templatePaths[idx];
            var newName = await AskNameAsync(Strings.Main_RenameTemplate, Path.GetFileNameWithoutExtension(oldPath),
                "Rename", Strings.Main_NewNameForThisTemplate);
            if (newName is null) return;
            var fileName = SafeTemplateName(newName);
            if (fileName.Length == 0) { Status(Strings.Main_ATemplateNeedsAName, StatusKind.Warning); return; }
            var newPath = Path.Combine(TemplatesDir, fileName);
            if (!string.Equals(newPath, oldPath, StringComparison.Ordinal))
            {
                if (File.Exists(newPath))
                { Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_ATemplateNamedPathGetFileNameWithoutExtension, Path.GetFileNameWithoutExtension(fileName)), StatusKind.Warning); return; }
                try { File.Move(oldPath, newPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_CouldNotRenameTheTemplate, ex.Message), StatusKind.Error); return; }
                templatePaths[idx] = newPath;
            }
            RefreshList(idx);
            Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_RenamedTemplateToPathGetFileNameWithoutExtension, Path.GetFileNameWithoutExtension(fileName)), StatusKind.Ready);
        };

        delete.Click += async (_, _) =>
        {
            var idx = list.SelectedIndex;
            if (idx < 0) { Status(Strings.Main_SelectATemplateToDelete, StatusKind.Warning); return; }
            var targetPath = templatePaths[idx];
            var name = Path.GetFileNameWithoutExtension(targetPath);
            if (!await ConfirmAsync(string.Format(CultureInfo.CurrentCulture, Strings.Main_DeleteTemplateName, name),
                Strings.Main_ProfilesYouAlreadyMadeFrom))
                return;
            try { File.Delete(targetPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_CouldNotDeleteTheTemplate, ex.Message), StatusKind.Error); return; }
            templatePaths.RemoveAt(idx);
            if (templatePaths.Count == 0)
            {
                dialog.Close();
                HomeError(Strings.Main_YouHaveNotSavedAny);
                Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_DeletedTemplateName, name), StatusKind.Ready);
                return;
            }
            RefreshList(Math.Min(idx, templatePaths.Count - 1));
            Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_DeletedTemplateName, name), StatusKind.Ready);
        };

        await ShowDialogInShellAsync(dialog);
        if (!confirmed || list.SelectedIndex < 0) return;

        var path = templatePaths[list.SelectedIndex];
        try
        {
            // savePath null: the copy is unsaved, so Save prompts for a new
            // location and the template file is never overwritten.
            OpenInEditor(ProfileFile.Load(await File.ReadAllTextAsync(path)), savePath: null, ProfileSource.New);
            Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_StartedFromTemplatePathGetFileNameWithoutExtension, Path.GetFileNameWithoutExtension(path)), StatusKind.Ready);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { HomeError(string.Format(CultureInfo.CurrentCulture, Strings.Main_CouldNotOpenThatTemplate, ex.Message)); }
    }

    bool _closeConfirmed;

    // The box shows the profile name WITHOUT its .csv; the extension is ours
    // to add back when the name is committed to the file.
    static string BareName(string? name) =>
        (name ?? "").EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? name![..^4] : name ?? "";

    void CommitFileName()
    {
        if (_file is null) return;
        var v = BareName((FileNameBox.Text ?? "").Trim()); // typed extension is fine too
        if (v.Length == 0) { FileNameBox.Text = BareName(_file.Document.CsvFileName); return; }
        FileNameBox.Text = v;
        var full = v + ".csv";
        if (full == _file.Document.CsvFileName) return;
        _file.SetCell(_file.Document.FileNameCellRow, 0, full);
        Title = string.Format(CultureInfo.CurrentCulture, Strings.Main_QuadstickConfigManagerUnofficialV, v);
        RefreshIssues(); // bad names surface immediately as errors
    }

    // Returning true means it is safe to discard the open profile and
    // proceed. Save only earns that if SaveAsync actually reached disk; a
    // cancelled picker or a failed write must keep the user right where
    // they were, work intact.
    bool _confirmingLeave;

    async Task<bool> ConfirmLeaveAsync()
    {
        if (_file is not { Dirty: true }) return true;

        // A mouth stick or a switch can double-fire one press, and every shell
        // button and the window close come through here. The prompt already up
        // is the question being asked; a second press is not a second answer,
        // so it waits rather than stacking another prompt over the first.
        if (_confirmingLeave) return false;
        _confirmingLeave = true;
        try { return await AskToSaveAsync(); }
        finally { _confirmingLeave = false; }
    }

    async Task<bool> AskToSaveAsync()
    {
        var title = Strings.Main_SaveYourChanges;
        var message = Strings.Main_ThisProfileHasUnsavedChanges;
        var save = new Button { Content = Strings.Main_Save, MinWidth = 140, IsDefault = true };
        var dontSave = new Button { Content = Strings.Main_DonTSave, MinWidth = 140 };
        var cancel = new Button { Content = Strings.Main_Cancel, MinWidth = 140, IsCancel = true };
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = ZoomWrap(new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 16,
                MaxWidth = 480,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeight.Bold, FontSize = Size("SubheadSize"), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize") },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { save, dontSave, cancel } },
                },
            }, _uiScale),
        };
        var choice = "cancel";
        save.Click += (_, _) => { choice = "save"; dialog.Close(); };
        dontSave.Click += (_, _) => { choice = "dontsave"; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        await ShowDialogInShellAsync(dialog);

        switch (choice)
        {
            case "save": return await SaveAsync(); // a blocked or cancelled save must not leave
            case "dontsave":
                // An explicit "don't save" is a discard: drop the autosave
                // draft too, or the next launch offers back the exact work
                // the user just declined to keep.
                try { File.Delete(DraftPath); } catch { /* best effort */ }
                _draftedRevision = -1;
                return true;
            default: return false;
        }
    }

    void UndoEdit()
    {
        if (_file is null || !_file.Undo()) { Status(Strings.Main_NothingToUndo); return; }
        FileNameBox.Text = BareName(_file.Document.CsvFileName);
        RefreshEditor();
        Status(Strings.Main_ChangeUndone, StatusKind.Ready);
    }

    ModeSheet? CurrentSheet =>
        _file != null && _sheetIndex < _file.Document.Sheets.Count ? _file.Document.Sheets[_sheetIndex] : null;

    // Clearing the panel detaches every box in it, and a detached box raises
    // LostFocus. Without this its commit would run against a list that has
    // just changed. Same guard ModesWindow uses, for the same reason.
    bool _rebuildingRows;

    void RebuildRows()
    {
        _rebuildingRows = true;
        RowsPanel.Children.Clear();
        _cellBorders.Clear();
        _rowPanels.Clear();
        _rebuildingRows = false;
        if (OnCustomNames) { BuildCustomNameRows(); RefreshIssues(); return; }
        if (CurrentSheet is null) { RefreshIssues(); return; }
        _dupes = DuplicateUses.In(CurrentSheet.Bindings);

        // Selected rows that left the sheet (deleted, or another sheet is
        // showing now) must not tint whatever row wears their number next.
        _selectedRows.RemoveWhere(r => CurrentSheet.Bindings.All(x => x.Row != r));

        bool prefs = CurrentSheet.Type != SheetType.ProfileName;
        // Sentence cards have no columns, so the column header would be a lie
        // above them. Width decides how a card wraps, same as in Device View.
        bool cards = !prefs && _settings.RowCards;
        if (cards) _narrowCards = NarrowCards(RowsPanel.Bounds.Width);
        else RowsPanel.Children.Add(prefs ? PrefsHeaderRow() : HeaderRow());
        // The sheet row, not the line's place in the list. The user's other copy
        // of this profile is a spreadsheet, and every conversation about a
        // binding is "row 14". The screen reader was already saying b.Row while
        // the label beside it said 1.
        int n = 0;
        foreach (var b in CurrentSheet.Bindings)
        {
            n++;
            // Accordion, the way Device View does it: every mapping is one
            // sentence, and the one being edited opens into the full row.
            if (cards && b.Row != _expandedMapping)
            { RowsPanel.Children.Add(SentenceCard(ZoneOfBinding(b), b, n)); continue; }
            var row = prefs ? PrefsRow(b, b.Row) : BindingRow(b, b.Row);
            RowsPanel.Children.Add(cards ? WithDoneButton(row) : row);
        }

        if (CurrentSheet.Bindings.Count == 0)
            RowsPanel.Children.Add(new TextBlock
            {
                Text = CurrentSheet.Type switch
                {
                    SheetType.Infrared => Strings.Main_NoCommandsOnThisSheet,
                    SheetType.Preferences => Strings.Main_NoSettingsOnThisSheet,
                    _ => Strings.Main_NoBindingsYetClickAdd,
                },
                FontSize = Size("BodySize"), Classes = { "muted" }, Margin = new Avalonia.Thickness(4, 12),
            });
        RepaintSelection(); // the prune above may have emptied it; the bar must follow
        RefreshIssues();
    }

    // The part a row belongs to, for a card built outside Device View. ZoneOf
    // sends anything it does not know, empty included, to "other".
    Zone ZoneOfBinding(Binding b) =>
        AllZones.First(z => z.Id == ZoneOf(b.Inputs.Count > 0 ? b.Inputs[0] : ""));

    // The way back to the sentence in Rows View. Device View puts Done in the
    // open card's own header; a list row has no header to put it in.
    Control WithDoneButton(Control row)
    {
        var done = new Button
        { Content = Strings.Main_Done, Classes = { "quiet" }, Padding = new Avalonia.Thickness(12, 2),
          HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(done, Strings.Main_Done);
        done.Click += (_, _) => { _expandedMapping = -1; RebuildRows(); };
        return new StackPanel { Spacing = 4, Children = { row, done } };
    }

    // A tinted, rounded column header used by both the bindings and preferences
    // header rows. No width of its own: the row's columns own that.
    static Control Swatch(string text, string tintKey)
    {
        var border = new Border
        {
            CornerRadius = new Avalonia.CornerRadius(5),
            // 6, not 8: at the narrowest window "(behavior)" was four pixels
            // over its column and broke across the middle of the word.
            Padding = new Avalonia.Thickness(6, 4),
            Child = new TextBlock
            {
                Text = text, FontWeight = FontWeight.Bold, FontSize = Size("SmallSize"),
                // WrapWithOverflow, not Wrap: a header breaks between its words
                // and never through the middle of one. Wrap read "Function
                // (behavio / r)" at the width this column gets.
                TextWrapping = TextWrapping.WrapWithOverflow,
            },
        };
        BindBrush(border, Border.BackgroundProperty, tintKey);
        return border;
    }

    // Every list row lays its cells on one set of columns, so a cell always
    // sits under its own header. The cells that can give ground are a share of
    // the row rather than a pixel count, because the row has to fit whatever
    // width the window has: scrolling sideways to reach a column is a thing a
    // mouth stick cannot reasonably do, and at 125% interface scale the old
    // fixed widths ran off the edge of a full-screen window.
    // Rows touch, with a hairline between them, the way a spreadsheet's do.
    // The gap they used to sit in cost a row's worth of height every nine rows
    // and made each row read as its own card rather than a line in a table.
    static Grid ListGrid(string columns)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(columns) };
        var line = new Border
        {
            Height = 1, VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false, // never in front of the cell above it
        };
        Grid.SetColumnSpan(line, g.ColumnDefinitions.Count);
        BindBrush(line, Border.BackgroundProperty, "SurfaceBorder");
        g.Children.Add(line);
        return g;
    }

    // A round icon button, and the gap between two of them stacked. Written
    // down because the columns that hold them have to be a fixed size: an Auto
    // column is only as wide as the row that fills it, so a row with two
    // buttons side by side and a row with them stacked would share out the
    // remaining width differently and their cells would stop lining up.
    const double IconButtonSize = 40; // Button.icon, App.axaml
    const double IconButtonGap = 6;

    // The gap between cells, which At() puts on the cell and every fixed
    // column has to allow for: a column narrower than the cell plus its margin
    // grows to fit, and a column that grows in some rows and not others is the
    // thing this whole layout exists to avoid.
    const double CellGap = 8;
    static double Fixed(double content) => content + CellGap;

    // handle, output, function, inputs, add/delete, note, up, down.
    // The function column holds one short word ("normal", "tap"); the inputs
    // column holds the part and what was done to it ("Side tube - sip"), and
    // was trimming that to "Side tube..." at any normal window width.
    // Output and inputs both take room off the note, which is an optional line
    // of somebody's own text: a row that has one can wrap it, and a row that
    // does not was paying for it. "Increment mode" beside a "used twice" chip
    // had so little left that it broke across the middle of the word.
    static string BindingColumns =>
        $"{RowNumberWidth + 4},2.6*,1.45*,2.95*,{Fixed(IconButtonSize * 2 + IconButtonGap)},1.4*,{Fixed(IconButtonSize)},{Fixed(IconButtonSize)}";

    // Puts a cell in its column, with the gap that used to be the panel's
    // Spacing. Added to whatever margin the cell already carries, and called
    // once per cell.
    static Control At(Control c, int column)
    {
        Grid.SetColumn(c, column);
        if (column > 0)
        {
            var m = c.Margin;
            c.Margin = new Avalonia.Thickness(m.Left + CellGap, m.Top, m.Right, m.Bottom);
        }
        return c;
    }

    // How wide the row-number column is, in RowNumberLabel and its matching
    // header spacer. Fixed so every row's Output cell lines up under the
    // Output swatch no matter how many digits the row number has.
    const double RowNumberWidth = 34;

    // The row this line is on in the spreadsheet, shown at the left edge. On a
    // standard mode sheet the first binding is row 4, because three header
    // lines sit above it. Counting from 1 instead made the label disagree with
    // the user's own Google Sheet, with the cell references in every warning,
    // and with what the screen reader was already reading out.
    static Control RowNumberLabel(int number) => new TextBlock
    {
        Text = number.ToString(), FontSize = Size("SmallSize"), Classes = { "muted" },
        Width = RowNumberWidth, VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Right, Margin = new Avalonia.Thickness(0, 0, 4, 0),
    };

    // Keeps the header swatches lined up over their columns now that every
    // data row starts with a row-number label.
    static Control RowNumberHeaderSpacer() => new Border
    { Width = RowNumberWidth, Margin = new Avalonia.Thickness(0, 0, 4, 0) }; // same margin as the label

    // List View rows reorder by dragging their row number onto another row;
    // the dragged row takes the drop target's place. The move chevrons stay:
    // dragging is exactly what some QuadStick users cannot do.
    const string RowDragFormat = "qcm-grid-row";

    // Rows selected by clicking their numbers, file-explorer style: click
    // picks one, Ctrl/Cmd toggles, Shift extends from the anchor, Escape or
    // a click on empty space clears. Keyed by CSV row on the current sheet.
    readonly HashSet<int> _selectedRows = new();
    int _selAnchor = -1;
    bool _selectionMode;
    readonly Dictionary<int, Panel> _rowPanels = new();

    // Which outputs and inputs this mode uses more than once. Recomputed once
    // per rebuild rather than per cell, which would be quadratic on a long mode.
    DuplicateUses.Counts _dupes = DuplicateUses.Counts.None;

    // The rows the user can actually see and select right now: only this
    // part's mappings in device view, the whole mode in list view. Shift
    // ranges and the Move menu must never reach rows outside this list.
    List<int> VisibleSelectableRows()
    {
        if (DeviceContainer.IsVisible && _selectedZone is { } z)
            return BindingsByZone().TryGetValue(z, out var bs)
                ? bs.Select(x => x.Row).ToList() : new List<int>();
        return CurrentSheet is { } sheet
            ? sheet.Bindings.Select(x => x.Row).ToList() : new List<int>();
    }

    void PaintRow(int row)
    {
        if (!_rowPanels.TryGetValue(row, out var p)) return;
        bool sel = _selectedRows.Contains(row);
        if (sel) BindBrush(p, Panel.BackgroundProperty, "SelectionTint");
        else if (Equals(p.Tag, "mapping-card")) BindBrush(p, Panel.BackgroundProperty, "Surface");
        else p.ClearValue(Panel.BackgroundProperty);
        // By tag, not by position: a row grid also holds the hairline that
        // separates it from the next one.
        if (p.Children.OfType<Border>().FirstOrDefault(x => x.Tag is string) is { Tag: string baseName } h)
            AutomationProperties.SetName(h,
                string.Format(CultureInfo.CurrentCulture,
            sel ? Strings.Main_RowSelected : Strings.Main_RowNotSelected, baseName));
    }

    void RepaintSelection()
    {
        foreach (var row in _rowPanels.Keys) PaintRow(row);
        bool selecting = _selectionMode || _selectedRows.Count > 0;
        SelectionCommandBar.IsVisible = selecting;
        SelectionModeButton.Classes.Set("primary", _selectionMode);
        SelectionCount.Text = _selectedRows.Count > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.Main_SelectedRowsCountSelected, _selectedRows.Count)
            : Strings.Shell_SelectRowsToMoveDeleteOrClear;
        SelectionMoveButton.IsEnabled = _selectedRows.Count > 0;
        SelectionDeleteButton.IsEnabled = _selectedRows.Count > 0;
        SelectionClearButton.IsEnabled = _selectedRows.Count > 0;
        AutomationProperties.SetName(SelectionMoveButton, DeviceContainer.IsVisible
            ? Strings.Shell_MoveTheSelectedMappingsTo
            : Strings.Shell_MoveTheSelectedRowsTo);
        AutomationProperties.SetName(SelectionDeleteButton, DeviceContainer.IsVisible
            ? Strings.Shell_DeleteTheSelectedMappings
            : Strings.Shell_DeleteTheSelectedRows);
    }

    void ToggleSelectionMode()
    {
        _selectionMode = !_selectionMode;
        if (!_selectionMode)
        {
            ClearSelection();
            RepaintSelection(); // ClearSelection is deliberately a no-op when empty.
        }
        else
        {
            RepaintSelection();
            Status(Strings.Shell_SelectRowsToMoveDeleteOrClear, StatusKind.Info);
        }
    }

    void DeleteSelectedRows()
    {
        if (_file is null || _selectedRows.Count == 0) return;
        var rows = _selectedRows.ToList();
        // Measure the hole before it exists: where the topmost deleted row sat
        // and how much height leaves, so the survivors can settle into it.
        int firstIndex = -1;
        if (CurrentSheet is { } sh)
            for (int i = 0; i < sh.Bindings.Count && firstIndex < 0; i++)
                if (_selectedRows.Contains(sh.Bindings[i].Row)) firstIndex = i;
        double gap = rows.Sum(r => _rowPanels.TryGetValue(r, out var rp) ? rp.Bounds.Height : 0);
        if (!DeviceContainer.IsVisible) // list view: every doomed row gets a send-off
            foreach (var r in rows.Take(12)) // ponytail: 12 ghosts max, a mass delete reads fine without full theater
                if (_rowPanels.TryGetValue(r, out var doomed)) GhostRowAway(doomed, ListOverlay);
        _selectedRows.Clear(); _selAnchor = -1;
        var off = GridScroll.Offset;
        _file.DeleteRows(rows); // one undo step for the whole selection
        if (DeviceContainer.IsVisible) { BuildDeviceView(); BuildZoneDetail(); RefreshIssues(); }
        else
        {
            RebuildRows();
            AnimateGapClose(RowsPanel, firstIndex + 1, gap); // +1: the header row is child 0
            RestoreListScroll(off, () => { });
        }
        Status(Plural.Of(rows.Count, "Count_RowDeleted"), StatusKind.Ready);
    }

    void ClearSelection()
    {
        if (_selectedRows.Count == 0) return;
        _selectedRows.Clear();
        _selAnchor = -1;
        RepaintSelection();
    }

    // Row numbers change when rows move, but the CSV grid rows themselves do
    // not. Remember those objects before a move and resolve their new numbers
    // afterwards, so a selection follows the mappings it names.
    HashSet<string[]> SelectedGridRows()
    {
        var selected = new HashSet<string[]>(); // arrays compare by identity
        if (_file is null) return selected;
        foreach (int row in _selectedRows)
            if (row is >= 1 && row <= _file.Grid.Count)
                selected.Add(_file.Grid[row - 1]);
        return selected;
    }

    void RestoreSelectedGridRows(HashSet<string[]> selected)
    {
        _selectedRows.Clear();
        if (_file is not null)
            for (int i = 0; i < _file.Grid.Count; i++)
                if (selected.Contains(_file.Grid[i])) _selectedRows.Add(i + 1);
        _selAnchor = _selectedRows.Count == 0 ? -1 : _selectedRows.Min();
    }

    // A rebuild preserves the old offset by default. That is right for an
    // ordinary edit, but after a move it can leave the selected mappings off
    // screen at their new home.
    void ScrollSelectedRowsIntoView()
    {
        var rows = _selectedRows.OrderBy(r => r)
            .Select(r => _rowPanels.GetValueOrDefault(r))
            .Where(p => p is not null).Cast<Panel>().ToList();
        if (rows.Count == 0) return;
        if (DeviceContainer.IsVisible)
        {
            // Cards live in their own ScrollViewer. The last card is the one
            // furthest along the moved block, so bringing it into view proves
            // the whole move reached its destination.
            rows[^1].BringIntoView();
            return;
        }
        if (GridScroll.Viewport.Height <= 0) return;

        var top = rows[0].TranslatePoint(new Point(0, 0), RowsPanel);
        var bottom = rows[^1].TranslatePoint(new Point(0, rows[^1].Bounds.Height), RowsPanel);
        if (top is not { } first || bottom is not { } last) return;

        double current = GridScroll.Offset.Y;
        double target = first.Y < current ? first.Y
            : last.Y > current + GridScroll.Viewport.Height
                ? last.Y - GridScroll.Viewport.Height
                : current;
        double maxY = Math.Max(0, GridScroll.Extent.Height - GridScroll.Viewport.Height);
        GridScroll.Offset = new Vector(GridScroll.Offset.X, Math.Clamp(target, 0, maxY));
    }

    MenuFlyout MoveMenu()
    {
        var menu = new MenuFlyout();
        void Populate()
        {
            menu.Items.Clear();

            var copy = new MenuItem { Header = Strings.Main_CopyToMode };
            foreach (var item in CopyToModeItems()) copy.Items.Add(item);
            copy.IsEnabled = copy.Items.Count > 0;
            menu.Items.Add(copy);
            menu.Items.Add(new Separator());

            var top = new MenuItem { Header = Strings.Main_ToTheTop };
            top.Click += (_, _) => MoveSelection(top: true);
            var bottom = new MenuItem { Header = Strings.Main_ToTheBottom };
            bottom.Click += (_, _) => MoveSelection(top: false);
            menu.Items.Add(top);
            menu.Items.Add(bottom);
        }

        // This flyout is attached while the window is being built, before a
        // profile exists. Its destinations must therefore be made when it
        // opens, not once at startup, or the visible Move button can never
        // offer Copy to mode.
        // Populate once as well, so the move choices are present for keyboard
        // and automation paths that inspect the flyout before its first open.
        Populate();
        menu.Opening += (_, _) => Populate();
        return menu;
    }

    void MoveSelection(bool top)
    {
        if (_file is null || _selectedRows.Count == 0) return;
        // Land against the first or last row the user can see that is not
        // already selected; with everything selected there is nowhere to go.
        var anchors = VisibleSelectableRows().Where(r => !_selectedRows.Contains(r)).ToList();
        if (anchors.Count == 0) return;
        var srcs = _selectedRows.OrderBy(r => r).ToArray();
        var selected = SelectedGridRows();
        var off = GridScroll.Offset;
        if (top) _file.MoveRowsBefore(srcs, anchors[0]);
        else _file.MoveRowsAfter(srcs, anchors[^1]);
        RestoreSelectedGridRows(selected);
        if (DeviceContainer.IsVisible)
        {
            BuildDeviceView(); BuildZoneDetail(); RefreshIssues();
            AfterLayout(ScrollSelectedRowsIntoView);
        }
        else
        {
            RebuildRows();
            RestoreListScroll(off, ScrollSelectedRowsIntoView);
        }
        Status(Plural.Of(srcs.Length, top ? "Count_RowMovedTop" : "Count_RowMovedBottom"), StatusKind.Ready);
    }

    void SelectFromClick(int row, KeyModifiers mods)
    {
        if (mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Meta))
        { if (!_selectedRows.Remove(row)) _selectedRows.Add(row); _selAnchor = row; }
        else if (mods.HasFlag(KeyModifiers.Shift) && _selAnchor >= 0)
        {
            var rows = VisibleSelectableRows();
            int a = rows.IndexOf(_selAnchor), z = rows.IndexOf(row);
            if (a < 0) a = z;
            _selectedRows.Clear();
            for (int i = Math.Min(a, z); i <= Math.Max(a, z); i++) _selectedRows.Add(rows[i]);
        }
        // A plain click on an already-selected row keeps the whole set, so a
        // multi-row drag can start from any of its rows.
        else if (!_selectedRows.Contains(row))
        { _selectedRows.Clear(); _selectedRows.Add(row); _selAnchor = row; }
        else _selAnchor = row;
        RepaintSelection();
    }

    Control DragHandle(Binding b, int number) =>
        WireDragHandle(new Border { Child = RowNumberLabel(number) }, b,
            string.Format(CultureInfo.CurrentCulture, Strings.Main_RowNumber, number));

    // Shared by the list-view row numbers and the device-view card handles:
    // click selects (with Ctrl/Cmd/Shift), Space selects, a real movement
    // starts a drag carrying the whole selection.
    Border WireDragHandle(Border h, Binding b, string baseName)
    {
        h.Background = Brushes.Transparent; // hit-testable everywhere
        h.Cursor = new Cursor(StandardCursorType.SizeAll);
        h.VerticalAlignment = VerticalAlignment.Center;
        h.Focusable = true; // Space selects for keyboard and switch users
        h.Tag = baseName;   // PaintRow appends ", selected" to this
        ToolTip.SetTip(h, Strings.Main_ClickToSelectDragTo);
        bool pressed = false, collapseOnRelease = false;
        var pressAt = new Avalonia.Point();
        h.PointerPressed += (_, e) =>
        {
            // A plain press inside a bigger selection keeps the set so a
            // multi-row drag can start; if no drag follows, the release
            // below collapses to just this row, like a file explorer.
            collapseOnRelease = e.KeyModifiers == KeyModifiers.None
                && _selectedRows.Contains(b.Row) && _selectedRows.Count > 1;
            SelectFromClick(b.Row, e.KeyModifiers);
            pressed = true;
            pressAt = e.GetPosition(this);
            h.Focus();
            e.Handled = true; // the click-away clear below must not see this press
        };
        h.PointerReleased += (_, _) =>
        {
            if (pressed && collapseOnRelease)
            {
                _selectedRows.Clear(); _selectedRows.Add(b.Row); _selAnchor = b.Row;
                RepaintSelection();
            }
            pressed = false;
        };
        h.PointerMoved += (_, e) =>
        {
            // Only a real movement starts a drag, so a plain click stays a click.
            var d = e.GetPosition(this) - pressAt;
            if (!pressed || Math.Abs(d.X) + Math.Abs(d.Y) < 6) return;
            pressed = false;
            var data = new DataObject();
            // The whole selection travels; the press above guaranteed the
            // pressed row is in it.
            data.Set(RowDragFormat, _selectedRows.OrderBy(r => r).ToArray());
            // Stopped here rather than on DragLeave or Drop: this runs however
            // the drag ended, including a cancel outside the window.
            _ = DragDrop.DoDragDrop(e, data, DragDropEffects.Move)
                .ContinueWith(_ => StopDragScroll(), TaskScheduler.FromCurrentSynchronizationContext());
        };
        h.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Space) return;
            if (!_selectedRows.Remove(b.Row)) { _selectedRows.Add(b.Row); _selAnchor = b.Row; }
            RepaintSelection();
            e.Handled = true;
        };
        return h;
    }

    // A drag that reaches the top or bottom edge of the list pulls the list
    // along, so a row can travel a hundred rows in one gesture. The drag loop
    // owns the pointer, so the wheel and the scrollbar are not available while
    // a row is in the air: without this the only way across a long mode is to
    // drop, scroll, pick up again.
    const double DragScrollBand = 48; // how close to an edge starts the pull
    const double DragScrollStep = 14; // pixels a tick, about a screen a second

    // Pure, so the arithmetic is tested without a real drag loop.
    internal static double DragScrollDelta(double pointerY, double viewport, double offsetY, double maxY)
    {
        if (pointerY <= DragScrollBand) return -Math.Min(DragScrollStep, Math.Max(0, offsetY));
        if (pointerY >= viewport - DragScrollBand) return Math.Min(DragScrollStep, Math.Max(0, maxY - offsetY));
        return 0;
    }

    DispatcherTimer? _dragScroll;
    ScrollViewer? _dragScrollView;
    double _dragScrollY;

    void WireDragScroll(ScrollViewer sv)
    {
        DragDrop.SetAllowDrop(sv, true);
        // Bubbled from the row under the pointer, which does not mark the
        // event handled. Over the empty space past the last row it arrives
        // straight here.
        sv.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (!e.Data.Contains(RowDragFormat)) return;
            _dragScrollView = sv;
            _dragScrollY = e.GetPosition(sv).Y;
            _dragScroll ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16),
                DispatcherPriority.Normal, (_, _) => DragScrollTick());
            _dragScroll.Start();
        });
    }

    // Ticks off the last position the drag reported, not off a fresh pointer
    // read: a drag held still at the edge stops sending moves and still has to
    // keep scrolling.
    void DragScrollTick()
    {
        if (_dragScrollView is not { } sv) return;
        var maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        var d = DragScrollDelta(_dragScrollY, sv.Viewport.Height, sv.Offset.Y, maxY);
        if (d != 0) sv.Offset = new Vector(sv.Offset.X, sv.Offset.Y + d);
    }

    void StopDragScroll()
    {
        _dragScroll?.Stop();
        _dragScrollView = null;
    }

    void WireRowDrop(Panel p, Binding b)
    {
        DragDrop.SetAllowDrop(p, true);
        p.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.Data.Contains(RowDragFormat) ? DragDropEffects.Move : DragDropEffects.None);
        p.AddHandler(DragDrop.DragEnterEvent, (_, e) =>
        { if (e.Data.Contains(RowDragFormat)) BindBrush(p, Panel.BackgroundProperty, "NewRowTint"); });
        p.AddHandler(DragDrop.DragLeaveEvent, (_, _) => PaintRow(b.Row)); // restore the selection tint
        p.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            PaintRow(b.Row);
            if (e.Data.Get(RowDragFormat) is not int[] srcs || srcs.Contains(b.Row)) return;
            var off = GridScroll.Offset;
            var selected = SelectedGridRows();
            _file!.MoveRows(srcs, b.Row);
            RestoreSelectedGridRows(selected);
            if (DeviceContainer.IsVisible)
            {
                BuildDeviceView(); BuildZoneDetail(); RefreshIssues();
                AfterLayout(ScrollSelectedRowsIntoView);
            }
            else
            {
                RebuildRows();
                RestoreListScroll(off, ScrollSelectedRowsIntoView);
            }
        });
    }

    // The spreadsheet fills a cell when its value appears elsewhere in the
    // same mode, and a tester who lost that mark said it cost them "a lot of
    // mental memorization" and one combination they never noticed was free.
    // A fill cannot carry it here, because nothing in this app may be said by
    // colour alone, so the mark is the count itself, in words for a reader.
    Control? DuplicateChip(int count)
    {
        if (count < 2) return null;
        var chip = new Border
        {
            Padding = new Avalonia.Thickness(8, 2),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(6, 0, 0, 0),
            Child = new TextBlock
            {
                Text = string.Format(CultureInfo.CurrentCulture, Strings.Main_DuplicateMark, count),
                FontSize = Math.Max(14, Size("BodySize")),
                FontWeight = Avalonia.Media.FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        BindBrush(chip, Border.BackgroundProperty, "Accent");
        BindBrush(chip, Border.BorderBrushProperty, "Accent");
        BindBrush((TextBlock)chip.Child, TextBlock.ForegroundProperty, "OnAccent");
        var said = string.Format(CultureInfo.CurrentCulture, Strings.Main_UsedNTimesInThisMode, count);
        AutomationProperties.SetName(chip, said);
        ToolTip.SetTip(chip, said);
        return chip;
    }

    // The cell as it was when its value is used once, so a profile with no
    // repeats looks exactly as it did.
    Control WithDuplicateMark(Control cell, int count)
    {
        if (DuplicateChip(count) is not { } chip) return cell;
        var line = ListGrid("*,Auto");
        line.Children.Add(At(cell, 0));
        line.Children.Add(At(chip, 1));
        return line;
    }

    // "N: Name", the way the modes list, the modes window and the import
    // review all say it. Only a mode takes a number, and two modes may share
    // a name, so the number is the half that actually identifies one.
    string ModeLabel(int sheetIndex)
    {
        var sheets = _file!.Document.Sheets;
        int mode = 0;
        for (int i = 0; i <= sheetIndex && i < sheets.Count; i++)
            if (sheets[i].Type == SheetType.ProfileName) mode++;
        return string.Format(CultureInfo.CurrentCulture, Strings.Main_ModeNumberAndName, mode,
            sheets[sheetIndex].ModeName.Length > 0 ? sheets[sheetIndex].ModeName : Strings.Main_UnnamedMode);
    }

    // Right click is how a spreadsheet user reaches copy, move and delete, and
    // it was the one gesture the list had no answer for: a tester rebuilt a
    // whole mode by hand because of it. The menu acts on the selection, so a
    // right click on an unselected row takes it first, the way a file
    // explorer does.
    void WireRowMenu(Control row, Binding b)
    {
        var menu = new MenuFlyout();
        // Built on open, not on build: the mode list, the selection and what
        // is legal to do with it all move underneath it.
        menu.Opening += (_, _) =>
        {
            if (!_selectedRows.Contains(b.Row)) SelectFromClick(b.Row, KeyModifiers.None);
            menu.Items.Clear();
            foreach (var item in RowMenuItems()) menu.Items.Add(item);
        };
        row.ContextFlyout = menu;
    }

    IEnumerable<Control> RowMenuItems()
    {
        var copy = new MenuItem { Header = Strings.Main_CopyToMode };
        foreach (var item in CopyToModeItems()) copy.Items.Add(item);
        // Left visible and dead rather than hidden: a one mode profile has
        // nowhere to copy to, and a menu that changes shape teaches nothing.
        copy.IsEnabled = copy.Items.Count > 0;

        var move = new MenuItem { Header = Strings.Main_Move };
        var top = new MenuItem { Header = Strings.Main_ToTheTop };
        top.Click += (_, _) => MoveSelection(top: true);
        var bottom = new MenuItem { Header = Strings.Main_ToTheBottom };
        bottom.Click += (_, _) => MoveSelection(top: false);
        move.Items.Add(top);
        move.Items.Add(bottom);

        var del = new MenuItem { Header = Strings.Main_Delete };
        del.Click += (_, _) => DeleteSelectedRows();

        return new Control[] { copy, move, new Separator(), del };
    }

    // Every other mode in the file. Not this one: a copy landing back where it
    // started is the one destination nobody means.
    IEnumerable<MenuItem> CopyToModeItems()
    {
        if (_file is null) yield break;
        var sheets = _file.Document.Sheets;
        for (int i = 0; i < sheets.Count; i++)
        {
            if (sheets[i].Type != SheetType.ProfileName || i == _sheetIndex) continue;
            int target = i;
            var item = new MenuItem { Header = ModeLabel(target) };
            item.Click += (_, _) => CopySelectionToMode(target);
            yield return item;
        }
    }

    void CopySelectionToMode(int sheetIndex)
    {
        if (_file is null || _selectedRows.Count == 0) return;
        if (sheetIndex < 0 || sheetIndex >= _file.Document.Sheets.Count) return;
        var rows = _selectedRows.ToArray();
        var off = GridScroll.Offset;
        int n = _file.CopyRowsToSheet(rows, sheetIndex);
        if (n == 0) return;
        _selectedRows.Clear(); _selAnchor = -1; // copying above here renumbers these rows
        if (DeviceContainer.IsVisible) { BuildDeviceView(); BuildZoneDetail(); RefreshIssues(); }
        else { RebuildRows(); RestoreListScroll(off, () => { }); }
        // The copies land in a mode that is not on screen. Being told is the
        // only way the user learns it worked, or how many went.
        Status(string.Format(CultureInfo.CurrentCulture,
            Plural.Wording(n, "Count_RowCopied"), n, ModeLabel(sheetIndex)), StatusKind.Ready);
    }

    Control HeaderRow()
    {
        var p = ListGrid(BindingColumns);
        p.Children.Add(At(RowNumberHeaderSpacer(), 0));
        p.Children.Add(At(Swatch(Strings.Main_OutputGameButton, OutputTint), 1));
        p.Children.Add(At(Swatch(Strings.Main_FunctionBehavior, FunctionTint), 2));
        p.Children.Add(At(Swatch(Strings.Main_InputsSipsPuffsJoystick, InputTint), 3));
        return p;
    }

    Control BindingRow(Binding b, int number)
    {
        var p = ListGrid(BindingColumns);
        p.Children.Add(At(DragHandle(b, number), 0));
        WireRowDrop(p, b);
        WireRowMenu(p, b);
        _rowPanels[b.Row] = p;
        PaintRow(b.Row);
        // Inputs stack DOWN (below), so every other cell centers vertically
        // against the taller stack instead of stretching or hugging the top.
        Control Mid(Control c) { c.VerticalAlignment = VerticalAlignment.Center; return c; }
        var outputs = OutputsFor(CurrentSheet!);
        p.Children.Add(At(Mid(WithDuplicateMark(ListPickerCell(b.Row, 0, OutputFieldValue(b), outputs.Options, string.Format(CultureInfo.CurrentCulture, Strings.Main_OutputForRowBRow, b.Row), OutputTint, outputs.Catalog, Strings.Main_AnOutput,
            picked => CommitOutputFromList(b, outputs, picked),
            _labelStyle == 0 ? null
                : token => OutputVisuals.Render(VisualFor(token), TokenLabel(token), compact: true)),
            _dupes.Output(b.Output))), 1));
        // List View is the raw grid, so the function's numbers explain
        // themselves through the cell's name rather than a panel: same
        // sentences Device View prints under its box.
        p.Children.Add(At(Mid(ListPickerCell(b.Row, 1, b.Function, FunctionSuggestions,
            string.Format(CultureInfo.CurrentCulture, Strings.Main_FunctionForRowBRow, b.Row, ParameterAccessibleName(b.Function)), FunctionTint, null, Strings.Main_AFunction)), 2));

        // A mode row whose output is a setting name is not a binding: the
        // device skips column B and reads column C as the setting's VALUE.
        // The row already carried a scope badge saying so, and then handed the
        // user an input picker on that same cell, with the whole input catalog
        // in it. Picking "lip" there sets the value to 0 on the hardware. So
        // the value gets the same control the settings sheet gives it, at
        // column C, and no way to add a second input to a row that has none.
        if (IsModePreferenceOverride(b))
        {
            var prefDef = Definition(b.Output);
            var prefValue = _file!.GetCell(b.Row, 2);
            bool prefTyped = prefDef is not null && CanRepresent(prefDef, prefValue, 2);
            var prefCell = PrefsValueCell(b, prefTyped ? prefDef : null, 2);
            // The value sits in the inputs column, because that is the column
            // the device reads it from. It keeps that column's whole width so
            // the note, chevrons and delete stay lined up with every other row.
            p.Children.Add(At(Mid(prefCell), 3));
            // The gap where "add another input" sits on every other row, held
            // open by the button itself rather than by a guessed width, so it
            // stays right if the icon or the padding ever changes. Invisible
            // and unreachable: a settings row has no inputs to add.
            var ghost = IconButton("IconAdd", "");
            ghost.Opacity = 0;
            ghost.IsHitTestVisible = false;
            ghost.IsTabStop = false;
            AutomationProperties.SetAccessibilityView(ghost, Avalonia.Automation.AccessibilityView.Raw);
            var prefButtons = new StackPanel
            {
                Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                Orientation = Orientation.Horizontal,
                Children = { ghost },
            };
            RowTail(p, b, prefButtons);
            // Under the row, not at the end of it: a badge in the row would
            // need a column of its own that every other row leaves empty, and
            // an empty column is what knocks the cells out from under their
            // headers. The way to a value the manager's own slider will not
            // reach, and the sentence explaining what the setting does, follow
            // it. The settings sheet has carried both for every row; a mode
            // override had neither.
            var under = new StackPanel { Spacing = 4, Children = { p, ScopeBadge(b) } };
            if (prefDef is null) return under;
            if (PreferenceInfoLine(b, prefDef, prefTyped ? prefCell : null, 2) is { } prefInfo)
                under.Children.Add(prefInfo);
            return under;
        }

        // Extra inputs go UNDER the first one. Sideways growth forced a
        // horizontal scroll, which the tester called out as inaccessible.
        var inputsBox = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        int inputCount = Math.Max(1, b.Inputs.Count);
        for (int i = 0; i < inputCount; i++)
        {
            // Write to each input's REAL column (inputs may have gaps in C..J).
            int col = i < b.InputCols.Count ? b.InputCols[i] : FirstFreeInputColumn(b);
            // The picker takes the column, the remove control sits beside it.
            var line = ListGrid("*,Auto");
            var inputToken = i < b.Inputs.Count ? b.Inputs[i] : "";
            line.Children.Add(At(WithDuplicateMark(ListPickerCell(b.Row, col, inputToken,
                InputSuggestions, string.Format(CultureInfo.CurrentCulture, Strings.Main_InputI1ForRow, i + 1, b.Row), InputTint, InputCatalog, Strings.Main_AnInput,
                labelFor: t => InputOptionLabel(t, "")),
                _dupes.Input(inputToken)), 0));
            // A round remove control beside each real input, so any input
            // can be taken out (not just emptied, and not just the last one).
            if (b.Inputs.Count > 1 && i < b.Inputs.Count)
            {
                int idx = i;
                var rmv = IconButton("IconDelete", string.Format(CultureInfo.CurrentCulture, Strings.Main_RemoveInputI1From, i + 1, b.Row));
                rmv.Click += (_, _) =>
                {
                    var off = GridScroll.Offset;
                    _file!.RemoveInput(b.Row, idx);
                    RebuildRows();
                    SayIfNothingFiresIt(b.Row);
                    RestoreListScroll(off, () =>
                    {
                        if (_cellBorders.TryGetValue($"A{b.Row}", out var border))
                        { border.BringIntoView(); (border.Child as Control)?.Focus(); }
                    });
                };
                line.Children.Add(At(Mid(rmv), 1));
            }
            inputsBox.Children.Add(line);
        }
        p.Children.Add(At(inputsBox, 3));

        // One input: plus and trash side by side. More than one: they stack
        // into one column so the per-input trashes never push the note and
        // chevrons further right.
        var rowButtons = new StackPanel
        {
            Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Orientation = b.Inputs.Count > 1 ? Orientation.Vertical : Orientation.Horizontal,
        };
        if (inputCount < 8)
        {
            var addInput = IconButton("IconAdd", string.Format(CultureInfo.CurrentCulture, Strings.Main_AddAnotherInputToRow, b.Row));
            ToolTip.SetTip(addInput, Strings.Main_AddAnotherInput);
            int nextCol = FirstFreeInputColumn(b);
            addInput.Click += (_, _) =>
            {
                // Add the cell directly; the file only changes when a value is committed.
                // In its own line, on the same columns as the committed inputs,
                // so an empty picker lines up with the ones above it.
                var newBox = ListPickerCell(b.Row, nextCol, "", InputSuggestions,
                    string.Format(CultureInfo.CurrentCulture, Strings.Main_InputNextCol1ForRow, nextCol - 1, b.Row), InputTint, InputCatalog, Strings.Main_AnInput,
                    labelFor: t => InputOptionLabel(t, ""));
                var newLine = ListGrid("*,Auto");
                newLine.Children.Add(At(newBox, 0));
                inputsBox.Children.Add(newLine);
                AnimateIn(newBox);
                nextCol++;
                while (nextCol < 10 && b.InputCols.Contains(nextCol)) nextCol++; // skip occupied cells
                if (nextCol >= 10) addInput.IsVisible = false;
                ((newBox as Border)!.Child as Control)!.Focus();
            };
            rowButtons.Children.Add(addInput);
        }

        RowTail(p, b, rowButtons);
        return p;
    }

    // Everything a row ends with, whatever its middle held: the whole-row
    // delete, the note, the reorder chevrons, and the scope badge on a settings
    // row. Shared because a preference override row has no inputs to add and
    // still has to end the same way, lined up under the same headers.
    void RowTail(Grid p, Binding b, StackPanel rowButtons)
    {
        Control Mid(Control c) { c.VerticalAlignment = VerticalAlignment.Center; return c; }

        // The whole-row delete: a red trash circle under the plus. Same control
        // the Modes window deletes a mode with; see RowControls.
        var del = RowControls.Delete(string.Format(CultureInfo.CurrentCulture, Strings.Main_DeleteRowBRow, b.Row));
        ToolTip.SetTip(del, Strings.Main_DeleteThisWholeRow);
        del.Click += (_, _) => DeleteListRow(b);
        rowButtons.Children.Add(del);
        p.Children.Add(At(rowButtons, 4));

        // The note wraps inside its column and grows the row taller, which is
        // the trade the whole layout makes: taller rows over a row that runs
        // off the side of the window.
        var note = NoteBox(b.Row, NoteColumn, string.Format(CultureInfo.CurrentCulture, Strings.Main_NoteForRowBRow, b.Row));
        p.Children.Add(At(Mid(note), 5));

        // Reorder within the mode. Both buttons always render (disabled at the
        // edges) so the click targets stay put while a row is walked up or down.
        int rowIndex = CurrentSheet!.Bindings.IndexOf(b);
        int column = 6;
        foreach (var (delta, word) in new[] { (-1, "up"), (+1, "down") })
        {
            var move = RowControls.Move(delta < 0,
                string.Format(CultureInfo.CurrentCulture, Strings.Main_MoveRowBRowWord, b.Row, word));
            move.Tag = (word, b.Row);
            move.IsEnabled = rowIndex + delta >= 0 && rowIndex + delta < CurrentSheet!.Bindings.Count;
            move.Click += (_, _) => MoveListRow(b, delta);
            p.Children.Add(At(Mid(move), column++));
        }
    }

    // A mode row whose output is a setting name is a per-mode override, so it
    // says so, on its own line under the row.
    Control ScopeBadge(Binding b)
    {
        var badge = new TextBlock
        {
            Text = ModeScope, FontSize = Size("SmallSize"), Classes = { "secondary" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(RowNumberWidth + 4, 0, 0, 0),
        };
        AutomationProperties.SetName(badge, string.Format(CultureInfo.CurrentCulture, Strings.Main_RowBRowScopeModeScope, b.Row, ModeScope));
        return badge;
    }

    // Swap this row with its neighbor in the same mode's binding list. After
    // the rebuild, focus follows the same-direction move button on the moved
    // row so a keyboard user can keep walking it without re-tabbing; at the
    // edge (button disabled) focus lands on the row's output cell instead.
    void MoveListRow(Binding b, int delta)
    {
        var sheet = CurrentSheet;
        if (sheet is null || _file is null) return;
        int i = sheet.Bindings.IndexOf(b);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= sheet.Bindings.Count) return;
        int destRow = sheet.Bindings[j].Row;
        var off = GridScroll.Offset;
        var selected = SelectedGridRows();
        _file.SwapRows(b.Row, destRow);
        RestoreSelectedGridRows(selected);
        RebuildRows();
        RestoreListScroll(off, () =>
        {
            var key = (delta < 0 ? "up" : "down", destRow);
            // Down the whole panel, not one level into each row: a row is a
            // grid, and a settings-override row wraps that grid in a stack.
            var moveButton = RowsPanel.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(x => x.Tag is ValueTuple<string, int> t && t.Equals(key));
            if (moveButton is { IsEnabled: true })
            { moveButton.BringIntoView(); moveButton.Focus(); }
            else if (_cellBorders.TryGetValue($"A{destRow}", out var border))
            { border.BringIntoView(); (border.Child as Control)?.Focus(); }
            ScrollSelectedRowsIntoView();
        });
    }

    // Delete a List View row without the scroll jumping to the top: restore the
    // saved scroll offset after the rebuild, then focus the row that slid into
    // place. (RebuildRows() clears and re-adds every row, which otherwise resets
    // the ScrollViewer to 0.)
    void DeleteListRow(Binding b)
    {
        int deletedIndex = CurrentSheet!.Bindings.IndexOf(b);
        double gap = _rowPanels.TryGetValue(b.Row, out var gone) ? gone.Bounds.Height : 0;
        if (gone is not null) GhostRowAway(gone, ListOverlay); // snapshot while still attached
        var off = GridScroll.Offset;
        _selectedRows.Clear(); _selAnchor = -1; // rows renumber under a stale selection
        _file!.DeleteRow(b.Row);
        RebuildRows();
        AnimateGapClose(RowsPanel, deletedIndex + 1, gap); // +1: the header row is child 0
        RestoreListScroll(off, () => FocusRowSibling(deletedIndex));
    }

    // Scroll and focus must wait until the rebuilt panel has been measured,
    // or BringIntoView runs against a zero-size control and the ScrollViewer
    // snaps back to the top.
    static void AfterLayout(Action act) => Dispatcher.UIThread.Post(act, DispatcherPriority.Loaded);

    void RestoreListScroll(Vector offset, Action thenFocus) =>
        Dispatcher.UIThread.Post(() =>
        {
            var maxY = Math.Max(0, GridScroll.Extent.Height - GridScroll.Viewport.Height);
            GridScroll.Offset = new Vector(offset.X, Math.Min(offset.Y, maxY));
            thenFocus();
        }, DispatcherPriority.Loaded);

    // alsoRebuild lets a caller ask for the same deferred row rebuild on its own
    // terms (old value, new value). A preference name uses it: a different
    // setting needs a different control under it.
    Control SuggestBox(int row, int col, string value, List<string> suggestions,
                       string accessibleName, string tintKey, Func<string, string, bool>? alsoRebuild = null)
    {
        var box = new AutoCompleteBox
        {
            Text = value,
            ItemsSource = suggestions,
            FilterMode = AutoCompleteFilterMode.Contains,
            MinimumPrefixLength = 0,
            // The width belongs to the wrapper below, the way ListPickerCell
            // does it: the wrapper's own border is what has to add up to the
            // column header's width, or every column sits 6px right of it.
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        box[!TemplatedControl.BackgroundProperty] = new DynamicResourceExtension(tintKey + "Brush");
        AutomationProperties.SetName(box, accessibleName);
        // Open the list of choices as soon as the field gets focus, so you can
        // click and pick instead of having to know and type a value.
        if (suggestions.Count > 0)
            box.GotFocus += (_, _) => box.IsDropDownOpen = true;
        void Commit()
        {
            if (_file is null) return;
            var v = (box.Text ?? "").Trim();
            var old = _file.GetCell(row, col);
            if (v == old) return; // no no-op undo snapshots
            _file.SetCell(row, col, v);
            RefreshIssues();
            // Keep the device diagram's summaries current without stealing
            // focus from the detail panel the user is typing in.
            if (DeviceContainer.IsVisible) { BuildDeviceView(); return; }
            // An input appearing or disappearing changes the row's own
            // controls (its remove buttons, the + input button), so rebuild
            // the rows instead of leaving stale controls until the next view
            // switch. Deferred: rebuilding synchronously destroys the box
            // that is mid focus/key event and fights its closing dropdown,
            // the same trap TokenField defers around. Only refocus when the
            // box was focused (Enter commit); a click-away commit must not
            // steal the click's focus back.
            if ((col is >= 2 and < 10 && (old.Length == 0) != (v.Length == 0))
                || (alsoRebuild?.Invoke(old, v) ?? false))
            {
                bool refocus = box.IsFocused;
                box.IsDropDownOpen = false;
                var off = GridScroll.Offset;
                Dispatcher.UIThread.Post(() =>
                {
                    RebuildRows();
                    RestoreListScroll(off, () =>
                    {
                        if (refocus && _cellBorders.TryGetValue($"{(char)('A' + col)}{row}", out var border))
                            (border.Child as Control)?.Focus();
                    });
                });
            }
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        var wrapper = new Border
        {
            Child = box,
            // Match the thickness RefreshIssues sets on an errored cell, so
            // flagging a problem only recolors the border and never reflows the
            // row. A thinner clean border would shift the row a pixel and knock
            // the row number off center.
            BorderThickness = new Avalonia.Thickness(3),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new Avalonia.CornerRadius(5),
        };
        _cellBorders[$"{(char)('A' + col)}{row}"] = wrapper;
        return wrapper;
    }

    // Column K is the first cell the device ignores, so notes live there.
    const int NoteColumn = 10;

    Control NoteBox(int row, int col, string accessibleName)
    {
        var box = new TextBox
        {
            Text = _file!.GetCell(row, col),
            Watermark = Strings.Main_Note,
            FontSize = Size("SmallSize"),
            // A long note used to sit on one clipped line. Wrapping needs a
            // width bound to grow vertically instead of sideways, so every
            // call site must give this box a fixed Width. Enter still commits
            // (see KeyDown below), so it must not also insert a newline.
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
            // Cap how tall one note can push its row before it scrolls
            // internally, so a pathological note can't fill the screen.
            MaxHeight = 92,
        };
        // TextBox doesn't expose vertical scrollbar visibility as its own
        // property; it reads the attached ScrollViewer property from its
        // own template instead.
        box.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(box, accessibleName);
        void Commit()
        {
            if (_file is null) return;
            var v = (box.Text ?? "").Trim();
            if (v == _file.GetCell(row, col)) return;
            _file.SetCell(row, col, v);
            RefreshIssues(); // the 1023-byte row limit can trip on a long note
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        return box;
    }

    void AddRow()
    {
        if (OnCustomNames) { AddCustomName(); return; }
        if (_file is null || CurrentSheet is null) { Status(Strings.Main_OpenOrCreateAProfile); return; }
        int newRow = _file.AddBindingRow(CurrentSheet); // already reparses
        if (DeviceContainer.IsVisible)
        {
            _selectedZone = "unset"; // the new row has no input yet; take the user to it
            BuildDeviceView(); BuildZoneDetail(); RefreshIssues();
            return;
        }
        RebuildRows();
        // Take the user to the row they just created.
        AfterLayout(() =>
        {
            if (!_cellBorders.TryGetValue($"A{newRow}", out var border)) return;

            border.BringIntoView();
            (border.Child as Control)?.Focus();

            // BringIntoView alone is not reliable here: ZoomHost scales the whole
            // tree with a LayoutTransform, and at some zoom levels the request
            // resolves against stale bounds and leaves GridScroll short of the
            // row. Compute the row's own position in GridScroll's coordinate
            // space (untouched by the ancestor zoom, same units as Offset) and
            // make sure its bottom edge is inside the viewport too.
            if (border.Parent is Control row)
            {
                ScrollRowIntoView(row);
                // A focused AutoCompleteBox is invisible to a mouse-only user
                // with no cursor to find it, so also flash the row itself.
                FlashNewRow(row);
                AnimateIn(row);
            }
        });
    }

    // Clamped the same way RestoreListScroll clamps a restored offset: never
    // past the scrollable extent.
    void ScrollRowIntoView(Control row)
    {
        var bottom = row.TranslatePoint(new Point(0, row.Bounds.Height), RowsPanel);
        if (bottom is not { } p) return;
        var viewport = GridScroll.Viewport.Height;
        var maxY = Math.Max(0, GridScroll.Extent.Height - viewport);
        var targetY = Math.Clamp(p.Y - viewport, 0, maxY);
        if (targetY > GridScroll.Offset.Y)
            GridScroll.Offset = new Vector(GridScroll.Offset.X, targetY);
    }

    // Briefly tints a just-added row so a mouse-only user can see where it
    // landed, then clears it. The row reference is only ever touched inside
    // this closure, so if the list gets rebuilt (another edit) before the
    // timer fires, this just paints/clears a control nobody looks at anymore
    // rather than throwing.
    static void FlashNewRow(Control row)
    {
        BindBrush(row, Panel.BackgroundProperty, "NewRowTint");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            row.ClearValue(Panel.BackgroundProperty);
        };
        timer.Start();
    }

    // Motion is feedback, never a gate: every data change below lands
    // synchronously and these overlays only show where it went. Fire and
    // forget, so headless tests (which never tick the render timer) see the
    // exact same resting state with or without the animation.
    //
    // Every built-in animator except Opacity's turned out to be internal in
    // Avalonia 11.1 (Animation.SetAnimator only accepts a CustomAnimatorBase),
    // and the TransformAnimator path silently animates nothing, so all motion
    // here runs through this one pinned linear-double animator. Proven live
    // by ListViewTests.Deleting_a_row_really_slides_the_survivors_up.
    sealed class LinearDouble : Avalonia.Animation.InterpolatingAnimator<double>
    {
        public override double Interpolate(double progress, double oldValue, double newValue) =>
            oldValue + (newValue - oldValue) * progress;
    }

    // Internal so the animation regression test can prove this exact helper
    // moves values on a ticking clock, not a lookalike. The test overrides ms
    // because the first headless render tick can cost longer than 180ms, which
    // would end the animation before the test ever sampled it.
    internal static Avalonia.Animation.Animation Between(AvaloniaProperty prop, double from, double to,
        Avalonia.Animation.FillMode fill = Avalonia.Animation.FillMode.Backward, double ms = 180)
    {
        Avalonia.Styling.Setter S(double v)
        {
            var s = new Avalonia.Styling.Setter(prop, v);
            Avalonia.Animation.Animation.SetAnimator(s, new LinearDouble());
            return s;
        }
        return new()
        {
            Duration = TimeSpan.FromMilliseconds(ms),
            Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
            FillMode = fill, // Backward: show the start value on frame one, no pop
            Children =
            {
                new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(0), Setters = { S(from) } },
                new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(1), Setters = { S(to) } },
            },
        };
    }

    // The animation must run ON the TranslateTransform (an Animatable): run on
    // the control, Avalonia routes Y to its internal TransformAnimator, which
    // does nothing.
    static void SlideFrom(Control c, double dy)
    {
        var lift = new TranslateTransform();
        c.RenderTransform = lift;
        _ = Between(TranslateTransform.YProperty, dy, 0).RunAsync(lift);
    }

    // A just-added row or card fades in while rising into place.
    static void AnimateIn(Control c)
    {
        SlideFrom(c, 10);
        _ = Between(OpacityProperty, 0, 1).RunAsync(c);
    }

    // After a delete, everything below the gap starts shifted down by the
    // deleted row's height and settles up into place, so the eye can track
    // what moved instead of seeing the list teleport.
    static void AnimateGapClose(Panel panel, int fromChildIndex, double dy)
    {
        dy = Math.Min(dy, 120); // a mass delete should settle, not fly
        if (dy <= 0 || fromChildIndex < 0) return;
        // ponytail: first 30 children only, offscreen rows need no theater
        foreach (var c in panel.Children.Skip(fromChildIndex).Take(30))
            SlideFrom(c, dy);
    }

    // The deleted row's send-off: a bitmap snapshot of the real row, floated
    // over the list at the exact spot the row occupied, fading and drifting
    // up while the survivors slide into the gap. A snapshot rather than the
    // live control, so its buttons can never be found or clicked again.
    // Call BEFORE the delete, while the row is still attached and rendered.
    // Headless tests have no renderer; the try/catch makes this a no-op there.
    void GhostRowAway(Control row, Panel overlay)
    {
        try
        {
            if (row.Bounds.Width <= 0 || row.Bounds.Height <= 0) return;
            if (row.TranslatePoint(default, overlay) is not { } at) return;
            var scale = RenderScaling;
            var shot = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new PixelSize((int)Math.Ceiling(row.Bounds.Width * scale), (int)Math.Ceiling(row.Bounds.Height * scale)),
                new Vector(96 * scale, 96 * scale));
            shot.Render(row);
            var ghost = new Image
            {
                Source = shot, Width = row.Bounds.Width, Height = row.Bounds.Height,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Avalonia.Thickness(at.X, at.Y, 0, 0), IsHitTestVisible = false,
            };
            overlay.Children.Add(ghost);
            var lift = new TranslateTransform();
            ghost.RenderTransform = lift;
            _ = Between(TranslateTransform.YProperty, 0, -8).RunAsync(lift);
            // Forward fill holds the ghost invisible after the fade until the
            // timer reclaims it; without it the ghost would pop back solid.
            _ = Between(OpacityProperty, 1, 0, Avalonia.Animation.FillMode.Forward).RunAsync(ghost);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += (_, _) => { timer.Stop(); overlay.Children.Remove(ghost); shot.Dispose(); };
            timer.Start();
        }
        catch
        {
            // No renderer (headless) or a mid-layout surprise: skip the ghost,
            // the delete itself already happened synchronously.
        }
    }

    // A cell's border holds whatever control that column needs: an
    // AutoCompleteBox on most rows, but a Button behind a picker and, since the
    // settings editor landed, a NumericUpDown, a CheckBox or a ComboBox. These
    // used to cast to AutoCompleteBox, so on exactly the rows this release
    // added, "jump to the problem" repainted the border and moved focus
    // nowhere. Control covers all of them.

    // After deleting a List View row, keep focus on the row that slid into
    // its place instead of dropping it (mirrors AddRow's "take the user
    // there" logic in reverse).
    void FocusRowSibling(int deletedIndex)
    {
        if (CurrentSheet is { Bindings.Count: > 0 } sheet)
        {
            int idx = Math.Min(deletedIndex, sheet.Bindings.Count - 1);
            var targetRow = sheet.Bindings[idx].Row;
            if (_cellBorders.TryGetValue($"A{targetRow}", out var border))
            { border.BringIntoView(); (border.Child as Control)?.Focus(); return; }
        }
        AddRowButton.Focus(); // no rows left; hand focus to the control that adds one
    }

    // Same idea for Device View's zone detail panel: focus the mapping card
    // that took the deleted one's place, or the "add mapping" button, or a
    // zone button, so a keyboard/switch user is never left with no focus.
    void FocusZoneDetailSibling(string zoneId, int deletedIndex)
    {
        BindingsByZone().TryGetValue(zoneId, out var remaining);
        int count = remaining?.Count ?? 0;
        if (count > 0)
        {
            // The heading is child 0. Back-panel guidance, when present, is
            // child 1, so the first mapping follows it rather than the old
            // permanent description line.
            int firstMapping = zoneId is "jacks" or "other" ? 2 : 1;
            int childIndex = firstMapping + Math.Min(deletedIndex, count - 1);
            if (childIndex < ZoneDetailPanel.Children.Count
                && ZoneDetailPanel.Children[childIndex] is Control card
                && FindFocusable(card) is { } target)
            { target.BringIntoView(); target.Focus(); return; }
        }
        if (ZoneDetailPanel.Children.OfType<Button>().FirstOrDefault() is { } addButton)
        { addButton.Focus(); return; }
        if (_zoneButtons.TryGetValue(zoneId, out var zoneButton)) zoneButton.Focus();
        else if (_zoneButtons.Count > 0) _zoneButtons.Values.First().Focus();
    }

    // Depth-first search for the first AutoCompleteBox or Button under a
    // mapping card's Border/StackPanel wrapper tree.
    static Control? FindFocusable(Control root) => root switch
    {
        AutoCompleteBox or Button or ComboBox => root,
        Border { Child: Control c } => FindFocusable(c),
        Panel p => p.Children.Select(FindFocusable).FirstOrDefault(f => f != null),
        _ => null,
    };

    // Jumps focus to the field an issue is about: the file name box for
    // filename problems, or the matching grid cell otherwise. Switches mode
    // sheet and, in Device View, zone selection first if the cell lives
    // somewhere not currently on screen.
    void FocusIssueCell(Issue issue)
    {
        if (_file is null) return;
        if (issue.Cell == $"A{_file.Document.FileNameCellRow}")
        { FileNameBox.Focus(); return; }

        if (int.TryParse(issue.Cell.AsSpan(1), out int row))
        {
            int sheetIdx = _file.Document.Sheets.FindIndex(s => s.Bindings.Any(b => b.Row == row));
            if (sheetIdx >= 0 && sheetIdx != _sheetIndex)
                SelectSheet(sheetIdx); // refreshes the editor synchronously

            if (_deviceView && sheetIdx >= 0)
            {
                var binding = _file.Document.Sheets[sheetIdx].Bindings.First(b => b.Row == row);
                var zoneId = binding.Inputs.Count > 0 ? ZoneOf(binding.Inputs[0]) : "unset";
                // In card mode a closed mapping has no cells at all, so jumping
                // to a problem landed on nothing and looked like a dead click.
                // Select the part AND open the row, then rebuild, every time:
                // the row can be the wrong one even when the part is right.
                _selectedZone = zoneId;
                _expandedMapping = row;
                BuildDeviceView(); BuildZoneDetail();
            }
        }

        if (_cellBorders.TryGetValue(issue.Cell, out var border))
        {
            border.BringIntoView();
            (border.Child as Control)?.Focus();
        }
    }

    void RefreshIssues()
    {
        foreach (var b in _cellBorders.Values)
        {
            b.BorderBrush = Brushes.Transparent;
            // Keep the same thickness an errored cell gets below, so toggling a
            // problem only recolors the border and never reflows the row height.
            b.BorderThickness = new Avalonia.Thickness(3);
            if (b.Child is Control c) AutomationProperties.SetName(b, AutomationProperties.GetName(c));
        }
        if (_file is null) { IssuesList.ItemsSource = null; return; }

        IssuesList.ItemsSource = _file.Issues.Count == 0
            ? new List<Control>
              {
                  new TextBlock { Text = Strings.Main_NoProblemsFound, FontSize = Size("SmallSize"),
                                  Classes = { "success" }, Margin = new Avalonia.Thickness(4) },
              }
            : _file.Issues
                .OrderBy(i => i.Severity == Severity.Error ? 0 : 1)
                .Select(IssueItem)
                .ToList();

        foreach (var issue in _file.Issues)
            if (_cellBorders.TryGetValue(issue.Cell, out var border))
            {
                var severityLabel = issue.Severity == Severity.Error ? "Error" : "Warning";
                BindBrush(border, Border.BorderBrushProperty, severityLabel);
                border.BorderThickness = new Avalonia.Thickness(3);
                var baseName = border.Child is Control c ? AutomationProperties.GetName(c) : null;
                AutomationProperties.SetName(border, string.Format(CultureInfo.CurrentCulture, Strings.Main_SeverityLabelBaseName, severityLabel, baseName));
            }

        var errors = _file.Issues.Count(i => i.Severity == Severity.Error);
        var warns = _file.Issues.Count - errors;
        // Only errors block install, so a file with none is not told about
        // them. "0 errors, 2 warnings. Errors block installing." read as a
        // refusal on a profile that installs fine.
        Status(errors + warns == 0
                ? Strings.Main_NoProblemsReadyToSave
                : errors == 0
                    ? Plural.Of(warns, "Count_Warning") + Strings.Main_TheDeviceSkipsThoseRows
                    : Plural.Of(errors, "Count_Error") + ", " + Plural.Of(warns, "Count_Warning") + Strings.Main_ErrorsBlockInstalling,
            errors > 0 ? StatusKind.Error : warns > 0 ? StatusKind.Warning : StatusKind.Ready);
        UpdateProblemsToggle();
    }

    // One row in the problems list. Unknown-input errors get a one-click cure:
    // old profiles keep notes in the input columns (C..J), which the device
    // reads as inputs; moving the text to the notes column keeps the note and
    // clears the error.
    Control IssueItem(Issue i)
    {
        var tb = new TextBlock
        {
            Text = i.ToString(),
            TextWrapping = TextWrapping.Wrap,
            FontSize = Size("SmallSize"),
            Classes = { i.Severity == Severity.Error ? "error" : "warn" },
            Tag = i, // lets SelectionChanged/Fix-first find the cell to focus
        };
        if (i.Kind != IssueKind.UnknownInput) return tb;

        var fix = new Button
        {
            Content = Strings.Main_MoveToNotes, Classes = { "quiet" },
            Margin = new Avalonia.Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(fix, string.Format(CultureInfo.CurrentCulture, Strings.Main_MoveTheTextInCell, i.Cell));
        fix.Click += (_, _) => MoveIssueTextToNotes(i);
        return new StackPanel { Children = { tb, fix }, Tag = i };
    }

    void MoveIssueTextToNotes(Issue i)
    {
        if (_file is null || !int.TryParse(i.Cell.AsSpan(1), out int row)) return;
        _file.MoveInputToNotes(row, i.Cell[0] - 'A');
        if (_deviceView) { BuildDeviceView(); BuildZoneDetail(); RefreshIssues(); }
        else { var off = GridScroll.Offset; RebuildRows(); RestoreListScroll(off, () => { }); }
        Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_MovedTheTextFromI, i.Cell), StatusKind.Info);
    }

    // ---- Small shared UI builders for the redesigned editor ----

    // A compact aligned row: a short muted label in a fixed-width first column,
    // the field filling the rest. Collapses the old label-above-field pairs so a
    // mapping reads across in far less vertical space.
    // The box one expanded mapping sits in. Shared so a settings row, which
    // has no inputs to show, comes out looking like every other card.
    Border MappingCard(Control body)
    {
        var card = new Border
        {
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(6),
            Padding = new Avalonia.Thickness(14),
            Child = body,
        };
        BindBrush(card, Border.BackgroundProperty, "Surface");
        BindBrush(card, Border.BorderBrushProperty, "SurfaceBorder");
        return card;
    }

    static Control Labeled(string label, Control field)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        g.Children.Add(new TextBlock
        {
            Text = label, FontSize = Size("SmallSize"), FontWeight = FontWeight.SemiBold, Classes = { "muted" },
            VerticalAlignment = VerticalAlignment.Center, MinWidth = 76, Margin = new Avalonia.Thickness(0, 0, 10, 0),
        });
        Grid.SetColumn(field, 1);
        g.Children.Add(field);
        return g;
    }

    static PathIcon Glyph(string iconKey, string tokenKey)
    {
        var icon = new PathIcon { Width = 16, Height = 16, Data = (Geometry)Application.Current!.FindResource(iconKey)! };
        BindBrush(icon, IconElement.ForegroundProperty, tokenKey);
        return icon;
    }

    static Control AddRowContent(string label)
    {
        // The localized labels conventionally begin with "+". The icon carries
        // that job in the toolbar, so remove just that duplicate marker while
        // retaining the translated action name.
        string text = label.StartsWith("+ ", StringComparison.Ordinal) ? label[2..] : label;
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            Children =
            {
                Glyph("IconAdd", "OnAccent"),
                new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
            },
        };
    }

    static Button IconButton(string iconKey, string accessibleName)
    {
        var b = new Button { Classes = { "icon" }, Content = Glyph(iconKey, "TextSecondary") };
        AutomationProperties.SetName(b, accessibleName);
        return b;
    }

    // Function picker: a real dropdown (opens on click, no typing needed) whose
    // items spell out in plain words what each behavior does, so the user never
    // has to already know what "toggle" or "pulse" means. Exotic values with
    // parameters (e.g. "repeat 5 2000") stay selectable so nothing is lost.
    Control FunctionCombo(Binding b, Zone zone)
    {
        var current = (b.Function ?? "").Trim();
        var tokens = current.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var firstToken = tokens.FirstOrDefault() ?? "";
        bool known = Vocab.FunctionArity.ContainsKey(firstToken);
        var currentParams = known ? string.Join(' ', tokens.Skip(1)) : "";

        var items = new List<string>(FunctionSuggestions);
        // An unknown value (e.g. a typo or a form the list doesn't carry) stays
        // selectable exactly as before; a known value with parameters shows the
        // bare name in the list and edits its values in the box below.
        if (!known && current.Length > 0 && !items.Contains(current)) items.Insert(0, current);

        var combo = new ComboBox { ItemsSource = items, HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.SelectedItem = known ? firstToken : items.FirstOrDefault(x => x == current);
        combo.ItemTemplate = new FuncDataTemplate<string>((name, _) =>
        {
            var sp = new StackPanel { Spacing = 1, Margin = new Avalonia.Thickness(0, 2) };
            sp.Children.Add(new TextBlock { Text = TokenLabel(name), FontWeight = FontWeight.SemiBold, FontSize = Size("BodySize") });
            var d = FunctionExplain(name);
            if (d.Length > 0)
                sp.Children.Add(new TextBlock { Text = d, FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });
            return sp;
        });
        AutomationProperties.SetName(combo, string.Format(CultureInfo.CurrentCulture, Strings.Main_HowShortInputZoneBPresses, ShortInput(zone, b), FunctionExplain(current)));

        bool startHasParams = Vocab.FunctionArity.TryGetValue(firstToken, out var startArity) && startArity.Max > 0;
        var paramsBox = new TextBox
        {
            Text = currentParams,
            Watermark = ParameterWatermark(firstToken),
            FontSize = Size("SmallSize"),
            Margin = new Avalonia.Thickness(0, 4, 0, 0),
            IsVisible = startHasParams,
        };
        AutomationProperties.SetName(paramsBox, ParameterAccessibleName(firstToken));

        // The ranges and defaults sit under the box, not in a tooltip: a
        // tooltip is unreachable by keyboard and silent to a screen reader,
        // and this is the guidance somebody needs before typing, not after.
        var paramsHint = new TextBlock
        {
            Text = ParameterHint(firstToken),
            FontSize = Size("SmallSize"),
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 2, 0, 0),
            IsVisible = startHasParams,
        };

        void Commit()
        {
            if (_file is null || combo.SelectedItem is not string name) return;
            // The raw exotic value has no arity: commit it as-is, never append params.
            if (!Vocab.FunctionArity.TryGetValue(name, out var arity))
            {
                if (name != _file.GetCell(b.Row, 1)) { _file.SetCell(b.Row, 1, name); RebuildDeviceAfterEdit(b.Row, 1); }
                return;
            }
            var p = (paramsBox.Text ?? "").Trim();
            var value = p.Length > 0 && arity.Max > 0 ? $"{name} {p}" : name;
            // Equality guard: RebuildDeviceAfterEdit replaces these controls, so a
            // stale LostFocus firing afterward must land as a no-op, not a loop.
            if (value != _file.GetCell(b.Row, 1)) { _file.SetCell(b.Row, 1, value); RebuildDeviceAfterEdit(b.Row, 1); }
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string name)
            {
                bool hasParams = Vocab.FunctionArity.TryGetValue(name, out var ar) && ar.Max > 0;
                paramsBox.IsVisible = hasParams;
                paramsHint.IsVisible = hasParams;
                paramsHint.Text = ParameterHint(name);
                paramsBox.Watermark = ParameterWatermark(name);
                AutomationProperties.SetName(paramsBox, ParameterAccessibleName(name));
                if (!hasParams) paramsBox.Text = "";
            }
            Commit();
        };
        paramsBox.LostFocus += (_, _) => Commit();
        paramsBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };

        // Register the function cell like the input/output fields so a function
        // error (bad name, too many params) can be highlighted and focused here
        // too. Without the wrapper, B{row} lives nowhere in _cellBorders.
        // RefreshIssues mirrors the wrapper child's accessible name onto the
        // highlight; the panel needs the combo's name or an error reads as nothing.
        var stack = new StackPanel { Children = { combo, paramsBox, paramsHint } };
        AutomationProperties.SetName(stack, AutomationProperties.GetName(combo));
        var wrapper = new Border
        {
            Child = stack,
            BorderThickness = new Avalonia.Thickness(2),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new Avalonia.CornerRadius(5),
        };
        _cellBorders[$"B{b.Row}"] = wrapper;
        return wrapper;
    }

    // The item that reveals a free-text box at the very bottom of a Device View
    // dropdown, so an exotic value is still reachable without making typing the
    // default. Reference-compared, never shown as a real token.
    static string TypeYourOwn => Strings.Main_TypeYourOwn;

    // A pick-don't-type field for Device View: a dropdown of known tokens shown
    // in the current label style, committing the raw token to the cell. The
    // last entry drops to a text box for anything not on the list. Keeps the
    // wrapper registered in _cellBorders so problem highlighting still lands.
    Control TokenField(int row, int col, string current, IReadOnlyList<string> options,
                       Func<string, string> labelFor, string accessibleName, string tintKey)
    {
        var wrapper = new Border
        {
            BorderThickness = new Avalonia.Thickness(2),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new Avalonia.CornerRadius(5),
        };
        _cellBorders[$"{(char)('A' + col)}{row}"] = wrapper;

        void ShowCombo()
        {
            var items = new List<string>(options);
            var cur = (current ?? "").Trim();
            if (cur.Length > 0 && !items.Contains(cur)) items.Insert(0, cur);
            items.Add(TypeYourOwn);

            var combo = new ComboBox { ItemsSource = items, HorizontalAlignment = HorizontalAlignment.Stretch };
            combo.SelectedItem = cur.Length > 0 ? items.FirstOrDefault(x => x == cur) : null;
            combo[!TemplatedControl.BackgroundProperty] = new DynamicResourceExtension(tintKey + "Brush");
            combo.ItemTemplate = new FuncDataTemplate<string>((token, _) =>
            {
                bool own = ReferenceEquals(token, TypeYourOwn);
                var tb = new TextBlock
                {
                    Text = own ? TypeYourOwn : labelFor(token),
                    FontSize = Size("BodySize"),
                    TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 2),
                };
                if (own) tb.Classes.Add("muted");
                return tb;
            });
            AutomationProperties.SetName(combo, accessibleName);
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is not string s || _file is null) return;
                // Swapping the child synchronously here fights the ComboBox as it
                // closes its own popup, so the text box never appears. Defer it.
                if (ReferenceEquals(s, TypeYourOwn)) { Dispatcher.UIThread.Post(ShowTyping); return; }
                if (s == _file.GetCell(row, col)) return;
                _file.SetCell(row, col, s);
                RebuildDeviceAfterEdit(row, col);
            };
            wrapper.Child = combo;
        }

        void ShowTyping()
        {
            var box = new AutoCompleteBox
            {
                Text = "", ItemsSource = options, FilterMode = AutoCompleteFilterMode.Contains,
                MinimumPrefixLength = 0, HorizontalAlignment = HorizontalAlignment.Stretch,
                Watermark = Strings.Main_TypeAValueOrLeave,
            };
            box[!TemplatedControl.BackgroundProperty] = new DynamicResourceExtension(tintKey + "Brush");
            AutomationProperties.SetName(box, accessibleName + Strings.Main_TypeACustomValue);
            void Commit()
            {
                if (_file is null) return;
                var v = (box.Text ?? "").Trim();
                if (v.Length == 0) { ShowCombo(); return; } // empty = cancel, back to the list
                if (v != _file.GetCell(row, col)) _file.SetCell(row, col, v);
                RebuildDeviceAfterEdit(row, col);
            }
            // Replacing the ComboBox briefly moves focus while the new box is
            // being attached. That blur is not the user leaving an empty field.
            // Only an actual focus loss after the box was usable may cancel it.
            bool armed = false;
            box.GotFocus += (_, _) => armed = true;
            box.LostFocus += (_, _) => { if (armed) Commit(); };
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
            wrapper.Child = box;
            Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Loaded);
        }

        ShowCombo();
        return wrapper;
    }

    // The Press field in Device View: the drill-down picker committing
    // through the device rebuild, so the card refreshes and refocuses.
    // An action name is shown as typed; only real tokens get translated.
    Control OutputPicker(Binding b, OutputCatalog.ProfileOutputs outputs,
                         string accessibleName, string tintKey)
    {
        var wrapper = new Border
        {
            BorderThickness = new Avalonia.Thickness(2),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new Avalonia.CornerRadius(5),
        };
        _cellBorders[$"A{b.Row}"] = wrapper;
        return PickerCell(wrapper, OutputFieldValue(b), outputs.Options,
            t => outputs.TokenFor.ContainsKey(t) ? t : TokenLabel(t),
            accessibleName, tintKey, outputs.Catalog, Strings.Main_AnOutput, picked =>
            {
                CommitOutput(b.Row, outputs, picked);
                RebuildDeviceAfterEdit(b.Row, 0);
            },
            token =>
            {
                var label = outputs.TokenFor.ContainsKey(token) ? token : TokenLabel(token);
                return OutputVisuals.Render(VisualFor(token, _ => label));
            });
    }

    // An input field in Device View that is not tied to the part on screen:
    // the same drill-down picker with search the List View uses, grouped by
    // part, so a second input can reach anything on the device.
    Control DeviceInputPicker(int row, int col, string current, string accessibleName, string cardZone)
    {
        var wrapper = new Border
        {
            BorderThickness = new Avalonia.Thickness(2),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new Avalonia.CornerRadius(5),
        };
        _cellBorders[$"{(char)('A' + col)}{row}"] = wrapper;
        return PickerCell(wrapper, current, InputSuggestions, t => InputOptionLabel(t, cardZone),
            accessibleName, InputTint, InputCatalog, Strings.Main_AnInput, token =>
            {
                if (_file is null || token == _file.GetCell(row, col)) return;
                _file.SetCell(row, col, token);
                RebuildDeviceAfterEdit(row, col);
            });
    }

    // Inputs group by the part of the device they live on, in AllZones order.
    // Switch jacks then split again by the socket on the back, so picking one
    // is "top jack, one switch" rather than knowing that means digital_in_8.
    //
    // Only Switch jacks gets a second level. ZoneOf sends everything it does
    // not recognise to "other", typos included, and a category with a SubOrder
    // shows nothing but its listed sockets: a token with no sub would be
    // unreachable in the Detailed picker. Every digital_in has one, so this
    // category is safe and USB devices is not. InputPickerGroupingTests holds
    // that line.
    static TokenCatalog? _inputCatalog;
    static TokenCatalog InputCatalog => _inputCatalog ??= new(
        t => (AllZones.First(z => z.Id == ZoneOf(t)).Title, SwitchJacks.PortLabel(SwitchJacks.For(t)?.Port ?? "")),
        AllZones.Select(z => z.Title).ToArray(),
        new Dictionary<string, string[]>
        {
            [Strings.Main_SwitchJacks] = SwitchJacks.Ports.Select(p => SwitchJacks.PortLabel(p.Port)).ToArray(),
        });

    // A List View cell backed by the drill-down picker. Commits like
    // SuggestBox did: set the cell, refresh issues, and rebuild the rows
    // when an input appears or disappears (its remove and plus buttons
    // change with it).
    // setValue replaces the plain "write this cell" commit. The output column
    // uses it: a pick there writes two cells, and the value on screen is the
    // row's action name, not the cell's text, so the usual no-op guard would
    // swallow a pick that only clears the name.
    // labelFor is how the cell reads, never what it writes: a pick still
    // commits the raw token. Without it every list cell showed the file's own
    // bytes, so the Words switch changed the output column and left the input
    // beside it saying mp_right_sip.
    Control ListPickerCell(int row, int col, string value, IReadOnlyList<string> options,
                           string accessibleName, string tintKey, TokenCatalog? catalog, string pickWord,
                           Action<string>? setValue = null, Func<string, Control>? visualFor = null,
                           Func<string, string>? labelFor = null)
    {
        var wrapper = new Border
        {
            // Match the thickness RefreshIssues sets on an errored cell, so
            // flagging a problem never reflows the row (see SuggestBox).
            BorderThickness = new Avalonia.Thickness(3),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new Avalonia.CornerRadius(5),
        };
        var key = $"{(char)('A' + col)}{row}";
        _cellBorders[key] = wrapper;
        return PickerCell(wrapper, value, options, labelFor ?? (t => t), accessibleName, tintKey, catalog, pickWord, token =>
        {
            if (_file is null) return;
            var old = _file.GetCell(row, col);
            bool wasSetting = IsSettingRow(row);
            var wasDefinition = SettingDefinition(row);
            if (setValue is null)
            {
                if (token == old) return;
                _file.SetCell(row, col, token);
            }
            else setValue(token);
            RefreshIssues();
            // The remove control beside an input only appears when a row has
            // more than one, so the LAST input is taken off through this picker,
            // by emptying it or by choosing the device's own word for nothing.
            // That is the edit most worth a word, and it was the one edit that
            // said nothing.
            if (col is >= 2 and < 10) SayIfNothingFiresIt(row);
            // An input appearing or disappearing changes the row's own
            // controls, so rebuild. Deferred: the flyout is still closing.
            //
            // So does column A or B flipping the row between a binding and a
            // setting, because the two put different controls on column C.
            // Without this the row kept the editor for what it used to be: a
            // number spinner left over an input cell wrote a bare number into
            // it on the next click.
            //
            // And so does one setting becoming another, which the flip test
            // above cannot see because both sides stay true. Column C would keep
            // the control built for the preference before: a spinner bounded 0
            // to 250 left over a toggle writes an out of range number into a 0
            // or 1 setting. The output picker offers game buttons only, so
            // nothing can reach this today; it is here because the Preferences
            // sheet has had the same guard since it was written and two rules
            // for the same thing are how they drift apart.
            if ((col is >= 2 and < 10 && (old.Length == 0) != (token.Length == 0))
                // Any replaced input can change the duplicate count on this
                // row and every other row using the old or new token. Rebuild
                // after the flyout closes, without making the person change views.
                || (setValue is null && col is >= 2 and < 10)
                || (col is 0 or 1 && (IsSettingRow(row) != wasSetting
                                      || SettingDefinition(row) != wasDefinition)))
            {
                var off = GridScroll.Offset;
                Dispatcher.UIThread.Post(() =>
                {
                    RebuildRows();
                    RestoreListScroll(off, () =>
                    {
                        if (_cellBorders.TryGetValue(key, out var border))
                        { border.BringIntoView(); (border.Child as Control)?.Focus(); }
                    });
                });
            }
        }, visualFor);
    }

    // The drill-down picker every big token list shares. The field is a
    // button whose dropdown (a flyout, so the layout never shifts) holds a
    // search box pinned over one level of a menu: categories from the
    // catalog, Back as the first row inside one, items at the bottom of the
    // drill. No catalog means the options are few enough to list flat.
    // Typing in the search replaces whatever level is showing with flat
    // matches. Picking hands the raw token to commitCell.
    Control PickerCell(Border wrapper, string current, IReadOnlyList<string> options,
                       Func<string, string> labelFor, string accessibleName, string tintKey,
                       TokenCatalog? catalog, string pickWord, Action<string> commitCell,
                       Func<string, Control>? visualFor = null)
    {
        var cur = (current ?? "").Trim();
        var all = new List<string>(options);
        if (cur.Length > 0 && !all.Contains(cur)) all.Insert(0, cur);

        var fly = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        var openLabel = new TextBlock { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        Button? openButton = null;

        Control ValueContent(string token) => token.Length == 0 || visualFor is null
            ? openLabel
            : visualFor(token);

        // An empty cell must read as empty at a glance: the placeholder is
        // muted and italic, a real value is plain and full strength.
        void ShowValue(string token)
        {
            bool empty = token.Length == 0;
            openLabel.Text = empty
                ? string.Format(CultureInfo.CurrentCulture, Strings.Main_PickWord, pickWord)
                : labelFor(token);
            openLabel.FontStyle = empty ? FontStyle.Italic : FontStyle.Normal;
            openLabel.Classes.Set("muted", empty);
            // The cell is one line and trims, so a long value ends in an
            // ellipsis. The tip is where the rest of it stays reachable.
            if (openButton is not null)
            {
                openButton.Content = ValueContent(token);
                ToolTip.SetTip(openButton, empty ? null : labelFor(token));
            }
        }
        ShowValue(cur);

        void Commit(string token)
        {
            fly.Hide();
            cur = token;
            // Refresh the closed button in place; a commit that rebuilds the
            // whole view just throws this away, which is fine.
            ShowValue(token);
            commitCell(token);
        }

        Button Item(string token)
        {
            var it = new Button
            {
                Content = visualFor is null
                    ? new TextBlock
                    { Text = labelFor(token), FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap }
                    : visualFor(token),
                Classes = { "quiet" },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            AutomationProperties.SetName(it, labelFor(token));
            it.Click += (_, _) => Commit(token);
            return it;
        }

        var search = new TextBox { Watermark = Strings.Main_Search };
        AutomationProperties.SetName(search, Strings.Main_SearchThisList);
        var body = new StackPanel { Spacing = 2 };
        var scroll = new ScrollViewer { Content = body, MaxHeight = 400 };

        List<string> TokensIn(string cat, string? sub) => all
            .Where(t => t != "none" && catalog!.Classify(t) is var c && c.Cat == cat
                     && (sub is null || c.Sub == sub))
            .ToList();

        Button NavButton(string title, int count, Action go)
        {
            var line = new DockPanel();
            var chevron = Glyph("IconChevron", "TextSecondary");
            DockPanel.SetDock(chevron, Dock.Right);
            line.Children.Add(chevron);
            line.Children.Add(new TextBlock
            { Text = string.Format(CultureInfo.CurrentCulture, Strings.Main_TitleCount, title, count), FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap });
            // No "quiet" here on purpose: a category keeps the bordered
            // button look so "opens more" reads differently from the flat
            // rows that pick an output.
            var it = new Button
            {
                Content = line,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(it, string.Format(CultureInfo.CurrentCulture, Strings.Main_TitleCountOptionsOpensThis, title, count));
            it.Click += (_, _) => go();
            return it;
        }

        // Flat files nothing: one searchable list, the way somebody who already
        // knows the token wants it. Wide keeps the categories and drops the
        // level under them.
        var grouping = _settings.PickerGrouping;
        if (grouping == "Flat") catalog = null;

        void ShowLevel(string? cat, string? sub)
        {
            body.Children.Clear();
            scroll.ScrollToHome(); // a new level always starts at its top
            if (cat is null)
            {
                // Top level: the do-nothing output first, then the categories.
                // A short list has no catalog and just shows its items.
                if (all.Contains("none")) body.Children.Add(Item("none"));
                if (catalog is null)
                {
                    foreach (var t in all.Where(t => t != "none")) body.Children.Add(Item(t));
                    return;
                }
                foreach (var c in catalog.CategoryOrder)
                {
                    var tokens = TokensIn(c, null);
                    if (tokens.Count == 0) continue;
                    var name = c;
                    body.Children.Add(NavButton(name, tokens.Count, () => ShowLevel(name, null)));
                }
                return;
            }

            var back = new Button
            {
                Content = new TextBlock { Text = Strings.Main_Back, FontSize = Size("BodySize") },
                Classes = { "quiet" },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            AutomationProperties.SetName(back,
                sub is null ? Strings.Main_BackToAllCategories : string.Format(CultureInfo.CurrentCulture, Strings.Main_BackToCat, cat));
            back.Click += (_, _) => { if (sub is null) ShowLevel(null, null); else ShowLevel(cat, null); };
            body.Children.Add(back);
            body.Children.Add(new TextBlock
            { Text = sub ?? cat, FontSize = Size("SmallSize"), Classes = { "muted" } });

            if (grouping == "Detailed" && sub is null && catalog!.SubOrder.TryGetValue(cat, out var subs))
            {
                foreach (var s in subs)
                {
                    var tokens = TokensIn(cat, s);
                    if (tokens.Count == 0) continue;
                    var name = s;
                    body.Children.Add(NavButton(name, tokens.Count, () => ShowLevel(cat, name)));
                }
                return;
            }

            // Wide never opens a subcategory, so a category shows everything
            // under it, not only the tokens filed directly on it.
            var items = TokensIn(cat, grouping == "Wide" ? null : sub ?? "");
            // Alphabetical puts f1, f10, f11 ... f2; sort by number.
            if (sub == Strings.Main_FunctionKeys) items = items.OrderBy(t => int.Parse(t.AsSpan(4))).ToList();
            foreach (var t in items) body.Children.Add(Item(t));
        }

        void ShowMatches(string q)
        {
            body.Children.Clear();
            scroll.ScrollToHome();
            var hits = all.Where(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)
                                   || labelFor(t).Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var t in hits.Take(40)) body.Children.Add(Item(t));
            if (hits.Count > 40)
                body.Children.Add(new TextBlock
                {
                    Text = string.Format(CultureInfo.CurrentCulture, Strings.Main_HitsCount40MoreKeep, hits.Count - 40),
                    FontSize = Size("SmallSize"), Classes = { "muted" },
                });
            if (hits.Count == 0)
                body.Children.Add(new TextBlock
                {
                    Text = Strings.Main_NothingMatchesTryFewerLetters,
                    FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
                });
        }

        search.TextChanged += (_, _) =>
        {
            var q = (search.Text ?? "").Trim();
            if (q.Length == 0) ShowLevel(null, null); else ShowMatches(q);
        };

        void ShowTyping()
        {
            var box = new AutoCompleteBox
            {
                Text = "", ItemsSource = options, FilterMode = AutoCompleteFilterMode.Contains,
                MinimumPrefixLength = 0, HorizontalAlignment = HorizontalAlignment.Stretch,
                Watermark = Strings.Main_TypeAValueOrLeave2,
            };
            box[!TemplatedControl.BackgroundProperty] = new DynamicResourceExtension(tintKey + "Brush");
            AutomationProperties.SetName(box, accessibleName + Strings.Main_TypeACustomValue);
            var field = wrapper.Child; // the button to restore on cancel
            void Done()
            {
                var v = (box.Text ?? "").Trim();
                if (v.Length == 0) { wrapper.Child = field; return; }
                Commit(v);
            }
            // The closing flyout hands focus around; a blur the box never
            // owned must not count as "left blank, cancel". Only a blur
            // after the box really held focus does.
            bool armed = false;
            box.GotFocus += (_, _) => armed = true;
            box.LostFocus += (_, _) => { if (armed) Done(); };
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Done(); };
            wrapper.Child = box;
            Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Loaded);
        }

        var typeOwn = new Button { Content = TypeYourOwn, Classes = { "quiet" } };
        AutomationProperties.SetName(typeOwn, Strings.Main_TypeACustomValue2);
        // Swap after the flyout has fully closed and given focus back, or
        // the swap and the close fight over focus and the box dies unused.
        typeOwn.Click += (_, _) => { fly.Hide(); Dispatcher.UIThread.Post(ShowTyping); };

        var panel = new StackPanel { Spacing = 6, MinWidth = 300 };
        panel.Children.Add(search);
        panel.Children.Add(scroll);
        panel.Children.Add(typeOwn);
        fly.Content = panel;

        // Every open starts fresh at the top level with an empty search; the
        // menu builds on open, not for every mapping card on screen.
        fly.Opened += (_, _) =>
        {
            if ((search.Text ?? "").Length > 0) search.Text = ""; // rebuilds via TextChanged
            else ShowLevel(null, null);
            Dispatcher.UIThread.Post(() => search.Focus(), DispatcherPriority.Loaded);
        };

        var open = new Button
        {
            Content = ValueContent(cur),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Flyout = fly,
        };
        openButton = open;
        open[!TemplatedControl.BackgroundProperty] = new DynamicResourceExtension(tintKey + "Brush");
        AutomationProperties.SetName(open, string.Format(CultureInfo.CurrentCulture, Strings.Main_AccessibleNameOpensASearchableList, accessibleName));
        wrapper.Child = open;
        return wrapper;
    }

    // A dismissable popup anchored to its "?" button: the answer is one click
    // away and never clutters the editing surface.
    static void ShowInfoFlyout(Control anchor, string title, string body)
    {
        var content = new StackPanel
        {
            Spacing = 8, MaxWidth = 340, Margin = new Avalonia.Thickness(4),
            Focusable = true, // focus lands here so screen readers read the tip, not silence
        };
        content.Children.Add(new TextBlock
        { Text = title, FontWeight = FontWeight.Bold, FontSize = Size("SubheadSize"), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock
        { Text = body, FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap, LineHeight = 21 });
        AutomationProperties.SetName(content, string.Format(CultureInfo.CurrentCulture, Strings.Main_TitleBodyPressEscapeTo, title, body));
        AutomationProperties.SetLiveSetting(content, AutomationLiveSetting.Polite);
        var flyout = new Flyout { Content = content, Placement = PlacementMode.Bottom };
        flyout.Opened += (_, _) => content.Focus();
        flyout.ShowAt(anchor);
    }

    bool _problemsExpanded;

    void ToggleProblems()
    {
        _problemsExpanded = !_problemsExpanded;
        ProblemsListBorder.IsVisible = _problemsExpanded;
        UpdateProblemsToggle();
    }

    // The bottom problems bar: always a slim one-line summary; the full list
    // expands above it only when asked. Icon + count read at a glance.
    void UpdateProblemsToggle()
    {
        int errors = 0, warns = 0;
        if (_file != null)
        {
            errors = _file.Issues.Count(i => i.Severity == Severity.Error);
            warns = _file.Issues.Count - errors;
        }
        string iconKey, token, label;
        if (errors + warns == 0)
        {
            iconKey = "IconCheck"; token = "Success";
            label = _problemsExpanded ? Strings.Main_NoProblemsClickToHide : Strings.Main_NoProblems;
        }
        else
        {
            // Plural.Of, not an "s" glued on: Count_Error and Count_Warning
            // already exist and every other count in the app goes through them.
            // The pseudo run drew this one button in English.
            var parts = new List<string>();
            if (errors > 0) parts.Add(Plural.Of(errors, "Count_Error"));
            if (warns > 0) parts.Add(Plural.Of(warns, "Count_Warning"));
            iconKey = errors > 0 ? "IconError" : "IconWarning";
            token = errors > 0 ? "Error" : "Warning";
            label = string.Join(", ", parts) + (_problemsExpanded ? Strings.Main_ClickToHide : Strings.Main_ClickToView);
        }
        var text = new TextBlock { Text = label, FontSize = Size("BodySize"), VerticalAlignment = VerticalAlignment.Center };
        BindBrush(text, TextBlock.ForegroundProperty, token);
        ProblemsToggle.Content = new StackPanel
        { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Glyph(iconKey, token), text } };
        // The visible label carries the live count ("2 errors"); mirror it to the
        // screen-reader name so it never reads a stale "show or hide" while the
        // eye sees a number. The glyph+text content itself isn't announced.
        AutomationProperties.SetName(ProblemsToggle,
            string.Format(CultureInfo.CurrentCulture,
                _problemsExpanded ? Strings.Main_HidesTheProblems : Strings.Main_ShowsTheProblems, label));
        FixFirstButton.IsVisible = _problemsExpanded && errors > 0;
        // The validation/status line now lives in this bottom status area, so
        // keep the area present even when the file is clean. This preserves
        // save/install feedback without adding another toolbar row above the
        // device.
        ProblemsDock.IsVisible = true;
    }

    static string FunctionToken(string function) =>
        (function ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    // The numbers beside a function are where people guess, and a guess here is
    // silent: "greater_than 250" is a strength no input reaches, so the row
    // simply never fires and nothing says why. Each line names one number, its
    // unit, how far it goes, and what the device substitutes when it is left
    // out. All of it read off firmware 2373, see FunctionParameters.
    internal static string ParameterHint(string function)
    {
        var spec = FunctionParameters.For(FunctionToken(function));
        return spec.Count == 0 ? "" : string.Join("\n", spec.Select(p => p.Sentence));
    }

    // A screen reader gets the same sentences the sighted user reads under the
    // box, because the ranges are the whole point of the field.
    internal static string ParameterAccessibleName(string function)
    {
        var spec = FunctionParameters.For(FunctionToken(function));
        return spec.Count == 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.Main_FunctionTakesNoNumbers, function)
            : string.Format(CultureInfo.CurrentCulture, Strings.Main_NumbersForFunctionOptionalWhole, function)
              + string.Join(" ", spec.Select(x => x.Sentence));
    }

    // What to put in an empty box: the parameter names in order, so the shape
    // of "repeat 5 2000" is visible before anything is typed.
    internal static string ParameterWatermark(string function)
    {
        var spec = FunctionParameters.For(FunctionToken(function));
        return spec.Count == 0 ? ""
            : "optional: " + string.Join("  ", spec.Select(x => x.Label.ToLowerInvariant()));
    }

    // Plain words for what a Function does, keyed on its first token so
    // "repeat 5 2000" still explains. Empty when blank or unknown, so the
    // note only appears once a behavior is actually chosen.
    internal static string FunctionExplain(string function)
    {
        var name = (function ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return name switch
        {
            "normal" => Strings.Main_HeldDownForAsLong,
            "toggle" => Strings.Main_OneActivationLatchesItOn,
            "repeat" => Strings.Main_RapidFireTapsWhileYour,
            "pulse" => Strings.Main_OneShortPressEachTime,
            "delayed_latch" => Strings.Main_AShortActivationTapsIt,
            "delay_on" => Strings.Main_WaitsAMomentAfterYou,
            "delay_off" => Strings.Main_KeepsPressingForAMoment,
            "tap" => Strings.Main_AQuickPressSendsOne,
            "force_off" => Strings.Main_TurnsOffAnOutputThat,
            "greater_than" => Strings.Main_FiresOnceYourInputPasses,
            "less_than" => Strings.Main_FiresWhileYourInputStays,
            "duty" => Strings.Main_PressesInARepeatingOn,
            // Not device settings: the device runs these only for real output
            // channels (DataFlow.c gates the whole function switch on the output
            // id). A row whose output is a setting name is read as a plain
            // setting override and its function cell is ignored.
            "increment_value" => Strings.Main_StepsAnAnalogOutputUp,
            "decrement_value" => Strings.Main_StepsAnAnalogOutputDown,
            _ => "",
        };
    }

    // Taking the last input off a row leaves it naming an output that nothing
    // can press. The finished file cannot be told from a correct one, because
    // the factory template ships twelve rows shaped exactly like that, so the
    // problems list will never mention it: only the edit knows an input used to
    // be there. The import review has said so since it was written and the two
    // editors, where people actually work, said nothing at all.
    void SayIfNothingFiresIt(int row)
    {
        if (_file is null) return;
        var b = _file.Document.Sheets.SelectMany(s => s.Bindings).FirstOrDefault(x => x.Row == row);
        if (b is null || !Vocab.NothingFiresIt(b)) return;
        Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_NothingPressesBOutputNow, b.Output), StatusKind.Warning);
    }

    void Status(string text, StatusKind kind = StatusKind.Info)
    {
        StatusHost.Content = StatusChip(kind, text);
        AutomationProperties.SetLiveSetting(StatusHost,
            kind == StatusKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
    }

    // Shared by ShowHelp() and the Settings window's Help tab (DRY): one
    // ground truth for the quick-guide copy.
    internal static (string Title, string Body)[] HelpSections() => new (string Title, string Body)[]
        {
            (Strings.Main_WhatIsAProfile,
             Strings.Main_OneCSVFileThatTells),

            (Strings.Main_TheThreeColumnsSameColors,
             Strings.Main_YellowOUTPUTTheGameButton),

            (Strings.Main_StartFromAWorkingProfile,
             Strings.Main_NewProfileGivesYouThe),

            (Strings.Main_CommunityProfiles,
             Strings.Main_TheCommunityProfilesCardOn),

            ("Renaming",
             Strings.Main_TheNameBoxAtThe),

            (Strings.Main_DeviceSettings,
             Strings.Main_PrefsCsvIsTheQuadStick),

            (Strings.Main_InstallingSafely,
             Strings.Main_PlugInTheQuadStickIt),

            (Strings.Main_ManagingFilesOnYourQuadStick,
             Strings.Main_TheManageFilesCardUnder),

            (Strings.Main_QuadStickNotShowingUp,
             Strings.Main_IfTheDeviceIsIn),

            ("Keyboard",
             Strings.Main_TabMovesBetweenFieldsArrows),

            (Strings.Main_FoundAProblem,
             Strings.Main_SelectAProblemInThe),
        };

    void ShowHelp()
    {
        var sections = HelpSections();

        var panel = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 14, MaxWidth = 640 };
        panel.Children.Add(new TextBlock { Text = Strings.Main_QuickGuide, FontSize = Size("TitleSize"), FontWeight = FontWeight.Bold });
        foreach (var (title, body) in sections)
        {
            panel.Children.Add(new TextBlock
            { Text = title, FontSize = Size("SubheadSize"), FontWeight = FontWeight.Bold, Margin = new Avalonia.Thickness(0, 8, 0, 0) });
            panel.Children.Add(new TextBlock
            { Text = body, FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap, LineHeight = 22 });
        }

        var win = new Window
        {
            Title = Strings.Main_QuickGuide,
            Width = Math.Min(720 * _uiScale, 1200), Height = Math.Min(680 * _uiScale, 900),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        win.Classes.Add("dialog");
        win.Content = DialogShell(win, new ScrollViewer { Content = ZoomWrap(panel, _uiScale) });
        win.Show(this);
    }

    async Task<string?> PickDeviceRootAsync(IReadOnlyList<string> candidates)
    {
        string? picked = null;
        var cancel = new Button { Content = Strings.Main_Cancel, MinWidth = 140, IsCancel = true };
        var choices = new StackPanel { Spacing = 8 };
        var dialog = new Window
        {
            Title = Strings.Main_ChooseQuadStick,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        foreach (var root in candidates)
        {
            var label = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(label);
            if (string.IsNullOrEmpty(name)) name = label;
            var btn = new Button
            {
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = name, FontWeight = FontWeight.Bold, FontSize = Size("BodySize") },
                        new TextBlock { Text = root, FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
                    },
                },
                MinWidth = 360,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = root,
            };
            // Complex button content is invisible to screen readers without
            // an explicit name; this is a safety-relevant choice.
            AutomationProperties.SetName(btn, string.Format(CultureInfo.CurrentCulture, Strings.Main_InstallToTheQuadStickNamed, name, root));
            btn.Click += (_, _) => { picked = (string)btn.Tag!; dialog.Close(); };
            choices.Children.Add(btn);
        }
        dialog.Content = ZoomWrap(new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 16,
            MaxWidth = 520,
            Children =
            {
                new TextBlock { Text = Strings.Main_MultipleQuadSticksFound, FontWeight = FontWeight.Bold, FontSize = Size("SubheadSize") },
                new TextBlock { Text = Strings.Main_ChooseWhichDriveToInstall, TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize") },
                choices,
                cancel,
            },
        }, _uiScale);
        cancel.Click += (_, _) => dialog.Close();
        await ShowDialogInShellAsync(dialog);
        return picked;
    }

    async Task<bool> ConfirmAsync(string title, string message)
    {
        var yes = new Button { Content = Strings.Main_YesContinue, MinWidth = 140 };
        var no = new Button { Content = Strings.Main_Cancel, MinWidth = 140, IsDefault = true, IsCancel = true };
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = ZoomWrap(new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 16,
                MaxWidth = 480,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeight.Bold, FontSize = Size("SubheadSize"), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize") },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { yes, no } },
                },
            }, _uiScale),
        };
        var result = false;
        yes.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        await ShowDialogInShellAsync(dialog);
        return result;
    }
}
