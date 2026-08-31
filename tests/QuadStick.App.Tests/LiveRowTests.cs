using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// A row lights while the QuadStick is sending that row's output.
//
// The device never says which input produced a report, so the row that lights
// is the row whose OUTPUT is on the wire. In the ordinary case, the device
// running the file on screen, that is the row the person just used. It is
// still true when it is not: a lit row means "this is being sent", never "you
// did this", and the wording on screen says so.
public class LiveRowTests
{
    // Two rows on one output on purpose: the device sends one square, and both
    // rows that produce a square are the honest answer.
    const string Profile =
        "Profile Name,,Solo\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "square,normal,lip\n" +
        "circle,normal,right_sip\n" +
        "square,normal,hard_puff\n" +
        "kb_a,normal,soft_sip\n";

    static MainWindow Open(bool deviceView = false)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow { FindDeviceRoots = Array.Empty<string> };
        w.Show();
        var file = ProfileFile.Load(Profile);
        file.Dirty = false; // else Close opens the save dialog and waits forever
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(deviceView);
        w.UpdateLayout();
        return w;
    }

    static LiveState Sending(params string[] outputs) =>
        new(0, 0, Array.Empty<int>(), "QuadStick",
            new HashSet<string>(outputs, StringComparer.Ordinal), true);

    // The dot is the cue that is not a colour, so counting it is counting what
    // a person who cannot tell the tints apart actually sees.
    static int Lit(MainWindow w) =>
        w.GetVisualDescendants().OfType<Border>()
         .Count(b => AutomationProperties.GetName(b) == Strings.Main_SendingNow && b.IsVisible);

    [AvaloniaFact]
    public void A_row_lights_while_its_output_is_being_sent()
    {
        var w = Open();
        Assert.Equal(0, Lit(w));

        w.ShowLiveInputForPreview(Sending("circle"));
        w.UpdateLayout();
        Assert.Equal(1, Lit(w));
    }

    [AvaloniaFact]
    public void Both_rows_that_make_the_same_output_light()
    {
        var w = Open();
        w.ShowLiveInputForPreview(Sending("square"));
        w.UpdateLayout();
        // The device sent one square. Two rows produce a square, and the app
        // cannot know which one the profile on the device used, so it says both.
        Assert.Equal(2, Lit(w));
    }

    [AvaloniaFact]
    public void Letting_go_puts_the_row_out()
    {
        var w = Open();
        w.ShowLiveInputForPreview(Sending("circle"));
        w.UpdateLayout();
        Assert.Equal(1, Lit(w));

        w.ShowLiveInputForPreview(Sending());
        w.UpdateLayout();
        Assert.Equal(0, Lit(w));
    }

    // The reader posts nothing when it loses the stick. A row left lit after
    // that would be the app claiming a device it no longer has is sending.
    [AvaloniaFact]
    public void Unplugging_the_stick_puts_every_row_out()
    {
        var w = Open();
        w.ShowLiveInputForPreview(Sending("square"));
        w.UpdateLayout();
        Assert.Equal(2, Lit(w));

        w.ShowLiveInputForPreview(null);
        w.UpdateLayout();
        Assert.Equal(0, Lit(w));
    }

    // An emulation mode whose report shape nobody has taught the app must light
    // nothing, rather than light the wrong rows off a table that does not apply.
    [AvaloniaFact]
    public void A_mode_the_app_cannot_read_lights_nothing()
    {
        var w = Open();
        w.ShowLiveInputForPreview(new LiveState(0, 0, Array.Empty<int>(), "Some other pad",
            new HashSet<string> { "square" }, false));
        w.UpdateLayout();
        Assert.Equal(0, Lit(w));
    }

    // Device View is the view the app opens in, so a light that only works in
    // List View is half a feature.
    [AvaloniaFact]
    public void Device_view_lights_its_mapping_cards_too()
    {
        var w = Open(deviceView: true);
        w.SelectZoneForPreview("lip");
        w.UpdateLayout();
        w.ShowLiveInputForPreview(Sending("square"));
        w.UpdateLayout();
        // Two: the mapping card in the side panel and the row for the same
        // gesture in the callout on the diagram. Both stand for the binding
        // being sent, and device view shows both at once.
        Assert.Equal(2, Lit(w));
    }

    // A row number belongs to a sheet, so row 4 of one profile is a different
    // binding from row 4 of the next. A lit row carried across would be the app
    // showing a binding that is not being sent, which is the one thing it must
    // never do.
    [AvaloniaFact]
    public void A_lit_row_does_not_carry_over_to_another_profile()
    {
        var w = Open();
        w.ShowLiveInputForPreview(Sending("square"));
        w.UpdateLayout();
        Assert.Equal(2, Lit(w));

        // Row 4 here is triangle, not the square that was lit in the last one.
        var other = ProfileFile.Load(
            "Profile Name,,Other\n" + "other.csv\n" + "Outputs,Function,usb\n" +
            "triangle,normal,lip\n" + "circle,normal,right_sip\n");
        other.Dirty = false;
        w.LoadProfile(other);
        w.UpdateLayout();
        Assert.Equal(0, Lit(w));
    }

    // Rows are torn down and rebuilt on every edit. A row rebuilt while its
    // output is still being sent has to come back lit.
    [AvaloniaFact]
    public void A_row_rebuilt_while_it_is_being_sent_comes_back_lit()
    {
        var w = Open();
        w.ShowLiveInputForPreview(Sending("circle"));
        w.UpdateLayout();
        Assert.Equal(1, Lit(w));

        // Switching views tears every row down and builds it again.
        w.SetDeviceViewForPreview(true);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();
        Assert.Equal(1, Lit(w));
    }
}
