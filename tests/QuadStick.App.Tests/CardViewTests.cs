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

public class CardViewTests
{
    // Two lip-switch mappings so the accordion has something to alternate.
    static ProfileFile TwoLipMappings() => ProfileFile.Load(
        "Profile Name,,Solo\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "x,normal,lip,,,,,,,,fire button\n" +
        "circle,turbo,lip\n");

    static MainWindow OpenOnLip(ProfileFile file, bool cards = true)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.DeviceCards = cards;
        // Rows View has its own card setting, and the settings file is shared
        // across tests: without this a test that turned rows into cards leaves
        // the next one looking at sentences.
        s.RowCards = false;
        s.CardSentenceStyle = "PressWhen";
        // A remembered window size resizes the window after layout, so a
        // point computed before the resize would land on the wrong control.
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(file);
        w.SelectZoneForPreview("lip");
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    static Button? Card(MainWindow w, int n) => w.GetVisualDescendants().OfType<Button>()
        .FirstOrDefault(b => (AutomationProperties.GetName(b) ?? "").StartsWith($"Mapping {n}:"));

    // A closed mapping reads as one sentence card; clicking it opens the
    // detailed editor for just that mapping, and opening another closes it.
    [AvaloniaFact]
    public void Cards_read_as_sentences_and_expand_one_at_a_time()
    {
        var file = TwoLipMappings();
        var w = OpenOnLip(file);

        Assert.StartsWith("Mapping 1: press X when you lip, as normal.",
            AutomationProperties.GetName(Card(w, 1)!));
        Assert.NotNull(Card(w, 2));

        Card(w, 2)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Assert.NotNull(Card(w, 1));  // still a card
        Assert.Null(Card(w, 2));     // now the editor
        Assert.Contains(w.GetVisualDescendants().OfType<Button>(),
            b => (AutomationProperties.GetName(b) ?? "").StartsWith("Close the editor for mapping 2"));

        // Accordion: opening the first closes the second.
        Card(w, 1)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Assert.Null(Card(w, 1));
        Assert.NotNull(Card(w, 2));

        // Done goes back to the sentence.
        w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Close the editor for mapping 1"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        w.UpdateLayout();
        Assert.NotNull(Card(w, 1));

        file.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void Simple_cards_include_their_controller_button_visual()
    {
        var file = TwoLipMappings();
        var w = OpenOnLip(file);

        var circle = Card(w, 2)!.GetVisualDescendants().OfType<Viewbox>()
            .SingleOrDefault(icon => AutomationProperties.GetName(icon) == "Circle");
        Assert.NotNull(circle);
        Assert.Equal(30, circle.Width);
        Assert.Equal(30, circle.Height);

        file.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void Input_first_sentence_style_reads_input_to_output_without_extra_reorder_controls()
    {
        var file = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "circle,turbo,lip\n" +
            "square,normal,lip\n");
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.DeviceCards = true;
        s.CardSentenceStyle = "InputToOutput";
        s.RememberWindow = false;
        Settings.Save(s);

        var w = new MainWindow();
        w.Show();
        w.LoadProfile(file);
        w.SelectZoneForPreview("lip");
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        Assert.StartsWith("Mapping 1: lip to X, as normal.", AutomationProperties.GetName(Card(w, 1)!));
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<Button>(),
            b => (AutomationProperties.GetName(b) ?? "").StartsWith("Move mapping"));
        Assert.Equal("InputToOutput", Settings.Load().CardSentenceStyle);

        file.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void Input_first_narrow_card_keeps_to_on_the_input_output_line()
    {
        var file = TwoLipMappings();
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.DeviceCards = true;
        s.CardSentenceStyle = "InputToOutput";
        s.RememberWindow = false;
        Settings.Save(s);

        var w = new MainWindow();
        w.Show();
        w.LoadProfile(file);
        w.SelectZoneForPreview("lip");
        w.Width = 760;
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        Control Element(string text) => (Control?)w.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(border => AutomationProperties.GetName(border) == text)
            ?? w.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == text);
        double Y(string text)
        {
            var element = Element(text);
            return element.TranslatePoint(new Point(0, 0), w)!.Value.Y + element.Bounds.Height / 2;
        }
        Assert.Equal(Y("lip"), Y("X"), 1);
        Assert.Equal(Y("X"), Y("to"), 1);
        Assert.True(Y("as") > Y("X"));
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<Button>(),
            b => (AutomationProperties.GetName(b) ?? "").StartsWith("Move mapping"));

