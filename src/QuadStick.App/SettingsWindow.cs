using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
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
// and persists it — MainWindow.AppSettings (_settings) stays the single
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

    // A pending interface-size preview: the timer counts down and _revertSize
    // puts the size back unless the user confirms. Null when nothing is pending.
    DispatcherTimer? _sizeTimer;
    Action? _revertSize;

    public SettingsWindow(MainWindow owner)
    {
        Title = "Settings";
        Width = Math.Min(640 * owner.UiScale, 1200);
        Height = Math.Min(640 * owner.UiScale, 900);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var tabControl = new TabControl
        {
            Items =
            {
                new TabItem { Header = "General", Content = GeneralTab(owner) },
                new TabItem { Header = "Advanced", Content = AdvancedTab(owner) },
                new TabItem { Header = "Help", Content = HelpTab(owner) },
                new TabItem { Header = "Contact", Content = ContactTab() },
            },
        };

        // A big Close button pinned to the top-right, outside the scroll and
        // zoom so it never scrolls off screen or shrinks at small interface
        // sizes. IsCancel wires Esc to it too, no focus needed.
        var close = new Button
        {
            Content = "Close", Classes = { "primary" }, IsCancel = true,
            FontSize = Size("SubheadSize"), Padding = new Thickness(28, 12),
            MinWidth = 150, VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(close, "Close settings");
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
                    Text = "Settings", FontSize = Size("SubheadSize"), FontWeight = FontWeight.Bold,
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
        Content = new DockPanel { LastChildFill = true, Children = { header, divider, body } };
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
        panel.Children.Add(Heading("General"));

        panel.Children.Add(Label("Appearance"));
        var appearance = new ComboBox
        {
            ItemsSource = new[] { "System", "Light", "Dark" },
            SelectedIndex = owner.CurrentSettings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 },
            MinWidth = 220,
        };
        AutomationProperties.SetName(appearance, "Appearance: choose System, Light, or Dark theme");
        appearance.SelectionChanged += (_, _) =>
        {
            if (appearance.SelectedItem is string choice) owner.ApplyTheme(choice);
        };
        panel.Children.Add(appearance);

        panel.Children.Add(Label("Interface size"));
        var scalePercents = MainWindow.ValidScalePercents;
        int scaleIndex = Array.IndexOf(scalePercents, owner.CurrentSettings.InterfaceScalePercent);
        var scale = new ComboBox
        {
            ItemsSource = Array.ConvertAll(scalePercents, p => $"{p}%"),
            SelectedIndex = scaleIndex >= 0 ? scaleIndex : 0,
            MinWidth = 220,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(scale, "Interface size, in percent");

        // A new size previews live but is not saved until the user confirms.
        // A countdown reverts to the last saved size otherwise, so a size that
        // turns out to be unusable can never trap the user, the same guard
        // Windows puts on a display-resolution change.
        var saveSize = new Button
        {
            Content = "Save size", Classes = { "primary" }, IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(saveSize, "Keep this interface size");
        var countdown = new TextBlock
        {
            IsVisible = false, VerticalAlignment = VerticalAlignment.Center,
            FontSize = Size("BodySize"), Classes = { "muted" },
        };

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
            countdown.Text = $"Reverting in {remaining}s";
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
            countdown.Text = $"Reverting in {remaining}s";
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
        panel.Children.Add(scaleRow);
        panel.Children.Add(Caption("Makes all text and controls larger or smaller."));

        panel.Children.Add(Label("Default QuadStick model"));
        var model = new ComboBox
        {
            ItemsSource = MainWindow.ModelDisplayNames,
            SelectedIndex = ModelIndexOf(owner.CurrentSettings.Model),
            MinWidth = 220,
        };
        AutomationProperties.SetName(model, "Default QuadStick model");
        model.SelectionChanged += (_, _) =>
        {
            if (model.SelectedIndex >= 0) owner.SetDefaultModel(model.SelectedIndex);
        };
        panel.Children.Add(model);

        panel.Children.Add(BackupArea(owner));

        return Tab(panel);
    }

    // Back up to Google Sheets: a checkbox that runs the OAuth flow when
    // turned on, a Cancel for the wait (the spec requires a visible cancel),
    // and a Reconnect for the revoked-token case.
    Control BackupArea(MainWindow owner)
    {
        var section = new StackPanel { Spacing = 16 };
        section.Children.Add(Heading("Back up to Google Sheets"));

        var configured = GoogleAuth.IsConfigured;
        var backupCheck = new CheckBox
        {
            Content = "Back up my profiles to Google Sheets",
            IsChecked = owner.CurrentSettings.DriveBackup,
            IsEnabled = configured,
            FontSize = Size("BodySize"),
        };
        AutomationProperties.SetName(backupCheck, "Back up my profiles to Google Sheets");
        section.Children.Add(backupCheck);
        section.Children.Add(Caption(configured
            ? "Saves push each profile to your own Google Drive as a sheet."
            : "This build is not connected to Google yet."));

        var waitingText = new TextBlock
        { Text = "Waiting for your browser...", FontSize = Size("BodySize"), Classes = { "muted" } };
        var cancelConnect = new Button { Content = "Cancel" };
        AutomationProperties.SetName(cancelConnect, "Cancel connecting to Google");
        var waitingRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, IsVisible = false,
            Children = { waitingText, cancelConnect },
        };
        section.Children.Add(waitingRow);

        var reconnect = new Button { Content = "Reconnect", IsVisible = owner.CurrentSettings.DriveBackup };
        AutomationProperties.SetName(reconnect, "Reconnect to Google");
        section.Children.Add(reconnect);

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
                reconnect.IsVisible = owner.CurrentSettings.DriveBackup;
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
                owner.DisableDriveBackup();
                reconnect.IsVisible = false;
            }
        };
        cancelConnect.Click += (_, _) => connectCts?.Cancel();
        reconnect.Click += async (_, _) => await RunConnectAsync();

        return section;
    }

    Control AdvancedTab(MainWindow owner)
    {
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        panel.Children.Add(Heading("Advanced"));

        var reduceMotion = new CheckBox
        { Content = "Reduce motion", IsChecked = owner.CurrentSettings.ReduceMotion, FontSize = Size("BodySize") };
        AutomationProperties.SetName(reduceMotion, "Reduce motion");
        reduceMotion.IsCheckedChanged += (_, _) => owner.SetReduceMotion(reduceMotion.IsChecked == true);
        panel.Children.Add(reduceMotion);
        panel.Children.Add(Caption("Turns off the tutorial fade animation."));

        var rememberWindow = new CheckBox
        {
            Content = "Remember window size and position",
            IsChecked = owner.CurrentSettings.RememberWindow,
            FontSize = Size("BodySize"),
        };
        AutomationProperties.SetName(rememberWindow, "Remember window size and position");
        rememberWindow.IsCheckedChanged += (_, _) =>
        {
            owner.CurrentSettings.RememberWindow = rememberWindow.IsChecked == true;
            owner.PersistSettings();
        };
        panel.Children.Add(rememberWindow);

        var showTutorial = new CheckBox
        {
            Content = "Show the tutorial next time I open the app",
            IsChecked = !owner.CurrentSettings.TutorialSeen,
            FontSize = Size("BodySize"),
        };
        AutomationProperties.SetName(showTutorial, "Show the tutorial next time I open the app");
        showTutorial.IsCheckedChanged += (_, _) =>
        {
            owner.CurrentSettings.TutorialSeen = showTutorial.IsChecked != true;
            owner.PersistSettings();
        };
        panel.Children.Add(showTutorial);

        var openFolder = new Button { Content = "Open settings folder" };
        AutomationProperties.SetName(openFolder, "Open the settings folder");
        openFolder.Click += async (_, _) =>
        {
            var dir = Path.GetDirectoryName(Settings.DefaultPath)!;
            try { Directory.CreateDirectory(dir); await Launcher.LaunchUriAsync(new Uri(dir)); }
            catch { /* best effort */ }
        };
        panel.Children.Add(openFolder);

        var reset = new Button { Content = "Reset all settings to defaults", Classes = { "danger" } };
        AutomationProperties.SetName(reset, "Reset all settings to defaults");
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
        panel.Children.Add(Heading("Quick guide"));

        var replay = new Button { Content = "Replay tutorial", Classes = { "primary" } };
        AutomationProperties.SetName(replay, "Replay the tutorial");
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

    Control ContactTab()
    {
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        panel.Children.Add(Heading("Contact"));
        panel.Children.Add(new TextBlock
        {
            Text = "Found a problem, or just want to say hello? Here is how to reach the person who made this.",
            FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(LinkButton(
            "Report a bug on GitHub",
            "https://github.com/Bbrizly/Quadstick-Config-Manager/issues",
            "Report a bug on GitHub, opens in your browser"));
        panel.Children.Add(LinkButton(
            "Website: bbrizly.github.io",
            "https://bbrizly.github.io",
            "Open the website, opens in your browser"));
        panel.Children.Add(LinkButton(
            "LinkedIn",
            "https://www.linkedin.com/in/bassam-k/",
            "Open LinkedIn, opens in your browser"));
        panel.Children.Add(LinkButton(
            "Email: bassamkamal.py@gmail.com",
            "mailto:bassamkamal.py@gmail.com",
            "Send an email, opens your mail app"));

        return Tab(panel);
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
