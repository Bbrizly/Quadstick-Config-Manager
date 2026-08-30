using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// The Google Sheets extension paints a cell whose value appears elsewhere in
// the same mode. A tester who lost that mark said it cost them "a lot of
// mental memorization" and one combination they finished the profile without
// ever noticing was free.
public class DuplicateMarkTests
{
    static MainWindow Open(string csv, out ProfileFile file)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.InterfaceScalePercent = 100;
        s.Model = "FPS";
        s.DeviceCards = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        file = ProfileFile.Load(csv);
        file.Dirty = false; // else Close waits forever on the save dialog
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    const string TwoModes =
        "Profile Name,,Walking\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "x,normal,lip\n" +
        "circle,normal,right_sip\n" +
        "\n" +
        "Profile Name,,Shooting\n" +
        "\n" +
        "Outputs,Function,usb\n" +
        "square,normal,left_puff\n";

    static string[] DuplicateMarks(MainWindow w) => w.GetVisualDescendants().OfType<Border>()
        .Select(b => AutomationProperties.GetName(b) ?? "")
        .Where(n => n.StartsWith("Used "))
        .ToArray();

    // The spreadsheet painted a cell whose value appeared elsewhere in the
    // mode. Here it is a count in the cell, so it survives being read aloud
    // and does not depend on telling two colours apart.
    [AvaloniaFact]
    public void A_value_used_twice_in_a_mode_is_marked_on_both_rows()
    {
        var w = Open(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "x,toggle,right_sip\n" +
            "circle,normal,left_puff\n", out var file);

        // Two rows fire x, so both say so; nothing else repeats.
        Assert.Equal(new[] { "Used 2 times in this mode", "Used 2 times in this mode" },
            DuplicateMarks(w));

        file.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void A_profile_with_no_repeats_is_marked_nowhere()
    {
        var w = Open(TwoModes, out var file);
        Assert.Empty(DuplicateMarks(w));
        file.Dirty = false;
        w.Close();
    }

    // Two rows with nothing in a cell are not two rows sharing a value. Every
    // row draws an input cell whether it has an input or not, so blank is the
    // most repeated thing on any screen and marking it would mark everything.
    [AvaloniaFact]
    public void Empty_cells_are_not_a_repeated_value()
    {
        var w = Open(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            ",normal,\n" +
            ",normal,\n", out var file);

        Assert.Empty(DuplicateMarks(w));

        file.Dirty = false;
        w.Close();
    }

    // An input repeats independently of an output: the same sip firing two
    // different buttons is the conflict the tester most wanted to see.
    [AvaloniaFact]
    public void A_repeated_input_is_marked_even_when_the_outputs_differ()
    {
        var w = Open(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "circle,normal,lip\n", out var file);

        Assert.Equal(2, DuplicateMarks(w).Length);
        Assert.All(DuplicateMarks(w), m => Assert.Equal("Used 2 times in this mode", m));

        file.Dirty = false;
        w.Close();
    }
}
