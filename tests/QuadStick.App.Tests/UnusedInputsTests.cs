using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// The list the tester asked for: which inputs this mode has nothing mapped to.
// A force_off row is housekeeping (it turns off an output a toggle left on),
// so it must not make its input read as taken.
public class UnusedInputsTests
{
    // Close asks to save a dirty file and then waits forever, so any test that
    // edits has to clear the flag first. Keeping the file in reach is the
    // whole reason Open hands it back.
    static void Done(MainWindow w, ProfileFile file) { file.Dirty = false; w.Close(); }

    static MainWindow Open(string csv, int modelIndex = 0) => Open(csv, out _, modelIndex);

    static MainWindow Open(string csv, out ProfileFile opened, int modelIndex = 0)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.Model = "FPS";
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.SetModelForPreview(modelIndex);
        opened = ProfileFile.Load(csv);
        opened.Dirty = false;
        w.LoadProfile(opened);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();
        return w;
    }

    static Button Toggle(MainWindow w) => w.GetVisualDescendants().OfType<Button>()
        .First(b => b.Name == "UnusedButton");

    // Each unused input is an actual mapping command. The raw token remains in
    // its accessible name even though the button shows the shorter part label.
    static string[] Listed(MainWindow w)
    {
        Toggle(w).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w.GetVisualDescendants().OfType<Button>()
            .Select(b => AutomationProperties.GetName(b) ?? "")
            .Where(n => n.StartsWith("Map ") && n.Contains(" to a new mapping on "))
            .Select(n => n[4..n.IndexOf(" to a new mapping on ", StringComparison.Ordinal)])
            .ToArray();
    }

    const string Header = "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\n";

    [AvaloniaFact]
    public void A_mapped_input_is_not_listed()
    {
        var w = Open(Header + "x,normal,lip\n");
        Assert.DoesNotContain("lip", Listed(w));
        Assert.Contains("mp_left_sip", Listed(w));
        w.Close();
    }

    // The count rides on the button, so it is readable without opening anything.
    [AvaloniaFact]
    public void The_button_carries_the_count()
    {
        var w = Open(Header + "x,normal,lip\n");
        int free = Listed(w).Length;
        Assert.True(free > 0);
        Assert.Equal($"Unused ({free})", Toggle(w).Content);
        w.Close();
    }

    // force_off alone does not count as using the input.
    [AvaloniaFact]
    public void An_input_used_only_by_force_off_is_still_listed()
    {
        var w = Open(Header + "kb_w,force_off,mp_triple_sip_soft\n");
        Assert.Contains("mp_triple_sip_soft", Listed(w));
        w.Close();
    }

    [AvaloniaFact]
    public void A_real_row_beats_a_force_off_row_on_the_same_input()
    {
        var w = Open(Header + "kb_w,toggle,mp_triple_sip_soft\nkb_w,force_off,mp_triple_sip_soft\n");
        Assert.DoesNotContain("mp_triple_sip_soft", Listed(w));
        w.Close();
    }

    // A Singleton has no left or right mouthpiece holes, so it must never be
    // told they are free to map.
    [AvaloniaFact]
    public void A_singleton_is_not_offered_holes_it_does_not_have()
    {
        var w = Open(Header + "x,normal,lip\n", modelIndex: 2);
        Assert.DoesNotContain(Listed(w), t => t.StartsWith("mp_left_"));
        w.Close();
    }

    // A function cell carries its parameters, so the name is the first word.
    // A prefix match would read "force_offf" as the real function and wrongly
    // leave its input on the free list.
    [AvaloniaFact]
    public void A_typo_for_force_off_does_not_free_the_input()
    {
        var w = Open(Header + "kb_w,force_offf,mp_triple_sip_soft\n");
        Assert.DoesNotContain("mp_triple_sip_soft", Listed(w));
        w.Close();
    }

    // Parameters after the name still read as force_off.
    [AvaloniaFact]
    public void Force_off_with_a_parameter_still_frees_the_input()
    {
        var w = Open(Header + "kb_w,force_off 500,mp_triple_sip_soft\n");
        Assert.Contains("mp_triple_sip_soft", Listed(w));
        w.Close();
    }

    // The point of the per-part list: you are looking at one part, you see what
    // it can still do, and one click starts the mapping on that exact input.
    [AvaloniaFact]
    public void A_free_input_on_a_part_can_be_mapped_in_one_click()
    {
        var w = Open(Header + "circle,normal,mp_center_puff\n", out var file);
        w.SetDeviceViewForPreview(true);
        w.SelectZoneForPreview("mp_center");
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();

        var free = w.GetVisualDescendants().OfType<Button>()
            .First(b => AutomationProperties.GetName(b) == "Map mp_center_sip to a new mapping on the Center mouthpiece hole");
        free.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();

        var sheet = w.CurrentSheetForPreview!;
        Assert.Contains(sheet.Bindings, b => b.Inputs.Contains("mp_center_sip"));
        // And it leaves the list, because it is not free any more.
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<Button>(),
            b => AutomationProperties.GetName(b) == "Map mp_center_sip to a new mapping on the Center mouthpiece hole");
        Done(w, file);
    }

    // Choosing an unused input begins the mapping in the view already open.
    // It must not jump to Device View: that made choosing Joystick North feel
    // like navigation instead of an edit.
    [AvaloniaFact]
    public void An_unused_input_starts_a_mapping_without_changing_view()
    {
        var w = Open(Header + "x,normal,lip\n", out var file);
        Toggle(w).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();

        var north = w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Map usb_1_up to a new mapping on "));
        north.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();

        Assert.True(w.GetVisualDescendants().OfType<Control>().First(c => c.Name == "GridContainer").IsVisible);
        Assert.Contains(w.CurrentSheetForPreview!.Bindings, b => b.Inputs.Contains("usb_1_up"));
        Done(w, file);
    }

    [AvaloniaFact]
    public void A_part_header_keeps_its_description_in_its_question_mark()
    {
        var w = Open(Header
            + "x,normal,mp_right_sip\n"
            + "y,normal,mp_right_puff\n"
            + "z,normal,mp_right_sip_soft\n");
        w.SetDeviceViewForPreview(true);
        w.SelectZoneForPreview("mp_right");
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();

        var detail = w.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "ZoneDetailPanel");
        var heading = Assert.IsType<Grid>(detail.Children[0]);
        var title = Assert.Single(heading.Children.OfType<TextBlock>());
        var count = heading.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "3");
        var help = heading.GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Content == "?");

        Assert.True(count.FontSize < title.FontSize);
        Assert.Equal("Right mouthpiece hole", AutomationProperties.GetName(help));
        Assert.DoesNotContain(detail.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == "Sip or puff on the right mouthpiece hole. A gentle sip or puff can do something different (the soft variants).");
        w.Close();
    }

    // The unused picker is useful while comparing either editor view, and its
    // toolbar position no longer changes when the view changes.
    [AvaloniaFact]
    public void The_unused_count_is_on_screen_in_every_mode_view()
    {
        var w = Open("Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\nx,normal,lip\n", out var f);
        Assert.True(Toggle(w).IsVisible);

        w.SetDeviceViewForPreview(true);
        w.UpdateLayout();
        Assert.True(Toggle(w).IsVisible);

        Done(w, f);
    }
}
