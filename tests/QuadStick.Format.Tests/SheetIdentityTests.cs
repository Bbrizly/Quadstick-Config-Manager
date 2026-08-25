using System.Linq;
using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// Cell C1 of the version header is the format's slot for the Google Sheet a
// profile is backed up to. It was written blank, and the link lived in
// settings under the file's path, so moving or renaming a profile orphaned its
// backup and the next save forked a second sheet.
public class SheetIdentityTests
{
    const string Id = "1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789";

    static ProfileFile Headed(string c1 = "") => ProfileFile.Load(
        $"QuadStick Configuration,Version 1.5,{c1},Rocket League\n" +
        "\n" +
        "Profile Name,,Driving\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "mouse_left,normal,lip\n");

    [Fact]
    public void The_sheet_id_is_written_into_C1()
    {
        var file = Headed();
        file.HeaderSheetId = Id;
        Assert.StartsWith($"QuadStick Configuration,Version 1.5,{Id},Rocket League", file.ToCsvText());
    }

    [Fact]
    public void A_stamped_file_reads_its_own_sheet_id_back()
    {
        var file = Headed();
        file.HeaderSheetId = Id;
        var reloaded = ProfileFile.Load(file.ToCsvText());
        Assert.Equal(Id, reloaded.Document.HeaderSource);
        Assert.True(SheetsUrl.TryGetEditUrlFromHeader(reloaded.Document.HeaderVersion, reloaded.Document.HeaderSource, out var url));
        Assert.Equal($"https://docs.google.com/spreadsheets/d/{Id}/edit", url);
    }

    // Stamping is bookkeeping. A file that was clean before a save is still
    // clean after one, and the id never shows up as something to undo.
    [Fact]
    public void Stamping_does_not_dirty_the_file_or_reach_undo()
    {
        var file = Headed();
        file.HeaderSheetId = Id;
        _ = file.ToCsvText();
        Assert.False(file.Dirty);
        Assert.False(file.CanUndo);
    }

    [Fact]
    public void An_id_already_in_the_file_is_left_alone_when_this_app_has_no_link()
    {
        var file = Headed("someone-elses-sheet-id");
        Assert.Contains(",someone-elses-sheet-id,", file.ToCsvText());
    }

    // A file with no version header is not given one here. Adding the header is
    // NormalizeForDeviceCsv's call, and it makes other edits at the same time.
    [Fact]
    public void A_file_with_no_header_is_not_given_one()
    {
        var file = ProfileFile.Load("Profile Name,,Driving\ngame.csv\nOutputs,Function,usb\n");
        file.HeaderSheetId = Id;
        Assert.DoesNotContain(Id, file.ToCsvText());
        Assert.StartsWith("Profile Name", file.ToCsvText());
    }

    // A header row cut short still gets the id, in the right column, without
    // losing the name that follows it.
    [Fact]
    public void A_short_header_row_is_widened_not_overwritten()
    {
        var file = ProfileFile.Load("QuadStick Configuration,Version 1.5\n\nProfile Name,,Driving\ngame.csv\n");
        file.HeaderSheetId = Id;
        var reloaded = ProfileFile.Load(file.ToCsvText());
        Assert.Equal(Id, reloaded.Document.HeaderSource);
        Assert.Equal("Version 1.5", reloaded.Document.HeaderVersion);
    }

    // The grid the editor shows is not touched, so nothing on screen moves and
    // the row numbers a view is bound to still mean what they meant.
    [Fact]
    public void The_grid_itself_is_never_changed()
    {
        var file = Headed();
        var before = file.Grid.Select(r => string.Join("|", r)).ToList();
        file.HeaderSheetId = Id;
        _ = file.ToCsvText();
        Assert.Equal(before, file.Grid.Select(r => string.Join("|", r)).ToList());
    }
}
