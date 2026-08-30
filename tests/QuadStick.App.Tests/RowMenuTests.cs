using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// The three things a tester rebuilt a whole profile without: a right-click
// menu on a row, a way to copy rows into another mode, and a mark on a value
// that is already used somewhere else in the mode. They had all three in the
// Google Sheets extension and did the work by hand here instead.
public class RowMenuTests
{
    const string TwoModes =
        "Profile Name,,Walking\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "x,normal,lip\n" +
        "circle,normal,right_sip\n" +
        "\n" +
        "Profile Name,,Shooting\n" +
        "\n" +
        "Outputs,Function,usb\n" +
        "square,normal,left_puff\n";

    static MainWindow Open(string csv, out ProfileFile file, bool cards = false)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.InterfaceScalePercent = 100;
        // Pinned, because the settings file outlives a test and which model is
        // picked decides which parts Device View has at all.
        s.Model = "FPS";
        // The sentence cards are the shape that carries a drag handle, and the
        // handle is what the menu hangs beside.
        s.DeviceCards = cards;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        file = ProfileFile.Load(csv);
        file.Dirty = false; // else Close waits forever on the save dialog
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        // The toolbar settles a pass late; without this every click lands a
        // row low.
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    // n is the nth binding on the open mode; three header lines sit above the
    // first one, so its sheet row is n + 3.
    static Border Handle(MainWindow w, int n) => w.GetVisualDescendants().OfType<Border>()
        .First(x => (AutomationProperties.GetName(x) ?? "").StartsWith($"Row {n + 3},")
                 || (AutomationProperties.GetName(x) ?? "").StartsWith($"Row {n + 3}."));

    static void Click(MainWindow w, int n, RawInputModifiers mods = RawInputModifiers.None)
    {
        var pt = Handle(w, n).TranslatePoint(new Point(3, 3), w)!.Value;
        w.MouseDown(pt, MouseButton.Left, mods);
        w.MouseUp(pt, MouseButton.Left, mods);
    }

    // The menu hangs on the row grid the handle sits in.
    static MenuFlyout Menu(MainWindow w, int n) => (MenuFlyout)Handle(w, n)
        .GetVisualAncestors().OfType<Control>()
        .First(c => c.ContextFlyout is MenuFlyout).ContextFlyout!;

    static MenuFlyout OpenMenu(MainWindow w, int n)
    {
        var menu = Menu(w, n);
        menu.ShowAt(Handle(w, n));
        Dispatcher.UIThread.RunJobs();
        return menu;
    }

    static string[] Headers(ItemCollection items) =>
        items.OfType<MenuItem>().Select(m => (string)m.Header!).ToArray();

    [AvaloniaFact]
    public void The_row_menu_offers_copy_move_and_delete()
    {
        var w = Open(TwoModes, out var file);
        Click(w, 1);
        var menu = OpenMenu(w, 1);

        Assert.Equal(new[] { "Copy to mode", "Move", "Delete" }, Headers(menu.Items));

        var copy = menu.Items.OfType<MenuItem>().First();
        // Every mode but the one on screen, numbered the way the device counts
        // them, because two modes may legally share a name.
        Assert.Equal(new[] { "2: Shooting" }, Headers(copy.Items));

        var move = menu.Items.OfType<MenuItem>().ElementAt(1);
        Assert.Equal(2, move.Items.Count);

        menu.Hide();
        file.Dirty = false;
        w.Close();
    }

    // Right-clicking a row nothing has selected acts on that row, the way a
    // file explorer does. Without this the menu silently worked on whatever
    // was selected last, somewhere else in the list.
    [AvaloniaFact]
    public void Opening_the_menu_on_an_unselected_row_takes_it()
    {
        var w = Open(TwoModes, out var file);
        Assert.DoesNotContain("selected", AutomationProperties.GetName(Handle(w, 2))!);

        var menu = OpenMenu(w, 2);
        Assert.Contains("selected", AutomationProperties.GetName(Handle(w, 2))!);
        Assert.DoesNotContain("selected", AutomationProperties.GetName(Handle(w, 1))!);

        menu.Hide();
        file.Dirty = false;
        w.Close();
    }

    // A selection of several rows is what the menu is for: the tester copied
    // blocks of rows between modes in the spreadsheet and had to retype them
    // here.
    [AvaloniaFact]
    public void Copying_the_selection_lands_the_whole_block_in_the_other_mode()
    {
        var w = Open(TwoModes, out var file);
        Click(w, 1);
        Click(w, 2, RawInputModifiers.Shift);

        var menu = OpenMenu(w, 1);
        var toShooting = menu.Items.OfType<MenuItem>().First().Items.OfType<MenuItem>().Single();
        menu.Hide();
        toShooting.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        // The mode it came from is untouched; the mode it went to gained both,
        // in order, after what was already there.
        Assert.Equal(new[] { "x", "circle" },
            file.Document.Sheets[0].Bindings.Select(b => b.Output).ToArray());
        Assert.Equal(new[] { "square", "x", "circle" },
            file.Document.Sheets[1].Bindings.Select(b => b.Output).ToArray());

        // And it says so: a copy into a mode that is not on screen is invisible
        // otherwise.
        Assert.Contains(w.GetVisualDescendants().OfType<TextBlock>(),
            t => (t.Text ?? "").Contains("2 rows copied to 2: Shooting"));

        file.Dirty = false;
        w.Close();
    }

    // Nothing to copy into is a dead entry, not a missing one: a menu that
    // changes shape between profiles teaches nobody where anything is.
    [AvaloniaFact]
    public void A_one_mode_profile_has_nowhere_to_copy_to()
    {
        var w = Open("Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\nx,normal,lip\n", out var file);
        Click(w, 1);
        var menu = OpenMenu(w, 1);

        var copy = menu.Items.OfType<MenuItem>().First();
        Assert.Empty(copy.Items);
        Assert.False(copy.IsEnabled);

        menu.Hide();
        file.Dirty = false;
        w.Close();
    }

    // Device View is the view the app opens in, so a gesture that only works
    // in the list is half a feature. The cards carry the same handles and the
    // same selection, so they carry the same menu.
    [AvaloniaFact]
    public void A_mapping_card_has_the_same_menu()
    {
        var w = Open(
            "Profile Name,,Walking\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,mp_center_sip\n" +
            "\n" +
            "Profile Name,,Shooting\n" +
            "\n" +
            "Outputs,Function,usb\n" +
            "square,normal,left_puff\n", out var file, cards: true);
        w.SetDeviceViewForPreview(true);
        w.SelectZoneForPreview("mp_center");
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        var handle = w.GetVisualDescendants().OfType<Border>()
            .First(x => (AutomationProperties.GetName(x) ?? "").StartsWith("Mapping 1"));
        var menu = (MenuFlyout)handle.GetVisualAncestors().OfType<Control>()
            .First(c => c.ContextFlyout is MenuFlyout).ContextFlyout!;
        menu.ShowAt(handle);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "Copy to mode", "Move", "Delete" }, Headers(menu.Items));
        Assert.Equal(new[] { "2: Shooting" },
            Headers(menu.Items.OfType<MenuItem>().First().Items));

        menu.Hide();
        file.Dirty = false;
        w.Close();
    }
}
