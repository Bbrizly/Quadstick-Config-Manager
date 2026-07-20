using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// A tester reported "moving modes up/down one, the first mode cannot be
// organized". Modes used to be managed through a hidden menu that swapped a
// mode with the sheet next to it, so a Preferences sheet in between froze both
// modes either side of it. The Modes window replaces that menu: every mode is
// a row you can rename, move, copy and delete in place.
public class ModesWindowTests
{
    static MainWindow Open(string csv)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(ProfileFile.Load(csv));
        return w;
    }

    // Two modes with a Preferences sheet between them: exactly the file you end
    // up with by adding preferences and then a second mode, and exactly the one
    // the old menu could not reorder.
    const string ModePrefsMode =
        "Profile Name,,Driving\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "x,normal,lip\n" +
        "Preferences\n" +
        ",\n" +
        "Preference,Value,Units,Description\n" +
        "Sip_Puff_Threshold,20\n" +
        "Profile Name,,Aiming\n" +
        ",,\n" +
        "Outputs,Function,usb\n" +
        "circle,normal,right_sip\n";

    static Button Find(Window w, string name) =>
        w.GetVisualDescendants().OfType<Button>()
            .First(b => AutomationProperties.GetName(b) == name);

    static void Tap(Window w, string name)
    {
        Find(w, name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
    }

    static string[] ModeNames(MainWindow w) =>
        w.OpenFile!.Document.Sheets.Where(s => s.Type == SheetType.ProfileName)
            .Select(s => s.ModeName).ToArray();

    [AvaloniaFact]
    public void The_first_mode_moves_down_past_a_preferences_sheet()
    {
        var w = Open(ModePrefsMode);
        var modes = new ModesWindow(w);
        _ = modes.ShowDialog(w);
        Dispatcher.UIThread.RunJobs();

        Tap(modes, "Move Driving down");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "Aiming", "Driving" }, ModeNames(w));
        // The Preferences sheet keeps its place; only the modes moved.
        Assert.Equal(SheetType.Preferences, w.OpenFile!.Document.Sheets[1].Type);

        modes.Close();
        w.OpenFile!.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void The_first_mode_can_be_deleted()
    {
        var w = Open(ModePrefsMode);
        var modes = new ModesWindow(w);
        _ = modes.ShowDialog(w);
        Dispatcher.UIThread.RunJobs();

        // Delete asks once in place before it removes anything.
        Tap(modes, "Delete Driving");
        Dispatcher.UIThread.RunJobs();
        Tap(modes, "Really delete Driving");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "Aiming" }, ModeNames(w));
        Assert.Equal("game.csv", w.OpenFile!.Document.CsvFileName);

        modes.Close();
        w.OpenFile!.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void One_click_on_delete_does_not_delete_anything()
    {
        var w = Open(ModePrefsMode);
        var modes = new ModesWindow(w);
        _ = modes.ShowDialog(w);
        Dispatcher.UIThread.RunJobs();

        Tap(modes, "Delete Driving");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "Driving", "Aiming" }, ModeNames(w));

        modes.Close();
        w.OpenFile!.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void A_mode_is_renamed_by_typing_in_its_row()
    {
        var w = Open(ModePrefsMode);
        var modes = new ModesWindow(w);
        _ = modes.ShowDialog(w);
        Dispatcher.UIThread.RunJobs();

        var box = modes.GetVisualDescendants().OfType<TextBox>()
            .First(t => AutomationProperties.GetName(t) == "Name of mode 1");
        Assert.Equal("Driving", box.Text);
        box.Focus();
        Dispatcher.UIThread.RunJobs();
        box.Text = "Racing";
        // The name commits when the box gives up focus, the same rule the
        // editor's cells follow.
        Find(modes, "Add a mode").Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "Racing", "Aiming" }, ModeNames(w));

        modes.Close();
        w.OpenFile!.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void Adding_a_mode_appends_a_row_ready_to_be_named()
    {
        var w = Open(ModePrefsMode);
        var modes = new ModesWindow(w);
        _ = modes.ShowDialog(w);
        Dispatcher.UIThread.RunJobs();

        Tap(modes, "Add a mode");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, ModeNames(w).Length);
        // The new row exists and holds the keyboard, so the name can be typed
        // straight away instead of through a separate naming dialog.
        var box = modes.GetVisualDescendants().OfType<TextBox>()
            .First(t => AutomationProperties.GetName(t) == "Name of mode 3");
        Assert.True(box.IsKeyboardFocusWithin);

        modes.Close();
        w.OpenFile!.Dirty = false;
        w.Close();
    }

    // Up on the first row and down on the last have nowhere to go. They stay
    // visible but disabled, so the row layout never shifts under the pointer.
    [AvaloniaFact]
    public void The_ends_of_the_list_have_their_arrows_disabled()
    {
        var w = Open(ModePrefsMode);
        var modes = new ModesWindow(w);
        _ = modes.ShowDialog(w);
        Dispatcher.UIThread.RunJobs();

        Assert.False(Find(modes, "Move Driving up").IsEnabled);
        Assert.True(Find(modes, "Move Driving down").IsEnabled);
        Assert.True(Find(modes, "Move Aiming up").IsEnabled);
        Assert.False(Find(modes, "Move Aiming down").IsEnabled);

        modes.Close();
        w.OpenFile!.Dirty = false;
        w.Close();
    }
}
