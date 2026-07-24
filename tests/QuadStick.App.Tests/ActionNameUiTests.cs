using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// A profile can give its own name to an output. The name lives in column L,
// which the device never reads, so the editor has to show the name while the
// file keeps the real token.
public class ActionNameUiTests
{
    // One row already named Shoot, one row still plain, both on the lip switch.
    static ProfileFile NamedAndPlain() => ProfileFile.Load(
        "Profile Name,,Solo\n" +
        "game.csv\n" +
        "Outputs,Function,usb,,,,,,,,,Action\n" +
        "mouse_left,normal,lip,,,,,,,,,Shoot\n" +
        "circle,normal,hard_lip\n");

    static MainWindow OpenOnLip(ProfileFile file, bool cards = false)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.DeviceCards = cards;
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

    // List View names its picker by row, so either row can be aimed at
    // exactly. It is also the path where a pick writes two cells at once.
    static MainWindow OpenList(ProfileFile file)
    {
        var w = OpenOnLip(file);
        w.SetDeviceViewForPreview(false);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    static Control OpenPicker(MainWindow w, string namePrefix)
    {
        var press = w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith(namePrefix));
        press.Flyout!.ShowAt(press);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return (Control)((Flyout)press.Flyout!).Content!;
    }

    static void Tap(MainWindow w, Control panel, string namePrefix)
    {
        panel.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith(namePrefix))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
    }

    // The picker's first section is the profile's own names, and picking one
    // writes the token AND the name, so the device still gets mouse_left.
    [AvaloniaFact]
    public void Picking_a_game_name_writes_the_token_and_the_name()
    {
        var file = NamedAndPlain();
        var w = OpenList(file);
        int plainRow = file.Document.Sheets[0].Bindings[1].Row;

        var panel = OpenPicker(w, $"Output for row {plainRow}.");
        Assert.NotNull(panel.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Game,")));

        Tap(w, panel, "Game,");
        Tap(w, panel, "Shoot");

        Assert.Equal("mouse_left", file.GetCell(plainRow, 0));
        Assert.Equal("Shoot", file.GetCell(plainRow, ProfileFile.ActionColumn));
        Assert.Equal("Shoot", file.Document.Sheets[0].Bindings[1].ActionName);
    }

    // The whole point: the row reads by its name, and the raw label style
    // still shows the truth in the file.
    [AvaloniaFact]
    public void A_named_row_reads_by_its_name_except_in_the_raw_style()
    {
        var file = NamedAndPlain();
        var w = OpenOnLip(file, cards: true);

        string Card1() => AutomationProperties.GetName(w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Mapping 1:"))) ?? "";

        Assert.Contains("Shoot", Card1());

        // Styles cycle friendly -> Xbox -> raw.
        w.CycleLabelStyleForPreview();
        w.CycleLabelStyleForPreview();
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        var raw = Card1();
        Assert.Contains("mouse_left", raw);
        Assert.DoesNotContain("Shoot", raw);
    }

    // A name describes an output. Move the row to a different plain token and
    // the name has to go with it, in the same undoable change.
    [AvaloniaFact]
    public void Picking_a_plain_token_clears_the_name()
    {
        var file = NamedAndPlain();
        var w = OpenList(file);
        int namedRow = file.Document.Sheets[0].Bindings[0].Row;

        var panel = OpenPicker(w, $"Output for row {namedRow}.");
        Tap(w, panel, "Controller,");
        Tap(w, panel, "Buttons,");
        Tap(w, panel, "triangle");

        Assert.Equal("triangle", file.GetCell(namedRow, 0));
        Assert.Equal("", file.GetCell(namedRow, ProfileFile.ActionColumn));
    }

    // The catalog is per profile, so an empty profile shows no Game section
    // and every token still lands where it always did.
    [Fact]
    public void A_profile_with_no_names_gets_the_plain_catalog()
    {
        var file = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "circle,normal,lip\n");
        var outputs = OutputCatalog.ForProfile(file, new[] { "circle", "x" });

        Assert.DoesNotContain("Game", outputs.Catalog.CategoryOrder);
        Assert.Equal(("circle", ""), outputs.Resolve("circle"));
    }

    [Fact]
    public void A_named_profile_lists_its_names_first_and_resolves_them()
    {
        var file = NamedAndPlain();
        var outputs = OutputCatalog.ForProfile(file, new[] { "circle", "mouse_left" });

        Assert.Equal("Game", outputs.Catalog.CategoryOrder[0]);
        Assert.Equal(("Game", ""), outputs.Catalog.Classify("Shoot"));
        Assert.Equal(("mouse_left", "Shoot"), outputs.Resolve("Shoot"));
        Assert.Equal(("circle", ""), outputs.Resolve("circle"));
        Assert.Equal("Shoot", outputs.Options[0]);
    }

    // Renaming in the names window rewrites every row using that name.
    [AvaloniaFact]
    public void The_names_window_renames_an_action_everywhere()
    {
        var file = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb,,,,,,,,,Action\n" +
            "mouse_left,normal,lip,,,,,,,,,Shoot\n" +
            "mouse_left,turbo,hard_lip,,,,,,,,,Shoot\n");
        var w = OpenOnLip(file);

        var win = new ActionsWindow(w);
        win.Show();
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();

        var box = win.GetVisualDescendants().OfType<TextBox>()
            .First(t => (AutomationProperties.GetName(t) ?? "").StartsWith("Name for mouse_left"));
        box.Text = "Fire";
        box.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "Fire", "Fire" },
            file.Document.Sheets[0].Bindings.Select(b => b.ActionName).ToArray());
        win.Close();
    }
}
