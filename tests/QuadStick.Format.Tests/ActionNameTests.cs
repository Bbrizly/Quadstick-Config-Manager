using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// Column L holds the profile's own name for a row's output. The device stops
// reading at column J, so these names have to survive every edit untouched and
// never be mistaken for data.
public class ActionNameTests
{
    // Two modes, both binding mouse_left, each naming it differently. The
    // second mode also carries a column-K note, so notes and names can be seen
    // to keep their own columns.
    static ProfileFile TwoNamedModes() => ProfileFile.Load(
        "Profile Name,,Combat\n" +
        "game.csv\n" +
        "Outputs,Function,usb,,,,,,,,,Action\n" +
        "mouse_left,normal,lip,,,,,,,,,Shoot\n" +
        "Profile Name,,Menus\n" +
        ",,\n" +
        "Outputs,Function,usb,,,,,,,,,Action\n" +
        "mouse_left,normal,lip,,,,,,,,pick note,Select\n");

    [Fact]
    public void Names_round_trip_and_never_become_bindings()
    {
        var text =
            "Profile Name,,Combat\n" +
            "game.csv\n" +
            "Outputs,Function,usb,,,,,,,,,Action\n" +
            "mouse_left,normal,lip,,,,,,,,my note,Shoot\n";
        var f = ProfileFile.Load(text);

        Assert.Equal(text.Replace("\n", "\r\n"), f.ToCsvText());
        var sheet = f.Document.Sheets[0];
        Assert.Single(sheet.Bindings);
        Assert.Equal("Shoot", sheet.Bindings[0].ActionName);
        Assert.Equal("mouse_left", sheet.Bindings[0].Output);
        // The name sits past the input columns, so it is not an input.
        Assert.Equal(new[] { "lip" }, sheet.Bindings[0].Inputs);
        Assert.DoesNotContain(f.Issues, i => i.Severity == Severity.Error);
    }

    [Fact]
    public void The_same_token_can_carry_a_different_name_in_each_mode()
    {
        var f = TwoNamedModes();
        Assert.Equal("Shoot", f.Document.Sheets[0].Bindings[0].ActionName);
        Assert.Equal("Select", f.Document.Sheets[1].Bindings[0].ActionName);
        Assert.Equal(new[] { "Shoot", "Select" }, f.ActionNames());
    }

    [Fact]
    public void Setting_the_output_and_the_name_is_one_undo_step()
    {
        var f = TwoNamedModes();
        int row = f.Document.Sheets[0].Bindings[0].Row;
        f.ClearUndo();

        Assert.True(f.SetOutput(row, "mouse_right", "Aim"));
        Assert.Equal("mouse_right", f.Document.Sheets[0].Bindings[0].Output);
        Assert.Equal("Aim", f.Document.Sheets[0].Bindings[0].ActionName);

        Assert.True(f.Undo());
        Assert.False(f.CanUndo);
        Assert.Equal("mouse_left", f.Document.Sheets[0].Bindings[0].Output);
        Assert.Equal("Shoot", f.Document.Sheets[0].Bindings[0].ActionName);
    }

    [Fact]
    public void Picking_a_plain_token_clears_the_name()
    {
        var f = TwoNamedModes();
        int row = f.Document.Sheets[0].Bindings[0].Row;

        Assert.True(f.SetOutput(row, "circle"));
        Assert.Equal("", f.Document.Sheets[0].Bindings[0].ActionName);
        Assert.Equal("", f.GetCell(row, ProfileFile.ActionColumn));
    }

    [Fact]
    public void A_name_that_reads_as_a_real_token_is_refused()
    {
        var f = TwoNamedModes();
        int row = f.Document.Sheets[0].Bindings[0].Row;

        Assert.False(f.SetOutput(row, "mouse_left", "circle"));
        Assert.False(f.SetOutput(row, "mouse_left", new string('x', 41)));
        Assert.Equal("Shoot", f.Document.Sheets[0].Bindings[0].ActionName);
    }

    [Fact]
    public void Naming_a_row_titles_the_column_on_the_sheets_label_row()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Combat\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "mouse_left,normal,lip\n");
        var sheet = f.Document.Sheets[0];

        Assert.True(f.SetOutput(sheet.Bindings[0].Row, "mouse_left", "Shoot"));
        Assert.Equal("Action", f.GetCell(sheet.StartRow + 2, ProfileFile.ActionColumn));
    }

    [Fact]
    public void Renaming_an_action_updates_every_row_using_it_in_one_step()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Combat\n" +
            "game.csv\n" +
            "Outputs,Function,usb,,,,,,,,,Action\n" +
            "mouse_left,normal,lip,,,,,,,,,Shoot\n" +
            "kb_r,normal,hard_puff,,,,,,,,,Shoot\n" +
            "circle,normal,left_sip,,,,,,,,,Jump\n");
        f.ClearUndo();

        Assert.True(f.RenameAction("Shoot", "Fire"));
        var names = f.Document.Sheets[0].Bindings.Select(b => b.ActionName).ToArray();
        Assert.Equal(new[] { "Fire", "Fire", "Jump" }, names);

        Assert.True(f.Undo());
        Assert.False(f.CanUndo);
        Assert.Equal(new[] { "Shoot", "Shoot", "Jump" },
            f.Document.Sheets[0].Bindings.Select(b => b.ActionName).ToArray());
    }

    [Fact]
    public void A_name_that_pushes_the_row_past_the_device_line_limit_is_flagged()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Combat\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "mouse_left,normal,lip,,,,,,,," + new string('n', 1000) + ",Shoot\n");

        Assert.Contains(f.Issues, i => i.Severity == Severity.Error && i.Message.Contains("1023"));
    }

    [Fact]
    public void The_64_character_cell_cap_does_not_apply_to_the_name_column()
    {
        // 40 is the app's own cap, but the device's 64-char keyword limit only
        // guards columns A..J, so a long name is never a device error.
        var f = ProfileFile.Load(
            "Profile Name,,Combat\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "mouse_left,normal,lip,,,,,,,," + new string('k', 70) + "," + new string('a', 70) + "\n");

        Assert.DoesNotContain(f.Issues, i => i.Severity == Severity.Error);
    }

    [Fact]
    public void A_name_travels_with_its_row_when_the_row_moves()
    {
        var f = TwoNamedModes();
        int first = f.Document.Sheets[0].Bindings[0].Row;
        int second = f.Document.Sheets[1].Bindings[0].Row;

        f.SwapRows(first, second);
        Assert.Equal("Select", f.Document.Sheets[0].Bindings[0].ActionName);
        Assert.Equal("Shoot", f.Document.Sheets[1].Bindings[0].ActionName);
    }

    [Fact]
    public void Editing_inputs_never_touches_the_name_or_the_note()
    {
        var f = TwoNamedModes();
        int row = f.Document.Sheets[1].Bindings[0].Row;

        f.RemoveInput(row, 0);
        Assert.Equal("pick note", f.GetCell(row, 10));
        Assert.Equal("Select", f.GetCell(row, ProfileFile.ActionColumn));

        // Healing a stray note out of an input column also leaves it alone.
        f.SetCell(row, 3, "typed a note here");
        f.MoveInputToNotes(row, 3);
        Assert.Equal("pick note; typed a note here", f.GetCell(row, 10));
        Assert.Equal("Select", f.GetCell(row, ProfileFile.ActionColumn));
    }
}
