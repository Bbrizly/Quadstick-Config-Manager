using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// Drew asked to be able to say "this mode talks over Bluetooth" inside the
// profile. The app read that cell, validated it and warned about it, and had
// no way at all to set it: the only route in was importing somebody else's
// file. This is the missing half.
public class ModeChannelTests
{
    const string TwoModes =
        "Profile Name,,Walking\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "x,normal,lip\n" +
        "Profile Name,,Driving\n" +
        ",,\n" +
        "Outputs,Function,usb\n" +
        "circle,normal,lip\n";

    static ProfileFile Load() => ProfileFile.Load(TwoModes);

    // Column C of the header row, two under the keyword: the same cell the
    // parser reads back as the channel.
    [Fact]
    public void Setting_a_channel_writes_the_cell_the_parser_reads()
    {
        var f = Load();
        Assert.True(f.SetModeChannel(1, "bluetooth"));
        Assert.Equal("bluetooth", f.Document.Sheets[1].Channel);
        Assert.Equal("usb", f.Document.Sheets[0].Channel);
    }

    // Blank is a value, not a hole. Configuration.c:528 falls back to USB for
    // it, so clearing the cell has to be allowed and has to stick.
    [Fact]
    public void Clearing_the_channel_is_allowed_and_stays_clear()
    {
        var f = Load();
        Assert.True(f.SetModeChannel(0, ""));
        Assert.Equal("", f.Document.Sheets[0].Channel);
    }

    // Setting it to what it already is is not an edit, or every rebuild of the
    // window would push an undo step nobody asked for.
    [Fact]
    public void Setting_the_same_channel_again_changes_nothing()
    {
        var f = Load();
        Assert.False(f.SetModeChannel(0, "usb"));
    }

    // The window has to offer one connection control per mode and none for the
    // preferences sheet, which has no channel cell.
    [AvaloniaFact]
    public void The_modes_window_offers_a_connection_for_each_mode()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(Load());
        w.UpdateLayout();

        var modes = new ModesWindow(w);
        modes.Show();
        modes.UpdateLayout();

        var combos = modes.GetVisualDescendants().OfType<ComboBox>()
            .Where(c => (AutomationProperties.GetName(c) ?? "").StartsWith("Connection for mode", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, combos.Count);

        // The warning about a Bluetooth-only mode losing mouse and keyboard
        // over a cable is the reason this control needs to say more than a
        // token, so the spoken name carries it.
        Assert.Contains("mouse or keyboard", AutomationProperties.GetName(combos[0]) ?? "", StringComparison.Ordinal);

        modes.Close();
        w.Close();
    }

    // The connection dropdown made the row wider than the window and pushed the
    // copy and delete buttons off the right edge, with no scrollbar to reach
    // them. A control you cannot see is a control you cannot use.
    [AvaloniaFact]
    public void Every_control_on_a_mode_row_is_inside_the_window()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(Load());
        w.UpdateLayout();

        var modes = new ModesWindow(w);
        modes.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        modes.UpdateLayout();

        foreach (var control in modes.GetVisualDescendants().OfType<Control>()
                     .Where(c => c is Button or ComboBox or TextBox)
                     .Where(c => c.Bounds.Width > 0))
        {
            var right = control.TranslatePoint(new Point(control.Bounds.Width, 0), modes);
            Assert.True(right is null || right.Value.X <= modes.Width,
                $"{AutomationProperties.GetName(control) ?? control.GetType().Name} "
                + $"reaches {right?.X} on a {modes.Width} wide window");
        }

        modes.Close();
        w.Close();
    }
}
