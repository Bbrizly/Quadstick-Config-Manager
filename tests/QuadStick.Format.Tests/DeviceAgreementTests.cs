using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// The one rule that keeps the app honest about the hardware:
//
//   Wherever the app's view of a profile differs from what the device
//   actually ends up with, the app must SAY SO on that row.
//
// The app is allowed to disagree with the device. It is not allowed to
// disagree silently. Every parsing bug found on 2026-07-30 was a silent
// disagreement: a trailing space that cost a user an input, a cell the device
// splits in two, a note in the comments columns that hid every binding below
// it. None of them could survive this test.
//
// The device's side comes from FirmwareOracle, a transcription of
// Configuration.c rather than a description of it. When this test fails, read
// the oracle and the firmware before touching either.
public class DeviceAgreementTests
{
    const string Head = "Profile Name,,Left joy\ngame.csv\nOutputs,Function,usb\n";

    // A name the app offers that the firmware has never heard of. The app's
    // vocabulary comes from Fred's validation endpoint, which tracks whatever
    // firmware is current, so the two are allowed to differ. What is NOT
    // allowed is the list growing without anybody noticing, which is what
    // The_only_names_the_two_disagree_on pins.
    static bool BeyondFirmware(string name) =>
        !FirmwareOracle.Outputs.Contains(name)
        && !FirmwareOracle.Inputs.Contains(name)
        && !FirmwareOracle.Preferences.Contains(name);

    // Every difference, with the row it sits on. Empty means the app and the
    // device agree completely. Explained marks the ones the app is entitled to:
    // a name from a newer firmware than the source we transcribed, or a row
    // past the device's 128 binding cap, which is reported once per mode.
    static List<(int Row, string What, bool Explained)> Differences(ProfileFile f)
    {
        f.NormalizeForDeviceCsv();
        var device = FirmwareOracle.Read(f.ToCsvText());
        var app = f.Document.Sheets.Where(s => s.Type == SheetType.ProfileName).ToList();
        var diffs = new List<(int, string, bool)>();

        if (app.Count != device.Count)
        {
            diffs.Add((app.FirstOrDefault()?.StartRow ?? 1,
                $"the app shows {app.Count} mode(s), the device loads {device.Count}", false));
            return diffs;
        }

        for (int m = 0; m < app.Count; m++)
        {
            var shown = app[m].Bindings;
            var got = device[m].Bindings;
            int d = 0;

            // The channel sits in the label row, and search_for_keyword takes
            // it whole: anything that is not exactly none, usb or bluetooth
            // falls back to usb. A mode that says "usb bluetooth" is one the
            // device runs on usb alone.
            var channel = app[m].Channel.Trim();
            if (channel.Length > 0 && channel != device[m].Channel)
                diffs.Add((app[m].StartRow + 2,
                    $"the app shows the channel \"{channel}\", the device uses \"{device[m].Channel}\"", false));

            foreach (var b in shown)
            {
                if (d < got.Count && got[d].Output == b.Output)
                {
                    // The app's own rule, not a second copy of it. Matching on
                    // the name alone made this branch swallow the one row the
                    // two really do disagree about: on an increment_value row
                    // the app binds an input, firmware 1476 reads that same
                    // cell as the setting's value, and comparing the cell to
                    // itself here called that agreement.
                    if (Vocab.IsPreferenceOverride(b.Output, b.Function))
                    {
                        // The device skips the function cell here and reads
                        // column C as the value, so the app holds the value in
                        // its first input slot and the row has no inputs at all.
                        var value = b.InputCols.Count > 0 && b.InputCols[0] == 2 ? b.Inputs[0] : "";
                        if (value.Trim() != got[d].Function.Trim())
                            diffs.Add((b.Row, $"the app shows the value \"{value}\", the device reads \"{got[d].Function}\"", false));
                        if (b.InputCols.Any(c => c > 2))
                            diffs.Add((b.Row, "the app shows inputs on a row the device reads as a setting", false));
                        d++;
                        continue;
                    }

                    var function = b.Function.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault() ?? "normal";
                    if (function != got[d].Function)
                        diffs.Add((b.Row, $"the app shows function \"{b.Function}\", the device uses \"{got[d].Function}\"", false));

                    // "none" is the device's own word for an empty cell, so it
                    // counts as absent on both sides.
                    var wanted = b.Inputs.Where(x => x != Vocab.NoneInput).ToList();
                    if (!wanted.SequenceEqual(got[d].Inputs, StringComparer.Ordinal))
                        diffs.Add((b.Row, $"the app shows inputs [{string.Join(", ", wanted)}], "
                            + $"the device gets [{string.Join(", ", got[d].Inputs)}]",
                            wanted.Except(got[d].Inputs).All(BeyondFirmware)));
                    d++;
                }
                else
                {
                    // Past the 128th binding the device simply stops, which
                    // the app reports once for the whole mode rather than on
                    // every row below it.
                    diffs.Add((b.Row, $"the app shows a binding for \"{b.Output}\" that the device never reads",
                        BeyondFirmware(b.Output) || got.Count >= FirmwareOracle.MaxBindings));
                }
            }

            for (; d < got.Count; d++)
                diffs.Add((app[m].StartRow, $"the device reads a binding for \"{got[d].Output}\" that the app never shows", false));
        }
        return diffs;
    }

