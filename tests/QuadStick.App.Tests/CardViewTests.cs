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

    // The Press dropdown was one ~380-item scroll. Its dropdown must hold a
    // search box on top and category expanders under it, and typing in the
    // search replaces the categories with flat matches. The field itself
    // never expands; everything lives in the flyout.
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

        // Browse: Controller > Buttons > Triangle.
        var panel = Open();
        var cats = panel.GetVisualDescendants().OfType<Expander>()
            .Select(e => e.Header as string).ToList();
        Assert.Contains("Controller", cats);
        Assert.Contains("Keyboard", cats);
        Assert.Contains("Mouse", cats);

        var controller = panel.GetVisualDescendants().OfType<Expander>()
            .First(e => (e.Header as string) == "Controller");
        controller.IsExpanded = true;
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();
        var buttons = controller.GetVisualDescendants().OfType<Expander>()
            .First(e => (e.Header as string) == "Buttons");
        buttons.IsExpanded = true;
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();
        buttons.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "") == "Triangle")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();
        Assert.Equal("triangle", file.Document.Sheets[0].Bindings[0].Output);

        // Search: typing hides the categories and goes straight to matches.
        panel = Open();
        var search = panel.GetVisualDescendants().OfType<TextBox>()
            .First(t => (AutomationProperties.GetName(t) ?? "") == "Search all outputs");
        search.Text = "squar";
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();
        Assert.Empty(panel.GetVisualDescendants().OfType<Expander>());
        panel.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "") == "Square")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("square", file.Document.Sheets[0].Bindings[0].Output);

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
