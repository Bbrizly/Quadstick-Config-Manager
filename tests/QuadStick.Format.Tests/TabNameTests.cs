using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// The community keeps a mode's name on the sheet tab (Menu, Gameplay) and
// leaves cell C1 as the template wrote it. Both this app and the device read
// C1, so a workbook of well named tabs came in as three modes all called
// "Left Joystick", and the user could not tell them apart in a list.
//
// A name is a label: the device reads C1 as the mode's name and nothing turns
// on it, so writing the tab title into C1 changes no binding. What it must
// never do is happen quietly, or overwrite a name the user chose.
public class TabNameTests
{
    static string[][] Mode(string c1, params string[] outputs)
    {
        var rows = new List<string[]>
        {
            new[] { "Profile Name", "", c1 },
            new[] { "mygame.csv" },
            new[] { "XBox Outputs", "Function", "usb" },
        };
        rows.AddRange(outputs.Select(o => new[] { o, "normal", "lip" }));
        return rows.ToArray();
    }

    static XlsxImport Import(params (string Tab, string[][] Rows)[] tabs)
    {
        using var wb = TestWorkbook.Build(tabs);
        return Xlsx.Import(wb);
    }

    static string[] Names(XlsxImport import) =>
        ProfileFile.Load(import.Csv).Document.Sheets.Select(s => s.ModeName).ToArray();

    [Fact]
    public void A_template_leftover_in_C1_gives_way_to_the_tab_name()
    {
        var import = Import(
            ("Menu", Mode("Left Joystick", "dpad_N")),
            ("Gameplay", Mode("Left Joystick", "dpad_S")));

        Assert.Equal(new[] { "Menu", "Gameplay" }, Names(import));
    }

    // A name the user typed is theirs. Only a name nobody chose is replaced.
    [Fact]
    public void A_name_the_user_chose_is_left_alone()
    {
        var import = Import(("Sheet1", Mode("Driving", "dpad_N")));

        Assert.Equal(new[] { "Driving" }, Names(import));
        Assert.Empty(import.Renamed);
    }

    [Fact]
    public void An_empty_C1_takes_the_tab_name()
    {
        var import = Import(("Flight", Mode("", "dpad_N")));

        Assert.Equal(new[] { "Flight" }, Names(import));
    }

    // Two tabs with one C1 is the copy-paste that names a whole workbook after
    // whichever mode was duplicated first.
    [Fact]
    public void Two_tabs_sharing_one_name_both_take_their_own()
    {
        var import = Import(
            ("Walking", Mode("Driving", "dpad_N")),
            ("Swimming", Mode("Driving", "dpad_S")));

        Assert.Equal(new[] { "Walking", "Swimming" }, Names(import));
    }

    // "Sheet2" is not a better name than "Left Joystick", it is a worse one.
    [Fact]
    public void A_tab_nobody_named_either_changes_nothing()
    {
        var import = Import(
            ("Sheet1", Mode("Left Joystick", "dpad_N")),
            ("Sheet2", Mode("Left Joystick", "dpad_S")));

        Assert.Equal(new[] { "Left Joystick", "Left Joystick" }, Names(import));
        Assert.Empty(import.Renamed);
    }

    // Never silently. The review has to be able to say which mode was named
    // from where, and what the cell had said.
    [Fact]
    public void Every_naming_is_reported_with_the_cell_it_replaced()
    {
        var import = Import(
            ("Menu", Mode("Left Joystick", "dpad_N")),
            ("Gameplay", Mode("Left Joystick", "dpad_S")));

        Assert.Equal(2, import.Renamed.Count);
        Assert.Equal(new[] { 1, 2 }, import.Renamed.Select(r => r.ModeNumber));
        Assert.Equal(new[] { "Menu", "Gameplay" }, import.Renamed.Select(r => r.TabName));
        Assert.All(import.Renamed, r => Assert.Equal("Left Joystick", r.CellC1));
    }

    // Preferences and Infrared have no name in C1 and are not modes. A tab
    // title must never land in one of their header rows.
    [Fact]
    public void Preferences_and_infrared_tabs_are_never_renamed()
    {
        var import = Import(
            ("Menu", Mode("Left Joystick", "dpad_N")),
            ("My prefs", new[] { new[] { "Preferences" }, new[] { "sip_puff_delay_soft", "130" } }),
            ("My IR", new[] { new[] { "Infrared" }, new[] { "ir_1", "power" } }));

        var csv = import.Csv;
        Assert.DoesNotContain("My prefs", csv);
        Assert.DoesNotContain("My IR", csv);
        Assert.Single(import.Renamed);
    }

    // A tab that did not import cannot lend its name to a mode that did.
    [Fact]
    public void A_skipped_tab_does_not_number_or_name_anything()
    {
        var import = Import(
            ("Reference Card", new[] { new[] { "Reference Card" }, new[] { "Tube:", "Left" } }),
            ("Menu", Mode("Left Joystick", "dpad_N")));

        Assert.Equal(new[] { "Menu" }, Names(import));
        Assert.Equal(1, Assert.Single(import.Renamed).ModeNumber);
    }

    // The rename lands in C1 and nowhere else: the bindings under it are the
    // user's and are not touched by naming a mode.
    [Fact]
    public void Naming_a_mode_changes_the_name_cell_and_nothing_under_it()
    {
        var import = Import(("Menu", Mode("Left Joystick", "dpad_N", "dpad_S")));
        var file = ProfileFile.Load(import.Csv);

        var sheet = Assert.Single(file.Document.Sheets);
        Assert.Equal("Menu", sheet.ModeName);
        Assert.Equal(new[] { "dpad_N", "dpad_S" }, sheet.Bindings.Select(b => b.Output));
        Assert.Equal("mygame.csv", file.Document.CsvFileName);
    }
}
