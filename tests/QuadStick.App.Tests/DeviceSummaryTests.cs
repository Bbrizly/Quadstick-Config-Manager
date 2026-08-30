using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

public class DeviceSummaryTests
{
    const string Header = "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\n";

    static ModeSheet Sheet(string rows) =>
        ProfileFile.Load(Header + rows).Document.Sheets[0];

    [Fact]
    public void Mouthpiece_summary_keeps_all_four_gestures_and_uses_action_names()
    {
        var sheet = Sheet(
            "circle,normal,mp_center_puff\n" +
            "square,normal,mp_center_sip\n" +
            "ps3,normal,mp_center_sip_soft,,,,,,,,,PS\n");

        var rows = DeviceSummary.Mouthpiece(sheet, "mp_center", Token);

        Assert.Equal(new[] { "Soft Puff", "Puff", "Sip", "Soft Sip" },
            rows.Select(x => x.FriendlyGestureName));
        Assert.Equal("—", Display(rows[0]));
        Assert.Equal("Circle", Display(rows[1]));
        Assert.Equal("Square", Display(rows[2]));
        Assert.Equal("PS", Display(rows[3]));
    }

    [Fact]
    public void Same_gesture_rows_aggregate_only_when_their_behavior_matches()
    {
        var safe = Sheet(
            "circle,normal,mp_center_puff\n" +
            "square,normal,mp_center_puff\n");
        var safeRow = DeviceSummary.Mouthpiece(safe, "mp_center", Token)[1];
        Assert.Equal("Circle · Square", Display(safeRow));
        Assert.False(safeRow.HasComplexBehavior);

        var complex = Sheet(
            "circle,normal,mp_center_puff\n" +
            "square,toggle,mp_center_puff\n");
        var complexRow = DeviceSummary.Mouthpiece(complex, "mp_center", Token)[1];
        Assert.True(complexRow.HasComplexBehavior);
        Assert.Equal(2, complexRow.Actions.Count);
    }

    [Fact]
    public void A_combo_row_does_not_leak_into_a_mouthpiece_summary()
    {
        var sheet = Sheet("circle,normal,mp_left_center_puff\n");
        var rows = DeviceSummary.Mouthpiece(sheet, "mp_center", Token);
        Assert.All(rows, row => Assert.False(row.IsMapped));
    }

    [Fact]
    public void Joystick_recognizes_the_exact_left_stick_pattern()
    {
        var sheet = Sheet(
            "left_joy_left,normal,left\n" +
            "left_joy_right,normal,right\n" +
            "left_joy_up,normal,up\n" +
            "left_joy_down,normal,down\n");

        var summary = DeviceSummary.Joystick(sheet);

        Assert.True(summary.IsRecognized);
        Assert.Equal("Left Stick", summary.Role);
        Assert.Equal(0, summary.ExtraActionCount);
    }

    [Fact]
    public void Joystick_falls_back_when_the_pattern_is_not_exact()
    {
        var sheet = Sheet(
            "left_joy_left,normal,left\n" +
            "left_joy_right,normal,right\n" +
            "left_joy_up,normal,up\n" +
            "circle,normal,any_direction\n");

        var summary = DeviceSummary.Joystick(sheet);

        Assert.False(summary.IsRecognized);
        Assert.Equal(4, summary.ActionCount);
    }

    [Fact]
    public void Side_tube_force_off_is_support_not_a_second_gesture()
    {
        var sheet = Sheet(
            "circle,normal,right_puff\n" +
            "circle,force_off 500,right_puff\n");
        var row = DeviceSummary.Mouthpiece(sheet, "side", Token)[1];

        Assert.True(row.IsMapped);
        Assert.True(row.HasComplexBehavior);
        Assert.Equal(4, DeviceSummary.Mouthpiece(sheet, "side", Token).Count);
        Assert.Equal(2, row.Actions.Count);
    }