    static void AssertNothingSilent(string csv)
    {
        var f = ProfileFile.Load(csv);
        var silent = Differences(f)
            .Where(d => !d.Explained && !f.Issues.Any(i => RowOf(i.Cell) == d.Row))
            .ToList();

        Assert.True(silent.Count == 0,
            "The app and the device disagree and the app says nothing about it:\n  "
            + string.Join("\n  ", silent.Select(s => $"row {s.Row}: {s.What}")));
    }

    static int RowOf(string cell) => int.TryParse(cell.AsSpan(1), out var r) ? r : -1;

    // The five real bugs, as the shapes that caused them.
    [Theory]
    // A trailing space: search_for_keyword compares the whole word, so the
    // device matched nothing and threw the input away. One public profile.
    [InlineData("left_2,force_off,mp_left_center_puff \n")]
    [InlineData("left_2 ,normal,lip\n")]
    // A cell the device cuts in two at a character next_word treats as a
    // separator, which shifts every column after it.
    [InlineData("x,normal,Aim (ADS)\n")]
    [InlineData("x,normal,lip,on/off\n")]
    // A note in the comments columns is not the end of the mode.
    [InlineData("x,normal,lip\n,,,,,,,,,,note\ncircle,normal,mp_center_sip\n")]
    [InlineData("x,normal,lip\n,,\ncircle,normal,mp_center_sip\n")]
    // A function cell that only starts with a keyword.
    [InlineData("x,toggled,lip\n")]
    // A row holding one cell of spaces. Every spreadsheet shows a row there,
    // and the app read it as one too, but saving trims the cell to nothing and
    // the line goes to the device empty, which ends the mode.
    [InlineData("x,normal,lip\n \ncircle,normal,mp_center_sip\n")]
    [InlineData("x,normal,lip\n\"\r\n\"\ncircle,normal,mp_center_sip\n")]
    // A setting name in column A with increment_value in column B. The app used
    // to read this as a binding that nudges the setting and the device never
    // did: it skips column B and reads column C as the value. The app agrees
    // now, and the row still has to be spoken for, because column C holds an
    // input name where the device wants a number.
    [InlineData("mouse_speed,increment_value 5,right_sip\n")]
    [InlineData("volume,decrement_value 1,lip\n")]
    // The plain cases, which must stay silent because there is nothing to say.
    [InlineData("x,normal,lip\n")]
    [InlineData("left_trigger,delay_on 200,mp_left_sip_soft,mp_center_sip\n")]
    [InlineData("mouse_speed,,50\n")]
    [InlineData(",normal,\n")]
    [InlineData("x,normal,aim\n")]
    [InlineData("x,wobble,lip\n")]
    [InlineData("x,normal,none,lip\n")]
    // A gap between inputs. The device squeezes the empty slots out and refills
    // the tail (Configuration.c: "fill out blank cells with None's"), so a gap
    // is spacing and nothing else. That is what makes reordering a sequence by
    // swapping a cell with a blank neighbour safe: the set and the order of the
    // inputs the device matches do not move with it.
    [InlineData("x,normal,,lip\n")]
    [InlineData("x,normal,lip,,mp_center_sip\n")]
    // A note, and a binding row whose output begins with the keyword. The
    // device dispatches sheets only between segments, so inside a mode both are
    // rows with no matching output: skipped, and the mode carries on. Reading
    // either as a sheet ate the two rows below it as a filename and a label row.
    [InlineData("x,normal,lip\nSee the other profile for aiming\ncircle,normal,mp_center_sip\ntriangle,normal,mp_left_sip\n")]
    [InlineData("x,normal,lip\nProfile switch,normal,lip\ncircle,normal,mp_center_sip\ntriangle,normal,mp_left_sip\n")]
    public void The_app_never_disagrees_with_the_device_in_silence(string rows) =>
        AssertNothingSilent(Head + rows);

