using System;
using System.Buffers.Binary;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// The back of the QuadStick was one flat "Switch jacks" list of eight
// digital_in numbers, and nothing anywhere said which socket a number came
// out of. Somebody setting a device up for another person had to already know.
public class BackPanelTests
{
    static MainWindow OnZone(string zone, string csv)
    {
        var s = Settings.Load();
        s.Model = "FPS";   // the diagram's zones depend on it, and neighbours change it
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.DeviceCards = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(ProfileFile.Load(csv));
        w.SelectZoneForPreview(zone);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    static string AllText(MainWindow w) =>
        string.Join(" ", w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? ""));

    const string WithJack =
        "Profile Name,,Solo\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "x,normal,digital_in_8\n";

    // The default is the fact people get wrong, so it has to be on screen.
    [AvaloniaFact]
    public void The_jacks_screen_names_which_socket_each_number_is()
    {
        var w = OnZone("jacks", WithJack);
        var text = AllText(w);
        Assert.Contains("Top jack", text, StringComparison.Ordinal);
        Assert.Contains("Bottom jack", text, StringComparison.Ordinal);
        Assert.Contains("Lip jack", text, StringComparison.Ordinal);
        Assert.Contains("digital_in_8", text, StringComparison.Ordinal);
        Assert.Contains("splitter", text, StringComparison.Ordinal);
        w.Close();
    }

    // Drew asked for a click-through for a joystick in the rear USB port. The
    // four names have always been legal; nothing pointed at them.
    [AvaloniaFact]
    public void The_usb_screen_names_the_rear_joystick_directions()
    {
        var w = OnZone("other", WithJack);
        var text = AllText(w);
        Assert.Contains("usb_1_up", text, StringComparison.Ordinal);
        Assert.Contains("usb_1_right", text, StringComparison.Ordinal);
        w.Close();
    }

    // A mouthpiece zone has no back panel, so the guide must not follow the
    // user around the device.
    [AvaloniaFact]
    public void A_front_zone_gets_no_back_panel()
    {
        var w = OnZone("lip",
            "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\nx,normal,lip\n");
        Assert.DoesNotContain("Back of the QuadStick", AllText(w), StringComparison.Ordinal);
        w.Close();
    }

    // In the picker a channel has to read as the socket, not as the token with
    // its underscores taken out.
    [Theory]
    [InlineData("digital_in_8", "Top jack, one switch")]
    [InlineData("digital_in_7", "Top jack, splitter, second switch")]
    [InlineData("digital_in_1", "Bottom jack, one switch")]
    [InlineData("usb_1_up", "Rear joystick, up")]
    public void A_jack_reads_as_its_socket(string token, string expected) =>
        Assert.Equal(expected, SwitchJacks.For(token)?.Label
                               ?? $"Rear joystick, {token["usb_1_".Length..]}");

    // The top jack is where a lone switch goes, so it is what the list should
    // offer first. Straight alphabetical put digital_in_1 there, which is the
    // bottom, and digital_in_3 third, which is not a socket at all.
    [AvaloniaFact]
    public void The_unused_list_starts_at_the_top_jack()
    {
        // Nothing mapped on the jacks, so all eight are unused and each socket
        // name appears exactly once. A mapped one would also show as a chip on
        // its card and there would be no telling the two apart by text.
        var w = OnZone("jacks",
            "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\nx,normal,lip\n");
        // Only the list entries, not the guide above it: an entry carries the
        // socket AND which half of it, so it always has a comma. The guide's
        // own rows are the bare port name.
        var lines = w.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? "")
            .Where(t => t.Contains(", one switch", StringComparison.Ordinal)
                     || t.Contains(", splitter,", StringComparison.Ordinal)
                     || t == SwitchJacks.UsbDataPort)
            .ToList();
        Assert.Equal(8, lines.Count);

