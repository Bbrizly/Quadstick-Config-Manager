using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// Device View is the view the app opens in, and the QuadStick is the thing it
// exists to show. Everything here is about the picture staying on screen and
// the controls around it saying which view they belong to.
//
// The headless window is a fixed 1024x768, which is close to the smallest
// window a real user will have, so anything that fits here fits anywhere.
public class DeviceChromeTests
{
    static MainWindow Open(bool deviceView)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.InterfaceScalePercent = 100;
        s.Model = "FPS";
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.NewFromTemplate("mygame.csv");
        file.Dirty = false; // else Close opens the save dialog and waits forever
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(deviceView);
        w.UpdateLayout();
        return w;
    }

    static Control Named(MainWindow w, string name) =>
        w.GetVisualDescendants().OfType<Control>().First(c => c.Name == name);

    // The scaled photo and its labels. Named through the canvas, not by taking
    // the first Viewbox in the window: the status chip has one too.
    static Viewbox Stage(MainWindow w) =>
        Named(w, "DeviceCanvas").GetVisualDescendants().OfType<Viewbox>().First();

    // Add Row in Device View put a row somewhere the user was not looking: the
    // canvas shows parts, not rows, so the new row landed off screen with
    // nothing to say where. Device View adds from the part itself.
    [AvaloniaFact]
    public void Add_row_belongs_to_rows_view_only()
    {
        var w = Open(deviceView: true);
        Assert.False(Named(w, "AddRowButton").IsVisible, "Add Row is offering to put a row nowhere");

        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();
        Assert.True(Named(w, "AddRowButton").IsVisible, "Rows view lost the one control that adds a row");

        w.Close();
    }

    // Off for this release. The implementation stays (UnusedInputsTests still
    // drives it); only the permanent toolbar slot is gone. Flip this when the
    // button comes back, the same way AgentOffTests pins the agent.
    [AvaloniaFact]
    public void The_unused_list_has_no_toolbar_slot()
    {
        var w = Open(deviceView: false);
        Assert.False(Named(w, "UnusedButton").IsVisible);
        w.Close();
    }

    // The picture, the whole picture, without scrolling. Whole combos, switch
    // jacks, USB devices and "No input yet" used to stack under the diagram
    // inside one scrolling column, so at any ordinary window size the QuadStick
    // was above the fold and the first thing on screen was a row of cards.
    [AvaloniaFact]
    public void The_quadstick_is_fully_visible_without_scrolling()
    {
        var w = Open(deviceView: true);

        var scroll = (ScrollViewer)Named(w, "DeviceStageScroll");
        var stage = Stage(w);

        Assert.True(stage.Bounds.Height > 0, "the diagram never laid out");
        Assert.True(stage.Bounds.Height <= scroll.Viewport.Height + 1,
            $"the QuadStick is taller than the room it has: {stage.Bounds.Height:0} in {scroll.Viewport.Height:0}");
        Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 1,
            "the device panel needs scrolling at 1024x768");

        w.Close();
    }

    // And it is still big enough to read. Fitting is only worth anything if the
    // part labels survive it.
    [AvaloniaFact]
    public void The_quadstick_keeps_a_readable_size()
    {
        var w = Open(deviceView: true);
        var stage = Stage(w);
        Assert.True(stage.Bounds.Height >= 240,
            $"the diagram shrank to {stage.Bounds.Height:0}px tall, which is under what its labels need");
        w.Close();
    }

    // The secondary parts live in the side panel, not under the picture, and
    // every one of them is still reachable.
    [AvaloniaFact]
    public void The_secondary_parts_live_in_the_side_panel()
    {
        var w = Open(deviceView: true);

        var list = (StackPanel)Named(w, "ZoneList");
        Assert.True(list.Children.Count >= 3, "the combos, jacks and USB rows went missing");
        Assert.True(list.IsVisible && list.Bounds.Height > 0, "the parts list never laid out");
        Assert.Contains(Named(w, "EditorSidebar"), list.GetVisualAncestors());
        Assert.DoesNotContain(Named(w, "DeviceContainer"), list.GetVisualAncestors());

        w.Close();
    }

    // A per-mode settings row had a zone and no way into it: the panel listed
    // combos, jacks and USB and dropped Mode settings, so the only way to reach
    // one was the Parts list. The app knowing something and not showing it is
    // the failure this project keeps having.
    [AvaloniaFact]
    public void A_mode_with_settings_rows_can_reach_them_from_the_panel()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.Model = "FPS";
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.Load(
            "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\nmouse_speed,,50\n");
        file.Dirty = false;
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(true);
        w.UpdateLayout();

        var list = (StackPanel)Named(w, "ZoneList");
        Assert.Contains(list.Children.OfType<Control>(),
            c => (Avalonia.Automation.AutomationProperties.GetName(c) ?? "").StartsWith("Mode settings"));

        w.Close();
    }

    // Which QuadStick, whether one is plugged in, and which mode is on screen
    // read as one thing in the side panel. As a band above the picture they
    // were five loose controls that between them cost the diagram its top.
    [AvaloniaFact]
    public void The_device_context_sits_in_the_side_panel()
    {
        var w = Open(deviceView: true);
        var sidebar = Named(w, "EditorSidebar");

        foreach (var name in new[] { "ModelPicker", "DeviceHeaderStatus", "DeviceHeaderMode" })
        {
            var c = Named(w, name);
            Assert.True(c.GetVisualAncestors().Contains(sidebar), $"{name} is outside the side panel");
        }

        // Read in order down the panel: type, then connection, then mode.
        double At(string name) => Named(w, name).TranslatePoint(new Point(0, 0), w)!.Value.Y;
        Assert.True(At("ModelPicker") <= At("DeviceHeaderStatus"));
        Assert.True(At("DeviceHeaderStatus") <= At("DeviceHeaderMode"));

        w.Close();
    }
}
