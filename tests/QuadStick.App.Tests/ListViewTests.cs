using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
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
    // The number beside a row is the row it is on in the spreadsheet, not its
    // place in the list. The user's other copy of this profile is a Google
    // Sheet, every warning in the app names a cell like "C7", and the screen
    // reader was already saying "row 4" while the label beside it said "1".
    [AvaloniaFact]
    public void Row_numbers_are_the_sheet_rows_not_one_two_three()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "circle,normal,right_sip\n"));
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();

        var numbers = w.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? "")
            .Where(t => t is "1" or "2" or "3" or "4" or "5")
            .ToList();

        Assert.Contains("4", numbers);
        Assert.Contains("5", numbers);
        Assert.DoesNotContain("1", numbers);
        w.Close();
    }

    // The one thing the row layout owes a mouth-stick user: a row that fits the
    // window it is in. Panning sideways to reach the note or the delete button
    // is not a movement this app can ask for, and the fixed-width cells used to
    // need 1102px on a bindings sheet, so a narrowed window, a laptop screen or
    // 125% interface scale all put a column out of reach.
    //
    // Pinned at the narrowest the window can be, on both kinds of sheet, and
    // with a mode override row, whose value cell is built by a different path.
    [AvaloniaFact]
    public void A_row_fits_the_window_however_narrow_it_is()
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
            "x,normal,lip,left_puff\n" +
            "square,normal,hard_sip\n" +
            "sip_threshold,,40\n" +
            "Preferences\n" +
            "\n" +
            "Preference,Value,Units\n" +
            "volume,40,percent\n");
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        var scroll = w.GetVisualDescendants().OfType<ScrollViewer>().First(x => x.Name == "GridScroll");
        foreach (var sheet in new[] { 0, 1 })
        {
            w.SelectSheetForPreview(sheet);
            foreach (var width in new double[] { 1180, 900, 760 })
            {
                w.Width = width;
                Dispatcher.UIThread.RunJobs();
                w.UpdateLayout();
                Assert.True(scroll.Extent.Width <= scroll.Viewport.Width,
                    $"sheet {sheet} at {width}: needs {scroll.Extent.Width}, has {scroll.Viewport.Width}");
            }
        }

        file.Dirty = false;
        w.Close();
    }

    // And a row that fits is only half of it: every row has to lay its cells on
    // the same columns, or the header stops naming what is under it. The cells
    // that hold buttons are the ones that used to break this, being as wide as
    // whichever row happened to fill them most.
    [AvaloniaFact]
    public void Every_row_puts_its_cells_in_the_same_columns()
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
            "x,normal,lip,left_puff\n" +   // two inputs: the buttons stack
            "square,normal,hard_sip\n" +   // one: they sit side by side
            "sip_threshold,,40\n" +        // an override row, wrapped in a stack
            "Preferences\n" +
            "\n" +
            "Preference,Value,Units\n" +
            "volume,40,percent\n" +        // a spinner, whose own minimum is wide
            "made_up_setting,x\n");        // and a plain box beside it
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        var rows = w.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "RowsPanel");
        // Narrow too: a squeezed column is where a control's own minimum size
        // can push past its share and drag the row out of line again.
        foreach (var sheet in new[] { 0, 1 })
        {
            w.SelectSheetForPreview(sheet);
            foreach (var width in new double[] { 1180, 760 })
            {
                w.Width = width;
                Dispatcher.UIThread.RunJobs();
                w.UpdateLayout();
                // A row that carries a heading, a scope badge or an explaining
                // line wraps its grid in a stack, so look one level in too.
                static Grid? RowGrid(Control c) =>
                    c as Grid ?? (c as StackPanel)?.Children.OfType<Grid>().FirstOrDefault();
                var widths = rows.Children.OfType<Control>().Select(RowGrid).OfType<Grid>()
                    .Select(g => string.Join(",", g.ColumnDefinitions.Select(c => c.ActualWidth)))
                    .Distinct().ToList();
                Assert.Single(widths); // the header and every row, one set of columns
            }
        }

        file.Dirty = false;
        w.Close();
    }

    // Rows sat in 6px of daylight each, which cost a row of height every nine
    // rows and made each one read as its own card. They touch now, parted by a
    // hairline, so the list reads as a table and more of it fits on a screen.
    [AvaloniaFact]
    public void Rows_touch_each_other_with_a_line_between()
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
            "x,normal,lip\n" +
            "square,normal,hard_sip\n");
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        var rows = w.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "RowsPanel");
        var children = rows.Children.OfType<Control>().ToList();
        for (int i = 1; i < children.Count; i++)
            Assert.Equal(children[i - 1].Bounds.Bottom, children[i].Bounds.Y);

        // And the line is really there, spanning the row it closes.
        var line = children.Last().GetVisualDescendants().OfType<Border>()
            .First(b => b.Height == 1);
        Assert.False(line.IsHitTestVisible);

        file.Dirty = false;
        w.Close();
    }

    // "Add row" on a Preferences sheet appeared to do nothing: the row it wrote
    // was blank in every column, and a blank row is where a sheet ends, so the
    // reparse threw it away before the list was rebuilt.
    [AvaloniaFact]
    public void Add_row_really_adds_a_row_to_a_preferences_sheet()
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
            "x,normal,lip\n" +
            "Preferences\n" +
            "\n" +
            "Preference,Value,Units\n" +
            "mouse_speed,201\n");
        w.LoadProfile(file);
        w.ModesChanged(1, ""); // show the Preferences sheet
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();

        // One name per row, but a templated cell repeats it on its inner parts.
        int Rows() => w.GetVisualDescendants().OfType<Control>()
            .Select(t => AutomationProperties.GetName(t) ?? "")
            .Where(n => n.StartsWith("Setting name for row ")).Distinct().Count();
        Assert.Equal(1, Rows());

        w.GetVisualDescendants().OfType<Button>()
            .First(b => AutomationProperties.GetName(b) == "Add a new binding row to this mode")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        Assert.Equal(2, Rows());
        Assert.Equal(2, file.Document.Sheets[1].Bindings.Count);

        file.Dirty = false; // else Close opens the save dialog and waits forever
        w.Close();
    }

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

        // The new cell is a picker; search for the value and tap it.
        var newCell = w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith($"Input 2 for row {row}"));
        newCell.Flyout!.ShowAt(newCell);
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();
        var panel = (Control)((Flyout)newCell.Flyout!).Content!;
        panel.GetVisualDescendants().OfType<TextBox>()
            .First(t => (AutomationProperties.GetName(t) ?? "") == "Search this list").Text = "mp_left_puff";
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();
        panel.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "") == "mp_left_puff")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
        var cell = w.GetVisualDescendants().OfType<Button>()
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

        // n is the nth binding; the label on it is its sheet row, and three
        // header lines sit above the first one.
        Border Handle(int n) => w.GetVisualDescendants().OfType<Border>()
            .First(x => (AutomationProperties.GetName(x) ?? "").StartsWith($"Row {n + 3},")
                     || (AutomationProperties.GetName(x) ?? "").StartsWith($"Row {n + 3}."));
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

        // n is the nth binding; the label on it is its sheet row, and three
        // header lines sit above the first one.
        Border Handle(int n) => w.GetVisualDescendants().OfType<Border>()
            .First(x => (AutomationProperties.GetName(x) ?? "").StartsWith($"Row {n + 3},")
                     || (AutomationProperties.GetName(x) ?? "").StartsWith($"Row {n + 3}."));
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

        // n is the nth binding; the label on it is its sheet row, and three
        // header lines sit above the first one.
        Border Handle(int n) => w.GetVisualDescendants().OfType<Border>()
            .First(x => (AutomationProperties.GetName(x) ?? "").StartsWith($"Row {n + 3},")
                     || (AutomationProperties.GetName(x) ?? "").StartsWith($"Row {n + 3}."));
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

        // n is the nth binding; the label on it is its sheet row, and three
        // header lines sit above the first one.
        Border Handle(int n) => w.GetVisualDescendants().OfType<Border>()
            .First(x => (AutomationProperties.GetName(x) ?? "").StartsWith($"Row {n + 3},")
                     || (AutomationProperties.GetName(x) ?? "").StartsWith($"Row {n + 3}."));

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

        // n is the nth binding; the label on it is its sheet row, and three
        // header lines sit above the first one.
        Border Handle(int n) => w.GetVisualDescendants().OfType<Border>()
            .First(x => (AutomationProperties.GetName(x) ?? "").StartsWith($"Row {n + 3},")
                     || (AutomationProperties.GetName(x) ?? "").StartsWith($"Row {n + 3}."));
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
        // Settle first. Cells share the window's width now, so a measurement
        // taken before the window has finished sizing reads a narrower row
        // than the one on screen a moment later.
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        Button Box(string name) => w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith(name + "."));
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

    // The list view's output, function and input cells open the same
    // drill-down picker the device view's Press field uses: search pinned
    // on top, categories that replace the list, flat matches while typing.
    // Functions are a short list, so they get no categories, just the items.
    [AvaloniaFact]
    public void List_cells_open_the_same_drill_down_picker()
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

        Button Cell(string prefix) => w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith(prefix));
        Control OpenFly(Button cell)
        {
            cell.Flyout!.ShowAt(cell);
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            return (Control)((Flyout)cell.Flyout!).Content!;
        }
        Button? Find(Control panel, string prefix) => panel.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => (AutomationProperties.GetName(b) ?? "").StartsWith(prefix));
        void Tap(Control panel, string prefix)
        {
            Find(panel, prefix)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
        }

        // Output cell: categories, raw token labels, commit updates the file.
        var panel = OpenFly(Cell("Output for row 4"));
        Assert.NotNull(Find(panel, "Controller,"));
        Tap(panel, "Controller,");
        Tap(panel, "Buttons,");
        Tap(panel, "circle");
        Assert.Equal("circle", file.Document.Sheets[0].Bindings[0].Output);

        // Function cell: no categories, just the flat searchable list.
        panel = OpenFly(Cell("Function for row 4"));
        Assert.Null(Find(panel, "Controller,"));
        Assert.Null(Find(panel, "Back"));
        Tap(panel, "toggle");
        Assert.Equal("toggle", file.Document.Sheets[0].Bindings[0].Function);

        // Input cell: categories are the parts of the device.
        panel = OpenFly(Cell("Input 1 for row 4"));
        Assert.NotNull(Find(panel, "Joystick,"));
        Tap(panel, "Left mouthpiece hole,");
        Tap(panel, "mp_left_puff");
        Assert.Equal("mp_left_puff", file.Document.Sheets[0].Bindings[0].Inputs[0]);

        file.Dirty = false;
        w.Close();
    }

    // An empty cell must read as empty: its "pick an input" placeholder is
    // muted and italic, not the same weight as a real value.
    [AvaloniaFact]
    public void Empty_cells_look_empty()
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
            ",normal,lip\n");   // no output: that cell is empty
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();

        TextBlock Label(string prefix) => (TextBlock)w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith(prefix)).Content!;

        var empty = Label("Output for row 4");
        Assert.Equal("pick an output", empty.Text);
        Assert.Contains("muted", empty.Classes);
        Assert.Equal(FontStyle.Italic, empty.FontStyle);

        // A filled cell stays plain and full strength.
        var filled = Label("Function for row 4");
        Assert.Equal("normal", filled.Text);
        Assert.DoesNotContain("muted", filled.Classes);
        Assert.Equal(FontStyle.Normal, filled.FontStyle);

        file.Dirty = false;
        w.Close();
    }

    // Hovering used to cross-fade the background and border, which read as a
    // flash when the pointer crossed between neighbouring controls. No brush
    // transitions anywhere: hover states apply instantly.
    [AvaloniaFact]
    public void Hover_states_do_not_cross_fade()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.UpdateLayout();

        var presenters = w.GetVisualDescendants().OfType<ContentPresenter>()
            .Where(c => c.FindAncestorOfType<Button>() is not null).ToList();
        Assert.NotEmpty(presenters);
        foreach (var p in presenters)
            Assert.DoesNotContain(p.Transitions ?? new Transitions(),
                t => t is BrushTransition);

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
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<Button>(),
            b => (AutomationProperties.GetName(b) ?? "").StartsWith("Input 1 for row 4"));

        file.Dirty = false;
        w.Close();
    }

    // The device reads the same setting names in three places and means
    // something different each time. All three had the same faceless grid, so
    // nothing on screen said whether a change was device-wide or not.
    static MainWindow ShowSheet(string csv, int sheetIndex, out ProfileFile file)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        file = ProfileFile.Load(csv);
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        if (sheetIndex > 0)
            w.GetVisualDescendants().OfType<ComboBox>()
                .First(c => c.Name == "SheetPicker").SelectedIndex = sheetIndex;
        w.UpdateLayout();
        return w;
    }

    static bool SaysAnywhere(MainWindow w, string phrase) =>
        w.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == phrase)
        && w.GetVisualDescendants().OfType<Control>()
            .Any(c => (AutomationProperties.GetName(c) ?? "").Contains(phrase, StringComparison.Ordinal));

    [AvaloniaFact]
    public void A_standalone_prefs_file_says_it_is_device_wide()
    {
        var w = ShowSheet("Preferences\nprefs.csv\nPreference,Value\nvolume,40\n", 0, out var file);

        Assert.True(SaysAnywhere(w, "Device-wide settings"));
        Assert.False(SaysAnywhere(w, "Active while this profile is loaded"));

        file.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void An_embedded_preferences_sheet_says_it_lasts_while_the_profile_is_loaded()
    {
        var w = ShowSheet(
            "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\nx,normal,lip\n" +
            "Preferences\n\nPreference,Value\nvolume,40\n", 1, out var file);

        Assert.True(SaysAnywhere(w, "Active while this profile is loaded"));
        Assert.False(SaysAnywhere(w, "Device-wide settings"));

        file.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void A_mode_sheet_override_row_says_it_only_applies_in_that_mode()
    {
        var w = ShowSheet(
            "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\n" +
            "x,normal,lip\nvolume,,40\n", 0, out var file);

        Assert.True(SaysAnywhere(w, "Active only in this mode"));

        // The plain binding row above it is not an override and gets no badge.
        Assert.Single(w.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Text == "Active only in this mode"));

        file.Dirty = false;
        w.Close();
    }

    // Preferences rows used to offer no suggestions at all: you had to already
    // know every setting name the device understands.
    [AvaloniaFact]
    public void A_preferences_row_suggests_catalog_names_and_still_takes_a_typed_one()
    {
        var w = ShowSheet("Preferences\nprefs.csv\nPreference,Value\nvolume,40\n", 0, out var file);

        var nameBox = w.GetVisualDescendants().OfType<AutoCompleteBox>()
            .First(b => AutomationProperties.GetName(b) == "Setting name for row 4");
        var offered = ((IEnumerable<string>)nameBox.ItemsSource!).ToList();
        Assert.Contains("mouse_speed", offered);
        Assert.Contains("bluetooth_device_mode", offered);

        // Free text survives: a name from firmware newer than this app.
        nameBox.Text = "some_future_setting";
        nameBox.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("some_future_setting", file.GetCell(4, 0));

        file.Dirty = false;
        w.Close();
    }

    // A mode sheet's output column also takes preference names, but only the
    // ones the firmware honors per mode, and picking one is a plain token, not
    // a custom action name.
    [AvaloniaFact]
    public void A_mode_output_offers_override_settings_without_writing_an_action_name()
    {
        var w = ShowSheet(
            "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\nx,normal,lip\n", 0, out var file);

        var cell = w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Output for row 4"));
        cell.Flyout!.ShowAt(cell);
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();
        var panel = (Control)((Flyout)cell.Flyout!).Content!;
        panel.GetVisualDescendants().OfType<TextBox>()
            .First(t => (AutomationProperties.GetName(t) ?? "") == "Search this list").Text = "volume";
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();
        panel.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "") == "volume")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();

        Assert.Equal("volume", file.GetCell(4, 0));
        Assert.Equal("", file.GetCell(4, ProfileFile.ActionColumn)); // never an action name

        file.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    // Both halves of the hard sip/puff delay are honored per mode on firmware
    // 2373. The hard one used to be offered on a Preferences sheet only,
    // because the 2017 source had no such preference name at all to honor.
    public void The_hard_sip_puff_delay_is_offered_as_a_mode_override()
    {
        var w = ShowSheet(
            "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\nx,normal,lip\n", 0, out var file);

        var cell = w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Output for row 4"));
        cell.Flyout!.ShowAt(cell);
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();
        var panel = (Control)((Flyout)cell.Flyout!).Content!;
        panel.GetVisualDescendants().OfType<TextBox>()
            .First(t => (AutomationProperties.GetName(t) ?? "") == "Search this list").Text = "sip_puff_delay";
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();

        var hits = panel.GetVisualDescendants().OfType<Button>()
            .Select(b => AutomationProperties.GetName(b) ?? "").ToList();
        Assert.Contains("sip_puff_delay_soft", hits);
        Assert.Contains("sip_puff_delay_hard", hits);

        file.Dirty = false;
        w.Close();
    }
}
