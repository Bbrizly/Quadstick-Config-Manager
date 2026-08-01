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

// Several inputs on one row are a SEQUENCE in time, not a chord: the device
// matches them against the inputs last used, newest first, so they have to be
// done one after the other. The tooltip and the help were changed to say so
// and the card's spoken sentence was not, which left a screen reader user
// being taught the opposite of what everyone else read.
public class SequenceWordingTests
{
    const string Header = "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\n";

    static MainWindow OpenOnLip(string csv, out ProfileFile opened)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.DeviceCards = true;
        s.RememberWindow = false;
        s.Model = "FPS";
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        opened = ProfileFile.Load(csv);
        opened.Dirty = false;
        w.LoadProfile(opened);
        w.SelectZoneForPreview("lip");
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    static string Card(MainWindow w, int n) => w.GetVisualDescendants().OfType<Control>()
        .Select(c => AutomationProperties.GetName(c) ?? "")
        .First(x => x.StartsWith($"Mapping {n}: "));

    [AvaloniaFact]
    public void Two_inputs_are_spoken_as_one_after_the_other()
    {
        var w = OpenOnLip(Header + "mouse_left,normal,lip,mp_center_sip\n", out var f);
        var said = Card(w, 1);

        Assert.Contains("one after the other", said);
        Assert.DoesNotContain(" and ", said); // "and" would say a chord
        f.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void One_input_is_still_spoken_plainly()
    {
        var w = OpenOnLip(Header + "mouse_left,normal,lip\n", out var f);
        var said = Card(w, 1);

        Assert.DoesNotContain("one after the other", said);
        Assert.DoesNotContain(", then ", said);
        f.Dirty = false;
        w.Close();
    }
}
