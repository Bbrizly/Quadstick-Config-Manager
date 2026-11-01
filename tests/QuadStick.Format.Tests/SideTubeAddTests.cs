using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// Drew Redepenning: "a lot of people will forget to map that and get stuck in
// their new mode once they upload it". A QuadStick user who cannot leave a mode
// cannot reach the file to fix it, so these prove the way out through the
// firmware's own reader rather than through the app's parser.
public class SideTubeAddTests
{
    const string Header = "QuadStick Configuration,Version 1.5,,game\n";

    static string Mode(string name, params string[] rows) =>
        $"Profile Name,,{name}\n\nOutput or Function,Function,usb\n" +
        string.Concat(rows.Select(r => r + "\n")) + "\n";

    static ProfileFile File(params string[] modes) =>
        ProfileFile.Load(Header + string.Concat(modes));

    // A mode's rows as written, so "left alone" means the bytes and not a
    // reading of them.
    static string Text(ProfileFile f, int sheet)
    {
        var lines = f.ToCsvText().Split('\n');
        int start = f.Document.Sheets[sheet].StartRow - 1;
        int end = sheet + 1 < f.Document.Sheets.Count ? f.Document.Sheets[sheet + 1].StartRow - 1 : lines.Length;
        // Without the blank lines closing it: whether one follows depends on
        // whether something was appended below, and that is not this mode's text.
        return string.Join("\n", lines[start..end]).TrimEnd();
    }

    static void AssertHasSideTube(FirmwareOracle.Mode mode)
    {
        var inc = Assert.Single(mode.Bindings, b => b.Output == "increment_mode");
        Assert.Equal("normal", inc.Function);
        Assert.Equal(new[] { "right_sip" }, inc.Inputs);
        var dec = Assert.Single(mode.Bindings, b => b.Output == "decrement_mode");
        Assert.Equal("normal", dec.Function);
        Assert.Equal(new[] { "right_puff" }, dec.Inputs);
    }

    static void AssertNoSwitching(FirmwareOracle.Mode mode) =>
        Assert.DoesNotContain(mode.Bindings, b => b.Output is "increment_mode" or "decrement_mode");

    [Fact]
    public void The_new_mode_reads_as_both_rows_on_the_device()
    {
        var f = File(Mode("Walking", "increment_mode,normal,lip"));

        var added = f.AddModeWithSideTube("Driving");

        Assert.Equal(1, added.SheetIndex);
        var seen = FirmwareOracle.Read(f.ToCsvText());
        Assert.Equal(2, seen.Count);
        Assert.Equal(2, seen[1].Bindings.Count);
        AssertHasSideTube(seen[1]);
    }

    [Fact]
    public void A_copy_of_a_mode_with_no_way_out_gets_the_rows()
    {
        var f = File(Mode("Walking", "x,normal,mp_center_sip"));

        int idx = f.DuplicateMode(0, "Walking copy", giveSideTube: true);

        var seen = FirmwareOracle.Read(f.ToCsvText());
        Assert.Equal(1, idx);
        AssertHasSideTube(seen[1]);
        AssertNoSwitching(seen[0]);
    }

    [Fact]
    public void A_copy_that_can_already_switch_stays_an_exact_copy()
    {
        var f = File(Mode("Walking", "increment_mode,normal,lip"));

        f.DuplicateMode(0, "Walking copy", giveSideTube: true);

        Assert.Equal(new[] { "lip" }, Assert.Single(FirmwareOracle.Read(f.ToCsvText())[1].Bindings).Inputs);
    }

    [Fact]
    public void Without_a_side_tube_the_copy_gets_no_rows()
    {
        var f = File(Mode("Walking", "x,normal,mp_center_sip"));

        f.DuplicateMode(0, "Walking copy");

        AssertNoSwitching(FirmwareOracle.Read(f.ToCsvText())[1]);
    }

