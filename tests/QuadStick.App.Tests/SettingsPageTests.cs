using Avalonia;
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

// Settings is a page in the shell now, not a second window. Escape and leaving
// with a size preview pending must still behave the way the old dialog did.
public class SettingsPageTests
{
    static void OpenSettings(MainWindow w)
    {
        w.ShowSettingsPage();
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
    }

    [AvaloniaFact]
    public void Escape_leaves_the_settings_page()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(ProfileFile.NewFromTemplate("smoke.csv"));

        OpenSettings(w);
        Assert.True(w.FindControl<DockPanel>("SettingsPage")!.IsVisible);

        w.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        Assert.False(w.FindControl<DockPanel>("SettingsPage")!.IsVisible);
        w.Close();
    }

    [AvaloniaFact]
    public void Leaving_with_a_size_preview_pending_reverts_without_crashing()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.InterfaceScalePercent = 100;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(ProfileFile.NewFromTemplate("smoke.csv"));

        OpenSettings(w);

        var scale = w.GetVisualDescendants().OfType<ComboBox>()
            .First(c => AutomationProperties.GetName(c) == "Interface size, in percent");
        scale.SelectedIndex = scale.ItemsSource!.Cast<string>().ToList().IndexOf("125%");
        Assert.Equal(1.25, w.UiScale, 2);

        w.LeaveSettingsPage();

        Assert.Equal(1.0, w.UiScale, 2);
        Assert.Equal(100, w.CurrentSettings.InterfaceScalePercent);
        w.Close();
    }

    [AvaloniaFact]
    public void Language_row_appears_only_when_there_is_more_than_one_language()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();

        OpenSettings(w);

        var rows = w.GetVisualDescendants().OfType<ComboBox>()
            .Count(c => AutomationProperties.GetName(c) == "Language: choose which language the app is written in");
        Assert.Equal(Localization.Languages.Length > 1 ? 1 : 0, rows);

        w.Close();
    }

    // A dropdown narrower than the room it is given centres itself, so every
    // control on this page used to sit forty pixels right of the label naming
    // it, and the words and the thing they name read as two columns that had
    // slipped. Each field is one left edge.
    [AvaloniaFact]
    public void Every_control_starts_where_its_label_starts()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        OpenSettings(w);

        var combos = w.FindControl<Panel>("SettingsPageBody")!.GetVisualDescendants()
            .OfType<ComboBox>()
            .Where(c => c.IsVisible && c.Bounds.Width > 0
                     && c.GetVisualParent() is StackPanel)
            .ToArray();
        Assert.NotEmpty(combos);
        foreach (var combo in combos)
        {
            // The label above it, inside the same field.
            var field = (StackPanel)combo.GetVisualParent()!;
            var label = field.Children.OfType<TextBlock>().First();
            Assert.Equal(
                label.TranslatePoint(default, w)!.Value.X,
                combo.TranslatePoint(default, w)!.Value.X,
                1);
        }
        w.Close();
    }

    [AvaloniaFact]
    public void Backup_checkbox_shows_and_is_disabled_when_google_is_not_configured()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();

        OpenSettings(w);

        var backupCheck = w.GetVisualDescendants().OfType<CheckBox>()
            .First(c => AutomationProperties.GetName(c) == "Back up my profiles to Google Drive");
        Assert.Equal(GoogleAuth.IsConfigured, backupCheck.IsEnabled);

        w.Close();
    }

    [AvaloniaFact]
    public void Connected_line_tracks_the_drive_connection_state()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();

        OpenSettings(w);

        var line = w.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Text == "Connected to Google Drive");
        Assert.Equal(w.DriveConnected, line.IsVisible);

        w.Close();
    }

    [AvaloniaFact]
    public void Back_button_returns_to_the_page_you_came_from()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(ProfileFile.NewFromTemplate("smoke.csv"));

        Assert.True(w.FindControl<DockPanel>("EditorView")!.IsVisible);
        OpenSettings(w);
        Assert.True(w.FindControl<DockPanel>("SettingsPage")!.IsVisible);

        var back = w.GetVisualDescendants().OfType<Button>()
            .First(b => AutomationProperties.GetName(b) == "Go back from Settings");
        back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(w.FindControl<DockPanel>("EditorView")!.IsVisible);
        Assert.False(w.FindControl<DockPanel>("SettingsPage")!.IsVisible);
        w.Close();
    }
}
