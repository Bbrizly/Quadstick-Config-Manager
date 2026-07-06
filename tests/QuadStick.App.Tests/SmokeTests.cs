using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(QuadStick.App.Tests.TestAppBuilder))]

namespace QuadStick.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

// UI smoke tests: build the REAL window headlessly and drive its public
// seams. Every screen and every model must construct without throwing.
// A crash here is a crash a disabled user would have hit; these run in CI
// on every push, so a crash-on-click can never ship unnoticed again.
public class SmokeTests
{
    static MainWindow NewWindow()
    {
        // A fresh CI machine has no settings file, which would auto-start
        // the tutorial overlay and wall off the UI mid-test. Pre-mark it seen.
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        return w;
    }

    [AvaloniaFact]
    public void Window_opens_on_home_without_throwing()
    {
        var w = NewWindow();
        Assert.Contains("QuadStick Config Manager", w.Title);
        w.Close();
    }

    [AvaloniaFact]
    public void New_profile_opens_the_editor_and_every_zone_builds()
    {
        var w = NewWindow();
        w.LoadProfile(ProfileFile.NewFromTemplate("smoke.csv"));
        Assert.Contains("smoke.csv", w.Title);

        // Selecting every part of the device must never throw, mapped or not.
        foreach (var zone in new[]
                 { "joystick", "mp_left", "mp_center", "mp_right", "combo", "side", "lip", "jacks", "other", "unset" })
            w.SelectZoneForPreview(zone);
        w.Close();
    }

    [AvaloniaFact]
    public void Every_model_rebuilds_the_device_view_without_throwing()
    {
        var w = NewWindow();
        w.LoadProfile(ProfileFile.NewFromTemplate("smoke.csv"));
        for (int model = 0; model < 3; model++)
        {
            w.SetModelForPreview(model);
            w.SelectZoneForPreview("mp_center");
        }
        w.Close();
    }

    [AvaloniaFact]
    public void Editing_through_the_file_keeps_the_window_alive()
    {
        var w = NewWindow();
        var f = ProfileFile.NewFromTemplate("smoke.csv");
        w.LoadProfile(f);
        var b = f.Document.Sheets[0].Bindings[0];
        f.SetCell(b.Row, 0, "circle");          // edit an output
        f.EnsureVersionHeader();                 // the P0 case: rows shift
        w.LoadProfile(f);                        // window must rebind cleanly
        w.SelectZoneForPreview("mp_left");
        Assert.True(f.Document.HasVersionHeader);
        // Closing dirty pops the "save your changes?" dialog, which has no
        // user to answer it headlessly (that guard doing its job is exactly
        // why it hangs). Mark saved so Close proceeds.
        f.Dirty = false;
        w.Close();
    }
}
