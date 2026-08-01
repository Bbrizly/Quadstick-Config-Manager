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

// A mode row whose output is a setting name keeps its VALUE in column C. The
// device skips column B and reads C through atoi, so a word there is zero.
//
// The editor knew that well enough to print a scope badge on the row, and then
// handed the user an input picker on that same cell with the whole input
// catalog inside it. Picking "lip" from it set the setting to 0. These tests
// hold the row to editing a value as a value.
public class PreferenceOverrideRowTests
{
    const string Header = "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\n";

    static MainWindow Open(string csv, out ProfileFile opened)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.Model = "FPS";
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        opened = ProfileFile.Load(csv);
        opened.Dirty = false;
        w.LoadProfile(opened);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    static string[] Spoken(MainWindow w, int row) => w.GetVisualDescendants().OfType<Control>()
        .Select(c => AutomationProperties.GetName(c) ?? "")
        .Where(n => n.Contains($"row {row}"))
        .Distinct().ToArray();

    [AvaloniaFact]
    public void A_settings_row_offers_a_value_and_never_an_input()
    {
        var w = Open(Header + "mouse_speed,,50\n", out var f);
        var said = Spoken(w, 4);

        Assert.Contains($"Setting value for row 4", said);
        Assert.DoesNotContain(said, n => n.StartsWith("Input 1 for row 4"));
        // Nor a way to add a second input to a row that reads none.
        Assert.DoesNotContain(said, n => n.StartsWith("Add another input to row 4"));
        // It still says which scope it is in, and still deletes and reorders.
        Assert.Contains(w.GetVisualDescendants().OfType<Control>()
            .Select(c => AutomationProperties.GetName(c) ?? ""), n => n == $"Row 4 scope: {MainWindow.ModeScope}");
        Assert.Contains("Delete row 4", said);
        Assert.Contains("Move row 4 up", said);

        f.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void An_increment_value_row_is_still_a_binding_with_an_input()
    {
        var w = Open(Header + "mouse_speed,increment_value 5,right_sip\n", out var f);
        var said = Spoken(w, 4);

        Assert.Contains(said, n => n.StartsWith("Input 1 for row 4"));
        Assert.DoesNotContain($"Setting value for row 4", said);

        f.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void An_ordinary_binding_row_is_untouched()
    {
        var w = Open(Header + "mouse_left,normal,lip\n", out var f);
        var said = Spoken(w, 4);

        Assert.Contains(said, n => n.StartsWith("Input 1 for row 4"));
        Assert.Contains(said, n => n.StartsWith("Add another input to row 4"));
        Assert.DoesNotContain($"Setting value for row 4", said);

        f.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void Typing_a_value_writes_column_C_and_not_column_B()
    {
        var w = Open(Header + "mouse_speed,,50\n", out var f);
        var box = w.GetVisualDescendants().OfType<NumericUpDown>()
            .First(n => AutomationProperties.GetName(n) == "Setting value for row 4");
        box.Value = 90;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("90", f.GetCell(4, 2));
        Assert.Equal("", f.GetCell(4, 1)); // B stays empty: the device skips it
        f.Dirty = false;
        w.Close();
    }
}
