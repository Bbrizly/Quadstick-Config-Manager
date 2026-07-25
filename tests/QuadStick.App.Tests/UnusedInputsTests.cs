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
    static MainWindow Open(string csv, int modelIndex = 0)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.Model = "FPS";
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.SetModelForPreview(modelIndex);
        var file = ProfileFile.Load(csv);
        file.Dirty = false; // else Close opens the save dialog and waits forever
        w.LoadProfile(file);
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

    // The tester's own case: they used mp_triple_sip_soft only to force kb_w
    // off, and still want it offered as free.
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
}