        file.Dirty = false;
        w.Close();
    }

    // The header toggle flips to the full editor for every mapping and the
    // choice survives a restart via settings.
    [AvaloniaFact]
    public void The_toggle_switches_views_and_is_remembered()
    {
        var file = TwoLipMappings();
        var w = OpenOnLip(file);
        Assert.NotNull(Card(w, 1));

        var toggle = w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "CardViewButton");
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        w.UpdateLayout();
        Assert.Null(Card(w, 1)); // both mappings show the detailed editor now
        Assert.Null(Card(w, 2));
        Assert.False(Settings.Load().DeviceCards); // remembered for next launch

        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        w.UpdateLayout();
        Assert.NotNull(Card(w, 1));
        Assert.True(Settings.Load().DeviceCards);

        file.Dirty = false;
        w.Close();
    }

    // The tester shift-clicked the second and last lip card and got "4
    // selected": the range walked every row in the mode, not just this
    // part's mappings. The range must stay inside the visible cards.
    [AvaloniaFact]
    public void Shift_click_counts_only_this_parts_mappings()
    {
        var file = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "square,normal,up\n" +      // joystick row between the lip rows
            "circle,turbo,lip\n" +
            "triangle,normal,down\n");
        var w = OpenOnLip(file);

        Border Handle(int n) => w.GetVisualDescendants().OfType<Border>()
            .First(x => (AutomationProperties.GetName(x) ?? "").StartsWith($"Mapping {n},")
                     || (AutomationProperties.GetName(x) ?? "").StartsWith($"Mapping {n}."));
        void Click(int n, RawInputModifiers mods = RawInputModifiers.None)
        {
            var pt = Handle(n).TranslatePoint(new Point(3, 3), w)!.Value;
            w.MouseDown(pt, MouseButton.Left, mods);
            w.MouseUp(pt, MouseButton.Left, mods);
        }

        Click(1);
        Click(2, RawInputModifiers.Shift);
        Assert.Equal("2 selected",
            w.GetVisualDescendants().OfType<TextBlock>().First(x => x.Name == "SelectionCount").Text);

        // And deleting takes only the lip rows, never the joystick rows.
        w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SelectionDeleteButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new[] { "square", "triangle" },
            file.Document.Sheets[0].Bindings.Select(b => b.Output).ToArray());

        file.Dirty = false;
        w.Close();
    }

    // The expanded editor's header is just Done and a big trash icon: no
    // "Mapping N" label, no "Remove" word.
    [AvaloniaFact]
    public void The_editor_header_is_done_plus_a_trash_icon()
    {
        var file = TwoLipMappings();
        var w = OpenOnLip(file);

        Card(w, 1)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        Assert.DoesNotContain(w.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == "Mapping 1" || t.Text == "Remove");
        var del = w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Remove the "));
        var icon = Assert.IsType<PathIcon>(del.Content);
        Assert.Equal(32, icon.Width); // double the usual 16

        file.Dirty = false;
        w.Close();
    }

    // The three-line handle selects like a list-view row number, and the top
    // command strip deletes the whole selection in one undo step.
    [AvaloniaFact]
    public void Card_handles_select_and_the_device_bar_deletes()
    {
        var file = TwoLipMappings();
        var w = OpenOnLip(file);

        Border Handle(int n) => w.GetVisualDescendants().OfType<Border>()
            .First(x => (AutomationProperties.GetName(x) ?? "").StartsWith($"Mapping {n},")
                     || (AutomationProperties.GetName(x) ?? "").StartsWith($"Mapping {n}."));
        void Click(int n, RawInputModifiers mods = RawInputModifiers.None)
        {
            var pt = Handle(n).TranslatePoint(new Point(3, 3), w)!.Value;
            w.MouseDown(pt, MouseButton.Left, mods);
            w.MouseUp(pt, MouseButton.Left, mods);
        }
        var bar = w.GetVisualDescendants().OfType<StackPanel>().First(x => x.Name == "SelectionCommandBar");
        Assert.False(bar.IsVisible);

        Click(1);
        Click(2, RawInputModifiers.Control);
        Assert.True(bar.IsVisible);
        Assert.Equal("2 selected",
            w.GetVisualDescendants().OfType<TextBlock>().First(x => x.Name == "SelectionCount").Text);

        w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SelectionDeleteButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        w.UpdateLayout();
        Assert.Empty(file.Document.Sheets[0].Bindings);
        Assert.False(bar.IsVisible);

        Assert.True(file.Undo()); // one step brings both mappings back
        Assert.Equal(2, file.Document.Sheets[0].Bindings.Count);

        file.Dirty = false;
        w.Close();
    }

    // The Press dropdown was one ~380-item scroll. Its dropdown drills down
    // like a menu: the top level lists categories, tapping one replaces the
    // list with that category's contents, Back as the first row and the
    // search always pinned on top. Typing in the search replaces whatever
    // level you are on with flat matches.
    [AvaloniaFact]
    public void The_press_dropdown_is_a_searchable_category_picker()
    {
        var file = TwoLipMappings();
        var w = OpenOnLip(file, cards: false);

        Button Press() => w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Game button pressed by"));
        Control Open()
        {
            var press = Press();
            press.Flyout!.ShowAt(press);
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            return (Control)((Flyout)press.Flyout!).Content!;
        }
        Button? Find(Control panel, string prefix) => panel.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => (AutomationProperties.GetName(b) ?? "").StartsWith(prefix));
        void Tap(Control panel, string prefix)
        {
            Find(panel, prefix)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
        }

        // Top level: the categories, no Back yet. A category row is styled
        // differently from an output row, so "opens more" and "picks this"
        // are visually distinct.
        var panel = Open();
        Assert.NotNull(Find(panel, "Controller,"));
        Assert.NotNull(Find(panel, "Keyboard,"));
        Assert.NotNull(Find(panel, "Mouse,"));
        Assert.Null(Find(panel, "Back"));
        Assert.DoesNotContain("quiet", Find(panel, "Controller,")!.Classes);
        Assert.Contains("quiet", Find(panel, "None")!.Classes);

        // Drill in: Controller replaces the list with its subcategories.
        Tap(panel, "Controller,");
        Assert.NotNull(Find(panel, "Buttons,"));
        Assert.NotNull(Find(panel, "Back"));
        Assert.Null(Find(panel, "Keyboard,"));

        // Back returns to the categories.
        Tap(panel, "Back");
        Assert.NotNull(Find(panel, "Keyboard,"));

        // All the way down to an actual output.
        Tap(panel, "Controller,");
        Tap(panel, "Buttons,");
        Tap(panel, "Triangle");
        Assert.Equal("triangle", file.Document.Sheets[0].Bindings[0].Output);

        // Search: typing replaces the categories with flat matches.
        panel = Open();
        var search = panel.GetVisualDescendants().OfType<TextBox>()
            .First(t => (AutomationProperties.GetName(t) ?? "") == "Search this list");
        search.Text = "squar";
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();
        Assert.Null(Find(panel, "Controller,"));
        Tap(panel, "Square");
        Assert.Equal("square", file.Document.Sheets[0].Bindings[0].Output);

        file.Dirty = false;
        w.Close();
    }

    // Type your own must swap the field for a working text box. It used to
    // die instantly: the closing flyout gave focus back, the box saw a
    // LostFocus with empty text and cancelled itself before it was usable.
    [AvaloniaFact]
    public void Type_your_own_gives_a_usable_text_box_that_commits()
    {
        var file = TwoLipMappings();
        var w = OpenOnLip(file, cards: false);

        var press = w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Game button pressed by"));
        press.Flyout!.ShowAt(press);
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();
        var panel = (Control)((Flyout)press.Flyout!).Content!;
        panel.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "") == "Type a custom value")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();

        // The box is alive, in the field's place, and focused for typing.
        var box = w.GetVisualDescendants().OfType<AutoCompleteBox>()
            .First(b => (AutomationProperties.GetName(b) ?? "").EndsWith("Type a custom value."));
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.IsKeyboardFocusWithin, "the custom value box should hold focus");

        box.Text = "my_custom_output";
        w.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();
        Assert.Equal("my_custom_output", file.Document.Sheets[0].Bindings[0].Output);

        file.Dirty = false;
        w.Close();
    }

    // The tester found the card's select/drag handle too small to hit, and
    // wants the add-input control to be a round plus button like the list view.
    [AvaloniaFact]
    public void The_card_handle_is_big_and_add_input_is_a_round_plus()
    {
        var file = TwoLipMappings();
        var w = OpenOnLip(file);

        var handle = w.GetVisualDescendants().OfType<Border>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Mapping 1."));
        Assert.True(handle.Bounds.Width >= 40, $"handle width {handle.Bounds.Width}");
        Assert.True(handle.Bounds.Height >= 40, $"handle height {handle.Bounds.Height}");

        Card(w, 1)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        var add = w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Add another input to mapping 1"));
        Assert.Contains("icon", add.Classes);
        Assert.IsType<PathIcon>(add.Content);

        file.Dirty = false;
        w.Close();
    }

    // Cards read "Press X when you lip", and the pills sit on shared columns
    // so every card's output starts at the same X and so does every input,
    // however wide the card above it was.
    [AvaloniaFact]
    public void Card_sentences_line_up_column_by_column()
    {
        var file = TwoLipMappings();
        var w = OpenOnLip(file);
        // The compact mapping rail is intentional at the normal window size.
        // Use a genuinely wide workspace here because this test is specifically
        // about the wide sentence layout's shared columns.
        w.Width = 2200;
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        // The pills are centered in their columns, so it is their middles that
        // have to agree, not their left edges.
        Border Pill(int n, string text) => w.GetVisualDescendants().OfType<Border>()
            .First(border => AutomationProperties.GetName(border) == text
                && border.GetVisualAncestors().Contains(Card(w, n)!));
        TextBlock Word(int n, string text) => w.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Text == text && t.GetVisualAncestors().Contains(Card(w, n)!));
        double Left(int n, string text) => Word(n, text).TranslatePoint(new Point(0, 0), w)!.Value.X;
        double Center(int n, string text)
        {
            var pill = Pill(n, text);
            var point = pill.TranslatePoint(new Point(0, 0), w)!.Value;
            return point.X + pill.Bounds.Width / 2;
        }

        Assert.Equal("Press", w.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.GetVisualAncestors().Contains(Card(w, 1)!)).Text);
        Assert.True(Math.Abs(Center(1, "X") - Center(2, "Circle")) <= 2, "outputs aligned");
        Assert.True(Math.Abs(Center(1, "lip") - Center(2, "lip")) <= 2, "inputs aligned");
        // The function reads as "as <what>", so that pair keeps a left edge.
        Assert.True(Math.Abs(Left(1, "as") - Left(2, "as")) <= 2, "functions aligned");

        file.Dirty = false;
        w.Close();
    }

    // A mapping with two inputs is two lines tall in the input column. The rest
    // of the sentence has to sit on that block's middle, not at its top.
    [AvaloniaFact]
    public void A_two_input_card_centers_the_rest_on_the_middle()
    {
        var file = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip,mp_center_puff\n");
        var w = OpenOnLip(file);
        // Room for the one-line sentence; narrow stacks instead. The sidebar
        // and compact mapping rail mean this needs a genuinely wide window.
        w.Width = 2200;
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        double MiddleY(string text)
        {
            var pill = w.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(border => AutomationProperties.GetName(border) == text);
            var control = (Control?)pill ?? w.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Text == text);
            return control.TranslatePoint(new Point(0, 0), w)!.Value.Y + control.Bounds.Height / 2;
        }
        // The two input pills straddle the sentence: one above the middle, one
        // below, with the output and the function on the middle itself.
        double middle = (MiddleY("lip") + MiddleY("puff")) / 2;
        Assert.Equal(middle, MiddleY("X"), 1);
        Assert.Equal(middle, MiddleY("normal"), 1);
        Assert.Equal(middle, MiddleY("Press"), 1);

        file.Dirty = false;
        w.Close();
    }

    // Squeezed narrow, one line per phrase would leave every name an ellipsis,
    // so the sentence stacks instead: the words down the left, the pills in one
    // centered column under each other.
    [AvaloniaFact]
    public void A_narrow_panel_stacks_the_sentence()
    {
        var file = TwoLipMappings();
        var w = OpenOnLip(file);
        w.Width = 760;
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        Point At(string text)
        {
            var pill = w.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(border => AutomationProperties.GetName(border) == text);
            var control = (Control?)pill ?? w.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Text == text);
            var p = control.TranslatePoint(new Point(0, 0), w)!.Value;
            return new Point(p.X + control.Bounds.Width / 2, p.Y + control.Bounds.Height / 2);
        }

        Assert.True(At("X").Y < At("lip").Y, "the input goes under the output");
        Assert.True(At("lip").Y < At("normal").Y, "and the function under that");
        // One centered column of pills. Outputs now include their controller
        // visual, so compare the pill bounds rather than their text, which is
        // intentionally offset to make room for the icon.
        double PillCenterX(string text)
        {
            var pill = w.GetVisualDescendants().OfType<Border>()
                .First(border => AutomationProperties.GetName(border) == text);
            var p = pill.TranslatePoint(new Point(0, 0), w)!.Value;
            return p.X + pill.Bounds.Width / 2;
        }
        Assert.True(Math.Abs(PillCenterX("X") - PillCenterX("lip")) <= 2, "input under output");
        Assert.True(Math.Abs(PillCenterX("X") - PillCenterX("normal")) <= 2, "function under both");
        double LeftOf(string text)
        {
            var t = w.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == text);
            return t.TranslatePoint(new Point(0, 0), w)!.Value.X;
        }
        Assert.Equal(LeftOf("Press"), LeftOf("when you"), 1); // words down one left edge
        Assert.Equal(LeftOf("Press"), LeftOf("as"), 1);

        file.Dirty = false;
        w.Close();
    }

    // The add-input plus used to hang under the rows. It sits in the last
    // input row now, left of that row's trash, and every trash stays in one
    // column down the card.
    [AvaloniaFact]
    public void Add_input_sits_left_of_the_last_rows_trash()
    {
        var file = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip,mp_center_puff\n");
        var w = OpenOnLip(file, cards: false);

        var add = w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Add another input to mapping 1"));
        var row = Assert.IsType<Grid>(add.Parent);
        var trash = row.Children.OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Remove this input"));
        Assert.Equal(1, Grid.GetColumn(add));
        Assert.Equal(2, Grid.GetColumn(trash));
        Assert.True(add.Bounds.X < trash.Bounds.X, "the plus goes left of the trash");

        // It rides down to whatever row is last, and the trashes stay aligned.
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();
        var rows = w.GetVisualDescendants().OfType<Button>()
            .Where(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Remove this "))
            .Select(b => b.TranslatePoint(new Point(0, 0), w)!.Value.X).ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, x => Assert.Equal(rows[0], x, 1));
        Assert.NotSame(row, add.Parent);

        file.Dirty = false;
        w.Close();
    }

    // The first input belongs to the part on screen, so a short dropdown fits.
    // The second can be anything on the device, so it gets the searchable
    // picker the List View uses.
    [AvaloniaFact]
    public void Second_input_uses_the_searchable_picker()
    {
        var file = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip,mp_center_puff\n");
        var w = OpenOnLip(file, cards: false);

        static string Name(Control c) => AutomationProperties.GetName(c) ?? "";
        var first = w.GetVisualDescendants().OfType<Control>()
            .First(c => Name(c).StartsWith("Input 1 for this"));
        var second = w.GetVisualDescendants().OfType<Control>()
            .First(c => Name(c).StartsWith("Input 2 for this"));

        Assert.IsType<ComboBox>(first);
        Assert.IsType<Button>(second);
        Assert.EndsWith("Opens a searchable list.", Name(second));

        file.Dirty = false;
        w.Close();
    }

    // The first input uses the compact device-local dropdown. Choosing its
    // escape hatch must leave the replacement box alive long enough to type.
    [AvaloniaFact]
    public void First_input_type_your_own_stays_open_for_typing()
    {
        var file = TwoLipMappings();
        var w = OpenOnLip(file, cards: false);

        var combo = w.GetVisualDescendants().OfType<ComboBox>()
            .First(c => (AutomationProperties.GetName(c) ?? "").StartsWith("Input 1 for this"));
        combo.SelectedItem = Strings.Main_TypeYourOwn;
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        var box = w.GetVisualDescendants().OfType<AutoCompleteBox>()
            .FirstOrDefault(b => (AutomationProperties.GetName(b) ?? "").EndsWith("Type a custom value."));
        Assert.NotNull(box);
        Assert.True(box!.IsKeyboardFocusWithin, "the first input's custom box should hold focus");

        box.Text = "future_input";
        w.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("future_input", file.GetCell(4, 2));

        file.Dirty = false;
        w.Close();
    }

    static Button Named(MainWindow w, string name) => w.GetVisualDescendants()
        .OfType<Button>().First(b => b.Name == name);

    static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    // The two toggles describe a mapping, not a view, so Rows View gets them
    // too. They still have nothing to say about a preferences sheet.
    [AvaloniaFact]
    public void Rows_view_keeps_the_words_and_card_toggles()
    {
        var file = TwoLipMappings();
        var w = OpenOnLip(file);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();

        Assert.True(Named(w, "LabelStyleButton").IsVisible);
        Assert.True(Named(w, "CardViewButton").IsVisible);
        // Rows View opens as the sheet it is, so its offer is the other one.
        Assert.Equal(Strings.Main_SimpleCards, Named(w, "CardViewButton").Content);

        file.Dirty = false;
        w.Close();
    }

    // List names is the file's own spelling: the raw token, no button art.
    [AvaloniaFact]
    public void List_names_show_the_raw_token_and_no_art_in_rows()
    {
        var file = TwoLipMappings();
        var w = OpenOnLip(file, cards: false);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();

        bool Shows(string t) => w.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == t);
        // A face button draws itself and drops the word beside it.
        Assert.False(Shows("circle"));

        var words = Named(w, "LabelStyleButton");
        for (int i = 0; i < 3 && (string?)words.Content != Strings.Main_WordsListNames; i++)
        { Click(words); w.UpdateLayout(); }
        Assert.Equal(Strings.Main_WordsListNames, words.Content);
        Assert.True(Shows("circle"), "list names spells the token out");

        file.Dirty = false;
        w.Close();
    }

    // Device View is the other half of the same rule: list names is the file's
    // own spelling there too, with no button art beside it.
    [AvaloniaFact]
    public void List_names_drop_the_art_in_device_view_too()
    {
        var file = TwoLipMappings();
        var w = OpenOnLip(file);

        var art = Card(w, 2)!.GetVisualDescendants().OfType<Viewbox>()
            .Any(icon => AutomationProperties.GetName(icon) == "Circle");
        Assert.True(art, "plain English draws the button");

        var words = Named(w, "LabelStyleButton");
        for (int i = 0; i < 3 && (string?)words.Content != Strings.Main_WordsListNames; i++)
        { Click(words); w.UpdateLayout(); }
        Assert.Equal(Strings.Main_WordsListNames, words.Content);

        var card = Card(w, 2)!;
        Assert.Empty(card.GetVisualDescendants().OfType<Viewbox>()
            .Where(icon => AutomationProperties.GetName(icon) == "Circle"));
        Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == "circle");

        file.Dirty = false;
        w.Close();
    }

    // An output the device has no button for gets words and nothing else. It
    // used to carry a neutral box that read as a control the QuadStick has.
    [AvaloniaFact]
    public void An_output_with_no_button_behind_it_is_words_alone()
    {
        var file = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "decrement_mode,normal,lip\n");
        var w = OpenOnLip(file);

        var card = Card(w, 1)!;
        Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == "Decrement mode");
        Assert.Empty(card.GetVisualDescendants().OfType<Viewbox>()
            .Where(icon => AutomationProperties.GetName(icon) == "Decrement mode"));

        file.Dirty = false;
        w.Close();
    }

    // Simple cards in Rows View: one sentence per row, and the row being
    // edited opens into the full editor with a way back.
    [AvaloniaFact]
    public void Rows_view_cards_open_one_row_and_close_again()
    {
        var file = TwoLipMappings();
        var w = OpenOnLip(file, cards: false);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();
        Assert.Null(Card(w, 1)); // detailed editor, the way Rows View starts

        Click(Named(w, "CardViewButton"));
        w.UpdateLayout();
        Assert.NotNull(Card(w, 1));
        Assert.NotNull(Card(w, 2));
        Assert.True(Settings.Load().RowCards);        // remembered for next launch
        Assert.False(Settings.Load().DeviceCards);    // Device View keeps its own

        Click(Card(w, 2)!);
        w.UpdateLayout();
        Assert.NotNull(Card(w, 1));
        Assert.Null(Card(w, 2)); // open in the row editor

        Click(w.GetVisualDescendants().OfType<Button>()
            .First(b => AutomationProperties.GetName(b) == Strings.Main_Done));
        w.UpdateLayout();
        Assert.NotNull(Card(w, 2));

        file.Dirty = false;
        w.Close();
    }
}
