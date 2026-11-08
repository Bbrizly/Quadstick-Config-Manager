using System;
using System.Globalization;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// Drew's November notes, both on the joystick page, found while getting ready
// to film the overview.
public class JoystickPageTests
{
    const string Header = "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\n";

    static MainWindow Open(ProfileFile file)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        var s = Settings.Load();
        s.Model = "FPS";
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(true);
        w.SelectZoneForPreview("joystick");
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    static void Done(MainWindow w, ProfileFile file) { file.Dirty = false; w.Close(); }

    // The free list sorted by raw text, so the eight-way ring (E, E_inner, N...)
    // came first and the four directions people map most sat at the bottom.
    [AvaloniaFact]
    public void The_four_directions_lead_the_free_list()
    {
        var file = ProfileFile.Load(Header + "x,normal,lip\n");
        var w = Open(file);
        var free = w.GetVisualDescendants().OfType<Button>()
            .Select(b => AutomationProperties.GetName(b) ?? "")
            .Where(n => n.StartsWith("Map ") && n.Contains(" to a new mapping on "))
            .Select(n => n[4..n.IndexOf(" to a new mapping on ", StringComparison.Ordinal)])
            .ToArray();

        Assert.Equal(new[] { "up", "down", "left", "right" }, free.Take(4));
        Done(w, file);
    }
}
