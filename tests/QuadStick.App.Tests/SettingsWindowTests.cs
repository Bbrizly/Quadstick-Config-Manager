using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
}
