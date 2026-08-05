using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// Found by running the whole public catalog through the importer on 2026-07-29:
// 161 of 309 real community profiles refused to install, and 148 of those were
// blocked only by rows the device ignores anyway. A leftover template row or a
// note parked in an input column is not a broken file, but Severity.Error was
// doing double duty for "the device will misread this file" and "this row does
// nothing", and Device.Install gates on HasErrors.
//
// The rule these tests hold down: Error means the device misreads the FILE.
// A row that simply does nothing is a Warning, so it is still reported and
// still installs.
public class InertRowTests
{
    // A profile that is perfectly good apart from one inert row. Nothing here
    // stops the device reading the file.
    static ProfileFile WithRow(string row) => ProfileFile.Load(
        "Profile Name,,Main\n" +
        "mygame.csv,,\n" +
        "PlayStation Outputs,Function,usb\n" +
        "x,normal,lip\n" +
        row + "\n");

    [Fact]
    public void A_leftover_template_row_does_not_block_install()
    {
        // The community sheets ship ~1000 pre-filled rows; every unused one is
        // a blank output beside a leftover "normal". 1,923 of these across the
        // catalog, on their own enough to block 94 workbooks.
        var f = WithRow(",normal,");

        Assert.Contains(f.Issues, i => i.Message.Contains("no output name"));
        Assert.False(f.HasErrors, "an empty template row does nothing on the device");
    }

    [Fact]
    public void A_note_parked_in_an_input_column_does_not_block_install()
    {
        // e.g. "x,toggle,mp_triple_puff,Aim". The device does not know "Aim",
        // so it ignores that keyword and the binding still works.
        var f = WithRow("x,toggle,mp_triple_puff,Aim");

        var issue = Assert.Single(f.Issues, i => i.Kind == IssueKind.UnknownInput);
        Assert.Equal("D5", issue.Cell); // still points at the offending column
        Assert.False(f.HasErrors, "an unreadable input keyword is skipped, not fatal");
    }

    // The row that started this, from a real user's BF6 sheet on 2026-07-30.
    // The label sits in column H, four columns past the input it describes, and
    // the app refused to install the whole profile over it.
    [Fact]
    public void The_bf6_row_that_blocked_a_real_users_install()
    {
        var f = WithRow("left_trigger,normal,mp_center_sip,,,,,aim");

        var issue = Assert.Single(f.Issues);
        Assert.Equal(Severity.Warning, issue.Severity);
        Assert.Equal("H5", issue.Cell);
        Assert.Equal(IssueKind.UnknownInput, issue.Kind);
        Assert.False(f.HasErrors);

        // The binding itself is intact: the device gets left_trigger on a
        // centre sip whatever the label says.
        var b = Assert.Single(f.Document.Sheets[0].Bindings, x => x.Output == "left_trigger");
        Assert.Equal("normal", b.Function);
        Assert.Contains("mp_center_sip", b.Inputs);
    }

    [Fact]
    public void An_unknown_function_does_not_block_install()
    {
        // The firmware falls back to code 0 ("normal") for a function it does
        // not recognize, which is exactly what Validator already documents.
        var f = WithRow("x,wobble,lip");

        Assert.Contains(f.Issues, i => i.Message.Contains("not a documented output function"));
        Assert.False(f.HasErrors);
    }

