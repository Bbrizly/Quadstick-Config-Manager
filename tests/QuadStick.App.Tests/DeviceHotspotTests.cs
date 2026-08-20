using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using System.Buffers.Binary;
using System.IO;
using Avalonia.Platform;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// Device view is a photo of the device with one label per part and a leader
// line to the part it names. A label that covers another label, or that hangs
// off the picture, hides the part it is there to point at. Both went wrong the
// first time the labels were dropped straight onto the parts.
public class DeviceHotspotTests
{
    static MainWindow Open()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.NewFromTemplate("mygame.csv");
        file.Dirty = false; // else Close opens the save dialog and waits forever
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(true);
        w.UpdateLayout();
        return w;
    }

    // The stage is the one Canvas holding the photo; every label is on it.
    static Canvas Stage(MainWindow w)
    {
        var stage = w.GetVisualDescendants().OfType<Canvas>()
            .FirstOrDefault(c => c.Children.OfType<Image>().Any());
        Assert.True(stage is not null, "the device photo is not in the visual tree");
        return stage!;
    }

    static (string Name, Rect Box)[] Labels(MainWindow w) =>
        Stage(w).Children.OfType<ToggleButton>()
            .Select(b => (AutomationProperties.GetName(b) ?? "",
                          new Rect(Canvas.GetLeft(b), Canvas.GetTop(b), b.Bounds.Width, b.Bounds.Height)))
            .ToArray();

    // The lit mode lights: the leader-line markers are ellipses too, but they
    // are drawn with a stroke and these are not.
    static double[] LitLights(MainWindow w) =>
        Stage(w).Children.OfType<Ellipse>()
            .Where(e => e.StrokeThickness == 0 && e.Opacity == 1)
            .Select(Canvas.GetLeft)
            .OrderBy(x => x)
            .ToArray();

    static string Caption(MainWindow w) =>
        w.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? "")
            .First(t => t.StartsWith("Device shows") || t.StartsWith("Device has no"));

    // Mode 1 lights the leftmost of the five; mode 2 the one to its right.
    // Both patterns come from the firmware's own table, see ModeLightsTests.
    [AvaloniaFact]
    public void The_mode_lights_follow_the_mode()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.NewFromTemplate("mygame.csv");
        int second = file.AddModeSheet("Driving");
        file.Dirty = false; // else Close opens the save dialog and waits forever
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(true);
        w.UpdateLayout();
        try
        {
            var one = LitLights(w);
            Assert.Single(one);
            Assert.Equal("Device shows light 1 purple", Caption(w));

            w.SelectSheetForPreview(second);
            w.UpdateLayout();

            var two = LitLights(w);
            Assert.Single(two);
            Assert.Equal("Device shows light 2 purple", Caption(w));
            Assert.True(two[0] > one[0],
                $"mode 2's light should sit right of mode 1's: {two[0]} vs {one[0]}");
        }
        finally { w.Close(); }
    }

    // Every hotspot and mode-light number in MainWindow is measured off this one
    // photo. Dropping in a differently framed picture leaves the numbers
    // pointing at the wrong holes, which is what happened when the first photo
    // was replaced. Pin the size so the swap fails here instead of on screen.
    [AvaloniaFact]
    public void The_photo_is_the_one_the_hotspots_were_measured_on()
    {
        // Read the PNG header rather than decoding: the headless platform hands
        // back a 1x1 stub bitmap, so PixelSize would prove nothing here.
        using var stream = AssetLoader.Open(
            new Uri("avares://QuadStickConfigManager/Assets/QuadStick.png"));
        var head = new byte[24];
        using (var all = new MemoryStream())
        {
            stream.CopyTo(all);
            all.Position = 0;
            Assert.Equal(24, all.Read(head, 0, 24));
        }
        int width = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(20, 4));
        Assert.True((width, height) == (1536, 1024),
            $"Assets/QuadStick.png is {width}x{height}, not 1536x1024. If the photo "
          + "changed, measure Hotspots and LedX/LedGap/LedY off the new one again.");
    }

    [AvaloniaFact]
    public void Every_part_on_the_device_has_a_label_on_the_photo()
    {
        var w = Open();
        try
        {
            var names = Labels(w).Select(l => l.Name).ToArray();
            foreach (var part in new[]
            {
                "Joystick", "Left mouthpiece hole", "Center mouthpiece hole",
                "Right mouthpiece hole", "Side tube", "Lip switch",
            })
                Assert.True(names.Any(n => n.StartsWith(part)), $"{part} has no label on the photo");
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void No_label_covers_another_one()
    {
        var w = Open();
        try
        {
            var labels = Labels(w);
            for (int i = 0; i < labels.Length; i++)
                for (int j = i + 1; j < labels.Length; j++)
                    Assert.False(labels[i].Box.Intersects(labels[j].Box),
                        $"'{labels[i].Name}' and '{labels[j].Name}' overlap on the photo");
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void No_label_hangs_off_the_picture()
    {
        var w = Open();
        try
        {
            var stage = Stage(w);
            var frame = new Rect(0, 0, stage.Width, stage.Height);
            foreach (var (name, box) in Labels(w))
                Assert.True(frame.Contains(box), $"'{name}' is not inside the picture: {box} in {frame}");
        }
        finally { w.Close(); }
    }
}
