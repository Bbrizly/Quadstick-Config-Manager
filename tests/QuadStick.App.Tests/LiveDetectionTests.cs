using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// A QuadStick that is plugged in should be a QuadStick the app says is plugged
// in, without anybody clicking anything first.
//
// The chip in the profile editor's sidebar used to ask one question: is a drive
// mounted? That misses a stick in an emulation mode that publishes no drive,
// and it only ran when an edit happened to redraw the toolbar. The live reader
// answers the other half, and it answers it while the app is open.
public class LiveDetectionTests
{
    static MainWindow Open()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow { FindDeviceRoots = Array.Empty<string> };
        w.Show();
        var file = ProfileFile.NewFromTemplate("mygame.csv");
        file.Dirty = false; // else Close opens the save dialog and waits forever
        w.LoadProfile(file);
        w.UpdateLayout();
        return w;
    }

    static string ChipText(MainWindow w) =>
        w.GetVisualDescendants().OfType<Control>().First(c => c.Name == "DeviceHeaderStatus")
         .GetVisualDescendants().OfType<TextBlock>().First().Text ?? "";

    [AvaloniaFact]
    public void A_stick_that_reads_is_a_stick_the_panel_calls_connected()
    {
        var w = Open();
        Assert.Equal(Strings.Main_NoQuadStickDetected, ChipText(w));

        // No drive mounted, and the reader has the gamepad open anyway. That is
        // a QuadStick, and every emulation mode reaches this state.
        w.ShowLiveInputForPreview(new LiveState(0, 0, Array.Empty<int>(), "QuadStick", new HashSet<string>(), true));
        Assert.Equal(Strings.Main_QuadStickConnected, ChipText(w));
    }

    [AvaloniaFact]
    public void A_stick_unplugged_goes_back_to_saying_nothing_is_here()
    {
        var w = Open();
        w.ShowLiveInputForPreview(new LiveState(0, 0, Array.Empty<int>(), "QuadStick", new HashSet<string>(), true));
        w.ShowLiveInputForPreview(null);
        Assert.Equal(Strings.Main_NoQuadStickDetected, ChipText(w));
    }

    [AvaloniaFact]
    public void A_mounted_drive_is_still_a_QuadStick_with_nothing_reading()
    {
        var w = Open();
        w.FindDeviceRoots = () => new[] { "/Volumes/QUADSTICK" };
        w.ShowLiveInputForPreview(null);
        Assert.Equal(Strings.Main_QuadStickConnected, ChipText(w));
    }
}
