using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// Drew asked for the bounds on a function's numbers to be visible while
// somebody is choosing them, not findable afterwards in the manual. So the
// sentences have to be real text in the window, reachable by a screen reader,
// and not a tooltip nobody on a keyboard can open.
public class FunctionHintTests
{
    static MainWindow Editor(string csv)
    {
        var s = Settings.Load();
        s.Model = "FPS";   // the diagram's zones depend on it, and neighbours change it
        s.TutorialSeen = true;
        s.RememberWindow = false;
        // Cards off: a closed card is one sentence, and the boxes with the
        // hints under them only exist in the open mapping editor.
        s.DeviceCards = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(ProfileFile.Load(csv));
        w.SelectZoneForPreview("lip");
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    static string AllText(MainWindow w) =>
        string.Join(" ", w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? ""));

    const string Csv =
        "Profile Name,,Solo\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "x,greater_than 40,lip\n";

    // The range and the default are the two facts he named. Both have to be on
    // screen beside the box, in words.
    [AvaloniaFact]
    public void The_editor_shows_the_range_and_the_default_beside_the_numbers()
    {
        var w = Editor(Csv);
        var text = AllText(w);
        Assert.Contains("Threshold: 1 to 100 percent", text, StringComparison.Ordinal);
        Assert.Contains("Blank means 100 percent", text, StringComparison.Ordinal);
        w.Close();
    }

    // A function that takes no numbers must not grow an empty hint under it.
    [AvaloniaFact]
    public void A_function_with_no_numbers_shows_no_hint()
    {
        Assert.Equal("", MainWindow.ParameterHint("normal"));
        Assert.Equal("", MainWindow.ParameterWatermark("toggle"));
        Assert.Contains("takes no numbers", MainWindow.ParameterAccessibleName("normal"), StringComparison.Ordinal);
    }

    // The hint is keyed on the first word, so a cell that already carries
    // values still explains them.
    [AvaloniaFact]
    public void A_function_that_already_has_values_still_explains_them()
    {
        var hint = MainWindow.ParameterHint("repeat 5 2000");
        Assert.Contains("taps a second", hint, StringComparison.Ordinal);
        Assert.Contains("First hold", hint, StringComparison.Ordinal);
        Assert.Contains("Blank means 10 a second", hint, StringComparison.Ordinal);
    }

    // Drew, 2026-09-05: the descriptions read long. What survived under the box
    // is the range and the default, which is what somebody needs before typing.
    // What the number does is a click away on the dot beside the box, and this
    // is the pair: the short half on screen, the long half not on screen but
    // reachable.
    [AvaloniaFact]
    public void The_behaviour_sentence_moves_off_the_screen_and_onto_the_dot()
    {
        var w = Editor(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,tap 500 1,lip\n");
        var text = AllText(w);

        Assert.Contains("Press: 1 to 16383 milliseconds", text, StringComparison.Ordinal);
        Assert.Contains("Blank means 100 ms", text, StringComparison.Ordinal);
        // The half that made the hint three lines tall.
        Assert.DoesNotContain("the next turns it off", text, StringComparison.Ordinal);

        var dot = w.GetVisualDescendants().OfType<Button>()
            .Single(b => (b.Content as string) == "?"
                      && AutomationProperties.GetName(b) == "Tap"
                      && b.IsEffectivelyVisible);
        Assert.Contains("the next turns it off", MainWindow.ParameterHint("tap"), StringComparison.Ordinal);

        w.Close();
    }

    // A screen reader never had the dot to click, so the box itself still says
    // everything. Shortening what is drawn must not shorten what is read out.
    [AvaloniaFact]
    public void The_box_still_reads_out_the_whole_sentence()
    {
        var name = MainWindow.ParameterAccessibleName("tap");
        Assert.Contains("the next turns it off", name, StringComparison.Ordinal);
        Assert.Contains("Blank means 100 ms", name, StringComparison.Ordinal);
    }

    // A function with no numbers still gets the dot, because the description
    // left the closed picker with it. What it must not grow is an empty hint.
    [AvaloniaFact]
    public void A_function_with_no_numbers_still_explains_itself()
    {
        var w = Editor(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip\n");
        Assert.Contains(w.GetVisualDescendants().OfType<Button>(),
            b => (b.Content as string) == "?"
              && AutomationProperties.GetName(b) == "Normal"
              && b.IsEffectivelyVisible);
        Assert.Equal(MainWindow.FunctionExplain("normal"), MainWindow.FunctionHelpBody("normal"));
        w.Close();
    }

    // The picker used to print the whole description inside the closed box, so
    // a 145px column carried ten lines of prose and pushed the numbers it
    // belongs to off the bottom. The list still explains every choice; the
    // resting state is the name, and the dot holds the words.
    [AvaloniaFact]
    public void The_closed_picker_shows_the_name_and_not_the_description()
    {
        var w = Editor(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,tap 500 1,lip\n");
        Assert.DoesNotContain("quickly press and release", AllText(w), StringComparison.Ordinal);
        Assert.Contains("quickly press and release", MainWindow.FunctionHelpBody("tap"), StringComparison.Ordinal);
        // Both halves, in the order somebody reads them.
        Assert.Contains("Counts as a tap", MainWindow.FunctionHelpBody("tap"), StringComparison.Ordinal);
        w.Close();
    }

    // Every function the dropdown offers has to answer for its numbers, or the
    // box appears with nothing under it.
    [AvaloniaFact]
    public void Every_function_with_numbers_has_a_hint_and_a_watermark()
    {
        foreach (var (name, arity) in Vocab.FunctionArity)
        {
            if (arity.Max == 0) continue;
            Assert.NotEqual("", MainWindow.ParameterHint(name));
            Assert.StartsWith("optional:", MainWindow.ParameterWatermark(name), StringComparison.Ordinal);
        }
    }
}