    [Fact]
    public void It_survives_a_save_and_a_reload()
    {
        // No trailing blank line and a Preferences sheet last: the shape where
        // the appended mode depends on the repair made on the way out.
        var f = ProfileFile.Load(Header + "Profile Name,,Walking\n\nOutput or Function,Function,usb\n" +
            "increment_mode,normal,lip\n\nPreferences\n\nPreference,Value\nmouse_speed,5\n");

        var added = f.AddModeWithSideTube("Driving");
        f.NormalizeForDeviceCsv();
        var saved = f.ToCsvText();

        var reloaded = ProfileFile.Load(saved);
        Assert.Equal("Driving", reloaded.Document.Sheets[added.SheetIndex].ModeName);
        Assert.Equal(new[] { "increment_mode", "decrement_mode" },
            reloaded.Document.Sheets[added.SheetIndex].Bindings.Select(b => b.Output));
        Assert.Equal(saved, reloaded.ToCsvText());

        var seen = FirmwareOracle.Read(saved);
        Assert.Equal(2, seen.Count);
        AssertHasSideTube(seen[1]);
    }

    [Fact]
    public void One_undo_puts_back_the_exact_grid_including_other_modes_it_changed()
    {
        var f = File(Mode("Walking", "x,normal,lip"), Mode("Menus", "circle,normal,mp_left_sip"));
        var before = f.ToCsvText();
        int revision = f.Revision;

        var added = f.AddModeWithSideTube("Driving", afterSheetIndex: 0);
        Assert.Equal(2, added.OtherModesGiven);
        Assert.Equal(revision + 1, f.Revision);

        Assert.True(f.Undo());
        Assert.Equal(before, f.ToCsvText());
        Assert.False(f.CanUndo);
    }

    [Fact]
    public void Added_mid_file_it_keeps_the_blank_line_and_the_mode_below_it_intact()
    {
        var f = File(
            Mode("Walking", "increment_mode,normal,lip"),
            Mode("Shooting", "decrement_mode,normal,lip", "circle,normal,mp_left_sip"),
            Mode("Menus", "increment_mode,normal,lip"));
        var below = Text(f, 1);

        var added = f.AddModeWithSideTube("Driving", afterSheetIndex: 0);

        Assert.Equal(1, added.SheetIndex);
        Assert.Equal(new[] { "Walking", "Driving", "Shooting", "Menus" },
            f.Document.Sheets.Select(s => s.ModeName));
        Assert.Equal(below, Text(f, 2));

        // Without the blank the device reads the next keyword row as a binding
        // of the new mode and never sees that mode at all.
        var seen = FirmwareOracle.Read(f.ToCsvText());
        Assert.Equal(4, seen.Count);
        AssertHasSideTube(seen[1]);
        Assert.Equal(2, seen[1].Bindings.Count);
        Assert.Equal(new[] { "decrement_mode", "circle" }, seen[2].Bindings.Select(b => b.Output));
    }

    [Fact]
    public void A_mode_with_no_way_out_and_a_free_tube_gets_the_rows()
    {
        var f = File(
            Mode("Stuck", "x,normal,lip", "circle,normal,mp_left_sip"),
            Mode("Fine", "increment_mode,normal,lip"));

        var added = f.AddModeWithSideTube("Driving");

        Assert.Equal(1, added.OtherModesGiven);
        Assert.Empty(added.ModesStillStuck);
        var seen = FirmwareOracle.Read(f.ToCsvText());
        AssertHasSideTube(seen[0]);
        // Under the rows it already had, which keep their order.
        Assert.Equal(new[] { "x", "circle", "increment_mode", "decrement_mode" },
            seen[0].Bindings.Select(b => b.Output));
    }

    [Fact]
    public void A_mode_with_no_rows_at_all_gets_them_above_its_blank_line()
    {
        var f = File(Mode("Empty"), Mode("Fine", "increment_mode,normal,lip"));

        f.AddModeWithSideTube("Driving");

        var seen = FirmwareOracle.Read(f.ToCsvText());
        Assert.Equal(3, seen.Count);
        AssertHasSideTube(seen[0]);
    }

    public static TheoryData<string, string> LeftAlone => new()
    {
        { "increment on the lip", "increment_mode,normal,lip" },
        { "decrement on the lip", "decrement_mode,normal,lip" },
        { "right_puff alone", "x,normal,right_puff" },
        { "right_sip alone", "x,normal,right_sip" },
        { "right_sip mid sequence", "x,normal,lip,right_sip,mp_left_sip" },
        { "right_puff in the last input column", "x,normal,,,,,,,,right_puff" },
    };

