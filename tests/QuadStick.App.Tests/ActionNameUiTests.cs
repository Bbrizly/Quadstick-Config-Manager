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
        Ui.Click(panel.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith(namePrefix)));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
    }

    // The picker's first section is the profile's own names, and picking one
    // writes the token AND the name, so the device still gets mouse_left.
    [AvaloniaFact]
    public void Picking_a_custom_name_writes_the_token_and_the_name()
    {
        var file = NamedAndPlain();
        var w = OpenList(file);
        int plainRow = file.Document.Sheets[0].Bindings[1].Row;

        var panel = OpenPicker(w, $"Output for row {plainRow}.");
        Assert.NotNull(panel.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Custom,")));

        Tap(w, panel, "Custom,");
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

    // The catalog is per profile, so an empty table shows no Custom section
    // and every token still lands where it always did.
    [Fact]
    public void A_profile_with_no_names_gets_the_plain_catalog()
    {
        var outputs = OutputCatalog.ForProfile(
            Array.Empty<(string, string)>(), new[] { "circle", "x" });

        Assert.DoesNotContain("Custom", outputs.Catalog.CategoryOrder);
        Assert.Equal(("circle", ""), outputs.Resolve("circle"));
    }

    [Fact]
    public void A_named_profile_lists_its_names_first_and_resolves_them()
    {
        var outputs = OutputCatalog.ForProfile(
            new[] { ("Shoot", "mouse_left") }, new[] { "circle", "mouse_left" });

        Assert.Equal("Custom", outputs.Catalog.CategoryOrder[0]);
        Assert.Equal(("Custom", ""), outputs.Catalog.Classify("Shoot"));
        Assert.Equal(("mouse_left", "Shoot"), outputs.Resolve("Shoot"));
        Assert.Equal(("circle", ""), outputs.Resolve("circle"));
        Assert.Equal("Shoot", outputs.Options[0]);
    }

    // Names first, outputs after: a name with no output yet is still in the
    // list, so you can lay out what you want the profile to do before you
    // decide which button does it.
    [Fact]
    public void A_name_with_no_output_yet_is_still_offered()
    {
        var outputs = OutputCatalog.ForProfile(
            new[] { ("Shoot", "mouse_left"), ("Reload", "") }, new[] { "circle" });

        Assert.Equal(new[] { "Shoot", "Reload", "circle" }, outputs.Options.ToArray());
        Assert.Equal(("Custom", ""), outputs.Catalog.Classify("Reload"));
        // Picking it names the row and leaves the output blank, which the
        // problems list flags.
        Assert.Equal(("", "Reload"), outputs.Resolve("Reload"));
    }

    // ---- The table itself ----

    static MainWindow OpenTable(ProfileFile file)
    {
        var w = OpenOnLip(file);
        w.SelectCustomNamesForPreview();
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    static TextBox NameBox(MainWindow w, string current) =>
        w.GetVisualDescendants().OfType<TextBox>()
            .First(t => (t.Text ?? "") == current
                     && (AutomationProperties.GetName(t) ?? "").StartsWith("Your name for"));

    // The table lists a name already on a row, without that name being stored
    // anywhere but the row itself.
    [AvaloniaFact]
    public void The_table_lists_the_names_already_on_rows()
    {
        var w = OpenTable(NamedAndPlain());
        Assert.Equal(new[] { ("Shoot", "mouse_left") }, w.CustomNameRows().ToArray());
        Assert.NotNull(NameBox(w, "Shoot"));
    }

    // Renaming in the table rewrites every row using that name.
    [AvaloniaFact]
    public void Renaming_in_the_table_renames_every_row()
    {
        var file = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb,,,,,,,,,Action\n" +
            "mouse_left,normal,lip,,,,,,,,,Shoot\n" +
            "mouse_left,turbo,hard_lip,,,,,,,,,Shoot\n");
        var w = OpenTable(file);

        var box = NameBox(w, "Shoot");
        box.Text = "Fire";
        box.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "Fire", "Fire" },
            file.Document.Sheets[0].Bindings.Select(b => b.ActionName).ToArray());
    }

    // The picker shows `triangle` as "Triangle", so a name spelled that way
    // would sit in the same list twice meaning two things. Refused, and the
    // box goes back rather than leaving the user guessing.
    [AvaloniaFact]
    public void A_name_that_reads_as_a_real_button_is_refused()
    {
        var file = NamedAndPlain();
        var w = OpenTable(file);

        var box = NameBox(w, "Shoot");
        box.Text = "Triangle";
        box.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        Assert.Equal("Shoot", file.Document.Sheets[0].Bindings[0].ActionName);
        Assert.NotNull(NameBox(w, "Shoot"));
    }

    // Fixing only the capitalization is a real edit, not a clash with itself.
    [AvaloniaFact]
    public void Re_spelling_a_name_in_another_case_is_allowed()
    {
        var file = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb,,,,,,,,,Action\n" +
            "mouse_left,normal,lip,,,,,,,,,shoot\n" +
            "mouse_left,turbo,hard_lip,,,,,,,,,Shoot\n");
        var w = OpenTable(file);

        // Both rows read as one name, so the table has one line for them.
        Assert.Single(w.CustomNameRows());

        var box = NameBox(w, "shoot");
        box.Text = "Shoot";
        box.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "Shoot", "Shoot" },
            file.Document.Sheets[0].Bindings.Select(b => b.ActionName).ToArray());
    }

    // Changing the output in the table moves every row carrying the name, so
    // the file still holds one real token per row.
    [AvaloniaFact]
    public void Changing_the_output_in_the_table_moves_every_row()
    {
        var file = NamedAndPlain();
        var w = OpenTable(file);
        int namedRow = file.Document.Sheets[0].Bindings[0].Row;

        var panel = OpenPicker(w, "Output that Shoot stands for");
        Tap(w, panel, "Controller,");
        Tap(w, panel, "Buttons,");
        Tap(w, panel, "Triangle");

        Assert.Equal("triangle", file.GetCell(namedRow, 0));
        Assert.Equal("Shoot", file.GetCell(namedRow, ProfileFile.ActionColumn));
    }

    // Deleting a name leaves the mapping alone: it keeps its output and goes
    // back to showing the real token.
    [AvaloniaFact]
    public void Removing_a_name_leaves_the_mapping_on_its_real_output()
    {
        var file = NamedAndPlain();
        var w = OpenTable(file);
        int namedRow = file.Document.Sheets[0].Bindings[0].Row;

        Ui.Click(w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "") == "Remove the name Shoot"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("mouse_left", file.GetCell(namedRow, 0));
        Assert.Equal("", file.GetCell(namedRow, ProfileFile.ActionColumn));
        Assert.Empty(w.CustomNameRows());
    }

    // The point of the table: define a name with no mapping yet, then pick it
    // from a mapping's output list.
    [AvaloniaFact]
    public void A_name_defined_in_the_table_can_be_picked_from_a_mapping()
    {
        var file = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "circle,normal,lip\n");
        var w = OpenTable(file);

        w.AddRowForPreview();
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        var panel = OpenPicker(w, "Output that New name stands for");
        Tap(w, panel, "Mouse,");
        Tap(w, panel, "Mouse left");

        var box = NameBox(w, "New name");
        box.Text = "Shoot";
        box.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { ("Shoot", "mouse_left") }, w.CustomNameRows().ToArray());

        // Nothing is written to the profile until a mapping uses the name.
        Assert.Equal("", file.GetCell(file.Document.Sheets[0].Bindings[0].Row, ProfileFile.ActionColumn));

        w.SelectSheetForPreview(0);
        w.SetDeviceViewForPreview(false);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        int row = file.Document.Sheets[0].Bindings[0].Row;
        var outPicker = OpenPicker(w, $"Output for row {row}.");
        Tap(w, outPicker, "Custom,");
        Tap(w, outPicker, "Shoot");

        Assert.Equal("mouse_left", file.GetCell(row, 0));
        Assert.Equal("Shoot", file.GetCell(row, ProfileFile.ActionColumn));
    }

    // Naming an output you have already picked is the easy way round: type the
    // name first, then hang it on a row, and the row's own button becomes what
    // the name stands for. Nothing is emptied and nothing is left to fix.
    [AvaloniaFact]
    public void A_name_with_no_output_takes_the_one_the_row_already_had()
    {
        var file = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "circle,normal,lip\n");
        var w = OpenTable(file);

        w.AddRowForPreview();
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        var box = NameBox(w, "New name");
        box.Text = "Punch";
        box.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { ("Punch", "") }, w.CustomNameRows().ToArray());

        w.SelectSheetForPreview(0);
        w.SetDeviceViewForPreview(false);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        int row = file.Document.Sheets[0].Bindings[0].Row;
        var picker = OpenPicker(w, $"Output for row {row}.");
        Tap(w, picker, "Custom,");
        Tap(w, picker, "Punch");

        Assert.Equal("circle", file.GetCell(row, 0));
        Assert.Equal("Punch", file.GetCell(row, ProfileFile.ActionColumn));
        Assert.Equal(new[] { ("Punch", "circle") }, w.CustomNameRows().ToArray());
        Assert.DoesNotContain(file.Issues, i => i.Severity == Severity.Error);
    }
}