    [Fact]
    public void A_row_after_the_modes_blank_terminator_does_not_block_install()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Main\n" +
            "mygame.csv,,\n" +
            "PlayStation Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "\n" +
            "circle,normal,lip\n");

        Assert.Contains(f.Issues, i => i.Message.Contains("appears after a blank row"));
        Assert.False(f.HasErrors, "the device just stops reading; the file is intact");
    }

    [Fact]
    public void Bad_function_parameters_do_not_block_install()
    {
        Assert.False(WithRow("x,repeat 1 2 3,lip").HasErrors);   // too many
        Assert.False(WithRow("x,repeat abc,lip").HasErrors);     // not a number
    }

    // The other half of the rule. These really do cost the user the file, so
    // they must keep blocking install.
    [Fact]
    public void A_line_over_the_devices_buffer_still_blocks_install()
    {
        // f_gets keeps 1023 characters and hands the overflow back on the next
        // call, so the tail is read as if it were the next row. Where the break
        // lands decides what happens: usually the tail matches no output and is
        // skipped, but a tail starting with '\n' or '\r' meets the binding
        // loop's own end-of-mode test and drops every row below. The damage is
        // not confined to this row, which is what makes it an error.
        var f = WithRow("x,normal,lip,,,,,,,," + new string('c', 1100));

        Assert.True(f.HasErrors);
    }

    // Read off next_word in Configuration.c (firmware 1476): it scans
    // answer[0..MAX_KEYWORD_LENGTH-1] for a separator, so a field of exactly 64
    // characters has its comma at index 64, never looks at it, and returns NULL.
    // 63 is the real limit, and the app used to allow 64.
    [Theory]
    [InlineData(63, false)]
    [InlineData(64, true)]
    [InlineData(70, true)]
    public void The_keyword_cap_is_63_characters_not_64(int length, bool reported)
    {
        var f = WithRow("x,normal," + new string('c', length));

        Assert.Equal(reported, f.Issues.Any(i => i.Message.Contains("after 64")));
    }

    [Fact]
    public void A_cell_over_the_keyword_cap_is_reported_without_blocking()
    {
        // next_word returns NULL and does not advance its index, so this cell
        // and every cell after it on the row read as nothing. The row loses its
        // inputs; the rest of the file is untouched.
        var f = WithRow("x,normal," + new string('c', 70));

        Assert.Contains(f.Issues, i => i.Severity == Severity.Warning && i.Message.Contains("after 64"));
        Assert.False(f.HasErrors);
    }

    [Fact]
    public void A_bad_filename_still_blocks_install()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Main\n" +
            "my game,,\n" +
            "PlayStation Outputs,Function,usb\n" +
            "x,normal,lip\n");

        Assert.True(f.HasErrors);
    }

    [Fact]
    public void A_file_with_no_profile_sheet_at_all_still_blocks_install()
    {
        Assert.True(ProfileFile.Load("something else,,\nx,normal,lip\n").HasErrors);
    }

    // The device splits sections on the START of the raw line and never looks
    // at column B, so a keyword row still opens a sheet when the author wrote
    // something beside the word. Community IR tabs do exactly that
    // ("Infrared,Samsung Most Models - Set #: 595"), and reading those rows as
    // part of the sheet above showed IR hex codes as broken preference values.
    [Fact]
    public void A_keyword_row_with_text_beside_it_still_starts_a_sheet()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Main\n" +
            "mygame.csv,,\n" +
            "PlayStation Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "Infrared,Samsung Most Models - Set #: 595,Comments\n" +
            ",http://irdb.globalcache.com/\n" +
            "Command Name,Hex Code\n" +
            "ir_tv_on_off,0000 006D 0000 0022 00AA 00AA\n");

        Assert.Equal(2, f.Document.Sheets.Count);
        Assert.Equal(SheetType.Infrared, f.Document.Sheets[1].Type);
        // The mode keeps its own single binding rather than swallowing the codes.
        Assert.Single(f.Document.Sheets[0].Bindings);
        Assert.False(f.HasErrors, "IR codes are not preference values");
    }

    // The loose match stays loose. "GTA Profile" only CONTAINS the keyword, so
    // the device skips that sheet, and the app has to keep saying so.
    [Fact]
    public void A_sheet_the_firmware_would_skip_is_still_reported()
    {
        var f = ProfileFile.Load(
            "GTA Profile,,Main\n" +
            "mygame.csv,,\n" +
            "PlayStation Outputs,Function,usb\n" +
            "x,normal,lip\n");

        Assert.Contains(f.Issues, i => i.Message.Contains("does not START with"));
        Assert.True(f.HasErrors, "a whole mode the device never loads is worth blocking");
    }

    // A binding row that merely contains the word must not split the file.
    [Fact]
    public void A_binding_row_containing_the_word_profile_does_not_split_the_file()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Main\n" +
            "mygame.csv,,\n" +
            "PlayStation Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "my profile row,normal,lip\n");

        Assert.Single(f.Document.Sheets);
        Assert.Equal(2, f.Document.Sheets[0].Bindings.Count);
    }

    // The same word in a note, with nothing beside it. The device only looks
    // for sheets between segments; inside one it reads this as a row whose
    // output matches nothing, skips it, and carries on down the mode. The app
    // used to open a sheet here and swallow the two rows below as that sheet's
    // filename and label rows, so two working bindings left the profile and
    // nothing said so.
    [Fact]
    public void A_note_containing_the_word_profile_does_not_split_the_file()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Main\n" +
            "mygame.csv,,\n" +
            "PlayStation Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "See the other profile for aiming\n" +
            "circle,normal,mp_center_sip\n" +
            "triangle,normal,mp_left_sip\n");

        Assert.Single(f.Document.Sheets);
        Assert.Contains(f.Document.Sheets[0].Bindings, b => b.Output == "circle");
        Assert.Contains(f.Document.Sheets[0].Bindings, b => b.Output == "triangle");
    }

    // And the same again where the row begins with the keyword exactly. Still
    // an ordinary row to the device, for the same reason.
    [Fact]
    public void A_binding_row_starting_with_the_word_profile_does_not_split_the_file()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Main\n" +
            "mygame.csv,,\n" +
            "PlayStation Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "Profile switch,normal,lip\n" +
            "circle,normal,mp_center_sip\n" +
            "triangle,normal,mp_left_sip\n");

        Assert.Single(f.Document.Sheets);
        Assert.Contains(f.Document.Sheets[0].Bindings, b => b.Output == "circle");
        Assert.Contains(f.Document.Sheets[0].Bindings, b => b.Output == "triangle");
    }

    // The filename row is skipped whole by the device, so what it is called
    // cannot open a sheet. Both spellings used to.
    [Theory]
    [InlineData("my profile.csv")]
    [InlineData("Profile.csv")]
    public void A_filename_row_that_reads_like_a_keyword_does_not_split_the_file(string name)
    {
        var f = ProfileFile.Load(
            "Profile Name,,Main\n" +
            name + ",,\n" +
            "PlayStation Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "circle,normal,mp_center_sip\n");

        Assert.Single(f.Document.Sheets);
        Assert.Equal(2, f.Document.Sheets[0].Bindings.Count);
    }

    // A header the user wrote without the blank line above it still opens a
    // sheet. The device merges it into the mode above, which is why saving puts
    // the blank line back, and reading the sheet is what makes that repair
    // possible.
    [Fact]
    public void A_real_header_without_its_separator_still_opens_a_sheet()
    {
        var f = ProfileFile.Load(
            "Profile Name,,Main\n" +
            "mygame.csv,,\n" +
            "PlayStation Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "Profile Name,,Aiming\n" +
            ",,\n" +
            "PlayStation Outputs,Function,usb\n" +
            "circle,normal,mp_center_sip\n");

        Assert.Equal(2, f.Document.Sheets.Count);
        Assert.Equal("Aiming", f.Document.Sheets[1].ModeName);
    }
}
