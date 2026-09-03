using System;
using System.Globalization;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// Drew Redepenning, who sets QuadSticks up for patients, asked for the picture
// to follow what is picked on the left. Before this, a hole combo and a switch
// jack were reached from a list while the photo above them showed the front of
// the device and named neither, so the one view whose whole job is to say "it
// is this bit of your device" said nothing about the two parts hardest to name.
public class DiagramFollowsPickTests
{
    const string Csv =
        "Profile Name,,Solo\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "x,normal,mp_left_center_sip\n" +
        "circle,normal,digital_in_8\n";

    static MainWindow Open(string? zone = null)
    {
        // These assertions read English out of the window, and another test
        // class leaving the UI culture elsewhere would otherwise fail them.
        // ComboPair is built from the hole names now, so it follows the culture.
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        var s = Settings.Load();
        s.Model = "FPS";   // the diagram's zones depend on it, and neighbours change it
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(ProfileFile.Load(Csv));
        // Explicit, not inherited: the view is a saved setting, and another
        // test class leaving it on Rows would take the diagram off the screen
        // these tests are about.
        w.SetDeviceViewForPreview(true);
        if (zone is not null) w.SelectZoneForPreview(zone);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    static string[] Names(MainWindow w) => w.GetVisualDescendants().OfType<Control>()
        .Select(c => AutomationProperties.GetName(c) ?? "").Where(n => n.Length > 0).ToArray();

    static Control Named(MainWindow w, string startsWith) =>
        w.GetVisualDescendants().OfType<Control>()
            .First(c => (AutomationProperties.GetName(c) ?? "").StartsWith(startsWith, StringComparison.Ordinal));

    static void Click(MainWindow w, string startsWith)
    {
        ((Control)Named(w, startsWith)).RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
    }

    // Picking the hole combos labels the pairings on the device instead of the
    // single holes. Each pairing sits between the two parts it uses, so which
    // holes "Left + Center" means is read off the picture, not off a token.
    [AvaloniaFact]
    public void Picking_hole_combos_labels_the_pairings_on_the_device()
    {
        var w = Open("combo");
        var names = Names(w);

        foreach (var pair in new[] { "Left + Center", "Right + Center", "Left + Right", "All three" })
            Assert.True(names.Any(n => n.StartsWith("Hole pairing: " + pair + ".", StringComparison.Ordinal)),
                pair + " MISSING. zone=" + (w.SelectedZoneForPreview ?? "null") + " names=" + string.Join(" | ", names));

        // The single holes are not on the picture while the pairings are, or
        // the same photo would carry two sets of callouts over each other.
        Assert.DoesNotContain(names, n => n.StartsWith("Left mouthpiece hole", StringComparison.Ordinal));
        w.Close();
    }

    // A pairing that is mapped says so, the way every other callout does.
    [AvaloniaFact]
    public void A_pairing_says_how_many_mappings_it_has()
    {
        var w = Open("combo");
        Assert.Contains(Names(w), n => n.StartsWith("Hole pairing: Left + Center. 1 mapping", StringComparison.Ordinal));
        Assert.Contains(Names(w), n => n.StartsWith("Hole pairing: All three. Not mapped", StringComparison.Ordinal));
        w.Close();
    }

    // Every pairing the app knows how to name gets a marker, so none of the
    // five can be reachable only through the table.
    [AvaloniaFact]
    public void Every_pairing_the_app_names_is_on_the_picture()
    {
        var w = Open("combo");
        var pairs = Vocab.Inputs
            .Where(t => MainWindow.ZoneOf(t) == "combo")
            .Select(MainWindow.ComboPair)
            .Distinct()
            .ToList();
        Assert.NotEmpty(pairs);
        foreach (var pair in pairs)
            Assert.Contains(Names(w), n => n.StartsWith("Hole pairing: " + pair + ".", StringComparison.Ordinal));
        w.Close();
    }

    // The sockets are on the back of the case, so picking them shows the back
    // of the case, and each socket is a control rather than a caption.
    [AvaloniaFact]
    public void Picking_the_switch_jacks_shows_the_back_of_the_case()
    {
        var w = Open("jacks");
        var stage = w.GetVisualDescendants().OfType<ToggleButton>()
            .Select(t => AutomationProperties.GetName(t) ?? "").ToArray();
        Assert.Contains(stage, n => n.StartsWith("Top jack", StringComparison.Ordinal));
        Assert.Contains(stage, n => n.StartsWith("USB-A port", StringComparison.Ordinal));

        // And the front of the device is not still sitting above it.
        Assert.DoesNotContain(Names(w), n => n.StartsWith("Side tube", StringComparison.Ordinal));
        w.Close();
    }

    // The panel used to draw the back of the case in the side column. With the
    // stage drawing it, drawing it again would be the same photo twice. The
    // sentence under it that explains the socket is a different thing and stays.
    [AvaloniaFact]
    public void The_back_of_the_case_is_drawn_once()
    {
        var w = Open("jacks");
        Assert.Equal(1, Names(w).Count(n => n.StartsWith("Top jack. One switch: in 8", StringComparison.Ordinal)));
        Assert.Contains(Names(w), n => n.StartsWith("Top jack. Plug one switch", StringComparison.Ordinal));
        w.Close();
    }

    // The sentence that says which number a plug lands on belongs under the
    // picture of the socket it names. In the side panel it sat below the
    // mappings, where it had to be scrolled to and read as a note about the
    // mapping above it.
    [AvaloniaFact]
    public void The_socket_guide_sits_under_the_picture()
    {
        var w = Open("jacks");
        var stage = w.GetVisualDescendants().OfType<Control>()
            .First(c => c.Name == "DeviceCanvas");
        Assert.Contains(stage.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == "Back of the QuadStick");
        // And not in the panel beside it, or the same guide is on screen twice.
        var panel = w.GetVisualDescendants().OfType<Control>()
            .First(c => c.Name == "ZoneDetailPanel");
        Assert.DoesNotContain(panel.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == "Back of the QuadStick");
        w.Close();
    }

    // "1 mapping" named neither what to do with the two holes nor what it
    // presses. A pairing reads like a hole does now: four gestures, and the
    // button each one sends.
    [AvaloniaFact]
    public void A_pairing_says_which_button_each_gesture_presses()
    {
        var w = Open("combo");
        Assert.Contains(Names(w),
            n => n == "Hole pairing: Left + Center. 1 mapping Soft Puff: \u2014, Puff: \u2014, Sip: X, Soft Sip: \u2014");
        w.Close();
    }

    // Picking a pairing rings the holes it needs. The pairings all sit on top
    // of one another on the photo, so the name is not enough: which two holes
    // "Left + Center" means has to be readable off the device.
    [AvaloniaFact]
    public void Picking_a_pairing_rings_the_holes_it_uses()
    {
        // Two holes, two rings, and the pairing with a mapping is the one the
        // view lands on, so the rings are there before anything is clicked.
        var w = Open("combo");
        Assert.Equal(2, Rings(w));
        w.PickInputForPreview("mp_left_center_sip");
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Assert.Equal(2, Rings(w));

        // All three is three holes, not the two a midpoint was averaged from.
        w.PickInputForPreview("mp_triple_sip");
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Assert.Equal(3, Rings(w));
        w.Close();
    }

    // Left hole: sip, then soft puff, then puff, in that order in the file.
    const string OutOfOrder =
        "Profile Name,,Solo\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "x,normal,mp_left_sip\n" +
        "circle,normal,mp_left_puff_soft\n" +
        "square,normal,mp_left_puff\n";

    static MainWindow OpenCards(string csv, string zone)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        var s = Settings.Load();
        s.Model = "FPS";   // the diagram's zones depend on it, and neighbours change it
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.DeviceCards = false; // the full card, which is what carries a trash
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(ProfileFile.Load(csv));
        w.SetDeviceViewForPreview(true);
        w.SelectZoneForPreview(zone);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    // The callout reads soft puff, puff, sip, soft sip down the picture, and
    // the panel beside it listed the same three mappings in whatever order
    // their rows happen to sit in the file. Two orders for one part is two
    // things to hold in your head while looking at both at once.
    [AvaloniaFact]
    public void The_panel_lists_a_part_in_the_order_the_picture_reads()
    {
        var w = OpenCards(OutOfOrder, "mp_left");
        var order = w.GetVisualDescendants().OfType<Button>()
            .Select(b => AutomationProperties.GetName(b) ?? "")
            .Where(n => n.StartsWith("Remove the ", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(
            new[] { "Remove the soft puff mapping", "Remove the puff mapping", "Remove the sip mapping" },
            order);
        w.Close();
    }

    // Nothing in the file moves: the order above is how they are shown.
    [AvaloniaFact]
    public void Showing_a_part_in_order_does_not_reorder_the_file()
    {
        var w = OpenCards(OutOfOrder, "mp_left");
        Assert.Equal(
            new[] { "mp_left_sip", "mp_left_puff_soft", "mp_left_puff" },
            w.CurrentSheetForPreview!.Bindings.Select(b => b.Inputs[0]).ToArray());
        w.Close();
    }

    // The plus was under every mapping and under the list of what is free, so
    // on a part with a few rows the one control that adds a mapping was off
    // the bottom of the panel.
    [AvaloniaFact]
    public void The_plus_is_above_the_mappings()
    {
        var w = OpenCards(OutOfOrder, "mp_left");
        var panel = (StackPanel)w.GetVisualDescendants().OfType<Control>()
            .First(c => c.Name == "ZoneDetailPanel");
        int add = panel.Children.ToList().FindIndex(c =>
            (AutomationProperties.GetName(c) ?? "").StartsWith("Add a new mapping", StringComparison.Ordinal));
        int firstCard = panel.Children.ToList().FindIndex(c =>
            c.GetVisualDescendants().OfType<Button>().Any(b =>
                (AutomationProperties.GetName(b) ?? "").StartsWith("Remove the ", StringComparison.Ordinal)));
        Assert.True(add >= 0 && firstCard > add,
            $"the plus is at {add} and the first mapping at {firstCard}");
        w.Close();
    }

    // The rings are 30px circles over the holes; the plain hotspot markers are
    // 14px and the mode lights are filled.
    static int Rings(MainWindow w) => w.GetVisualDescendants()
        .OfType<Avalonia.Controls.Shapes.Ellipse>().Count(e => e.Width == 30);

    // The way back. Without it the front of the device would be unreachable
    // once the picture had followed a pick somewhere else.
    [AvaloniaFact]
    public void Main_controls_brings_the_front_of_the_device_back()
    {
        var w = Open("combo");
        Assert.DoesNotContain(Names(w), n => n.StartsWith("Left mouthpiece hole", StringComparison.Ordinal));

        Click(w, "Main controls");
        Assert.Contains(Names(w), n => n.StartsWith("Left mouthpiece hole", StringComparison.Ordinal));
        Assert.Null(w.SelectedZoneForPreview);
        w.Close();
    }

    // Main controls is offered whichever part is picked, and counts the front
    // of the device the way every other row counts itself.
    [AvaloniaFact]
    public void Main_controls_is_always_offered_and_counts_the_front()
    {
        foreach (var zone in new string?[] { null, "combo", "jacks", "other" })
        {
            var w = Open(zone);
            Assert.Contains(Names(w), n => n.StartsWith("Main controls.", StringComparison.Ordinal));
            w.Close();
        }
    }

    // Drew reported the per-mode Bluetooth dropdown as missing. It had shipped
    // a week earlier, in a column with no heading, in a dialog behind a pencil
    // icon, and nothing on the profile page mentioned a mode's connection at
    // all. A Bluetooth mode now says so where the modes are listed.
    [AvaloniaFact]
    public void A_mode_that_is_not_on_the_cable_says_so_in_the_modes_list()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        var s = Settings.Load();
        s.Model = "FPS";   // the diagram's zones depend on it, and neighbours change it
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.SetDeviceViewForPreview(true);
        w.LoadProfile(ProfileFile.Load(
            "Profile Name,,Cable\ngame.csv\nOutputs,Function,usb\nx,normal,lip\n"
            + "Profile Name,,Wireless\n\nOutputs,Function,bluetooth\ncircle,normal,lip\n"));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        var names = Names(w);
        Assert.Contains(names, n => n.StartsWith("2: Wireless. Bluetooth", StringComparison.Ordinal));
        // The ordinary one stays one line: a note on every row saying USB would
        // be noise on the screen with the least room to spare.
        Assert.Contains(names, n => n == "1: Cable");
        w.Close();
    }
}