    // The two rows under a keyword row are skipped whole by the device, so
    // neither can open a sheet however it is spelled.
    [Theory]
    [InlineData("my profile.csv")]
    [InlineData("Profile.csv")]
    public void A_filename_row_that_reads_like_a_keyword_stays_a_filename_row(string name) =>
        AssertNothingSilent($"Profile Name,,Left joy\n{name}\nOutputs,Function,usb\n"
            + "x,normal,lip\ncircle,normal,mp_center_sip\n");

    // The device compares the first nine bytes of the file case sensitively and
    // ignores the whole file when they are not exactly "QuadStick", falling back
    // to its built-in configuration. The app read the header case insensitively,
    // so a hand-typed one looked fine and installed a file the device threw away.
    [Theory]
    [InlineData("quadstick configuration,Version 1.5,,game\n")]
    [InlineData("QUADSTICK CONFIGURATION,Version 1.5,,game\n")]
    [InlineData("QuadStick Configuration,Version 1.5,,game\n")]
    public void A_header_in_the_wrong_case_still_reaches_the_device(string header) =>
        AssertNothingSilent(header + Head + "x,normal,lip\n");

    // search_for_keyword takes the channel cell whole and case sensitively, so
    // anything that is not exactly none, usb or bluetooth falls back to usb.
    [Theory]
    [InlineData("usb bluetooth")]
    [InlineData("bluetooth usb")]
    [InlineData("Bluetooth")]
    [InlineData("BLUETOOTH")]
    [InlineData("bluetooth")]
    [InlineData("usb")]
    [InlineData("none")]
    public void A_channel_the_device_will_not_match_is_not_passed_off_as_working(string channel) =>
        AssertNothingSilent($"Profile Name,,Left joy\ngame.csv\nOutputs,Function,{channel}\nx,normal,lip\n");

    [Fact]
    public void A_whole_profile_of_awkward_rows_stays_honest()
    {
        AssertNothingSilent(Head
            + "x,normal,lip\n"
            + "circle,toggle,mp_center_sip,mp_left_sip\n"
            + ",normal,\n"
            + "mouse_speed,,60\n"
            + "left_trigger,repeat 250 3,mp_triple_puff\n"
            + ",,,,,,,,,,just a note\n"
            + "square,normal,mp_right_center_sip_soft\n"
            + "\n"
            + "Profile Name,,Right joy\n"
            + ",,\n"
            + "Outputs,Function,bluetooth\n"
            + "triangle,normal,lip\n");
    }

    // The files the app ships and the workbooks the importer is tested on.
    [Theory]
    [InlineData("default.csv")]
    [InlineData("device-style.csv")]
    [InlineData("gta-mode1.csv")]
    public void The_corpus_profiles_agree_with_the_device(string name) =>
        AssertNothingSilent(File.ReadAllText(Path.Combine("corpus", name)));

    [Theory]
    [InlineData("single-tab.xlsx")]
    [InlineData("multi-tab.xlsx")]
    public void The_corpus_workbooks_agree_with_the_device(string name)
    {
        using var stream = File.OpenRead(Path.Combine("corpus", name));
        AssertNothingSilent(Xlsx.ToCsv(stream));
    }