    [Theory]
    [MemberData(nameof(LeftAlone))]
    public void A_mode_failing_either_rule_is_left_byte_for_byte(string why, string row)
    {
        var f = File(Mode("Kept", "circle,normal,mp_left_sip", row, "square,normal,mp_left_puff"));
        var before = Text(f, 0);

        var added = f.AddModeWithSideTube("Driving");

        Assert.True(before == Text(f, 0), $"{why}: the mode was changed");
        Assert.Equal(0, added.OtherModesGiven);
    }

    [Fact]
    public void Only_a_mode_with_no_way_out_is_reported_as_stuck_and_by_its_number()
    {
        // Same name twice and Preferences between, so neither a name nor a sheet
        // index could say which one is meant. The device numbers Profile
        // segments only. Added under the first mode, which moves the rest down.
        var f = ProfileFile.Load(Header +
            Mode("Same", "increment_mode,normal,lip") +
            "Preferences\n\nPreference,Value\nmouse_speed,5\n\n" +
            Mode("Same", "x,normal,right_sip") +
            Mode("Same", "decrement_mode,normal,right_puff"));

        var added = f.AddModeWithSideTube("Driving", afterSheetIndex: 0);

        Assert.Equal(0, added.OtherModesGiven);
        Assert.Equal(new[] { 3 }, added.ModesStillStuck);
        AssertNoSwitching(FirmwareOracle.Read(f.ToCsvText())[2]);
    }

    [Fact]
    public void A_way_out_the_device_cannot_read_is_not_a_way_out()
    {
        var f = File(
            Mode("Typo", "increment_mode,normal,lipp"),
            Mode("Nothing", "decrement_mode,normal,none"),
            Mode("NoInput", "increment_mode,normal"),
            // Below the blank line: the device stopped reading before this row.
            Mode("Dead", "x,normal,lip") + "increment_mode,normal,lip\n\n");

        var added = f.AddModeWithSideTube("Driving");

        Assert.Equal(4, added.OtherModesGiven);
        var seen = FirmwareOracle.Read(f.ToCsvText());
        Assert.Equal(5, seen.Count);
        foreach (var mode in seen)
            Assert.Contains(mode.Bindings, b => b.Output == "increment_mode" && b.Inputs.SequenceEqual(new[] { "right_sip" }));
    }

    [Fact]
    public void A_tube_only_used_below_the_blank_line_is_still_free()
    {
        var f = File(Mode("Dead", "x,normal,lip") + "circle,normal,right_sip\n\n");

        var added = f.AddModeWithSideTube("Driving");

        Assert.Equal(1, added.OtherModesGiven);
        AssertHasSideTube(FirmwareOracle.Read(f.ToCsvText())[0]);
    }

    // The two rows would land past the device's last slot and be thrown away,
    // so saying the mode has a way out would be false.
    [Theory]
    [InlineData(126, 1)]
    [InlineData(127, 0)]
    [InlineData(128, 0)]
    public void A_mode_with_no_slots_left_is_left_alone_and_reported(int rows, int given)
    {
        var f = File(Mode("Full", Enumerable.Repeat("x,normal,lip", rows).ToArray()));
        var before = Text(f, 0);

        var added = f.AddModeWithSideTube("Driving");

        Assert.Equal(given, added.OtherModesGiven);
        if (given == 0)
        {
            Assert.Equal(before, Text(f, 0));
            Assert.Equal(new[] { 1 }, added.ModesStillStuck);
            return;
        }
        AssertHasSideTube(FirmwareOracle.Read(f.ToCsvText())[0]);
    }

    [Fact]
    public void Preferences_and_Infrared_sheets_are_never_touched()
    {
        var prefs = "Preferences\n\nPreference,Value\nmouse_speed,5\n\n";
        var ir = "Infrared,Samsung\n\nName,Code\npower,0xE0E040BF\n\n";
        var f = ProfileFile.Load(Header + prefs + Mode("Fine", "increment_mode,normal,lip") + ir);

        var added = f.AddModeWithSideTube("Driving");

        Assert.Equal(0, added.OtherModesGiven);
        var text = f.ToCsvText().Replace("\r\n", "\n");
        Assert.Contains(prefs, text);
        Assert.Contains(ir, text);
        Assert.Empty(f.Document.Sheets.Single(s => s.Type == SheetType.Preferences)
            .Bindings.Where(b => b.Output is "increment_mode" or "decrement_mode"));
    }

