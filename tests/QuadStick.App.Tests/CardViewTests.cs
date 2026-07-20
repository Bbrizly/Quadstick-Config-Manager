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

        Assert.StartsWith("Mapping 1: X when you lip, as normal.",
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
            w.GetVisualDescendants().OfType<TextBlock>().First(x => x.Name == "DeviceSelectionCount").Text);

        // And deleting takes only the lip rows, never the joystick rows.
        w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "DeviceSelectionDeleteButton")
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

    // The three-line handle selects like a list-view row number, and the bar
    // above the cards deletes the whole selection in one undo step.
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
        var bar = w.GetVisualDescendants().OfType<Border>().First(x => x.Name == "DeviceSelectionBar");
        Assert.False(bar.IsVisible);

        Click(1);
        Click(2, RawInputModifiers.Control);
        Assert.True(bar.IsVisible);
        Assert.Equal("2 selected",
            w.GetVisualDescendants().OfType<TextBlock>().First(x => x.Name == "DeviceSelectionCount").Text);

        w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "DeviceSelectionDeleteButton")
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
}