        // A lone switch goes in the top jack, so that is what to offer first.
        var top = lines.FindIndex(t => t.StartsWith("Top jack", StringComparison.Ordinal));
        Assert.Equal("Top jack, one switch", lines[0]);
        var bottom = lines.FindIndex(t => t.StartsWith("Bottom jack", StringComparison.Ordinal));
        var pins = lines.IndexOf(SwitchJacks.UsbDataPort);
        Assert.True(top < bottom, "the top jack has to come before the bottom one");
        Assert.True(bottom < pins, "the USB-A data pins sort last: nothing plugs into them");
        w.Close();
    }

    // The picture is the thing Drew asked for by name. Every socket on it has
    // to carry its own label, or it is a photo with dots on it.
    [AvaloniaFact]
    public void The_photo_names_every_socket_on_it()
    {
        var w = OnZone("jacks", WithJack);
        // The sockets are buttons on the main stage now, not labels, so this
        // reads names off any control rather than off a Border. A socket that
        // already has something mapped says so after its name, so these are
        // the start of the name rather than the whole of it.
        var spoken = w.GetVisualDescendants().OfType<Control>()
            .Select(b => AutomationProperties.GetName(b) ?? "")
            .ToList();
        foreach (var expected in new[]
                 {
                     "Top jack. One switch: in 8",
                     "Lip jack. One switch: in 5",
                     "Bottom jack. One switch: in 1",
                     "USB-B port. To the computer",
                     "USB-A port. Joystick, or in 3-4",
                 })
            Assert.Contains(spoken, n => n.StartsWith(expected, StringComparison.Ordinal));
        w.Close();
    }

    // A picture of the back of the case that says nothing about the switches
    // already mapped to it is a picture of nobody's device. The socket carries
    // what it presses, the way a hole on the front does.
    [AvaloniaFact]
    public void A_socket_that_is_mapped_says_what_it_presses()
    {
        var w = OnZone("jacks", WithJack);
        var spoken = w.GetVisualDescendants().OfType<Control>()
            .Select(b => AutomationProperties.GetName(b) ?? "")
            .ToList();
        Assert.Contains(spoken, n => n.StartsWith("Top jack. One switch: in 8. ", StringComparison.Ordinal));
        // And a socket with nothing in it stays the bare name: no empty row,
        // and no word for "nothing", which would read as something mapped.
        Assert.Contains(spoken, n => n == "Bottom jack. One switch: in 1");
        w.Close();
    }

    // The socket points are fractions of this exact photo. A different crop
    // puts every marker somewhere the socket is not, and nothing else would
    // notice.
    [AvaloniaFact]
    public void The_photo_is_the_one_the_sockets_were_measured_on()
    {
        using var stream = AssetLoader.Open(
            new Uri("avares://QuadStickConfigManager/Assets/QuadStickBack.png"));
        var head = new byte[24];
        using var all = new MemoryStream();
        stream.CopyTo(all);
        all.Position = 0;
        Assert.Equal(24, all.Read(head, 0, 24));
        int width = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(20, 4));
        Assert.True((width, height) == (588, 319),
            $"Assets/QuadStickBack.png is {width}x{height}, not 588x319. If the photo "
          + "changed, measure the socket points in BackSockets off the new one again.");
    }

    // The rear USB-A port is a socket on the case, so its card is on the
    // device view before anything maps to it. It used to appear only once a
    // profile already used a usb_1_* name, which is the wrong way round: you
    // click the card to find those names.
    [AvaloniaFact]
    public void The_usb_card_is_on_the_device_before_anything_maps_to_it()
    {
        var s = Settings.Load();
        s.Model = "FPS";   // the diagram's zones depend on it, and neighbours change it
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.NewFromTemplate("mygame.csv");
        file.Dirty = false;
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(true);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Assert.Contains("USB devices", AllText(w), StringComparison.Ordinal);
        w.Close();
    }

    // Sixteen button numbers sort before "usb_1_up", so the four directions
    // Drew asked for came last on a list of seventy-three.
    [AvaloniaFact]
    public void The_usb_screen_offers_the_joystick_directions_first()
    {
        var w = OnZone("other",
            "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\nx,normal,lip\n");
        var offered = w.GetVisualDescendants().OfType<Button>()
            .Select(b => AutomationProperties.GetName(b) ?? "")
            .Where(n => n.StartsWith("Map ", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(
            SwitchJacks.RearJoystick.Select(t => $"Map {t} to a new mapping on the USB devices").ToArray(),
            offered.Take(4).ToArray());
        w.Close();
    }

    // The Detailed picker only lists a category's declared subcategories, so a
    // token with no socket would be unreachable. Every digital_in has one.
    [Fact]
    public void Every_jack_channel_has_a_socket_the_picker_lists()
    {
        var listed = SwitchJacks.Ports.Select(p => p.Port).ToHashSet(StringComparer.Ordinal);
        foreach (var token in Vocab.Inputs.Where(i => i.StartsWith("digital_in", StringComparison.Ordinal)))
        {
            var jack = SwitchJacks.For(token);
            Assert.NotNull(jack);
            Assert.Contains(jack!.Port, listed);
        }
    }
}
