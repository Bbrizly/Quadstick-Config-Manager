using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// Moving one cell to another column in the same row: what the advanced grid's
// drag and its Move buttons run on. The rules matter more than the mechanics,
// because this runs against a config that already works on someone's hardware.
public class MoveCellTests
{
    // The shape that started this: a word the user meant as a label, parked in
    // an input column, where the device silently ignores it.
    static ProfileFile Aim() => ProfileFile.Load(
        "Profile Name,,Combat\n" +
        "game.csv\n" +
        "Outputs,Function,usb,,,,,,,,,Action\n" +
        "left_trigger,normal,mp_center_sip,,,,,aim\n");

    const int H = 7;

    [Fact]
    public void A_stray_word_moves_into_the_note_column_and_leaves_nothing_behind()
    {
        var f = Aim();
        Assert.True(f.CanMoveCell(4, H, ProfileFile.NoteColumn));
        Assert.True(f.MoveCell(4, H, ProfileFile.NoteColumn));

        Assert.Equal("", f.GetCell(4, H));
        Assert.Equal("aim", f.GetCell(4, ProfileFile.NoteColumn));
        // The binding keeps the one input it always had, and the device now
        // reads the row exactly as it did before, minus a word it ignored.
        Assert.Equal(new[] { "mp_center_sip" }, f.Document.Sheets[0].Bindings[0].Inputs);
        Assert.DoesNotContain(f.Issues, i => i.Kind == IssueKind.UnknownInput);
    }

    [Fact]
    public void The_note_column_absorbs_instead_of_overwriting()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Combat\n" +
            "game.csv\n" +
            "Outputs,Function,usb,,,,,,,,,Action\n" +
            "left_trigger,normal,mp_center_sip,,,,,aim,,,keep me\n");

        Assert.True(f.MoveCell(4, H, ProfileFile.NoteColumn));
        Assert.Equal("keep me; aim", f.GetCell(4, ProfileFile.NoteColumn));
    }

    [Fact]
    public void A_move_onto_an_occupied_cell_is_refused_rather_than_silently_overwriting()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Combat\n" +
            "game.csv\n" +
            "Outputs,Function,usb,,,,,,,,,Action\n" +
            "left_trigger,normal,mp_center_sip,,,,,aim,,,,Fire\n");

        // Column L already carries a name, so the word has nowhere to land.
        Assert.False(f.CanMoveCell(4, H, ProfileFile.ActionColumn));
        Assert.False(f.MoveCell(4, H, ProfileFile.ActionColumn));
        Assert.Equal("aim", f.GetCell(4, H));
        Assert.Equal("Fire", f.GetCell(4, ProfileFile.ActionColumn));

        // And onto a filled input cell, the same answer.
        Assert.False(f.MoveCell(4, H, 2));
        Assert.Equal("mp_center_sip", f.GetCell(4, 2));
    }

    [Fact]
    public void Moving_into_the_name_column_obeys_the_same_rules_as_the_name_box()
    {
        // Another row already answers to "aim" with a different output, which is
        // the one case the editor's own name box refuses.
        var f = ProfileFile.Load(
            "Profile Name,,Combat\n" +
            "game.csv\n" +
            "Outputs,Function,usb,,,,,,,,,Action\n" +
            "mouse_left,normal,lip,,,,,,,,,aim\n" +
            "left_trigger,normal,mp_center_sip,,,,,aim\n");

        Assert.False(f.CanMoveCell(5, H, ProfileFile.ActionColumn));
        // The note column is still open to it, since notes are free text.
        Assert.True(f.CanMoveCell(5, H, ProfileFile.NoteColumn));
    }

    [Fact]
    public void A_word_can_be_promoted_the_other_way_into_a_real_input()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Combat\n" +
            "game.csv\n" +
            "Outputs,Function,usb,,,,,,,,,Action\n" +
            "left_trigger,normal,,,,,,,,,lip\n");

        // K held a real input name all along. Moving it to C makes the device
        // read it, which is the whole point of letting the grid be edited.
        Assert.True(f.MoveCell(4, ProfileFile.NoteColumn, 2));
        Assert.Equal(new[] { "lip" }, f.Document.Sheets[0].Bindings[0].Inputs);
        Assert.Equal("", f.GetCell(4, ProfileFile.NoteColumn));
    }

    [Fact]
    public void An_empty_cell_and_a_move_onto_itself_are_both_refused()
    {
        var f = Aim();
        Assert.False(f.CanMoveCell(4, 3, ProfileFile.NoteColumn)); // D is empty
        Assert.False(f.CanMoveCell(4, H, H));
        Assert.False(f.CanMoveCell(99, H, ProfileFile.NoteColumn)); // no such row
    }

    [Fact]
    public void Asking_never_changes_anything()
    {
        var f = Aim();
        var before = f.ToCsvText();
        var revision = f.Revision;

        Assert.True(f.CanMoveCell(4, H, ProfileFile.NoteColumn));
        Assert.False(f.CanMoveCell(4, H, 2));

        Assert.Equal(before, f.ToCsvText());
        Assert.Equal(revision, f.Revision);
    }

    [Fact]
    public void A_move_is_one_undo_step()
    {
        var f = Aim();
        var before = f.ToCsvText();

        Assert.True(f.MoveCell(4, H, ProfileFile.NoteColumn));
        Assert.NotEqual(before, f.ToCsvText());

        Assert.True(f.Undo());
        Assert.Equal(before, f.ToCsvText());
        Assert.Equal("aim", f.GetCell(4, H));
    }
}
