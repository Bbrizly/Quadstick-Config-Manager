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

    // The picker shows a token by its friendly label: "triangle" reads as
    // "Triangle" and "mouse_left" as "Mouse left". So the refusal has to be
    // loose about case and underscores, or the list holds two entries that
    // read identically and mean different things.
    [Theory]
    [InlineData("circle")]
    [InlineData("Circle")]
    [InlineData("CIRCLE")]
    [InlineData("Triangle")]
    [InlineData("Mouse left")]
    [InlineData("Kb_R")]
    public void A_name_that_reads_as_a_real_output_is_refused_whatever_its_case(string name)
        => Assert.False(ProfileFile.IsLegalActionName(name));

    [Theory]
    [InlineData("Shoot")]
    [InlineData("shoot")]
    [InlineData("Sprint left")]
    public void A_name_that_reads_as_nothing_on_the_device_is_allowed(string name)
        => Assert.True(ProfileFile.IsLegalActionName(name));

    // One name means one output, and "Shoot" and "shoot" are one name to a
    // reader. Every row carrying it has to move together.
    [Fact]
    public void A_name_is_one_name_whatever_its_case()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Combat\n" +
            "game.csv\n" +
            "Outputs,Function,usb,,,,,,,,,Action\n" +
            "mouse_left,normal,lip,,,,,,,,,Shoot\n" +
            "mouse_left,turbo,hard_puff,,,,,,,,,shoot\n");

        Assert.Equal(new[] { "Shoot" }, f.ActionNames().ToArray());
        Assert.True(f.RenameAction("Shoot", "Fire"));
        Assert.Equal(new[] { "Fire", "Fire" },
            f.Document.Sheets[0].Bindings.Select(b => b.ActionName).ToArray());
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

    // The names table gives one name one output, so changing the output there
    // has to move every row carrying the name, not just the first.
    [Fact]
    public void Retargeting_an_action_moves_every_row_using_it_in_one_step()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Combat\n" +
            "game.csv\n" +
            "Outputs,Function,usb,,,,,,,,,Action\n" +
            "mouse_left,normal,lip,,,,,,,,,Shoot\n" +
            "mouse_left,turbo,hard_puff,,,,,,,,,Shoot\n" +
            "circle,normal,left_sip,,,,,,,,,Jump\n");
        f.ClearUndo();

        Assert.True(f.RetargetAction("Shoot", "kb_r"));
        Assert.Equal(new[] { "kb_r", "kb_r", "circle" },
            f.Document.Sheets[0].Bindings.Select(b => b.Output).ToArray());
        Assert.Equal(new[] { "Shoot", "Shoot", "Jump" },
            f.Document.Sheets[0].Bindings.Select(b => b.ActionName).ToArray());

        Assert.True(f.Undo());
        Assert.False(f.CanUndo);
        Assert.Equal("mouse_left", f.Document.Sheets[0].Bindings[0].Output);
    }

    // Deleting a name from the table must not touch what the row does. The
    // mapping keeps its output and goes back to showing the real token.
    [Fact]
    public void Clearing_an_action_leaves_every_row_on_its_output()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Combat\n" +
            "game.csv\n" +
            "Outputs,Function,usb,,,,,,,,,Action\n" +
            "mouse_left,normal,lip,,,,,,,,,Shoot\n" +
            "circle,normal,left_sip,,,,,,,,,Jump\n");
        f.ClearUndo();

        Assert.True(f.ClearAction("Shoot"));
        Assert.Equal(new[] { "mouse_left", "circle" },
            f.Document.Sheets[0].Bindings.Select(b => b.Output).ToArray());
        Assert.Equal(new[] { "", "Jump" },
            f.Document.Sheets[0].Bindings.Select(b => b.ActionName).ToArray());
        Assert.False(f.ClearAction("Shoot")); // gone, so nothing left to undo

        Assert.True(f.Undo());
        Assert.False(f.CanUndo);
        Assert.Equal("Shoot", f.Document.Sheets[0].Bindings[0].ActionName);
    }

    // Picking a name whose button is not set yet is still an error, the row
    // does nothing on the device. But the row HAS been told what it does, so
    // the message names the missing piece instead of asking for a pick the
    // user already made.
    [Fact]
    public void A_row_named_after_an_output_that_is_not_set_yet_says_so()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Combat\n" +
            "game.csv\n" +
            "Outputs,Function,usb,,,,,,,,,Action\n" +
            ",normal,lip,,,,,,,,,Punch\n");

        var issue = Assert.Single(f.Issues, i => i.Cell == "A4");
        Assert.Equal(Severity.Warning, issue.Severity);
        Assert.Contains("Punch", issue.Message);
        Assert.Contains("Custom output names", issue.Fix);
        Assert.DoesNotContain("has no output name", issue.Message);
    }

    [Fact]
    public void A_row_with_no_output_and_no_name_still_asks_for_an_output()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Combat\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            ",normal,lip\n");

        var issue = Assert.Single(f.Issues, i => i.Cell == "A4");
        Assert.Contains("has no output name", issue.Message);
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

    // The real case this exists for: a profile that works on the device and
    // carries "aim" in an input column, where the user meant it as their own
    // word for the row, not as an input the QuadStick has ever heard of.
    static ProfileFile StrayWordInAnInputColumn() => ProfileFile.Load(
        "Profile Name,,Left joy\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "left_trigger,normal,mp_center_sip,,,,,aim\n");

    [Fact]
    public void A_stray_word_can_become_the_rows_name_instead_of_being_dropped()
    {
        var f = StrayWordInAnInputColumn();

        Assert.True(f.CanMoveInputToActionName(4, 7));
        Assert.True(f.MoveInputToActionName(4, 7));

        Assert.Equal("aim", f.GetCell(4, ProfileFile.ActionColumn));
        Assert.Equal("", f.GetCell(4, 7));
        var b = f.Document.Sheets[0].Bindings[0];
        Assert.Equal("aim", b.ActionName);
        Assert.Equal("left_trigger", b.Output);
        Assert.Equal(new[] { "mp_center_sip" }, b.Inputs);
        // The column gets its title so a shared sheet reads properly.
        Assert.Equal("Action", f.GetCell(3, ProfileFile.ActionColumn));
        Assert.True(f.Undo());
        Assert.Equal("aim", f.GetCell(4, 7));
    }

    // A word that is really an output would show up twice in the picker, and
    // Preferences rows have no names at all: column L there is a note.
    [Fact]
    public void A_word_that_cannot_be_a_name_is_refused_not_mangled()
    {
        var f = StrayWordInAnInputColumn();
        f.SetCell(4, 7, "triangle");
        Assert.False(f.CanMoveInputToActionName(4, 7));
        Assert.False(f.MoveInputToActionName(4, 7));
        Assert.Equal("triangle", f.GetCell(4, 7));

        var prefs = ProfileFile.Load("Preferences\nprefs.csv\nName,Value\nvolume,5,,,,,aim\n");
        Assert.False(prefs.CanMoveInputToActionName(4, 6));
    }

    [Fact]
    public void A_name_already_in_the_row_is_never_overwritten()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Left joy\n" +
            "game.csv\n" +
            "Outputs,Function,usb,,,,,,,,,Action\n" +
            "left_trigger,normal,mp_center_sip,,,,,aim,,,,Fire\n");

        Assert.False(f.CanMoveInputToActionName(4, 7));
        Assert.Equal("Fire", f.GetCell(4, ProfileFile.ActionColumn));
        Assert.Equal("aim", f.GetCell(4, 7));
    }
}