    [Fact]
    public void A_new_profile_from_the_template_agrees_with_the_device() =>
        AssertNothingSilent(ProfileFile.NewFromTemplate("mygame.csv").ToCsvText());

    // The whole public catalog, when it is on the machine. Point QSCM_CATALOG
    // at a directory of .csv profiles (see the community catalog endpoint in
    // CommunityCatalog.cs) and this runs the real corpus, 300-odd profiles
    // written by people who are not us. It does nothing when the variable is
    // unset, because the catalog is downloaded rather than checked in.
    //
    // But when the variable IS set it has to run. It used to return quietly if
    // the directory was unreadable, so a mistyped or unreachable path reported
    // a pass while checking nothing, which is the one thing this whole file
    // exists to stop.
    [Fact]
    public void Every_profile_in_the_local_catalog_agrees_with_the_device()
    {
        var dir = Environment.GetEnvironmentVariable("QSCM_CATALOG");
        if (string.IsNullOrEmpty(dir)) return;

        Assert.True(Directory.Exists(dir), $"QSCM_CATALOG is set to \"{dir}\" but that directory cannot be read.");
        var files = Directory.GetFiles(dir, "*.csv");
        Assert.True(files.Length > 0, $"QSCM_CATALOG \"{dir}\" holds no .csv profiles.");

        var broken = new List<string>();
        foreach (var path in files.OrderBy(p => p))
        {
            var f = ProfileFile.Load(File.ReadAllText(path));
            foreach (var d in Differences(f)
                .Where(d => !d.Explained && !f.Issues.Any(i => RowOf(i.Cell) == d.Row)))
                broken.Add($"{Path.GetFileName(path)} row {d.Row}: {d.What}");
        }

        Assert.True(broken.Count == 0,
            $"{broken.Count} silent disagreements across the catalog:\n  "
            + string.Join("\n  ", broken.Take(30)));
    }

    // The 131 workbooks in agent/corpus/silas are one real user's whole
    // library, checked in, so unlike the downloaded catalog above this runs
    // everywhere including CI. Thirty six thousand bindings written by
    // somebody who is not us: if a parse change starts dropping rows, or
    // starts reading one the device would not, it shows here rather than on
    // his QuadStick.
    [Fact]
    public void Every_workbook_in_the_shipped_corpus_agrees_with_the_device()
    {
        var dir = Path.Combine(RepoRoot, "agent", "corpus", "silas");
        var files = Directory.GetFiles(dir, "*.xlsx").OrderBy(p => p).ToList();
        Assert.True(files.Count > 100, $"{dir} holds {files.Count} workbooks, expected the whole library.");

        var broken = new List<string>();
        foreach (var path in files)
        {
            string csv;
            using (var stream = File.OpenRead(path)) csv = Xlsx.ToCsv(stream);
            var f = ProfileFile.Load(csv);
            foreach (var d in Differences(f)
                .Where(d => !d.Explained && !f.Issues.Any(i => RowOf(i.Cell) == d.Row)))
                broken.Add($"{Path.GetFileName(path)} row {d.Row}: {d.What}");
        }

        Assert.True(broken.Count == 0,
            $"{broken.Count} silent disagreements across the corpus:\n  "
            + string.Join("\n  ", broken.Take(30)));
    }

    static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "QuadStick.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }

    // The whole of the disagreement we are knowingly living with, and on 2373
    // there is none: every name the app offers is in the device's own tables.
    //
    // It used to hold 34 names, all of them things Fred's validation endpoint
    // listed and the 2017 source did not: the Xbox Adaptive Controller outputs,
    // the six that split sip from puff, mp_right_mode_*, any_direction. 2373
    // has all 34. The endpoint was never wrong, it was just ahead of the source
    // we could read.
    //
    // If this test fails, the app's vocabulary moved. Either a newer firmware
    // source has arrived and the tables under corpus/ need re-dumping, or a
    // name has appeared that nothing supports.
    [Fact]
    public void The_only_names_the_two_disagree_on_are_the_ones_we_know_about()
    {
        var appNames = Vocab.Inputs.Concat(Vocab.KnownOutputs).Concat(Vocab.PreferenceOverrides);
        var beyond = appNames.Where(BeyondFirmware).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(Array.Empty<string>(), beyond);
    }

