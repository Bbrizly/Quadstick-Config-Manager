using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

public class ListViewTests
{
    // The tester added an input with "+ input", typed a value, and got no
    // remove (trash) button until switching views and back. The row must
    // rebuild on commit so the remove button is there right away, and
    // clicking it must really take the input out of the file.
    [AvaloniaFact]
    public void A_newly_typed_input_gets_a_working_remove_button_immediately()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.NewFromTemplate("smoke.csv");
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout(); // realize the list rows inside the scroll viewer

        Button Find(string name) => w.GetVisualDescendants().OfType<Button>()
            .First(b => AutomationProperties.GetName(b) == name);
        bool Exists(string name) => w.GetVisualDescendants().OfType<Button>()
            .Any(b => AutomationProperties.GetName(b) == name);

        var addInput = w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Add another input to row "));
        int row = int.Parse(AutomationProperties.GetName(addInput)!.Split(' ')[^1]);
        addInput.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var newBox = w.GetVisualDescendants().OfType<AutoCompleteBox>()
            .First(b => AutomationProperties.GetName(b) == $"Input 2 for row {row}");
        newBox.Text = "left_puff";
        // Commit fires when focus leaves the box, exactly like a user
        // clicking elsewhere. Park focus outside the rows so the rebuild
        // never destroys the focused control.
        w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "AddRowButton").Focus();
        Dispatcher.UIThread.RunJobs(); // the rebuild is deferred out of the event
        w.UpdateLayout();

        Assert.True(Exists($"Remove input 2 from row {row}")); // no view switch needed

        Find($"Remove input 2 from row {row}").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(Exists($"Remove input 2 from row {row}")); // really removed from the file

        file.Dirty = false; // else Close opens the save dialog and waits forever
        w.Close();
    }

    // The tester noticed the column titles sit a little left of the columns
    // themselves. The row-number label carries a 4px margin its header spacer
    // did not, so every header swatch was off by exactly that much.
    [AvaloniaFact]
    public void Column_headers_line_up_with_their_columns()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.NewFromTemplate("smoke.csv");
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();

        var header = w.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Text == "Output (game button)");
        var cell = w.GetVisualDescendants().OfType<AutoCompleteBox>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Output for row "));

        // Compare the swatch border and the cell border, both in window space.
        var swatch = header.FindAncestorOfType<Border>()!;
        var cellBorder = cell.FindAncestorOfType<Border>()!;
        double headerX = swatch.TranslatePoint(new Avalonia.Point(0, 0), w)!.Value.X;
        double cellX = cellBorder.TranslatePoint(new Avalonia.Point(0, 0), w)!.Value.X;
        Assert.Equal(cellX, headerX);

        file.Dirty = false;
        w.Close();
    }

    // Rows select like files in a file explorer: click the row number for
    // one, Shift-click for a range, Ctrl-click to toggle, Escape to clear,
    // and Space on a focused number for keyboard and switch users.
    [AvaloniaFact]
    public void Row_numbers_select_like_a_file_explorer()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false; // a late window resize would move the click targets
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "circle,normal,right_sip\n" +
            "square,normal,left_puff\n");
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();

        Border Handle(int n) => w.GetVisualDescendants().OfType<Border>()
            .First(x => (AutomationProperties.GetName(x) ?? "").StartsWith($"Row {n},")
                     || (AutomationProperties.GetName(x) ?? "").StartsWith($"Row {n}."));
        bool Selected(int n) => AutomationProperties.GetName(Handle(n))!.Contains("selected");
        void Click(int n, RawInputModifiers mods = RawInputModifiers.None)
        {
            var pt = Handle(n).TranslatePoint(new Point(3, 3), w)!.Value;
            w.MouseDown(pt, MouseButton.Left, mods);
            w.MouseUp(pt, MouseButton.Left, mods);
        }

        Click(1);
        Assert.True(Selected(1));

        Click(3, RawInputModifiers.Shift); // range from the anchor
        Assert.True(Selected(1) && Selected(2) && Selected(3));

        Click(2, RawInputModifiers.Control); // toggle one out
        Assert.True(Selected(1) && !Selected(2) && Selected(3));

        w.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.True(!Selected(1) && !Selected(2) && !Selected(3));

        Handle(2).Focus();
        w.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Assert.True(Selected(2)); // Space selects without a pointer

        file.Dirty = false;
        w.Close();
    }

    // While rows are selected a bar says how many and offers Delete and
    // Clear; the Delete key works too, and one undo restores everything.
    [AvaloniaFact]
    public void The_selection_bar_deletes_selected_rows_together()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false; // a late window resize would move the click targets
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "circle,normal,right_sip\n" +
            "square,normal,left_puff\n");
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();

        Border Handle(int n) => w.GetVisualDescendants().OfType<Border>()
            .First(x => (AutomationProperties.GetName(x) ?? "").StartsWith($"Row {n},")
                     || (AutomationProperties.GetName(x) ?? "").StartsWith($"Row {n}."));
        void Click(int n, RawInputModifiers mods = RawInputModifiers.None)
        {
            var pt = Handle(n).TranslatePoint(new Point(3, 3), w)!.Value;
            w.MouseDown(pt, MouseButton.Left, mods);
            w.MouseUp(pt, MouseButton.Left, mods);
        }
        var bar = w.GetVisualDescendants().OfType<Border>().First(x => x.Name == "SelectionBar");
        var count = w.GetVisualDescendants().OfType<TextBlock>().First(x => x.Name == "SelectionCount");

        Assert.False(bar.IsVisible); // nothing selected, no bar

        Click(1);
        Click(3, RawInputModifiers.Control);
        Assert.True(bar.IsVisible);
        Assert.Equal("2 selected", count.Text);

        w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SelectionDeleteButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        w.UpdateLayout();
        Assert.Equal(new[] { "circle" },
            file.Document.Sheets[0].Bindings.Select(b => b.Output).ToArray());
        Assert.False(bar.IsVisible); // selection is spent

        Assert.True(file.Undo()); // one step, both rows back
        Assert.Equal(3, file.Document.Sheets[0].Bindings.Count);

        // The Delete key does the same as the button.
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();
        Click(2);
        w.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        Assert.Equal(new[] { "x", "square" },
            file.Document.Sheets[0].Bindings.Select(b => b.Output).ToArray());

        file.Dirty = false;
        w.Close();
    }

    // A plain click on a row that is already part of a bigger selection
    // collapses the selection to just that row, like a file explorer. The
    // multi-drag still works because the drag starts before release.
    [AvaloniaFact]
    public void A_plain_click_on_a_selected_row_keeps_just_that_row()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false; // a late window resize would move the click targets
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "circle,normal,right_sip\n" +
            "square,normal,left_puff\n");
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();

        Border Handle(int n) => w.GetVisualDescendants().OfType<Border>()
            .First(x => (AutomationProperties.GetName(x) ?? "").StartsWith($"Row {n},")
                     || (AutomationProperties.GetName(x) ?? "").StartsWith($"Row {n}."));
        bool Selected(int n) => AutomationProperties.GetName(Handle(n))!.Contains("selected");
        void Click(int n, RawInputModifiers mods = RawInputModifiers.None)
        {
            var pt = Handle(n).TranslatePoint(new Point(3, 3), w)!.Value;
            w.MouseDown(pt, MouseButton.Left, mods);
            w.MouseUp(pt, MouseButton.Left, mods);
        }

        Click(1);
        Click(3, RawInputModifiers.Shift);
        Assert.True(Selected(1) && Selected(2) && Selected(3));

        Click(2); // plain click inside the selection
        Assert.True(!Selected(1) && Selected(2) && !Selected(3));

        file.Dirty = false;
        w.Close();
    }

    // The bar floats over the list corner; selecting must never push the
    // rows down (the tester called the jump "very bad").
    [AvaloniaFact]
    public void The_selection_bar_floats_without_moving_the_rows()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false; // a late window resize would move the click targets
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "circle,normal,right_sip\n");
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();

        Border Handle(int n) => w.GetVisualDescendants().OfType<Border>()
            .First(x => (AutomationProperties.GetName(x) ?? "").StartsWith($"Row {n},")
                     || (AutomationProperties.GetName(x) ?? "").StartsWith($"Row {n}."));

        double before = Handle(1).TranslatePoint(new Point(0, 0), w)!.Value.Y;
        var pt = Handle(1).TranslatePoint(new Point(3, 3), w)!.Value;
        w.MouseDown(pt, MouseButton.Left, RawInputModifiers.None);
        w.MouseUp(pt, MouseButton.Left, RawInputModifiers.None);
        w.UpdateLayout();

        Assert.True(w.GetVisualDescendants().OfType<Border>().First(x => x.Name == "SelectionBar").IsVisible);
        Assert.Equal(before, Handle(1).TranslatePoint(new Point(0, 0), w)!.Value.Y);

        file.Dirty = false;
        w.Close();
    }

    // The Move button's menu sends the whole selection to the top or the
    // bottom of the mode, one undo step per move.
    [AvaloniaFact]
    public void The_move_menu_sends_the_selection_to_the_top_or_bottom()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false; // a late window resize would move the click targets
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "circle,normal,right_sip\n" +
            "square,normal,left_puff\n");
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();

        Border Handle(int n) => w.GetVisualDescendants().OfType<Border>()
            .First(x => (AutomationProperties.GetName(x) ?? "").StartsWith($"Row {n},")
                     || (AutomationProperties.GetName(x) ?? "").StartsWith($"Row {n}."));
        void Click(int n, RawInputModifiers mods = RawInputModifiers.None)
        {
            var pt = Handle(n).TranslatePoint(new Point(3, 3), w)!.Value;
            w.MouseDown(pt, MouseButton.Left, mods);
            w.MouseUp(pt, MouseButton.Left, mods);
        }
        string[] Order() => file.Document.Sheets[0].Bindings.Select(b => b.Output).ToArray();
        MenuItem Item(string header) => ((MenuFlyout)w.GetVisualDescendants().OfType<Button>()
            .First(b => b.Name == "SelectionMoveButton").Flyout!).Items.OfType<MenuItem>()
            .First(m => (string?)m.Header == header);

        Click(2);
        Click(3, RawInputModifiers.Control); // circle + square
        Item("To the top").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        w.UpdateLayout();
        Assert.Equal(new[] { "circle", "square", "x" }, Order());

        Click(1);
        Click(2, RawInputModifiers.Control); // circle + square again
        Item("To the bottom").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        w.UpdateLayout();
        Assert.Equal(new[] { "x", "circle", "square" }, Order());

        Assert.True(file.Undo()); // one step per move
        Assert.Equal(new[] { "circle", "square", "x" }, Order());

        file.Dirty = false;
        w.Close();
    }

    // The Preferences sheet must show the official template's Units and
    // Description columns; hiding them hid the tester's own setting notes.
    [AvaloniaFact]
    public void Preferences_sheet_shows_units_and_description()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "Preferences\n" +
            "\n" +
            "Preference,Value,Units,Description\n" +
            "mouse_speed,201,,how fast the pointer moves\n");
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);

        var picker = w.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "SheetPicker");
        picker.SelectedIndex = 1; // the Preferences sheet
        w.UpdateLayout();

        Assert.Contains(w.GetVisualDescendants().OfType<AutoCompleteBox>(),
            b => AutomationProperties.GetName(b) == "Units for row 8");
        var desc = w.GetVisualDescendants().OfType<TextBox>()
            .First(t => (AutomationProperties.GetName(t) ?? "").StartsWith("Description for row 8"));
        Assert.Equal("how fast the pointer moves", desc.Text);

        file.Dirty = false;
        w.Close();
    }

    // The tester does not want sideways scrolling: a row's inputs must stack
    // under each other, with the output and function cells centered against
    // the taller stack, and adding one more input grows the row down, not right.
    [AvaloniaFact]
    public void Extra_inputs_stack_below_the_first_one()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip,left_puff\n");
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();

        AutoCompleteBox Box(string name) => w.GetVisualDescendants().OfType<AutoCompleteBox>()
            .First(b => AutomationProperties.GetName(b) == name);
        Point At(Control c) => c.TranslatePoint(new Point(0, 0), w)!.Value;

        var one = At(Box("Input 1 for row 4"));
        var two = At(Box("Input 2 for row 4"));
        Assert.Equal(one.X, two.X);       // same column
        Assert.True(two.Y > one.Y);       // below, not beside

        // With more than one input the plus and the row trash stack into one
        // column (plus on top), so the extra per-input trashes do not push
        // the note further right. A single-input row keeps them side by side.
        Button Btn(string name) => w.GetVisualDescendants().OfType<Button>()
            .First(b => AutomationProperties.GetName(b) == name);
        var plus = At(Btn("Add another input to row 4"));
        var trash = At(Btn("Delete row 4"));
        Assert.Equal(plus.X, trash.X);
        Assert.True(trash.Y > plus.Y);

        var add = w.GetVisualDescendants().OfType<Button>()
            .First(b => AutomationProperties.GetName(b) == "Add another input to row 4");
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        var three = At(Box("Input 3 for row 4"));
        Assert.Equal(one.X, three.X);     // the new one stacks too
        Assert.True(three.Y > two.Y);

        file.Dirty = false;
        w.Close();
    }

    // "+ input" and "Delete row" become round icon buttons: a plus circle,
    // then a red trash circle for the whole row right next to it. The trash
    // must really delete the row.
    [AvaloniaFact]
    public void The_row_has_a_round_plus_and_a_red_round_trash()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip\n");
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();

        Button Find(string name) => w.GetVisualDescendants().OfType<Button>()
            .First(b => AutomationProperties.GetName(b) == name);

        var add = Find("Add another input to row 4");
        Assert.Contains("icon", add.Classes);
        Assert.IsType<PathIcon>(add.Content);

        var del = Find("Delete row 4");
        Assert.Contains("icon", del.Classes);
        Assert.Contains("danger", del.Classes);
        Assert.IsType<PathIcon>(del.Content);

        // One input: the trash sits beside the plus, on the same line.
        Point At(Control c) => c.TranslatePoint(new Point(0, 0), w)!.Value;
        Assert.True(At(del).X > At(add).X);
        Assert.Equal(At(add).Y, At(del).Y);

        del.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<AutoCompleteBox>(),
            b => AutomationProperties.GetName(b) == "Input 1 for row 4");

        file.Dirty = false;
        w.Close();
    }
}
