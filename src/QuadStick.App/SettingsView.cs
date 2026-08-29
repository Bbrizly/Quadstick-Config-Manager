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

// App settings: General / Advanced / Help / Contact. A page in the shell, not a
// second window, because settings are something you tune while the rest of the
// app waits, and a modal dialog on top of a profile editor is the wrong shape.
//
// Every control reads its starting value from owner.CurrentSettings and calls
// straight back into a MainWindow method that applies the change live and
// persists it. MainWindow.AppSettings (_settings) stays the single source of
// truth; this view never keeps its own copy.
public class SettingsView : UserControl
{
    readonly MainWindow _owner;
    readonly Button _back;
    readonly TabControl _tabs;

    // The feedback button rides on the usage-data consent, and that consent is
    // toggled on a different tab of this same page. Held here so the toggle
    // can reach it: without this the button keeps whatever state it had when
    // the page opened, so turning usage data on leaves it stuck disabled.
    Button? _feedbackSend;

    // A pending interface-size preview: the timer counts down and _revertSize
    // puts the size back unless the user confirms. Null when nothing is pending.
    DispatcherTimer? _sizeTimer;
    Action? _revertSize;

    public SettingsView(MainWindow owner)
    {
        _owner = owner;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        _back = new Button
        {
            Content = new TextBlock { Text = Strings.Main_Back, FontSize = Size("BodySize") },
            Classes = { "shellnav" },
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(_back, Strings.Settings_BackHelp);
        _back.Click += (_, _) => _owner.LeaveSettingsPage();

        _tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = Strings.Settings_TabGeneral, Content = GeneralTab() },
                new TabItem { Header = Strings.Settings_TabAdvanced, Content = AdvancedTab() },
                new TabItem { Header = Strings.Settings_TabHelp, Content = HelpTab() },
                new TabItem { Header = Strings.Settings_TabContact, Content = ContactTab() },
            },
        };

        var title = new TextBlock
        {
            Text = Strings.Settings_Title, Classes = { "section" },
            VerticalAlignment = VerticalAlignment.Center,
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 14,
            Margin = new Thickness(0, 0, 0, 18),
            Children = { _back, title },
        };

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children =
            {
                header,
                _tabs,
            },
        };
        Grid.SetRow(_tabs, 1);
    }

    internal void FocusBack() => _back.Focus();

    // Leaving with an interface-size preview still pending counts as "not
    // confirmed", so put the size back, matching the countdown.
    internal void OnLeaving()
    {
        _sizeTimer?.Stop();
        _revertSize?.Invoke();
        _revertSize = null;
    }

    static double Size(string tokenKey) => (double)Application.Current!.FindResource(tokenKey)!;

    static TextBlock Heading(string text) =>
        new() { Text = text, FontSize = Size("SubheadSize"), FontWeight = FontWeight.Bold };

    static TextBlock Label(string text) =>
        new() { Text = text, FontSize = Size("BodySize") };

    // Capped, and left where the label starts. A caption with the page's own
    // width ran under the scroll bar, so the last words of every explanation
    // on this screen were cut in half.
    static TextBlock Caption(string text) => new()
    {
        Text = text, FontSize = Size("SmallSize"), Classes = { "muted" },
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = FieldWidth, HorizontalAlignment = HorizontalAlignment.Left,
    };

    // One measure for every control and every line of prose on this page.
    const double FieldWidth = 560;

    static Control Field(string label, string? caption, params Control[] controls)
    {
        var group = new StackPanel { Spacing = 4 };
        group.Children.Add(Label(label));
        foreach (var c in controls) group.Children.Add(c);
        if (!string.IsNullOrWhiteSpace(caption)) group.Children.Add(Caption(caption));

        // A stretching control in a column with room to spare centres itself,
        // so every dropdown on this page sat a hundred pixels right of the
        // label naming it. Capping the column makes "stretch" mean "fill up
        // to the cap, starting where the label starts", and it still shrinks
        // on a narrow window.
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(1, GridUnitType.Star) { MaxWidth = FieldWidth },
            },
            Children = { group },
        };
    }

    // Each tab scrolls vertically only, inside a panel of its own, the way
    // Home and the device settings hold their content. Before this the fields
    // sat straight on the page background with the scroll bar floating in the
    // gap beside them.
    static Control Tab(Control content)
    {
        if (content is Layoutable l) l.HorizontalAlignment = HorizontalAlignment.Stretch;

        var card = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 16),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content,
            },
        };
        MainWindow.BindBrushTo(card, Border.BackgroundProperty, "Surface");
        MainWindow.BindBrushTo(card, Border.BorderBrushProperty, "SurfaceBorder");
        card[!Border.CornerRadiusProperty] = new DynamicResourceExtension("PanelRadiusCorner");
        return card;
    }

    // No width of its own: Field caps the column, and a narrower control
    // inside a wider column centres, which is what left every dropdown here
    // sitting forty pixels right of the label above it.
    static ComboBox ChoiceBox(IEnumerable<object> items, int selectedIndex) => new()
    {
        ItemsSource = items,
        SelectedIndex = selectedIndex,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    static int ModelIndexOf(string modelName) => modelName switch
    {
        "Original" => 1,
        "Singleton" => 2,
        _ => 0,
    };

    Control GeneralTab()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 24), Spacing = 16 };
        panel.Children.Add(Heading(Strings.Settings_TabGeneral));

        if (Localization.Languages.Length > 1)
        {
            var language = ChoiceBox(Localization.Choices(),
                Localization.IndexOf(_owner.CurrentSettings.Language));
            AutomationProperties.SetName(language, Strings.Settings_LanguageHelp);
            language.SelectionChanged += (_, _) =>
            {
                if (language.SelectedIndex < 0) return;
                var next = _owner.SetLanguage(Localization.TagAt(language.SelectedIndex));
                if (!ReferenceEquals(next, _owner)) next.ShowSettingsPage();
            };
            panel.Children.Add(Field(Strings.Settings_Language, null, language));
        }

        var appearance = ChoiceBox(QuadStick.App.Theme.Choices,
            _owner.CurrentSettings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 });
        AutomationProperties.SetName(appearance, Strings.Settings_AppearanceHelp);
        appearance.SelectionChanged += (_, _) =>
        {
            _owner.ApplyTheme(QuadStick.App.Theme.ChoiceAt(appearance.SelectedIndex));
        };
        panel.Children.Add(Field(Strings.Settings_Appearance, null, appearance));

        var scalePercents = MainWindow.ValidScalePercents;
        int scaleIndex = Array.IndexOf(scalePercents, _owner.CurrentSettings.InterfaceScalePercent);
        var scale = ChoiceBox(
            Array.ConvertAll(scalePercents, p => $"{p}%"),
            scaleIndex >= 0 ? scaleIndex : 0);
        AutomationProperties.SetName(scale, Strings.Settings_ScaleHelp);

        var saveSize = new Button
        {
            Content = Strings.Settings_SaveSize, Classes = { "primary" }, IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(saveSize, Strings.Settings_SaveSizeHelp);
        var countdown = new TextBlock
        {
            IsVisible = false, FontSize = Size("BodySize"), Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetLiveSetting(countdown, AutomationLiveSetting.Assertive);

        bool suppress = false;
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
            var saved = _owner.CurrentSettings.InterfaceScalePercent;
            EndPreview();
            suppress = true;
            int idx = Array.IndexOf(scalePercents, saved);
            scale.SelectedIndex = idx >= 0 ? idx : 0;
            suppress = false;
            _owner.ApplyInterfaceScale(saved);
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

            if (pct == _owner.CurrentSettings.InterfaceScalePercent)
            {
                EndPreview();
                _owner.ApplyInterfaceScale(pct);
                return;
            }

            _owner.ApplyInterfaceScale(pct);
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
            _owner.SetInterfaceScale(scalePercents[scale.SelectedIndex]);
            EndPreview();
        };

        var scaleCol = new StackPanel { Spacing = 10, Children = { scale, saveSize, countdown } };
        panel.Children.Add(Field(Strings.Settings_Scale, Strings.Settings_ScaleCaption, scaleCol));

        var model = ChoiceBox(MainWindow.ModelDisplayNames, ModelIndexOf(_owner.CurrentSettings.Model));
        AutomationProperties.SetName(model, Strings.Settings_Model);
        model.SelectionChanged += (_, _) =>
        {
            if (model.SelectedIndex >= 0) _owner.SetDefaultModel(model.SelectedIndex);
        };
        panel.Children.Add(Field(Strings.Settings_Model, null, model));

        panel.Children.Add(BackupArea());
        panel.Children.Add(UpdateArea());

        return Tab(panel);
    }

    Control UpdateArea()
    {
        var version = UpdateCheck.CurrentVersion;
        var line = new TextBlock
        {
            Text = string.Format(CultureInfo.CurrentCulture, Strings.Settings_YouAreOn, version),
            FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap, Classes = { "muted" },
        };
        AutomationProperties.SetLiveSetting(line, AutomationLiveSetting.Polite);

        var check = new Button { Content = Strings.Settings_CheckUpdates, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(check, Strings.Settings_CheckUpdates);

        var download = new Button { Content = Strings.Settings_OpenDownload, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(download, Strings.Settings_OpenDownloadHelp);
        string? url = null;
        download.Click += async (_, _) =>
        {
            if (url is null) return;
            try { await _owner.Launcher.LaunchUriAsync(new Uri(url)); } catch { /* best effort */ }
        };

        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            line.Text = Strings.Settings_Checking;
            var result = await UpdateCheck.LatestAsync(_owner.HttpClient, version);
            line.Text = result.Message;
            url = result.DownloadUrl;
            download.IsVisible = result.IsNewer && url is not null;
            check.IsEnabled = true;
        };

        return Field(Strings.Settings_Updates, Strings.Settings_UpdatesCaption,
            new StackPanel { Spacing = 10, Children = { check, download } },
            line);
    }

    Control BackupArea()
    {
        var section = new StackPanel { Spacing = 16 };
        section.Children.Add(Heading(Strings.Settings_Backup));

        var configured = GoogleAuth.IsConfigured;
        var backupCheck = new CheckBox
        {
            Content = Strings.Settings_BackupToggle,
            IsChecked = _owner.DriveConnected,
            IsEnabled = configured,
            FontSize = Size("BodySize"),
        };
        AutomationProperties.SetName(backupCheck, Strings.Settings_BackupToggle);
        var group = new StackPanel { Spacing = 4 };
        group.Children.Add(backupCheck);

        var connected = new TextBlock
        {
            Text = Strings.Settings_Connected,
            FontSize = Size("BodySize"),
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = _owner.DriveConnected,
        };
        connected[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("SuccessBrush");
        AutomationProperties.SetName(connected, Strings.Settings_Connected);
        group.Children.Add(connected);
        void RefreshConnected() => connected.IsVisible = _owner.DriveConnected;

        group.Children.Add(Caption(configured
            ? Strings.Settings_BackupCaption
            : Strings.Settings_BackupUnavailable));
        section.Children.Add(group);

        var waitingText = new TextBlock
        { Text = Strings.Settings_WaitingBrowser, FontSize = Size("BodySize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        var cancelConnect = new Button { Content = Strings.Settings_Cancel, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(cancelConnect, Strings.Settings_CancelConnect);
        var waitingRow = new StackPanel
        {
            Spacing = 10, IsVisible = false,
            Children = { waitingText, cancelConnect },
        };
        section.Children.Add(waitingRow);

        var reconnect = new Button
        {
            Content = Strings.Settings_ReconnectShort,
            IsVisible = configured && _owner.CurrentSettings.DriveBackup,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(reconnect, Strings.Settings_Reconnect);
        section.Children.Add(reconnect);

        var importDrive = new Button
        {
            Content = Strings.Settings_ImportDrive,
            IsEnabled = _owner.CurrentSettings.DriveBackup,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(importDrive, Strings.Settings_ImportDriveHelp);
        importDrive.Click += async (_, _) =>
        {
            _owner.LeaveSettingsPage();
            await _owner.ShowDrivePickerAsync(preCheck: false);
        };
        section.Children.Add(importDrive);

        bool suppress = false;
        CancellationTokenSource? connectCts = null;

        async Task RunConnectAsync()
        {
            connectCts = new CancellationTokenSource();
            waitingRow.IsVisible = true;
            try
            {
                bool ok = await _owner.ConnectGoogleAsync(connectCts.Token);
                if (!ok)
                {
                    suppress = true;
                    backupCheck.IsChecked = false;
                    suppress = false;
                }
                reconnect.IsVisible = configured && _owner.CurrentSettings.DriveBackup;
                importDrive.IsEnabled = _owner.CurrentSettings.DriveBackup;
                RefreshConnected();

                if (ok && await _owner.ConfirmRestoreAfterConnectAsync())
                    await _owner.ShowDrivePickerAsync(preCheck: true);
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
                _owner.DisableDriveBackup();
                reconnect.IsVisible = false;
                importDrive.IsEnabled = false;
                RefreshConnected();
            }
        };
        cancelConnect.Click += (_, _) => connectCts?.Cancel();
        reconnect.Click += async (_, _) => await RunConnectAsync();

        return section;
    }

    Control AdvancedTab()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 24), Spacing = 16 };
        panel.Children.Add(Heading(Strings.Settings_TabAdvanced));

        var reduceMotion = new CheckBox
        { Content = Strings.Settings_ReduceMotion, IsChecked = _owner.CurrentSettings.ReduceMotion, FontSize = Size("BodySize") };
        AutomationProperties.SetName(reduceMotion, Strings.Settings_ReduceMotion);
        reduceMotion.IsCheckedChanged += (_, _) => _owner.SetReduceMotion(reduceMotion.IsChecked == true);
        panel.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children = { reduceMotion, Caption(Strings.Settings_ReduceMotionCaption) },
        });

        var grouping = ChoiceBox(MainWindow.PickerGroupings,
            Math.Max(0, Array.IndexOf(MainWindow.PickerGroupings, _owner.CurrentSettings.PickerGrouping)));
        AutomationProperties.SetName(grouping, Strings.Settings_GroupingHelp);
        grouping.SelectionChanged += (_, _) =>
        {
            if (grouping.SelectedItem is string choice) _owner.SetPickerGrouping(choice);
        };
        panel.Children.Add(Field(Strings.Settings_Grouping, Strings.Settings_GroupingCaption, grouping));

        panel.Children.Add(PrivacyArea());

        var rememberWindow = new CheckBox
        {
            Content = Strings.Settings_RememberWindow,
            IsChecked = _owner.CurrentSettings.RememberWindow,
            FontSize = Size("BodySize"),
        };
        AutomationProperties.SetName(rememberWindow, Strings.Settings_RememberWindow);
        rememberWindow.IsCheckedChanged += (_, _) =>
        {
            _owner.CurrentSettings.RememberWindow = rememberWindow.IsChecked == true;
            _owner.PersistSettings();
        };
        panel.Children.Add(rememberWindow);

        var showTutorial = new CheckBox
        {
            Content = Strings.Settings_ShowTutorial,
            IsChecked = !_owner.CurrentSettings.TutorialSeen,
            FontSize = Size("BodySize"),
        };
        AutomationProperties.SetName(showTutorial, Strings.Settings_ShowTutorial);
        showTutorial.IsCheckedChanged += (_, _) =>
        {
            _owner.CurrentSettings.TutorialSeen = showTutorial.IsChecked != true;
            _owner.PersistSettings();
        };
        panel.Children.Add(showTutorial);

        var openFolder = new Button { Content = Strings.Settings_OpenFolder, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(openFolder, Strings.Settings_OpenFolderHelp);
        openFolder.Click += async (_, _) =>
        {
            var dir = Path.GetDirectoryName(Settings.DefaultPath)!;
            try { Directory.CreateDirectory(dir); await _owner.Launcher.LaunchUriAsync(new Uri(dir)); }
            catch { /* best effort */ }
        };
        panel.Children.Add(openFolder);

        var reset = new Button { Content = Strings.Settings_Reset, Classes = { "danger" }, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(reset, Strings.Settings_Reset);
        reset.Click += async (_, _) =>
        {
            if (await _owner.ConfirmResetAsync()) { _owner.ResetSettings(); _owner.LeaveSettingsPage(); }
        };
        panel.Children.Add(reset);

        return Tab(panel);
    }

    Control HelpTab()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 24), Spacing = 14 };
        panel.Children.Add(Heading(Strings.Settings_QuickGuide));

        var replay = new Button { Content = Strings.Settings_ReplayTutorial, Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(replay, Strings.Settings_ReplayTutorialHelp);
        replay.Click += (_, _) => { _owner.LeaveSettingsPage(); _owner.StartTutorial(); };
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

    Control PrivacyArea()
    {
        var s = _owner.CurrentSettings;

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

        var copyId = new Button
        {
            Content = Strings.Settings_CopyInstallId,
            IsEnabled = s.InstallId.Length > 0,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(copyId, Strings.Settings_CopyInstallIdHelp);
        copyId.Click += async (_, _) =>
        {
            try
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } c)
                    await c.SetTextAsync(_owner.CurrentSettings.InstallId);
            }
            catch { /* best effort */ }
        };

        var savedUsage = s.UsageAnalytics;
        usage.IsCheckedChanged += (_, _) =>
        {
            var want = usage.IsChecked == true;
            if (want == savedUsage) return;

            if (_owner.ApplyTelemetryAnswer(want)) savedUsage = want;
            else usage.IsChecked = savedUsage;

            var live = _owner.CurrentSettings.UsageAnalytics;
            idText.Text = _owner.CurrentSettings.InstallId.Length == 0
                ? Strings.Settings_NoInstallId
                : $"Install ID: {_owner.CurrentSettings.InstallId}";
            copyId.IsEnabled = _owner.CurrentSettings.InstallId.Length > 0;
            if (_feedbackSend is not null) _feedbackSend.IsEnabled = live;
        };

        var savedAsk = s.AskAboutCrashes;
        askCrashes.IsCheckedChanged += (_, _) =>
        {
            var want = askCrashes.IsChecked == true;
            if (want == savedAsk) return;

            _owner.CurrentSettings.AskAboutCrashes = want;
            if (!Settings.TrySave(_owner.CurrentSettings))
            {
                _owner.CurrentSettings.AskAboutCrashes = savedAsk;
                askCrashes.IsChecked = savedAsk;
                return;
            }
            savedAsk = want;

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
                LinkButton(Strings.Settings_Privacy,
                           MainWindow.PrivacyPolicyUrl,
                           Strings.Settings_PrivacyHelp),
            },
        };
    }

    Control ContactTab()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 24), Spacing = 16 };
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

        panel.Children.Add(FeedbackArea());
        return Tab(panel);
    }

    Control FeedbackArea()
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 90,
            MaxLength = Telemetry.MaxFeedbackChars,
            Watermark = Strings.Settings_FeedbackWatermark,
            FontSize = Size("BodySize"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(box, Strings.Settings_FeedbackLabel);

        var status = new TextBlock
        { FontSize = Size("SmallSize"), TextWrapping = TextWrapping.Wrap, IsVisible = false, Classes = { "muted" } };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);

        var send = new Button
        {
            Content = Strings.Settings_SendFeedback,
            IsEnabled = _owner.CurrentSettings.UsageAnalytics,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(send, Strings.Settings_SendFeedback);
        _feedbackSend = send;

        send.Click += async (_, _) =>
        {
            send.IsEnabled = false;
            status.Text = Strings.Settings_Sending;
            status.IsVisible = true;
            try
            {
                if (await Telemetry.SendFeedbackAsync(box.Text ?? ""))
                {
                    box.Text = "";
                    status.Text = Strings.Settings_FeedbackSent;
                }
                else status.Text = Strings.Settings_FeedbackFailed;
            }
            finally { send.IsEnabled = _owner.CurrentSettings.UsageAnalytics; }
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
            try { await _owner.Launcher.LaunchUriAsync(new Uri(url)); } catch { /* best effort */ }
        };
        return btn;
    }
}