    // What licenses modelling 2373 alone. The app cannot read the firmware off
    // an attached device, so it has to pick one to describe, and it picks the
    // current one. That is only safe because 2373 took nothing away: every
    // keyword a 2017 device knows, a 2025 device still knows, so no profile
    // written against the old firmware can break on the new one.
    //
    // If this fails, that stopped being true and the app owes its users a
    // warning it does not currently have.
    [Theory]
    [InlineData("outputs")]
    [InlineData("inputs")]
    [InlineData("preferences")]
    [InlineData("functions")]
    [InlineData("connections")]
    public void The_new_firmware_took_nothing_away(string table)
    {
        static string[] Read(int version, string table) =>
            System.Text.Json.JsonDocument
                .Parse(File.ReadAllBytes(Path.Combine("corpus", $"firmware-{version}.json")))
                .RootElement.GetProperty(table)
                .EnumerateArray().Select(x => x.GetString()!).ToArray();

        var dropped = Read(1476, table).Except(Read(2373, table), StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(Array.Empty<string>(), dropped);
    }

    // The oracle is only worth anything if it really is the firmware's reader,
    // so pin the pieces that decided the bugs above.
    [Fact]
    public void The_oracle_stops_a_word_at_64_characters_and_keeps_its_place()
    {
        var line = new string('c', 64) + ",lip\n";
        int i = 0;

        Assert.Null(FirmwareOracle.NextWord(line, ref i));
        Assert.Equal(0, i); // next_word leaves *index alone when it returns NULL
    }

    [Fact]
    public void The_oracle_skips_leading_spaces_but_not_trailing_ones()
    {
        Assert.Equal("lip", FirmwareOracle.Match("  lip", FirmwareOracle.Inputs));
        Assert.Null(FirmwareOracle.Match("lip ", FirmwareOracle.Inputs));
    }

    // The channel was the one table the oracle wrote out by hand, so it stayed
    // on the 2017 list of three while everything else moved to 2373. A mode on
    // "both" read as "usb", which is a real disagreement with a real device
    // reported as agreement, and the catalog sweep only stayed green because
    // the app's own "both needs 2025 firmware" warning happens to sit on that
    // same row and masked it.
    [Fact]
    public void The_oracle_knows_the_channel_the_new_firmware_added()
    {
        Assert.Equal(new[] { "none", "usb", "bluetooth", "both" }, FirmwareOracle.Connections);

        var modes = FirmwareOracle.Read("QuadStick\nProfile Name,,Solo\ngame.csv\nOutputs,Function,both\nx,normal,lip\n");

        Assert.Equal("both", Assert.Single(modes).Channel);
    }

    [Fact]
    public void The_oracle_still_falls_back_to_usb_on_a_word_the_device_cannot_match()
    {
        var modes = FirmwareOracle.Read("QuadStick\nProfile Name,,Solo\ngame.csv\nOutputs,Function,wibble\nx,normal,lip\n");

        Assert.Equal("usb", Assert.Single(modes).Channel);
    }

    [Fact]
    public void The_oracle_matches_a_function_on_its_prefix_only()
    {
        Assert.Equal("toggle", FirmwareOracle.MatchWithParameter("toggled", FirmwareOracle.Functions));
        Assert.Equal("delay_on", FirmwareOracle.MatchWithParameter("delay_on 200", FirmwareOracle.Functions));
        Assert.Null(FirmwareOracle.MatchWithParameter("wobble", FirmwareOracle.Functions));
    }

    [Fact]
    public void The_oracle_breaks_a_line_the_way_f_gets_does()
    {
        // 1023 characters kept, the rest handed back as the next line.
        var lines = FirmwareOracle.ReadLines(new string('c', 1100) + "\r\n");

        Assert.Equal(2, lines.Count);
        Assert.Equal(1023, lines[0].Length);
    }

    [Fact]
    public void The_oracle_reads_a_file_the_device_would_reject()
    {
        Assert.Empty(FirmwareOracle.Read("Profile Name,,Left joy\ngame.csv\nOutputs,Function,usb\nx,normal,lip\n"));
    }
}
