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

    static void Plus(MainWindow w)
    {
        w.GetVisualDescendants().OfType<Button>().First(b =>
                (AutomationProperties.GetName(b) ?? "").StartsWith("Add a new mapping", StringComparison.Ordinal))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
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

    // The plus always seeded "up", and rows sharing an input sit in one box, so
    // with up mapped it added a line to that box instead of a new one.
    [AvaloniaFact]
    public void The_plus_starts_on_a_free_direction_when_up_is_taken()
    {
        var file = ProfileFile.Load(Header + "left_joy_up,normal,up\n");
        var w = Open(file);
        Plus(w);

        var inputs = file.Document.Sheets[0].Bindings.Select(b => b.Inputs.Count > 0 ? b.Inputs[0] : "").ToArray();
        Assert.Equal(new[] { "up", "down" }, inputs);
        Done(w, file);
    }

    // A force_off row is free to the unused list but still has a box on screen.
    [AvaloniaFact]
    public void The_plus_skips_an_input_held_only_by_force_off()
    {
        var file = ProfileFile.Load(Header + "left_joy_up,force_off,up\n");
        var w = Open(file);
        Plus(w);

        Assert.Equal("down", file.Document.Sheets[0].Bindings[^1].Inputs[0]);
        Done(w, file);
    }

    // Drew's own flow: press + once per direction on an empty stick.
    [AvaloniaFact]
    public void Four_presses_give_the_four_directions_in_four_boxes()
    {
        var file = ProfileFile.Load(Header);
        var w = Open(file);
        for (int i = 0; i < 4; i++) Plus(w);

        Assert.Equal(new[] { "up", "down", "left", "right" },
            file.Document.Sheets[0].Bindings.Select(b => b.Inputs[0]));
        Done(w, file);
    }

    // Nothing free on the part: a second line on a used input is the only add
    // left, and that is how the device does a combo, so the plus still adds one.
    [AvaloniaFact]
    public void A_full_joystick_still_adds_a_row_on_up()
    {
        string[] all =
        {
            "up", "down", "left", "right", "center", "any_direction",
            "N", "NE", "E", "SE", "S", "SW", "W", "NW",
            "N_inner", "NE_inner", "E_inner", "SE_inner", "S_inner", "SW_inner", "W_inner", "NW_inner",
        };
        var file = ProfileFile.Load(Header + string.Concat(all.Select(i => $"x,normal,{i}\n")));
        var w = Open(file);
        Plus(w);

        var rows = file.Document.Sheets[0].Bindings;
        Assert.Equal(all.Length + 1, rows.Count);
        Assert.Equal("up", rows[^1].Inputs[0]);
        Done(w, file);
    }

    // A picked hole pairing that is free is still what the plus adds.
    [AvaloniaFact]
    public void A_picked_free_pairing_is_what_the_plus_adds()
    {
        var file = ProfileFile.Load(Header);
        var w = Open(file);
        w.PickInputForPreview("mp_right_center_sip_soft");
        Plus(w);

        Assert.Equal("mp_right_center_sip_soft", file.Document.Sheets[0].Bindings.Single().Inputs[0]);
        Done(w, file);
    }

    // A picked pairing whose sip is taken moves to another gesture of the same
    // pairing, not to a different pair of holes.
    [AvaloniaFact]
    public void A_picked_taken_pairing_stays_on_its_own_holes()
    {
        var file = ProfileFile.Load(Header + "x,normal,mp_right_center_sip\n");
        var w = Open(file);
        w.PickInputForPreview("mp_right_center_sip");
        Plus(w);

        var rows = file.Document.Sheets[0].Bindings;
        Assert.Equal(2, rows.Count);
        Assert.Equal("mp_right_center_sip", rows[0].Inputs[0]);
        var added = rows[1].Inputs[0];
        Assert.StartsWith("mp_right_center_", added);
        Assert.NotEqual("mp_right_center_sip", added);
        Done(w, file);
    }

    // Drew said up first is fine, so an empty joystick keeps it.
    [AvaloniaFact]
    public void The_plus_still_seeds_up_on_an_empty_joystick()
    {
        var file = ProfileFile.Load(Header);
        var w = Open(file);
        Plus(w);

        Assert.Equal("up", file.Document.Sheets[0].Bindings.Single().Inputs[0]);
        Done(w, file);
    }
}
