using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;

namespace QuadStick.App;

// Settings ▸ General / Advanced / Help / Contact. Follows the app's existing
// dialog idiom (ConfirmAsync / ShowHelp / InstallFlow): a plain Window built
// in code-behind, no inline Background (the app-wide "Window" style in
// App.axaml already themes it), ShowDialog(owner) from the caller.
//
// Every control here reads its starting value from owner.CurrentSettings and
// calls straight back into a MainWindow method that applies the change live
// and persists it. MainWindow.AppSettings (_settings) stays the single
// source of truth, this window never keeps its own copy.
public class SettingsWindow : Window
{
    // Interface-size choices live on MainWindow.ValidScalePercents; labels are
    // just those percents formatted, so this window keeps no copy of its own.

    // MainWindow.ZoomWrap just returns the bare content at scale 1.0 and
    // hands back a brand-new LayoutTransformControl otherwise, with nothing
    // keeping a handle to it afterward. That's fine for a one-shot dialog,
    // but this window has to rescale itself live while it's still open, so
    // it builds and holds its own LayoutTransformControl instead.
    readonly LayoutTransformControl _zoom;

    // The feedback button rides on the usage-data consent, and that consent is
    // toggled on a different tab of this same window. Held here so the toggle
    // can reach it: without this the button keeps whatever state it had when
    // the window opened, so turning usage data on leaves it stuck disabled.
    Button? _feedbackSend;

    // A pending interface-size preview: the timer counts down and _revertSize
    // puts the size back unless the user confirms. Null when nothing is pending.
    DispatcherTimer? _sizeTimer;
    Action? _revertSize;

    public SettingsWindow(MainWindow owner)
    {
        Classes.Add("dialog");
        Title = Strings.Settings_Title;
        Width = Math.Min(640 * owner.UiScale, 1200);
        Height = Math.Min(640 * owner.UiScale, 900);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var tabControl = new TabControl
        {
            Items =
            {
                new TabItem { Header = Strings.Settings_TabGeneral, Content = GeneralTab(owner) },
                new TabItem { Header = Strings.Settings_TabAdvanced, Content = AdvancedTab(owner) },
                new TabItem { Header = Strings.Settings_TabHelp, Content = HelpTab(owner) },
                new TabItem { Header = Strings.Settings_TabContact, Content = ContactTab(owner) },
            },
        };

        // A big Close button pinned to the top-right, outside the scroll and
        // zoom so it never scrolls off screen or shrinks at small interface
        // sizes. IsCancel wires Esc to it too, no focus needed.
        var close = new Button
        {
            Content = Strings.Settings_Close, Classes = { "primary" }, IsCancel = true,
            FontSize = Size("SubheadSize"), Padding = new Thickness(28, 12),
            MinWidth = 150, VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(close, Strings.Settings_CloseHelp);
        close.Click += (_, _) => Close();
        // A dialog can open with keyboard focus still on the window behind
        // it, and then every key press (Escape included) bypasses this window
        // entirely. Focusing a real control on open pulls the keyboard in, so
        // Escape and Tab work from the first press.
        Opened += (_, _) => close.Focus();

        var header = new Grid
        {
            Margin = new Thickness(20, 12),
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children =
            {
                new TextBlock
                {
                    Text = Strings.Settings_Title, FontSize = Size("SubheadSize"), FontWeight = FontWeight.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                close,
            },
        };
        Grid.SetColumn(close, 1);
        var divider = new Border { Height = 1, Background = Application.Current!.FindResource("SurfaceBorderBrush") as IBrush };

        _zoom = new LayoutTransformControl { LayoutTransform = new ScaleTransform(owner.UiScale, owner.UiScale), Child = tabControl };
        var body = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _zoom,
        };

        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(divider, Dock.Top);
        // The shared workflow frame owns the title and close action. Starting
        // directly with the tabs avoids the old title → giant Close bar → tabs
        // stack that made Settings look like a dialog nested inside a dialog.
        Content = MainWindow.DialogShell(this, body);
        // Give the dialog a real keyboard target on first paint. Without one,
        // platform-level Escape events can remain on the owner window.
        Opened += (_, _) => tabControl.Focus();
    }

    // A settings window closed with an interface-size preview still pending
    // counts as "not confirmed", so put the size back, matching the countdown.
    // The Close button's IsCancel only fires once keyboard focus lives inside
    // the window's content, and a freshly opened dialog can have no focused
    // element at all, which is exactly the stuck window the tester hit.
    // Handling the key on the window itself works no matter where focus is.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape) { e.Handled = true; Close(); }
    }

