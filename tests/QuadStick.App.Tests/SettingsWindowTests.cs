using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// The tester could not leave Settings with Escape and had to kill the window
// from the taskbar. This drives a real key press through the headless
// platform so the escape path is proven, not assumed.
public class SettingsWindowTests
{
    [AvaloniaFact]
    public void Escape_closes_the_settings_window()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(ProfileFile.NewFromTemplate("smoke.csv"));

        var settings = new SettingsWindow(w);
        _ = settings.ShowDialog(w);
        Assert.True(settings.IsVisible);

        // The key must go to the settings window exactly as the OS would send
        // it, with whatever focus state the window really has after opening.
        settings.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        Assert.False(settings.IsVisible);
        w.Close();
    }

    // The tester previewed a new interface size, closed Settings without
    // saving, and the app crashed when the revert countdown fired against the
    // dead window. Closing with a preview pending must revert cleanly instead.
    [AvaloniaFact]
    public void Closing_with_a_size_preview_pending_reverts_without_crashing()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.InterfaceScalePercent = 100;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(ProfileFile.NewFromTemplate("smoke.csv"));

        var settings = new SettingsWindow(w);
        _ = settings.ShowDialog(w);

        var scale = settings.GetVisualDescendants().OfType<ComboBox>()
            .First(c => AutomationProperties.GetName(c) == "Interface size, in percent");
        scale.SelectedIndex = scale.ItemsSource!.Cast<string>().ToList().IndexOf("125%");
        Assert.Equal(1.25, w.UiScale, 2); // preview applied, not saved

        settings.Close();

        Assert.Equal(1.0, w.UiScale, 2); // reverted on close, no crash
        Assert.Equal(100, w.CurrentSettings.InterfaceScalePercent);
        w.Close();
    }

    // The backup checkbox must track GoogleAuth.IsConfigured: disabled on a
    // placeholder build so the user can't start an OAuth flow that can't
    // work, enabled on a build with a real client (GoogleClient.Local.cs).
    [AvaloniaFact]
    public void Backup_checkbox_shows_and_is_disabled_when_google_is_not_configured()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();

        var settings = new SettingsWindow(w);
        _ = settings.ShowDialog(w);

        var backupCheck = settings.GetVisualDescendants().OfType<CheckBox>()
            .First(c => AutomationProperties.GetName(c) == "Back up my profiles to Google Sheets");
        Assert.Equal(GoogleAuth.IsConfigured, backupCheck.IsEnabled);

        settings.Close();
        w.Close();
    }
}
