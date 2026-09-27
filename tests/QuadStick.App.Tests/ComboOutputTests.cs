using System;
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

// A button combo is two rows in the sheet: one input, two outputs, one row
// each. The device has no other way to press two buttons off one sip. The
// editor drew those as two separate cards, so the pair only existed in the
// user's head. Drew Redepenning asked for one box with a plus, 2026-09-19.
public class ComboOutputTests
{
    // Two outputs on the centre hole's sip and one on another hole, so the
    // narrowed list holds a combo in one case and a plain mapping in the other.
    const string Csv =
        "Profile Name,,Solo\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "x,normal,mp_center_sip\n" +
        "circle,normal,mp_center_sip\n" +
        "square,normal,mp_right_puff\n";

    static MainWindow Open()
    {
        var s = Settings.Load();
        s.Model = "FPS";
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.DeviceCards = false; // every mapping expanded, which is the editor under test
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(ProfileFile.Load(Csv));
        w.PickInputForPreview("mp_center_sip");
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    // A dirty file opens the save dialog on Close and the headless run hangs.
    static void Close(MainWindow w)
    {
        if (w.OpenFile is { } f) f.Dirty = false;
        w.Close();
    }

    static Control Panel(MainWindow w) => w.GetVisualDescendants().OfType<Control>()
        .First(c => c.Name == "ZoneDetailPanel");

    static int LabelCount(MainWindow w, string text) => Panel(w).GetVisualDescendants()
        .OfType<TextBlock>().Count(t => t.Text == text);

    static Button? ByName(MainWindow w, string name) => Panel(w).GetVisualDescendants()
        .OfType<Button>().FirstOrDefault(b => AutomationProperties.GetName(b) == name);

    // One card, two outputs. The input row is what proves they are together:
    // two cards would ask for the input twice.
    [AvaloniaFact]
    public void Two_outputs_on_one_input_are_one_card()
    {
        var w = Open();
        Assert.Equal(2, LabelCount(w, Strings.Main_PressVerb));
        Assert.Equal(1, LabelCount(w, Strings.Main_WhenYou2));
        Assert.Equal(1, LabelCount(w, Strings.Main_TheseArePressedTogether));
        Close(w);
    }

    // The combo's own rows share its input by design, so the card must not
    // flag that input as used twice. Only a use outside the card counts.
    [AvaloniaFact]
    public void A_combo_input_is_not_marked_as_its_own_duplicate()
    {
        var w = Open();
        string twice = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Main_DuplicateMark, 2);
        Assert.Equal(0, LabelCount(w, twice));
        Close(w);
    }

    // A single mapping is unchanged: no combo line, no per-output trash.
    [AvaloniaFact]
    public void A_single_output_still_reads_as_one_mapping()
    {
        var w = Open();
        w.PickInputForPreview("mp_right_puff");
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Assert.Equal(1, LabelCount(w, Strings.Main_PressVerb));
        Assert.Equal(0, LabelCount(w, Strings.Main_TheseArePressedTogether));
        Close(w);
    }

    // The plus copies the inputs. A new row that started empty would not be a
    // combo, it would be a mapping nothing fires.
    [AvaloniaFact]
    public void The_plus_adds_an_output_on_the_same_input()
    {
        var w = Open();
        var add = ByName(w, string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Strings.Main_AddAnotherOutputToMapping, 1));
        Assert.NotNull(add);

        var before = w.OpenFile!.Document.Sheets[0].Bindings
            .Count(b => b.Inputs.Contains("mp_center_sip"));
        add!.Command?.Execute(null);
        add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        var after = w.OpenFile!.Document.Sheets[0].Bindings
            .Where(b => b.Inputs.Contains("mp_center_sip")).ToList();
        Assert.Equal(before + 1, after.Count);
        // The new row is the one with nothing to press yet.
        Assert.Contains(after, b => b.Output.Trim().Length == 0);
        Assert.Equal(3, LabelCount(w, Strings.Main_PressVerb));
        Close(w);
    }

    // One gesture, one Undo. The edit and the copy onto the other rows used to
    // be two snapshots, so Undo put the siblings back and left the edited row
    // changed: a combo split in half by the control meant to put it back.
    [AvaloniaFact]
    public void One_undo_puts_the_whole_combo_back()
    {
        var file = ProfileFile.Load(Csv);
        var rows = file.Document.Sheets[0].Bindings
            .Where(b => b.Inputs.Contains("mp_center_sip")).Select(b => b.Row).ToList();
        Assert.Equal(2, rows.Count);

        // What the editor does when the input on a combo card is retyped.
        file.SetCell(rows[0], 2, "mp_center_puff");
        file.CopyInputs(rows[0], rows);
        Assert.All(rows, r => Assert.Equal("mp_center_puff", file.GetCell(r, 2)));

        Assert.True(file.Undo());
        Assert.All(rows, r => Assert.Equal("mp_center_sip", file.GetCell(r, 2)));
    }
}
