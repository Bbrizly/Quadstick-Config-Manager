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
