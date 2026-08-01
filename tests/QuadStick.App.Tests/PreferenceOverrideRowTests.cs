using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia;
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

    // Device View is the view the app opens in. The first version of this fix
    // only reached List View, so the bug it was written for was still fully
    // reachable by default.
    static MainWindow OpenCards(string csv, out ProfileFile opened)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.Model = "FPS";
        s.DeviceCards = true;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        opened = ProfileFile.Load(csv);
        opened.Dirty = false;
        w.LoadProfile(opened);
        w.SelectZoneForPreview("other");
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    static string[] Names(MainWindow w) => w.GetVisualDescendants().OfType<Control>()
        .Select(c => AutomationProperties.GetName(c) ?? "").Where(n => n.Length > 0).Distinct().ToArray();

    [AvaloniaFact]
    public void The_card_view_calls_a_settings_row_a_setting_and_not_a_mapping()
    {
        var w = OpenCards(Header + "mouse_speed,,50\n", out var f);
        var said = Names(w);

        Assert.Contains(said, n => n.StartsWith("Setting 1:") && n.Contains("set mouse_speed to 50"));
        Assert.DoesNotContain(said, n => n.StartsWith("Mapping 1:"));

        f.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void The_card_view_editor_offers_a_value_and_never_an_input()
    {
        var w = OpenCards(Header + "mouse_speed,,50\n", out var f);
        var card = w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Setting 1:"));
        card.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        var said = Names(w);

        Assert.Contains($"Setting value for row 4", said);
        Assert.DoesNotContain(said, n => n.StartsWith("Input 1 for"));
        Assert.DoesNotContain(said, n => n.StartsWith("Add another input"));

        f.Dirty = false;
        w.Close();
    }

    // Firmware reads a mode row's value with a bare atoi (Configuration.c:495),
    // so a dropdown of words there would write something the device sees as 0.
    // The settings sheet is the only place those words are real.
    [AvaloniaFact]
    public void A_word_valued_setting_gets_no_dropdown_on_a_mode_row()
    {
        var w = Open(Header + "bluetooth_device_mode,,keyboard\n", out var f);

        Assert.Empty(w.GetVisualDescendants().OfType<ComboBox>()
            .Where(c => (AutomationProperties.GetName(c) ?? "").StartsWith("Setting value for row 4")));
        // And the app says what the device will actually do with it.
        Assert.Contains(f.Issues, i => i.Cell == "C4" && i.Severity == Severity.Error
            && i.Message.Contains("sets it to 0"));

        f.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void Turning_a_binding_into_a_setting_swaps_the_editor_straight_away()
    {
        var w = Open(Header + "mouse_left,normal,lip\n", out var f);
        Assert.Contains(Spoken(w, 4), n => n.StartsWith("Input 1 for row 4"));

        // Whatever route wrote column A, the row must not keep the old editor:
        // a number spinner left over an input cell writes a bare number into it.
        f.SetCell(4, 0, "mouse_speed");
        w.LoadProfile(f);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var said = Spoken(w, 4);
        Assert.Contains($"Setting value for row 4", said);
        Assert.DoesNotContain(said, n => n.StartsWith("Input 1 for row 4"));

        f.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void A_settings_row_keeps_the_way_out_to_a_value_the_slider_will_not_reach()
    {
        // 177 of the 1186 mode override rows in the public catalog hold a value
        // outside the manager's own range. The settings sheet has always had
        // this escape; a mode row was left locked to the spinner.
        var w = Open(Header + "mouse_speed,,50\n", out var f);
        Assert.Contains(w.GetVisualDescendants().OfType<Control>()
            .Select(c => AutomationProperties.GetName(c) ?? ""),
            n => n.StartsWith("Type an exact value"));
        f.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void A_settings_row_lines_up_with_the_rows_around_it()
    {
        var w = Open(Header + "mouse_left,normal,lip\nmouse_speed,,50\nmouse_right,normal,mp_center_sip\n", out var f);
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        double NoteX(int row)
        {
            var c = w.GetVisualDescendants().OfType<Visual>()
                .First(v => (AutomationProperties.GetName(v as Control ?? new Border()) ?? "")
                    .StartsWith($"Note for row {row}."));
            return c.TranslatePoint(new Avalonia.Point(0, 0), w)!.Value.X;
        }

        // Row 5 is the settings row, between two ordinary bindings.
        Assert.Equal(NoteX(4), NoteX(5), 0);
        Assert.Equal(NoteX(4), NoteX(6), 0);

        f.Dirty = false;
        w.Close();
    }
}