    [Fact]
    public void It_raises_no_new_validation_issues()
    {
        var f = File(Mode("Walking", "x,normal,lip"), Mode("Fine", "increment_mode,normal,lip"));
        var before = f.Issues.Select(i => i.Message).ToList();

        f.AddModeWithSideTube("Driving", afterSheetIndex: 0);

        Assert.Equal(before, f.Issues.Select(i => i.Message));
    }

    [Fact]
    public void AddModeSheet_itself_still_makes_an_empty_mode_and_touches_nothing_else()
    {
        var f = File(Mode("Stuck", "x,normal,lip"));
        var before = Text(f, 0);

        int idx = f.AddModeSheet("Driving");

        Assert.Empty(f.Document.Sheets[idx].Bindings);
        Assert.Equal(before, Text(f, 0));
    }

    [Fact]
    public void Duplicating_a_mode_adds_nothing()
    {
        var f = File(Mode("Stuck", "x,normal,lip"));

        int idx = f.DuplicateMode(0, "Copy");

        Assert.Equal(new[] { "x" }, f.Document.Sheets[idx].Bindings.Select(b => b.Output));
        Assert.Equal(new[] { "x" }, f.Document.Sheets[0].Bindings.Select(b => b.Output));
    }

    static string[] Stranded(int count) =>
        Enumerable.Range(1, count).Select(i => Mode("M" + i, "x,normal,lip")).ToArray();

    [Fact]
    public void A_mode_past_the_limit_says_so_and_leaves_the_others_past_it_alone()
    {
        // Sixteen with a way out and two the device never reads.
        var f = File(Enumerable.Range(1, 16).Select(i => Mode("M" + i, "increment_mode,normal,lip"))
            .Concat(Stranded(2)).ToArray());
        var dead = new[] { Text(f, 16), Text(f, 17) };

        var added = f.AddModeWithSideTube("Driving");

        Assert.Equal(19, added.ModeNumber);
        Assert.True(added.PastDeviceLimit);
        Assert.False(added.PushedOnePastLimit);
        Assert.Equal(0, added.OtherModesGiven);
        Assert.Empty(added.ModesStillStuck);
        Assert.Equal(dead, new[] { Text(f, 16), Text(f, 17) });
        // The firmware still counts the segment and reads none of its rows.
        Assert.All(FirmwareOracle.Read(f.ToCsvText()).Skip(16), m => Assert.Empty(m.Bindings));
    }

    [Fact]
    public void A_stranded_mode_the_device_still_loads_is_given_a_way_out_by_a_mode_it_does_not()
    {
        var f = File(Stranded(16));

        var added = f.AddModeWithSideTube("Driving");

        Assert.True(added.PastDeviceLimit);
        Assert.Equal(16, added.OtherModesGiven);
        Assert.Empty(added.ModesStillStuck);
    }

    [Fact]
    public void A_mode_in_the_middle_counts_only_what_the_device_still_loads_after_it()
    {
        // Mode 16 is stranded with its side tube free. The add moves it to 17,
        // so it is neither given rows nor reported.
        var f = File(Enumerable.Range(1, 15).Select(i => Mode("M" + i, "increment_mode,normal,lip"))
            .Concat(new[] { Mode("Last", "x,normal,lip") }).ToArray());
        var last = Text(f, 15);

        var added = f.AddModeWithSideTube("Driving", afterSheetIndex: 0);

        Assert.Equal(2, added.ModeNumber);
        Assert.False(added.PastDeviceLimit);
        Assert.True(added.PushedOnePastLimit);
        Assert.Equal(0, added.OtherModesGiven);
        Assert.Empty(added.ModesStillStuck);
        Assert.Equal(last, Text(f, 16));
        // The firmware still counts the segment and reads none of its rows.
        Assert.All(FirmwareOracle.Read(f.ToCsvText()).Skip(16), m => Assert.Empty(m.Bindings));
    }

    [Fact]
    public void A_stuck_mode_past_the_limit_is_not_reported_as_stuck()
    {
        var f = File(Enumerable.Range(1, 16).Select(i => Mode("M" + i, "increment_mode,normal,lip"))
            .Concat(new[] { Mode("Taken", "x,normal,right_sip") }).ToArray());

        var added = f.AddModeWithSideTube("Driving");

        Assert.Empty(added.ModesStillStuck);
    }
}
