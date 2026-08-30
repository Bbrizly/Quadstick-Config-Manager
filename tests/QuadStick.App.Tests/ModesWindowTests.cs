using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;
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

    // Every fixed width in a row added up to more than the window, so the last
    // button was cut in half by the edge with no scrollbar to reach it. Run at
    // the narrowest the window can be dragged, which is where it broke: the
    // name box is the only thing in the row that can give, and it has to be the
    // only thing that does.
    [AvaloniaFact]
    public void No_row_control_is_clipped_at_the_narrowest_the_window_goes()
    {
        var w = Open(ModePrefsMode);
        var modes = new ModesWindow(w);
        modes.Width = modes.MinWidth;
        _ = modes.ShowDialog(w);
        Dispatcher.UIThread.RunJobs();
        modes.UpdateLayout();

        double floor = (double)Application.Current!.FindResource("IconButton")!;
        int seen = 0;
        foreach (var b in modes.GetVisualDescendants().OfType<Button>())
        {
            var name = AutomationProperties.GetName(b) ?? "";
            if (!name.StartsWith("Delete ") && !name.StartsWith("Move ") && !name.StartsWith("Make a copy")) continue;
            seen++;
            Assert.True(b.Bounds.Width >= floor,
                $"\"{name}\" squeezed to {b.Bounds.Width:0}px, under the {floor:0}px click-target floor");
            var corner = b.TranslatePoint(new Avalonia.Point(b.Bounds.Width, 0), modes);
            Assert.True(corner.HasValue && corner.Value.X <= modes.Bounds.Width + 1,
                $"\"{name}\" runs off the right edge: {corner?.X:0} > {modes.Bounds.Width:0}");
        }
        Assert.True(seen >= 6, $"only found {seen} row controls to check");

        modes.Close();
        w.OpenFile!.Dirty = false;
        w.Close();
    }

    // Moving a mode and moving a row are the same job, so they are the same
    // control. This window drew them as the typed characters "▲ ▼ ✕" while the
    // editor drew the app's own icons, which is two answers to one question and
    // a missing glyph away from a row of empty boxes.
    [AvaloniaFact]
    public void Move_and_delete_look_the_same_here_as_they_do_on_a_row()
    {
        var w = Open(ModePrefsMode);
        var modes = new ModesWindow(w);
        _ = modes.ShowDialog(w);
        Dispatcher.UIThread.RunJobs();
        modes.UpdateLayout();

        foreach (var prefix in new[] { "Move ", "Delete ", "Make a copy" })
        {
            var b = modes.GetVisualDescendants().OfType<Button>()
                .First(x => (AutomationProperties.GetName(x) ?? "").StartsWith(prefix));
            Assert.True(b.Content is Avalonia.Controls.PathIcon,
                $"\"{prefix}\" is drawn with something other than the app's icon set");
            Assert.Contains("icon", b.Classes);
        }

        modes.Close();
        w.OpenFile!.Dirty = false;
        w.Close();
    }

    static string[] ModeNames(MainWindow w) =>
        w.OpenFile!.Document.Sheets.Where(s => s.Type == SheetType.ProfileName)
            .Select(s => s.ModeName).ToArray();

    // The preferences sheet is a row in this list too, so one press moves one
    // row: Driving trades places with it, then with Aiming.
    [AvaloniaFact]
    public void The_first_mode_moves_down_past_a_preferences_sheet()
    {
        var w = Open(ModePrefsMode);
        var modes = new ModesWindow(w);
        _ = modes.ShowDialog(w);
        Dispatcher.UIThread.RunJobs();

        Tap(modes, "Move Driving down");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SheetType.Preferences, w.OpenFile!.Document.Sheets[0].Type);
        Assert.Equal(new[] { "Driving", "Aiming" }, ModeNames(w));

        Tap(modes, "Move Driving down");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "Aiming", "Driving" }, ModeNames(w));

        modes.Close();
        w.OpenFile!.Dirty = false;
        w.Close();
    }

    // The preferences sheet moves on its own, which is the whole reason it is
    // listed here instead of hidden behind a button.
    [AvaloniaFact]
    public void The_preferences_sheet_moves_up_like_any_other_row()
    {
        var w = Open(ModePrefsMode);
        var modes = new ModesWindow(w);
        _ = modes.ShowDialog(w);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SheetType.Preferences, w.OpenFile!.Document.Sheets[1].Type);

        Tap(modes, "Move the preferences sheet up");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SheetType.Preferences, w.OpenFile!.Document.Sheets[0].Type);
        // The profile filename belongs to the file, so it goes to whichever
        // sheet is now first.
        Assert.Equal("game.csv", w.OpenFile!.Document.CsvFileName);
        Assert.Equal(new[] { "Driving", "Aiming" }, ModeNames(w));

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
    public void Adding_a_mode_lands_under_the_open_one_ready_to_be_named()
    {
        var w = Open(ModePrefsMode);
        var modes = new ModesWindow(w);
        _ = modes.ShowDialog(w);
        Dispatcher.UIThread.RunJobs();

        Tap(modes, "Add a mode");
        Dispatcher.UIThread.RunJobs();

        // Driving is the open mode, so the new one is 2 and Aiming becomes 3.
        Assert.Equal(new[] { "Driving", "Mode 3", "Aiming" }, ModeNames(w));
        // The new row exists and holds the keyboard, so the name can be typed
        // straight away instead of through a separate naming dialog.
        var box = modes.GetVisualDescendants().OfType<TextBox>()
            .First(t => AutomationProperties.GetName(t) == "Name of mode 2");
        Assert.True(box.IsKeyboardFocusWithin);

        modes.Close();
        w.OpenFile!.Dirty = false;
        w.Close();
    }

    // Alt with an arrow moves the mode from inside its name box. That rebuilds
    // the list under the box, and the box losing focus used to commit its name
    // against the row number it had before the move, renaming the mode it just
    // swapped with.
    [AvaloniaFact]
    public void Moving_with_the_keyboard_does_not_rename_the_mode_it_passes()
    {
        var w = Open(ModePrefsMode);
        var modes = new ModesWindow(w);
        _ = modes.ShowDialog(w);
        Dispatcher.UIThread.RunJobs();

        modes.GetVisualDescendants().OfType<TextBox>()
            .First(t => AutomationProperties.GetName(t) == "Name of mode 1").Focus();
        Dispatcher.UIThread.RunJobs();

        modes.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        modes.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "Aiming", "Driving" }, ModeNames(w));

        modes.Close();
        w.OpenFile!.Dirty = false;
        w.Close();
    }

    // An armed delete is remembered by sheet number, and removing the
    // preferences sheet renumbers everything below it. The arming must not
    // survive and point at a different mode.
    [AvaloniaFact]
    public void An_armed_delete_does_not_move_to_another_mode()
    {
        var w = Open(ModePrefsMode +
            "Profile Name,,Menus\n" +
            ",,\n" +
            "Outputs,Function,usb\n" +
            "square,normal,left_puff\n");
        var modes = new ModesWindow(w);
        _ = modes.ShowDialog(w);
        Dispatcher.UIThread.RunJobs();

        Tap(modes, "Delete Aiming");
        Assert.NotNull(Find(modes, "Really delete Aiming"));

        Tap(modes, "Delete the preferences sheet");
        Tap(modes, "Really delete the preferences sheet");

        Assert.Empty(modes.GetVisualDescendants().OfType<Button>()
            .Where(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Really delete")));
        Assert.Equal(new[] { "Driving", "Aiming", "Menus" }, ModeNames(w));

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
