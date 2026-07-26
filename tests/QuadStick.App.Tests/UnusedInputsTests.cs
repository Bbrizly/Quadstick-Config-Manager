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

    // The chips show the short form; the raw token lives on the automation name,
    // which is what a screen reader and this test both read.
    static string[] Listed(MainWindow w)
    {
        Toggle(w).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w.GetVisualDescendants().OfType<Border>()
            .Select(b => AutomationProperties.GetName(b) ?? "")
            .Where(n => n.EndsWith(", not used in this mode"))
            .Select(n => n[..n.IndexOf(',')])
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

    // The global list is for reading; its headings are the way into the part
    // where you can act.
    [AvaloniaFact]
    public void A_zone_heading_opens_that_part_in_device_view()
    {
        var w = Open(Header + "x,normal,lip\n");
        Toggle(w).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();

        var head = w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Center mouthpiece hole,"));
        head.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();

        var detail = w.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "ZoneDetailPanel");
        var title = detail.Children.OfType<TextBlock>().First().Text ?? "";
        Assert.StartsWith("Center mouthpiece hole", title);
        w.Close();
    }
}
