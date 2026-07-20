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
}
