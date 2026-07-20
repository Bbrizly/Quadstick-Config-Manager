using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

public class ModeEditTests
{
    // First sheet with a filename and a binding, then two more modes. The middle
    // mode carries a column-K comment so a move can be checked to take it along.
    static ProfileFile ThreeModes() => ProfileFile.Load(
        "Profile Name,,Driving\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "x,normal,lip\n" +
        "Profile Name,,Aiming\n" +
        ",,\n" +
        "Outputs,Function,usb\n" +
        "circle,normal,right_sip,,,,,,,,scope note\n" +
        "Profile Name,,Menus\n" +
        ",,\n" +
        "Outputs,Function,usb\n" +
        "square,normal,left_puff\n");

    [Fact]
    public void DuplicateMode_result_is_independent_of_the_original()
    {
        var f = ThreeModes();
        int idx = f.DuplicateMode(1, "Aiming Copy");
        Assert.Equal(f.Document.Sheets.Count - 1, idx);
        Assert.Equal("Aiming Copy", f.Document.Sheets[idx].ModeName);

        // Editing a cell in the copy must leave the original untouched.
        var copyBindingRow = f.Document.Sheets[idx].Bindings[0].Row;
        f.SetCell(copyBindingRow, 0, "triangle");
        Assert.Equal("triangle", f.Document.Sheets[idx].Bindings[0].Output);
        Assert.Equal("circle", f.Document.Sheets[1].Bindings[0].Output);
    }

    [Fact]
    public void Duplicating_the_first_sheet_leaves_the_profile_filename_unchanged()
    {
        var f = ThreeModes();
        Assert.Equal("game.csv", f.Document.CsvFileName);
        f.DuplicateMode(0, "Driving Copy");
        Assert.Equal("game.csv", f.Document.CsvFileName);
    }

    [Fact]
    public void MoveMode_down_then_up_restores_identical_text()
    {
        var f = ThreeModes();
        var before = f.ToCsvText();
        Assert.True(f.MoveMode(1, 1));
        Assert.NotEqual(before, f.ToCsvText());
        Assert.True(f.MoveMode(2, -1));
        Assert.Equal(before, f.ToCsvText());
    }

    [Fact]
    public void Guards_reject_bad_operations()
    {
        var f = ThreeModes();
        Assert.False(f.RenameMode(99, "Nope"));    // rename out of range
        Assert.False(f.DeleteMode(0));             // sheet 0 holds the filename
        Assert.False(f.MoveMode(0, -1));           // nothing above the first sheet
        Assert.False(f.MoveMode(2, 1));            // last sheet cannot move down
        Assert.Equal(-1, f.DuplicateMode(0, "  ")); // blank name

        // A profile with a single mode (here after a Preferences sheet) refuses
        // to delete that last mode.
        var one = ProfileFile.Load(
            "Preferences,,\n" +
            ",,\n" +
            "Preference,Value,Units\n" +
            "mouse_speed,201\n" +
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip\n");
        Assert.Equal(SheetType.ProfileName, one.Document.Sheets[1].Type);
        Assert.False(one.DeleteMode(1)); // one mode remains, cannot delete it
    }

    [Fact]
    public void Undo_after_DeleteMode_restores_the_previous_text()
    {
        var f = ThreeModes();
        var before = f.ToCsvText();
        Assert.True(f.DeleteMode(1));
        Assert.NotEqual(before, f.ToCsvText());
        Assert.True(f.Undo());
        Assert.Equal(before, f.ToCsvText());
    }

    [Fact]
    public void RenameMode_shows_in_the_parsed_sheet_name()
    {
        var f = ThreeModes();
        Assert.True(f.RenameMode(1, "Combat"));
        Assert.Equal("Combat", f.Document.Sheets[1].ModeName);
        // Renaming to the same name is a no-op that adds no undo entry.
        Assert.False(f.RenameMode(1, "Combat"));
    }

    [Fact]
    public void Moving_the_first_mode_keeps_the_filename_on_the_file()
    {
        var f = ThreeModes();
        var before = f.ToCsvText();

        Assert.True(f.MoveMode(0, 1));
        Assert.Equal("Aiming", f.Document.Sheets[0].ModeName);
        Assert.Equal("Driving", f.Document.Sheets[1].ModeName);
        Assert.Equal("game.csv", f.Document.CsvFileName); // moved to the new first sheet

        // Moving back restores the exact original text, filename included.
        Assert.True(f.MoveMode(1, -1));
        Assert.Equal(before, f.ToCsvText());
    }

    [Fact]
    public void MoveRow_drops_a_row_onto_another_rows_place()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "circle,normal,right_sip\n" +
            "square,normal,left_puff\n");
        var before = f.ToCsvText();

        f.MoveRow(4, 6); // drag the first row onto the last
        Assert.Equal(new[] { "circle", "square", "x" },
            f.Document.Sheets[0].Bindings.Select(b => b.Output).ToArray());

        f.MoveRow(6, 4); // drag it back
        Assert.Equal(before, f.ToCsvText());
    }

    [Fact]
    public void MoveRows_drags_a_selection_as_one_block()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "circle,normal,right_sip\n" +
            "square,normal,left_puff\n" +
            "triangle,normal,hard_puff\n");
        var before = f.ToCsvText();

        // A non-contiguous pair dropped on the last row keeps its order.
        f.MoveRows(new[] { 4, 6 }, 7);
        Assert.Equal(new[] { "circle", "triangle", "x", "square" },
            f.Document.Sheets[0].Bindings.Select(b => b.Output).ToArray());

        Assert.True(f.Undo()); // one step undoes the whole move
        Assert.Equal(before, f.ToCsvText());

        // Dropping onto a row that is itself selected does nothing.
        f.MoveRows(new[] { 4, 5 }, 5);
        Assert.Equal(before, f.ToCsvText());
    }

    [Fact]
    public void DeleteRows_takes_several_rows_in_one_undo_step()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "circle,normal,right_sip\n" +
            "square,normal,left_puff\n");
        var before = f.ToCsvText();

        f.DeleteRows(new[] { 4, 6 });
        Assert.Equal(new[] { "circle" },
            f.Document.Sheets[0].Bindings.Select(b => b.Output).ToArray());

        Assert.True(f.Undo()); // one step brings the whole selection back
        Assert.Equal(before, f.ToCsvText());
    }

    [Fact]
    public void Preferences_sheet_deletes_and_comes_back()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "Preferences\n" +
            "\n" +
            "Preference,Value,Units\n" +
            "mouse_speed,201\n");
        Assert.Equal(SheetType.Preferences, f.Document.Sheets[1].Type);

        Assert.True(f.DeleteMode(1));
        Assert.Single(f.Document.Sheets);

        int idx = f.AddPreferencesSheet();
        Assert.Equal(1, idx);
        Assert.Equal(SheetType.Preferences, f.Document.Sheets[1].Type);
        Assert.Equal(-1, f.AddPreferencesSheet()); // the device reads only one
    }

    [Fact]
    public void Column_K_comment_travels_with_a_moved_mode()
    {
        var f = ThreeModes();
        var row = f.Document.Sheets[1].Bindings[0].Row;
        Assert.Equal("scope note", f.GetCell(row, 10));

        Assert.True(f.MoveMode(1, 1)); // move the Aiming mode down
        // The comment stayed on its row inside the moved block.
        var moved = f.Document.Sheets[2].Bindings[0];
        Assert.Equal("right_sip", moved.Inputs[0]);
        Assert.Equal("scope note", f.GetCell(moved.Row, 10));
    }
}
