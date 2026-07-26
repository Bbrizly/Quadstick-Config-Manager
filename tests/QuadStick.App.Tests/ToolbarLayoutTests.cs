using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// Toolbar buttons must stay on screen at narrow width and at 200% scale.
// The headless window is a fixed 1024x768 and ignores Width, so scale is the
// only lever here: the layout gets window / scale, so 200% means 512 px.
public class ToolbarLayoutTests
{
    static readonly string[] EditorButtons =
    {
        "HomeButton", "ShareButton", "SaveButton", "UndoButton", "SaveTemplateButton",
        "InstallButton", "HelpButton", "EditorSettingsButton", "ModeHelpButton",
        "ModesButton", "DeviceViewButton", "RailViewButton",
        "ListViewButton", "AddRowButton", "UnusedButton",
    };

    // Settings live in one file that every test in the run shares, so a scale
    // left behind here silently re-lays-out every other test's window. Always
    // hand it back at 100.
    static void Close(MainWindow w)
    {
        w.ApplyInterfaceScale(100);
        var s = Settings.Load();
        s.InterfaceScalePercent = 100;
        Settings.Save(s);
        w.Close();
    }

    static MainWindow Open(int scalePercent)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.InterfaceScalePercent = scalePercent;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.ApplyInterfaceScale(scalePercent);
        var file = ProfileFile.NewFromTemplate("mygame.csv");
        file.Dirty = false; // else Close opens the save dialog and waits forever
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();
        return w;
    }

    // Horizontal is the axis that used to lose controls with no way back. Every
    // button has to end inside the window's width.
    static void AssertNothingRunsOffTheSide(MainWindow w, string where)
    {
        foreach (var name in EditorButtons)
        {
            var b = w.GetVisualDescendants().OfType<Button>().FirstOrDefault(x => x.Name == name);
            Assert.True(b is not null, $"{name} missing {where}");
            Assert.True(b!.Bounds.Width > 0 && b.Bounds.Height > 0, $"{name} has no size {where}");
            // The far corner of the button in window coordinates. TranslatePoint
            // walks the interface-scale transform, so this is where the user's
            // pointer actually has to reach.
            var corner = b.TranslatePoint(new Point(b.Bounds.Width, b.Bounds.Height), w);
            Assert.True(corner.HasValue, $"{name} is not in the visual tree {where}");
            Assert.True(corner!.Value.X <= w.Bounds.Width + 1,
                $"{name} runs off the right edge {where}: {corner.Value.X:0} > {w.Bounds.Width:0}");
        }
    }

    [AvaloniaFact]
    public void No_toolbar_button_runs_off_the_side_at_100_percent()
    {
        var w = Open(100);
        // finally: reset shared scale even when assert fails.
        try { AssertNothingRunsOffTheSide(w, "at 100%"); }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void No_toolbar_button_runs_off_the_side_at_200_percent()
    {
        var w = Open(200);
        try { AssertNothingRunsOffTheSide(w, "at 200% scale (512 logical px)"); }
        finally { Close(w); }
    }

    // Min size doubles at 200% scale.
    [AvaloniaFact]
    public void The_window_minimum_grows_with_the_interface_scale()
    {
        var w = Open(100);
        try
        {
            Assert.Equal(760, w.MinWidth);
            Assert.Equal(560, w.MinHeight);

            w.ApplyInterfaceScale(200);
            Assert.Equal(1520, w.MinWidth);
            Assert.Equal(1120, w.MinHeight);

            w.ApplyInterfaceScale(100);
            Assert.Equal(760, w.MinWidth);
        }
        finally { Close(w); }
    }
}