    [AvaloniaFact]
    public void Center_callout_renders_the_four_physical_gestures()
    {
        var settings = Settings.Load();
        settings.TutorialSeen = true;
        // Pinned, not inherited: the settings file is shared with every other
        // test in this assembly, and one that leaves the model on a Singleton
        // takes the lip switch and the side tube off the photo, so the callout
        // this looks for is simply not there.
        settings.Model = "FPS";
        settings.DeviceCards = true;
        settings.RememberWindow = false;
        Settings.Save(settings);

        var window = new MainWindow();
        window.Show();
        var file = ProfileFile.Load(Header +
            "circle,normal,mp_center_puff\n" +
            "square,normal,mp_center_sip\n" +
            "ps3,normal,mp_center_sip_soft,,,,,,,,,PS\n");
        file.Dirty = false;
        window.LoadProfile(file);
        window.SetDeviceViewForPreview(true);
        window.UpdateLayout();

        try
        {
            var callout = window.GetVisualDescendants().OfType<ToggleButton>()
                .First(b => (AutomationProperties.GetName(b) ?? "")
                    .StartsWith("Center mouthpiece hole", StringComparison.Ordinal));
            var text = string.Join(" ", callout.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text ?? ""));
            Assert.Contains("Soft Puff", text);
            Assert.Contains("Puff", text);
            Assert.Contains("Sip", text);
            Assert.Contains("Soft Sip", text);
            Assert.Contains("—", text);
            // A face button draws itself and drops its name. ps3 carries the
            // user's own name for it, which no picture says, so PS stays.
            Assert.DoesNotContain("Circle", text);
            Assert.DoesNotContain("Square", text);
            Assert.Contains("PS", text);
            Assert.DoesNotContain("button", text.ToLowerInvariant());
            foreach (var drawn in new[] { "Circle", "Square" })
                Assert.Contains(callout.GetVisualDescendants().OfType<Control>(),
                    c => AutomationProperties.GetName(c) == drawn);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Device_callouts_remain_reachable_at_two_hundred_percent_scale()
    {
        var settings = Settings.Load();
        settings.TutorialSeen = true;
        // Pinned, not inherited: the settings file is shared with every other
        // test in this assembly, and one that leaves the model on a Singleton
        // takes the lip switch and the side tube off the photo, so the callout
        // this looks for is simply not there.
        settings.Model = "FPS";
        settings.RememberWindow = false;
        settings.InterfaceScalePercent = 200;
        Settings.Save(settings);

        var window = new MainWindow();
        window.Show();
        var file = ProfileFile.NewFromTemplate("scaled.csv");
        file.Dirty = false;
        window.LoadProfile(file);
        window.SetDeviceViewForPreview(true);
        window.ApplyInterfaceScale(200);
        window.UpdateLayout();

        try
        {
            var stage = window.GetVisualDescendants().OfType<Canvas>()
                .First(c => c.Name == "DeviceStage");
            var frame = new Rect(0, 0, stage.Bounds.Width, stage.Bounds.Height);
            // The photo is drawn full size inside a window clipped to the part
            // worth showing, so the window is what a callout must not cover.
            var photo = stage.Children.OfType<Canvas>().Single(c => c.Name == "DevicePhotoFrame");
            var photoFrame = new Rect(Canvas.GetLeft(photo), Canvas.GetTop(photo),
                photo.Width, photo.Height);
            var labels = stage.Children.OfType<ToggleButton>().ToList();
            Assert.NotEmpty(labels);
            Assert.All(labels, label =>
            {
                // Bounds, not the Canvas.Left/Top pair: a top callout is pinned
                // by its bottom edge so it can grow upward, and Canvas.GetTop
                // reads NaN for those.
                var box = label.Bounds;
                Assert.True(frame.Contains(box), $"callout is outside the scaled stage: {box}");
                Assert.True(box.Bottom <= photoFrame.Top || box.Top >= photoFrame.Bottom
                    || box.Right <= photoFrame.Left || box.Left >= photoFrame.Right,
                    $"callout overlaps the device photo: {box}");
                Assert.True(label.Bounds.Width > 0 && label.Bounds.Height > 0);
            });
        }
        finally
        {
            window.ApplyInterfaceScale(100);
            var restored = Settings.Load();
            restored.InterfaceScalePercent = 100;
            Settings.Save(restored);
            window.Close();
        }
    }

    // The lip switch has three inputs. Showing one action for the whole part
    // hid the other two.
    [Fact]
    public void Lip_summary_keeps_all_three_switch_inputs()
    {
        var sheet = Sheet(
            "circle,normal,lip\n" +
            "square,normal,push\n");

        var rows = DeviceSummary.Mouthpiece(sheet, "lip", Token);

        Assert.Equal(new[] { "Lip", "Soft Lip", "Push" },
            rows.Select(x => x.FriendlyGestureName));
        Assert.Equal("Circle", Display(rows[0]));
        Assert.Equal("—", Display(rows[1]));
        Assert.Equal("Square", Display(rows[2]));
    }

    [Fact]
    public void A_recognized_stick_carries_the_token_its_art_is_drawn_from()
    {
        var sheet = Sheet(
            "left_joy_left,normal,left\n" +
            "left_joy_right,normal,right\n" +
            "left_joy_up,normal,up\n" +
            "left_joy_down,normal,down\n");

        Assert.Equal("left_stick", DeviceSummary.Joystick(sheet).RoleToken);
        Assert.Equal("", DeviceSummary.Joystick(Sheet(
            "mouse_left,normal,left\n" +
            "mouse_right,normal,right\n" +
            "mouse_up,normal,up\n" +
            "mouse_down,normal,down\n")).RoleToken);
    }

    [AvaloniaFact]
    public void Lip_callout_lists_the_three_inputs_and_the_stick_shows_its_art()
    {
        var settings = Settings.Load();
        settings.TutorialSeen = true;
        // Pinned, not inherited: the settings file is shared with every other
        // test in this assembly, and one that leaves the model on a Singleton
        // takes the lip switch and the side tube off the photo, so the callout
        // this looks for is simply not there.
        settings.Model = "FPS";
        settings.DeviceCards = true;
        settings.RememberWindow = false;
        Settings.Save(settings);

        var window = new MainWindow();
        window.Show();
        var file = ProfileFile.Load(Header +
            "circle,normal,lip\n" +
            "square,normal,push\n" +
            "left_joy_left,normal,left\n" +
            "left_joy_right,normal,right\n" +
            "left_joy_up,normal,up\n" +
            "left_joy_down,normal,down\n");
        file.Dirty = false;
        window.LoadProfile(file);
        window.SetDeviceViewForPreview(true);
        window.UpdateLayout();

        try
        {
            string Words(string name) => string.Join(" ", window.GetVisualDescendants()
                .OfType<ToggleButton>()
                .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith(name, StringComparison.Ordinal))
                .GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? ""));

            var lipButton = window.GetVisualDescendants().OfType<ToggleButton>()
                .First(b => (AutomationProperties.GetName(b) ?? "")
                    .StartsWith("Lip switch", StringComparison.Ordinal));
            var lip = Words("Lip switch");
            Assert.Contains("Soft Lip", lip);
            Assert.Contains("Push", lip);
            Assert.Contains("—", lip);
            // Same rule on this callout: the prompt is the label.
            Assert.DoesNotContain("Circle", lip);
            Assert.DoesNotContain("Square", lip);
            foreach (var drawn in new[] { "Circle", "Square" })
                Assert.Contains(lipButton.GetVisualDescendants().OfType<Control>(),
                    c => AutomationProperties.GetName(c) == drawn);

            var stick = window.GetVisualDescendants().OfType<ToggleButton>()
                .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Joystick", StringComparison.Ordinal));
            Assert.DoesNotContain("Left Stick", string.Join(" ", stick.GetVisualDescendants()
                .OfType<TextBlock>().Select(t => t.Text ?? "")));
            Assert.Contains(stick.GetVisualDescendants().OfType<Control>(),
                c => AutomationProperties.GetName(c) == "Left Stick");
        }
        finally { window.Close(); }
    }

    static string Token(string token) =>
        token.Length == 0 ? token : char.ToUpperInvariant(token[0]) + token[1..].Replace('_', ' ');

    static string Display(GestureSummary row) =>
        row.Actions.Where(a => !a.IsSupport && a.FriendlyOutput.Length > 0)
            .Select(a => a.FriendlyOutput).Distinct().DefaultIfEmpty("—")
            .Aggregate((a, b) => a + " · " + b);
}
