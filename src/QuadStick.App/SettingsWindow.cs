using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

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

        // At high interface scale the window can grow taller than the screen,
        // pushing the titlebar's own close control out of reach. IsCancel wires
        // Esc to this button at the window level (no focus needed), and it stays
        // reachable by scrolling since it lives in the same scrollable content
        // as the tabs, matching this app's Cancel-button dialog idiom elsewhere.
        var close = new Button { Content = "Close", MinWidth = 140, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(close, "Close settings");
        close.Click += (_, _) => Close();

        var footer = new StackPanel
        {
            Margin = new Thickness(24, 0, 24, 24),
            Spacing = 16,
            Children =
            {
                new Border { Height = 1, Background = (Application.Current!.FindResource("SurfaceBorderBrush") as IBrush) },
                close,
            },
        };
        var layout = new StackPanel { Children = { tabControl, footer } };

        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = MainWindow.ZoomWrap(layout, owner.UiScale),
        };
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
        };
        AutomationProperties.SetName(scale, "Interface size, in percent");
        scale.SelectionChanged += (_, _) =>
        {
            if (scale.SelectedIndex >= 0) owner.SetInterfaceScale(scalePercents[scale.SelectedIndex]);
        };
        panel.Children.Add(scale);
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

        return Tab(panel);
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