    // This must run in OnClosing, not OnClosed: the revert rescales this
    // window, and after OnClosed the window has no platform backing left, so
    // touching Screens crashes. The tester hit exactly that by closing the
    // window with the countdown still running.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        _sizeTimer?.Stop();
        _revertSize?.Invoke();
        _revertSize = null;
    }

    // Keeps this window's own zoom and size in sync with the interface-size
    // setting while it's open, instead of leaving it at its stale zoom until
    // it's closed and reopened. Called after owner.SetInterfaceScale, once
    // owner.UiScale already reflects the new value.
    void RescaleTo(MainWindow owner)
    {
        var scale = owner.UiScale;
        _zoom.LayoutTransform = new ScaleTransform(scale, scale);

        // Mirrors MainWindow.EnsureWindowFitsScale's clamp so the rescaled
        // window still fits the working area at any monitor's DPI. Width and
        // Height are set explicitly (this window never used SizeToContent),
        // and the Close button and Esc stay reachable through the same
        // two-axis ScrollViewer this window already scrolls with.
        var screen = Screens?.ScreenFromWindow(this) ?? Screens?.Primary;
        if (screen is null) return;
        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
        Width = Math.Min(Math.Min(640 * scale, 1200), screen.WorkingArea.Width / scaling);
        Height = Math.Min(Math.Min(640 * scale, 900), screen.WorkingArea.Height / scaling);
    }

    // Same one-time resource read used throughout MainWindow.axaml.cs: type
    // scale doesn't change with theme, so this is safe outside a DynamicResource.
    static double Size(string tokenKey) => (double)Application.Current!.FindResource(tokenKey)!;

    static TextBlock Heading(string text) =>
        new() { Text = text, FontSize = Size("SubheadSize"), FontWeight = FontWeight.Bold };

    static TextBlock Label(string text) =>
        new() { Text = text, FontSize = Size("BodySize") };

    static TextBlock Caption(string text) => new()
    {
        Text = text, FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
    };

    // A field is title + control + optional caption, packed tight (4px) so they
    // read as one unit. The tab's 16px spacing then sits between fields, not
    // between a title and its box. Extra controls can ride along.
    static Control Field(string label, string? caption, params Control[] controls)
    {
        var group = new StackPanel { Spacing = 4 };
        group.Children.Add(Label(label));
        foreach (var c in controls) group.Children.Add(c);
        if (!string.IsNullOrWhiteSpace(caption)) group.Children.Add(Caption(caption));
        return group;
    }

    // The outer window ScrollViewer allows horizontal scrolling (so zoomed-up
    // content is reachable), which means it measures tab content at infinite
    // width and TextWrapping never fires. Bounding each tab to a readable
    // measure makes the text wrap and fit the window instead of running off
    // the right edge.
    static Control Tab(Control content)
    {
        if (content is Layoutable l)
        {
            l.MaxWidth = 560;
            l.HorizontalAlignment = HorizontalAlignment.Left;
        }
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = content,
        };
    }

    static int ModelIndexOf(string modelName) => modelName switch
    {
        "Original" => 1,
        "Singleton" => 2,
        _ => 0,
    };

    Control GeneralTab(MainWindow owner)
    {
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        panel.Children.Add(Heading(Strings.Settings_TabGeneral));

        // One language is not a choice. A release build has only English, so
        // the row would be a dropdown that cannot change anything; it appears
        // the moment a translation ships.
        if (true)
        {
            var language = new ComboBox
            {
                ItemsSource = Localization.Choices(),
                SelectedIndex = Localization.IndexOf(owner.CurrentSettings.Language),
                MinWidth = 220,
            };
            AutomationProperties.SetName(language, Strings.Settings_LanguageHelp);
            var languageNote = new TextBlock
            {
                Text = Strings.Settings_LanguageRestart, IsVisible = false, TextWrapping = TextWrapping.Wrap,
                FontSize = Size("BodySize"), Classes = { "muted" },
            };
            // Nothing on screen changes when this is picked, so a sighted user sees
            // the note appear and a screen reader user has to be told.
            AutomationProperties.SetLiveSetting(languageNote, AutomationLiveSetting.Polite);
            language.SelectionChanged += (_, _) =>
            {
                if (language.SelectedIndex < 0) return;
                var tag = Localization.TagAt(language.SelectedIndex);
                owner.SetLanguage(tag);
                languageNote.IsVisible = true;
            };
            panel.Children.Add(Field(Strings.Settings_Language, null, language, languageNote));
        }

        var appearance = new ComboBox
        {
            ItemsSource = QuadStick.App.Theme.Choices,
            SelectedIndex = owner.CurrentSettings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 },
            MinWidth = 220,
        };
        AutomationProperties.SetName(appearance, Strings.Settings_AppearanceHelp);
        appearance.SelectionChanged += (_, _) =>
        {
            owner.ApplyTheme(QuadStick.App.Theme.ChoiceAt(appearance.SelectedIndex));
        };
        panel.Children.Add(Field(Strings.Settings_Appearance, null, appearance));

        var scalePercents = MainWindow.ValidScalePercents;
        int scaleIndex = Array.IndexOf(scalePercents, owner.CurrentSettings.InterfaceScalePercent);
        var scale = new ComboBox
        {
            ItemsSource = Array.ConvertAll(scalePercents, p => $"{p}%"),
            SelectedIndex = scaleIndex >= 0 ? scaleIndex : 0,
            MinWidth = 220,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(scale, Strings.Settings_ScaleHelp);

        // A new size previews live but is not saved until the user confirms.
        // A countdown reverts to the last saved size otherwise, so a size that
        // turns out to be unusable can never trap the user, the same guard
        // Windows puts on a display-resolution change.
        var saveSize = new Button
        {
            Content = Strings.Settings_SaveSize, Classes = { "primary" }, IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(saveSize, Strings.Settings_SaveSizeHelp);
        var countdown = new TextBlock
        {
            IsVisible = false, VerticalAlignment = VerticalAlignment.Center,
            FontSize = Size("BodySize"), Classes = { "muted" },
        };
        // The one control here with a time limit on it. Without this a screen
        // reader user changes the size, moves on, and never hears that it is
        // about to put itself back.
        AutomationProperties.SetLiveSetting(countdown, AutomationLiveSetting.Assertive);

        bool suppress = false; // stops the programmatic revert re-triggering this
        int remaining = 0;
        const int RevertSeconds = 15;

        void EndPreview()
        {
            _sizeTimer?.Stop();
            _revertSize = null;
            saveSize.IsVisible = false;
            countdown.IsVisible = false;
        }

        void Revert()
        {
            var saved = owner.CurrentSettings.InterfaceScalePercent;
            EndPreview();
            suppress = true;
            int idx = Array.IndexOf(scalePercents, saved);
            scale.SelectedIndex = idx >= 0 ? idx : 0;
            suppress = false;
            owner.ApplyInterfaceScale(saved);
            RescaleTo(owner);
        }

        _sizeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sizeTimer.Tick += (_, _) =>
        {
            remaining--;
            if (remaining <= 0) { Revert(); return; }
            countdown.Text = string.Format(CultureInfo.CurrentCulture, Strings.Settings_Reverting, remaining);
        };

        scale.SelectionChanged += (_, _) =>
        {
            if (suppress || scale.SelectedIndex < 0) return;
            int pct = scalePercents[scale.SelectedIndex];

            // Picking the already-saved size just applies it, no countdown.
            if (pct == owner.CurrentSettings.InterfaceScalePercent)
            {
                EndPreview();
                owner.ApplyInterfaceScale(pct);
                RescaleTo(owner);
                return;
            }

            owner.ApplyInterfaceScale(pct); // preview only; SetInterfaceScale saves
            RescaleTo(owner);
            remaining = RevertSeconds;
            countdown.Text = string.Format(CultureInfo.CurrentCulture, Strings.Settings_Reverting, remaining);
            saveSize.IsVisible = true;
            countdown.IsVisible = true;
            _revertSize = Revert;
            _sizeTimer.Stop();
            _sizeTimer.Start();
        };

        saveSize.Click += (_, _) =>
        {
            if (scale.SelectedIndex < 0) return;
            owner.SetInterfaceScale(scalePercents[scale.SelectedIndex]); // now persist it
            EndPreview();
        };

        var scaleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children = { scale, saveSize, countdown },
        };
        panel.Children.Add(Field(Strings.Settings_Scale, Strings.Settings_ScaleCaption, scaleRow));

        var model = new ComboBox
        {
            ItemsSource = MainWindow.ModelDisplayNames,
            SelectedIndex = ModelIndexOf(owner.CurrentSettings.Model),
            MinWidth = 220,
        };
        AutomationProperties.SetName(model, Strings.Settings_Model);
        model.SelectionChanged += (_, _) =>
        {
            if (model.SelectedIndex >= 0) owner.SetDefaultModel(model.SelectedIndex);
        };
        panel.Children.Add(Field(Strings.Settings_Model, null, model));

        panel.Children.Add(BackupArea(owner));
        panel.Children.Add(UpdateArea(owner));

        return Tab(panel);
    }

    // Asks GitHub, says what it found, and opens the release page. It never
    // downloads or replaces anything: an unsigned self-update teaches people to
    // click through the warning their computer is right to show them.
    Control UpdateArea(MainWindow owner)
    {
        var version = UpdateCheck.CurrentVersion;
        var line = new TextBlock
        {
            Text = string.Format(CultureInfo.CurrentCulture, Strings.Settings_YouAreOn, version),
            FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap, Classes = { "muted" },
        };
        AutomationProperties.SetLiveSetting(line, AutomationLiveSetting.Polite);

        var check = new Button { Content = Strings.Settings_CheckUpdates };
        AutomationProperties.SetName(check, Strings.Settings_CheckUpdates);

        var download = new Button { Content = Strings.Settings_OpenDownload, IsVisible = false };
        AutomationProperties.SetName(download, Strings.Settings_OpenDownloadHelp);
        string? url = null;
        download.Click += async (_, _) =>
        {
            if (url is null) return;
            try { await Launcher.LaunchUriAsync(new Uri(url)); } catch { /* best effort */ }
        };

        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            line.Text = Strings.Settings_Checking;
            var result = await UpdateCheck.LatestAsync(owner.HttpClient, version);
            line.Text = result.Message;
            url = result.DownloadUrl;
            // The button is shown for a newer version only. Offering a download
            // to someone already on the newest is a question with no answer.
            download.IsVisible = result.IsNewer && url is not null;
            check.IsEnabled = true;
        };

        return Field(Strings.Settings_Updates, Strings.Settings_UpdatesCaption,
            new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children = { check, download },
            },
            line);
    }

    // Backup checkbox: runs OAuth when turned on, signs out when turned off,
    // Cancel for the wait, Reconnect for a revoked token.
    Control BackupArea(MainWindow owner)
    {
        var section = new StackPanel { Spacing = 16 };
        section.Children.Add(Heading(Strings.Settings_Backup));

        var configured = GoogleAuth.IsConfigured;
        var backupCheck = new CheckBox
        {
            Content = Strings.Settings_BackupToggle,
            // Ticked only when a token is stored, not from the default setting.
            IsChecked = owner.DriveConnected,
            IsEnabled = configured,
            FontSize = Size("BodySize"),
        };
        AutomationProperties.SetName(backupCheck, Strings.Settings_BackupToggle);
        // Checkbox, Connected line, and caption are one field: pack tight so
        // status and explanation sit right under the checkbox.
        var group = new StackPanel { Spacing = 4 };
        group.Children.Add(backupCheck);

        // Green line only when backup is truly live (on, real client, token
        // stored). RefreshConnected keeps it in step.
        var connected = new TextBlock
        {
            Text = Strings.Settings_Connected,
            FontSize = Size("BodySize"),
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = owner.DriveConnected,
        };
        // Bind dynamically: the brush lives in a theme dictionary, and a plain
        // FindResource misses theme-scoped brushes (text stays invisible).
        connected[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("SuccessBrush");
        AutomationProperties.SetName(connected, Strings.Settings_Connected);
        group.Children.Add(connected);
        void RefreshConnected() => connected.IsVisible = owner.DriveConnected;

        group.Children.Add(Caption(configured
            ? Strings.Settings_BackupCaption
            : Strings.Settings_BackupUnavailable));
        section.Children.Add(group);

        var waitingText = new TextBlock
        { Text = Strings.Settings_WaitingBrowser, FontSize = Size("BodySize"), Classes = { "muted" } };
        var cancelConnect = new Button { Content = Strings.Settings_Cancel };
        AutomationProperties.SetName(cancelConnect, Strings.Settings_CancelConnect);
        var waitingRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, IsVisible = false,
            Children = { waitingText, cancelConnect },
        };
        section.Children.Add(waitingRow);

        // Shown whenever backup is on. Also used to switch Google accounts.
        var reconnect = new Button { Content = Strings.Settings_ReconnectShort, IsVisible = configured && owner.CurrentSettings.DriveBackup };
        AutomationProperties.SetName(reconnect, Strings.Settings_Reconnect);
        section.Children.Add(reconnect);

        // Bulk restore. On only while backup is on; closes this window and
        // opens the picker so restored profiles land in the home library.
        var importDrive = new Button
        { Content = Strings.Settings_ImportDrive, IsEnabled = owner.CurrentSettings.DriveBackup };
        AutomationProperties.SetName(importDrive, Strings.Settings_ImportDriveHelp);
        importDrive.Click += async (_, _) => { Close(); await owner.ShowDrivePickerAsync(preCheck: false); };
        section.Children.Add(importDrive);

        bool suppress = false; // stops the programmatic uncheck below re-triggering this
        CancellationTokenSource? connectCts = null;

        async Task RunConnectAsync()
        {
            connectCts = new CancellationTokenSource();
            waitingRow.IsVisible = true;
            try
            {
                bool ok = await owner.ConnectGoogleAsync(connectCts.Token);
                if (!ok)
                {
                    suppress = true;
                    backupCheck.IsChecked = false;
                    suppress = false;
                }
                reconnect.IsVisible = configured && owner.CurrentSettings.DriveBackup;
                importDrive.IsEnabled = owner.CurrentSettings.DriveBackup;
                RefreshConnected();

                // New-machine moment: right after a fresh connect, offer to
                // pull the backups down, all pre-checked.
                if (ok && await owner.ConfirmRestoreAfterConnectAsync())
                    await owner.ShowDrivePickerAsync(preCheck: true);
            }
            finally
            {
                waitingRow.IsVisible = false;
                connectCts = null;
            }
        }

        backupCheck.IsCheckedChanged += async (_, _) =>
        {
            if (suppress) return;
            if (backupCheck.IsChecked == true) await RunConnectAsync();
            else
            {
                // Off signs out too, so the account is forgotten rather than
                // sitting in the keychain behind an unticked box.
                owner.DisableDriveBackup();
                reconnect.IsVisible = false;
                importDrive.IsEnabled = false;
                RefreshConnected();
            }
        };
        cancelConnect.Click += (_, _) => connectCts?.Cancel();
        reconnect.Click += async (_, _) => await RunConnectAsync();

        return section;
    }

    Control AdvancedTab(MainWindow owner)
    {
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        panel.Children.Add(Heading(Strings.Settings_TabAdvanced));

        var reduceMotion = new CheckBox
        { Content = Strings.Settings_ReduceMotion, IsChecked = owner.CurrentSettings.ReduceMotion, FontSize = Size("BodySize") };
        AutomationProperties.SetName(reduceMotion, Strings.Settings_ReduceMotion);
        reduceMotion.IsCheckedChanged += (_, _) => owner.SetReduceMotion(reduceMotion.IsChecked == true);
        panel.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children = { reduceMotion, Caption(Strings.Settings_ReduceMotionCaption) },
        });

        var grouping = new ComboBox
        {
            ItemsSource = MainWindow.PickerGroupings,
            SelectedIndex = Math.Max(0, Array.IndexOf(MainWindow.PickerGroupings,
                owner.CurrentSettings.PickerGrouping)),
            MinWidth = 220,
        };
        AutomationProperties.SetName(grouping,
            Strings.Settings_GroupingHelp);
        grouping.SelectionChanged += (_, _) =>
        {
            if (grouping.SelectedItem is string choice) owner.SetPickerGrouping(choice);
        };
        panel.Children.Add(Field(Strings.Settings_Grouping,
            Strings.Settings_GroupingCaption,
            grouping));

        panel.Children.Add(PrivacyArea(owner));

        var rememberWindow = new CheckBox
        {
            Content = Strings.Settings_RememberWindow,
            IsChecked = owner.CurrentSettings.RememberWindow,
            FontSize = Size("BodySize"),
        };
        AutomationProperties.SetName(rememberWindow, Strings.Settings_RememberWindow);
        rememberWindow.IsCheckedChanged += (_, _) =>
        {
            owner.CurrentSettings.RememberWindow = rememberWindow.IsChecked == true;
            owner.PersistSettings();
        };
        panel.Children.Add(rememberWindow);

        var showTutorial = new CheckBox
        {
            Content = Strings.Settings_ShowTutorial,
            IsChecked = !owner.CurrentSettings.TutorialSeen,
            FontSize = Size("BodySize"),
        };
        AutomationProperties.SetName(showTutorial, Strings.Settings_ShowTutorial);
        showTutorial.IsCheckedChanged += (_, _) =>
        {
            owner.CurrentSettings.TutorialSeen = showTutorial.IsChecked != true;
            owner.PersistSettings();
        };
        panel.Children.Add(showTutorial);

        var openFolder = new Button { Content = Strings.Settings_OpenFolder };
        AutomationProperties.SetName(openFolder, Strings.Settings_OpenFolderHelp);
        openFolder.Click += async (_, _) =>
        {
            var dir = Path.GetDirectoryName(Settings.DefaultPath)!;
            try { Directory.CreateDirectory(dir); await Launcher.LaunchUriAsync(new Uri(dir)); }
            catch { /* best effort */ }
        };
        panel.Children.Add(openFolder);

        var reset = new Button { Content = Strings.Settings_Reset, Classes = { "danger" } };
        AutomationProperties.SetName(reset, Strings.Settings_Reset);
        reset.Click += async (_, _) =>
        {
            if (await owner.ConfirmResetAsync()) { owner.ResetSettings(); Close(); }
        };
        panel.Children.Add(reset);

        return Tab(panel);
    }

    Control HelpTab(MainWindow owner)
    {
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 14 };
        panel.Children.Add(Heading(Strings.Settings_QuickGuide));

        var replay = new Button { Content = Strings.Settings_ReplayTutorial, Classes = { "primary" } };
        AutomationProperties.SetName(replay, Strings.Settings_ReplayTutorialHelp);
        replay.Click += (_, _) => { Close(); owner.StartTutorial(); };
        panel.Children.Add(replay);

        foreach (var (title, body) in MainWindow.HelpSections())
        {
            panel.Children.Add(new TextBlock
            { Text = title, FontSize = Size("SubheadSize"), FontWeight = FontWeight.Bold, Margin = new Thickness(0, 8, 0, 0) });
            panel.Children.Add(new TextBlock
            { Text = body, FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap, LineHeight = 22 });
        }

        return Tab(panel);
    }

    // Everything the app may send, and the switches for it, in one place. The
    // install ID is shown because a deletion request needs it: without it
    // there is no way to point at your own data.
    Control PrivacyArea(MainWindow owner)
    {
        var s = owner.CurrentSettings;

        var usage = new CheckBox
        { Content = Strings.Settings_UsageData, IsChecked = s.UsageAnalytics, FontSize = Size("BodySize") };
        AutomationProperties.SetName(usage, Strings.Settings_UsageData);

        var askCrashes = new CheckBox
        { Content = Strings.Settings_AskCrashes, IsChecked = s.AskAboutCrashes, FontSize = Size("BodySize") };
        AutomationProperties.SetName(askCrashes, Strings.Settings_AskCrashes);

        var idText = new TextBlock
        {
            Text = s.InstallId.Length == 0 ? Strings.Settings_NoInstallId
                                           : $"Install ID: {s.InstallId}",
            FontSize = Size("SmallSize"), TextWrapping = TextWrapping.Wrap, Classes = { "muted" },
        };

        var copyId = new Button { Content = Strings.Settings_CopyInstallId, IsEnabled = s.InstallId.Length > 0 };
        AutomationProperties.SetName(copyId, Strings.Settings_CopyInstallIdHelp);
        copyId.Click += async (_, _) =>
        {
            try { if (Clipboard is { } c) await c.SetTextAsync(owner.CurrentSettings.InstallId); }
            catch { /* best effort */ }
        };

        // What is actually on disk, same rule as the crash box below. Reading
        // it back off CurrentSettings does not work: ApplyTelemetryAnswer has
        // already overwritten that, and on a failed save it forces false, so
        // turning the box off with a read-only settings file used to look like
        // it worked while the file still said true and the next launch turned
        // telemetry back on with nobody told.
        var savedUsage = s.UsageAnalytics;
        usage.IsCheckedChanged += (_, _) =>
        {
            var want = usage.IsChecked == true;
            if (want == savedUsage) return;   // our own revert, or nothing changed

            if (owner.ApplyTelemetryAnswer(want)) savedUsage = want;
            else usage.IsChecked = savedUsage;   // re-enters once, then the guard above stops it

            // These follow the runtime, not the file: a failed save leaves
            // nothing being sent even though the box shows the stored answer.
            var live = owner.CurrentSettings.UsageAnalytics;
            idText.Text = owner.CurrentSettings.InstallId.Length == 0
                ? Strings.Settings_NoInstallId
                : $"Install ID: {owner.CurrentSettings.InstallId}";
            copyId.IsEnabled = owner.CurrentSettings.InstallId.Length > 0;
            if (_feedbackSend is not null) _feedbackSend.IsEnabled = live;
        };

        // What is actually on disk. Reverting to a remembered value gives the
        // handler a fixed point, so the revert re-enters once and stops. The
        // old code assigned the negation instead, which alternates forever: on
        // a settings file that keeps failing to write (read-only folder, full
        // disk) it recursed until the stack overflowed and killed the app.
        var savedAsk = s.AskAboutCrashes;
        askCrashes.IsCheckedChanged += (_, _) =>
        {
            var want = askCrashes.IsChecked == true;
            if (want == savedAsk) return;   // our own revert, or nothing changed

            owner.CurrentSettings.AskAboutCrashes = want;
            if (!Settings.TrySave(owner.CurrentSettings))
            {
                owner.CurrentSettings.AskAboutCrashes = savedAsk;
                askCrashes.IsChecked = savedAsk;
                return;
            }
            savedAsk = want;

            // Turning the asking off is also the moment to stop keeping the
            // reports. The "Stop asking" button in the crash dialog does this
            // too, and a user who switches it off here means the same thing.
            if (!want) CrashReport.Discard();
        };

        return new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                new TextBlock
                {
                    Text = Strings.Settings_Privacy_Heading, FontSize = Size("SubheadSize"),
                    FontWeight = FontWeight.Bold, Margin = new Thickness(0, 8, 0, 0),
                },
                usage,
                Caption(Strings.Settings_UsageCaption),
                askCrashes,
                Caption(Strings.Settings_CrashCaption),
                idText,
                copyId,
                // Reachable from the toggles themselves, not just from the
                // notice at first launch, which most people will never see again.
                LinkButton(Strings.Settings_Privacy,
                           MainWindow.PrivacyPolicyUrl,
                           Strings.Settings_PrivacyHelp),
            },
        };
    }

    Control ContactTab(MainWindow owner)
    {
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        panel.Children.Add(Heading(Strings.Settings_TabContact));
        panel.Children.Add(new TextBlock
        {
            Text = Strings.Settings_ContactIntro,
            FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(LinkButton(
            Strings.Settings_ReportBug,
            "https://github.com/Bbrizly/Quadstick-Config-Manager/issues",
            Strings.Settings_ReportBugHelp));
        panel.Children.Add(LinkButton(
            string.Format(CultureInfo.CurrentCulture, Strings.Settings_WebsiteLink, "bbrizly.github.io"),
            "https://bbrizly.github.io",
            Strings.Settings_WebsiteHelp));
        panel.Children.Add(LinkButton(
            "LinkedIn",
            "https://www.linkedin.com/in/bassam-k/",
            Strings.Settings_LinkedInHelp));
        panel.Children.Add(LinkButton(
            string.Format(CultureInfo.CurrentCulture, Strings.Settings_EmailLink, "bassamkamal.py@gmail.com"),
            "mailto:bassamkamal.py@gmail.com",
            Strings.Settings_EmailHelp));

        panel.Children.Add(FeedbackArea(owner));
        return Tab(panel);
    }

    // The one place free text is sent, and only because the user typed it into
    // a box that says where it goes. Disabled unless usage data is on, because
    // that is the consent it rides on.
    Control FeedbackArea(MainWindow owner)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 90,
            MaxLength = Telemetry.MaxFeedbackChars,
            Watermark = Strings.Settings_FeedbackWatermark,
            FontSize = Size("BodySize"),
        };
        AutomationProperties.SetName(box, Strings.Settings_FeedbackLabel);

        var status = new TextBlock
        { FontSize = Size("SmallSize"), TextWrapping = TextWrapping.Wrap, IsVisible = false, Classes = { "muted" } };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);

        var send = new Button { Content = Strings.Settings_SendFeedback, IsEnabled = owner.CurrentSettings.UsageAnalytics };
        AutomationProperties.SetName(send, Strings.Settings_SendFeedback);
        _feedbackSend = send;   // so the Advanced tab's toggle can follow it

        send.Click += async (_, _) =>
        {
            // Disabled while it is in flight. The await frees the UI thread, so
            // without this the button stays live and a second press queues the
            // same feedback again.
            send.IsEnabled = false;
            status.Text = Strings.Settings_Sending;
            status.IsVisible = true;
            try
            {
                // Only clear the box on a send that was actually accepted. Saying
                // "thanks, sent" and throwing the text away when nothing left the
                // machine is the one outcome that is worse than no button at all.
                if (await Telemetry.SendFeedbackAsync(box.Text ?? ""))
                {
                    box.Text = "";
                    status.Text = Strings.Settings_FeedbackSent;
                }
                else status.Text = Strings.Settings_FeedbackFailed;
            }
            finally { send.IsEnabled = owner.CurrentSettings.UsageAnalytics; }
        };

        return new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0),
            Children =
            {
                new TextBlock
                {
                    Text = Strings.Settings_SendFeedback, FontSize = Size("SubheadSize"),
                    FontWeight = FontWeight.Bold, Margin = new Thickness(0, 8, 0, 0),
                },
                // One sentence that is true whichever way the toggle sits, so
                // there is no second piece of state to keep in step with it.
                Caption(Strings.Settings_FeedbackCaption),
                box,
                send,
                status,
            },
        };
    }

    Button LinkButton(string text, string url, string accessibleName)
    {
        var btn = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(btn, accessibleName);
        btn.Click += async (_, _) =>
        {
            try { await Launcher.LaunchUriAsync(new Uri(url)); } catch { /* best effort */ }
        };
        return btn;
    }
}
