using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// Rules learned from the firmware's own CSV reader (Configuration.c and the
// keyword tables, firmware source snapshot FW_VERSION 1476). The reader is
// the final authority on what the device does with a file.
public class FirmwareReaderTests
{
    static List<Issue> All(string csv)
    {
        var (doc, parseIssues) = Parser.Parse(csv);
        return parseIssues.Concat(Validator.Validate(doc)).ToList();
    }

    const string Head = "Profile Name,,Left joy\ngame.csv\nOutputs,Function,usb\n";

    [Fact]
    public void Lowercase_profile_keyword_is_an_error_because_strncmp_is_case_sensitive()
    {
        var issues = All("profile name,,Left joy\ngame.csv\nOutputs,Function,usb\nx,normal,lip\n");
        Assert.Contains(issues, i => i.Severity == Severity.Error && i.Cell == "A1");
    }

    [Fact]
    public void Preference_override_row_with_value_in_column_C_is_clean()
    {
        // Firmware: output lookup misses, preference lookup hits, column B is
        // skipped, column C is the value. "mouse_speed,,50" is the canonical shape.
        var issues = All(Head + "mouse_speed,,50\n");
        Assert.Empty(issues.Where(i => i.Severity == Severity.Error));
    }

    [Fact]
    public void Preference_override_row_with_value_in_column_B_warns_but_does_not_block()
    {
        // Real device files carry "mouse_speed,201". Firmware 1476 skips
        // column B and would read the (empty) third column as 0.
        var issues = All(Head + "mouse_speed,201\n");
        Assert.Empty(issues.Where(i => i.Severity == Severity.Error));
        Assert.Contains(issues, i => i.Severity == Severity.Warning && i.Cell.StartsWith('B'));
    }

    // A setting name in column A wins whatever column B says. The device takes
    // the preference branch on the name and then skips the function cell
    // without reading it, so column C is the value and never an input. Firmware
    // 2373 has increment_value in its function table and still does this.
    [Fact]
    public void Preference_name_with_increment_value_is_still_read_as_a_setting()
    {
        // "right_sip" is an input name, not a number, so the device stores 0.
        var issues = All(Head + "mouse_speed,increment_value 5,right_sip\n");
        Assert.Contains(issues, i =>
            i.Cell == "C4" && i.Severity == Severity.Error && i.Message.Contains("whole number"));

        // And it is not treated as an input, so no unknown-input complaint.
        var bad = All(Head + "mouse_speed,increment_value 5,not_an_input\n");
        Assert.DoesNotContain(bad, i => i.Kind == IssueKind.UnknownInput);
    }

    // A Preferences sheet is not a mode sheet: the value lives in column B,
    // not C (Fred Davison, 2026-07-08). These guard that distinction.
    // Layout is load-bearing: the parser reads data from the sheet's 4th row,
    // so the keyword, blank, and "Preference,Value,..." header must precede it.
    const string PrefsHead =
        "Profile Name,,Left joy\ngame.csv\nOutputs,Function,usb\nx,normal,lip\n,,\n"
        + "Preferences,,\n,,\nPreference,Value,Units,Description\n";

    [Fact]
    public void Preferences_sheet_value_in_column_B_is_clean()
    {
        // Real device prefs.csv rows: "mouse_speed,201". On a Preferences
        // sheet column B is correct and must not warn.
        var issues = All(PrefsHead + "mouse_speed,201\n");
        Assert.Empty(issues.Where(i => i.Severity == Severity.Error));
        Assert.DoesNotContain(issues, i => i.Cell == "B9"); // the mouse_speed value row is clean
    }

    [Fact]
    public void Preferences_sheet_value_misplaced_in_column_C_warns()
    {
        // The mode-sheet shape "name,,value" is wrong on a Preferences sheet.
        var issues = All(PrefsHead + "mouse_speed,,201\n");
        Assert.Empty(issues.Where(i => i.Severity == Severity.Error));
        Assert.Contains(issues, i => i.Severity == Severity.Warning
            && i.Cell.StartsWith('B') && i.Message.Contains("column B"));
    }

    [Fact]
    public void Preferences_sheet_missing_value_warns_reads_as_zero()
    {
        var issues = All(PrefsHead + "mouse_speed,\n");
        Assert.Empty(issues.Where(i => i.Severity == Severity.Error));
        Assert.Contains(issues, i => i.Severity == Severity.Warning
            && i.Cell == "B9" && i.Message.Contains("reads it as 0"));
    }

    [Fact]
    public void Preferences_sheet_non_numeric_value_is_an_error()
    {
        // "not a number at all" is an Error here, same as a mode-sheet override.
        var issues = All(PrefsHead + "mouse_speed,fast\n");
        Assert.Contains(issues, i => i.Severity == Severity.Error
            && i.Cell == "B9" && i.Message.Contains("whole number"));
    }

    [Fact]
    public void Preferences_sheet_bluetooth_word_value_is_clean()
    {
        // bluetooth_* preferences take word values, so a non-number is fine.
        var issues = All(PrefsHead + "bluetooth_device_mode,game_pad\n");
        Assert.Empty(issues.Where(i => i.Severity == Severity.Error));
        Assert.DoesNotContain(issues, i => i.Cell == "B9");
    }

    // Catalog-aware checks. The catalog's bounds come from the sliders in the
    // official manager, which are recommendations, so a value outside them is
    // a Warning. Its toggle and choice values come from the firmware's own
    // keyword tables, so a value outside those is an Error.

    [Fact]
    public void Unknown_preference_name_warns_and_never_blocks()
    {
        // Firmware newer than this app has preferences this app cannot know.
        var issues = All(PrefsHead + "future_setting,5\n");
        Assert.Empty(issues.Where(i => i.Severity == Severity.Error));
        Assert.Contains(issues, i => i.Severity == Severity.Warning
            && i.Cell == "A9" && i.Message.Contains("future_setting"));
    }

    [Fact]
    public void Known_preference_name_does_not_warn()
    {
        Assert.DoesNotContain(All(PrefsHead + "volume,40\n"), i => i.Cell == "A9");
    }

    [Fact]
    public void Value_above_the_catalog_maximum_warns_but_does_not_block()
    {
        var issues = All(PrefsHead + "mouse_speed,900\n"); // catalogued 0..250
        Assert.Empty(issues.Where(i => i.Severity == Severity.Error));
        Assert.Contains(issues, i => i.Severity == Severity.Warning
            && i.Cell == "B9" && i.Message.Contains("above 250"));
    }

    [Fact]
    public void Value_below_the_catalog_minimum_warns_but_does_not_block()
    {
        var issues = All(PrefsHead + "sip_puff_threshold_soft,1\n"); // catalogued 5..100
        Assert.Empty(issues.Where(i => i.Severity == Severity.Error));
        Assert.Contains(issues, i => i.Severity == Severity.Warning
            && i.Cell == "B9" && i.Message.Contains("below 5"));
    }

    [Fact]
    public void Values_on_the_catalog_bounds_are_clean()
    {
        Assert.DoesNotContain(All(PrefsHead + "mouse_speed,250\n"), i => i.Cell == "B9");
        Assert.DoesNotContain(All(PrefsHead + "mouse_speed,0\n"), i => i.Cell == "B9");
    }

    [Fact]
    public void An_integer_preference_with_no_catalogued_bounds_is_not_range_checked()
    {
        // deflection_multiplier_up has no proven range, so nothing is claimed.
        Assert.DoesNotContain(All(PrefsHead + "deflection_multiplier_up,9000\n"), i => i.Cell == "B9");
    }

    // PREF-07: a legacy toggle value the firmware would still read is a Warning.
    // The firmware treats a toggle as a number and coerces a stray one, so
    // blocking the install would keep a working file off the device.
    [Fact]
    public void A_toggle_that_is_not_0_or_1_warns_but_does_not_block()
    {
        var issues = All(PrefsHead + "watchdog_disable,2\n");
        Assert.Empty(issues.Where(i => i.Severity == Severity.Error));
        Assert.Contains(issues, i => i.Severity == Severity.Warning
            && i.Cell == "B9" && i.Message.Contains("on/off"));
    }

    // The other half of the same split: a choice word outside the firmware's
    // keyword table is not coerced, the lookup fails and the device lands on a
    // different mode, so that one still blocks.
    [Fact]
    public void An_odd_toggle_warns_where_an_unknown_choice_still_blocks()
    {
        var toggle = All(PrefsHead + "watchdog_disable,2\n");
        Assert.Empty(toggle.Where(i => i.Severity == Severity.Error));

        var choice = All(PrefsHead + "bluetooth_device_mode,PS4\n");
        Assert.Contains(choice, i => i.Severity == Severity.Error
            && i.Cell == "B9" && i.Message.Contains("bluetooth_device_mode"));
    }

    // "PS4" is a plausible thing to type and it is not a firmware keyword. The
    // keyword lookup does not fall back, so the device would come up in some
    // other device mode. That is worth blocking an install for.
    [Fact]
    public void An_unknown_bluetooth_device_mode_word_is_a_blocking_error()
    {
        var issues = All(PrefsHead + "bluetooth_device_mode,PS4\n");
        Assert.Contains(issues, i => i.Severity == Severity.Error
            && i.Cell == "B9" && i.Message.Contains("PS4"));
    }

    // enable_select_files rather than watchdog_disable: the device really reads
    // this one, so 1 stays clean. watchdog_disable is parsed and never read, and
    // setting it now draws the warning that says so.
    [Fact]
    public void A_toggle_set_to_0_or_1_is_clean()
    {
        Assert.DoesNotContain(All(PrefsHead + "enable_select_files,0\n"), i => i.Cell == "B9");
        Assert.DoesNotContain(All(PrefsHead + "enable_select_files,1\n"), i => i.Cell == "B9");
    }

    [Fact]
    public void A_toggle_with_a_word_value_reports_one_issue_not_two()
    {
        // The whole-number error already says what is wrong; the toggle check
        // must not pile a second issue onto the same cell.
        var issues = All(PrefsHead + "watchdog_disable,off\n");
        var cell = issues.Where(i => i.Cell == "B9").ToList();
        Assert.Single(cell);
        Assert.Equal(Severity.Error, cell[0].Severity);
    }

    [Fact]
    public void A_choice_value_outside_the_firmware_keywords_is_an_error()
    {
        var numbered = All(PrefsHead + "mouse_response_curve,7\n"); // 0, 1 or 2
        Assert.Contains(numbered, i => i.Severity == Severity.Error
            && i.Cell == "B9" && i.Message.Contains("mouse_response_curve"));

        var worded = All(PrefsHead + "bluetooth_connection_mode,handshake\n");
        Assert.Contains(worded, i => i.Severity == Severity.Error
            && i.Cell == "B9" && i.Message.Contains("bluetooth_connection_mode"));
    }

    [Fact]
    public void A_choice_value_in_the_firmware_keywords_is_clean()
    {
        Assert.DoesNotContain(All(PrefsHead + "mouse_response_curve,2\n"), i => i.Cell == "B9");
        Assert.DoesNotContain(All(PrefsHead + "bluetooth_connection_mode,pair\n"), i => i.Cell == "B9");
    }

    [Fact]
    public void A_text_preference_takes_any_value_the_device_might_read()
    {
        // enable_DS3_emulation and the addresses stay raw until a current
        // source proves their values, so the catalog claims nothing about them.
        Assert.DoesNotContain(All(PrefsHead + "bluetooth_remote_address,001122334455\n"),
            i => i.Cell == "B9");
        Assert.DoesNotContain(All(PrefsHead + "enable_DS3_emulation,3\n"), i => i.Cell == "B9");
    }

    // The four orderings the sources establish. Each warns only when both
    // rows are present and both are whole numbers.

    [Theory]
    [InlineData("sip_puff_threshold_soft,39\nsip_puff_threshold,40\n")]
    [InlineData("sip_puff_threshold,40\nsip_puff_maximum,41\n")]
    [InlineData("lip_position_minimum,30\nlip_position_maximum,34\n")]
    [InlineData("joystick_D_Pad_inner,25\njoystick_D_Pad_outer,26\n")]
    public void Preferences_too_close_together_warn(string rows)
    {
        var issues = All(PrefsHead + rows);
        Assert.Empty(issues.Where(i => i.Severity == Severity.Error));
        Assert.Contains(issues, i => i.Severity == Severity.Warning
            && i.Cell == "B10" && i.Message.Contains("between them"));
    }

    [Theory]
    [InlineData("sip_puff_threshold_soft,38\nsip_puff_threshold,40\n")]
    [InlineData("sip_puff_threshold,40\nsip_puff_maximum,42\n")]
    [InlineData("lip_position_minimum,30\nlip_position_maximum,35\n")]
    [InlineData("joystick_D_Pad_inner,25\njoystick_D_Pad_outer,27\n")]
    public void Preferences_exactly_the_required_gap_apart_are_clean(string rows)
    {
        Assert.DoesNotContain(All(PrefsHead + rows), i => i.Message.Contains("between them"));
    }

    [Fact]
    public void One_preference_of_a_pair_on_its_own_says_nothing()
    {
        // The other value lives on the device, where this app cannot see it.
        Assert.DoesNotContain(All(PrefsHead + "sip_puff_threshold,40\n"),
            i => i.Message.Contains("between them"));
        Assert.DoesNotContain(All(PrefsHead + "sip_puff_threshold_soft,90\n"),
            i => i.Message.Contains("between them"));
    }

    [Fact]
    public void A_pair_check_is_skipped_when_a_value_is_not_a_whole_number()
    {
        var issues = All(PrefsHead + "sip_puff_threshold_soft,high\nsip_puff_threshold,40\n");
        Assert.DoesNotContain(issues, i => i.Message.Contains("between them"));
    }

    [Fact]
    public void A_pair_check_is_skipped_when_a_value_is_missing()
    {
        var issues = All(PrefsHead + "sip_puff_threshold_soft,\nsip_puff_threshold,40\n");
        Assert.DoesNotContain(issues, i => i.Message.Contains("between them"));
    }

    [Fact]
    public void Mode_override_values_are_checked_against_the_catalog_too()
    {
        var far = All(Head + "mouse_speed,,900\n");
        Assert.Empty(far.Where(i => i.Severity == Severity.Error));
        Assert.Contains(far, i => i.Severity == Severity.Warning
            && i.Cell == "C4" && i.Message.Contains("above 250"));

        var wrongToken = All(Head + "mouse_response_curve,,7\n");
        Assert.Contains(wrongToken, i => i.Severity == Severity.Error && i.Cell == "C4");
    }

    [Fact]
    public void The_catalog_checks_follow_the_column_each_sheet_really_uses()
    {
        // A mode sheet reads column C, a Preferences sheet reads column B. A
        // number in the other column is a misplaced value, not a value to check.
        Assert.DoesNotContain(All(Head + "mouse_speed,900\n"), i => i.Message.Contains("above 250"));
        Assert.DoesNotContain(All(PrefsHead + "mouse_speed,,900\n"), i => i.Message.Contains("above 250"));
    }

    [Fact]
    public void Blank_function_is_a_warning_because_the_device_defaults_to_normal()
    {
        // search_for_keyword_with_parameter returns 0 on a miss and NORMAL is
        // enum value 0, so a blank function cell IS "normal" on the device.
        var issues = All(Head + "x,,lip\n");
        Assert.Empty(issues.Where(i => i.Severity == Severity.Error));
        Assert.Contains(issues, i => i.Severity == Severity.Warning && i.Message.Contains("normal"));
    }

    [Fact]
    public void Decimal_parameter_warns_because_atoi_truncates()
    {
        var issues = All(Head + "x,repeat 2.5,lip\n");
        Assert.Empty(issues.Where(i => i.Severity == Severity.Error));
        Assert.Contains(issues, i => i.Severity == Severity.Warning && i.Message.Contains("2.5"));
    }

    [Fact]
    public void Non_numeric_parameter_is_still_reported()
    {
        // atoi reads "fast" as 0, so the timing is wrong but the file is fine.
        var issues = All(Head + "x,repeat fast,lip\n");
        Assert.Contains(issues, i => i.Severity == Severity.Warning && i.Message.Contains("fast"));
    }

    [Fact]
    public void First_parameter_above_14_bits_warns_because_it_overflows_into_the_second()
    {
        var issues = All(Head + "x,tap 20000,lip\n");
        Assert.Contains(issues, i => i.Severity == Severity.Warning && i.Message.Contains("16383"));
    }

    [Fact]
    public void None_is_a_valid_input_keyword()
    {
        var issues = All(Head + "x,normal,none,lip\n");
        Assert.Empty(issues.Where(i => i.Severity == Severity.Error));
    }

    [Fact]
    public void Legacy_firmware_input_names_warn_instead_of_blocking()
    {
        // In the firmware's input table but not the current official list.
        var issues = All(Head + "x,normal,lip_soft\ncircle,normal,right_sip_long\n");
        Assert.Empty(issues.Where(i => i.Severity == Severity.Error));
        Assert.Equal(2, issues.Count(i => i.Severity == Severity.Warning && i.Message.Contains("legacy")));
    }

    [Fact]
    public void Row_longer_than_the_1024_byte_line_buffer_is_an_error()
    {
        var longComment = new string('c', 1100);
        var issues = All(Head + $"x,normal,lip,,,,,,,,{longComment}\n");
        Assert.Contains(issues, i => i.Severity == Severity.Error && i.Message.Contains("1023"));
    }

    [Fact]
    public void Cell_longer_than_the_64_char_keyword_limit_is_reported()
    {
        // next_word gives up after 64 characters and stops advancing, so the
        // row reads as empty from here on. Row-local, so a warning.
        var longName = new string('x', 70);
        var issues = All(Head + $"{longName},normal,lip\n");
        Assert.Contains(issues, i => i.Severity == Severity.Warning && i.Message.Contains("after 64"));
    }

    [Fact]
    public void Preference_description_columns_may_exceed_64_chars()
    {
        // The official prefs.csv keeps a sentence of description in the columns
        // after the value. The device never reads past column B on these rows.
        var longText = new string('d', 100);
        var issues = All(Head + "x,normal,lip\n\n" +
            $"Preferences\nprefs.csv\nPreference,Value,\nsip_threshold,20,,{longText}\n");
        Assert.Empty(issues.Where(i => i.Severity == Severity.Error));
    }

    [Fact]
    public void A_preference_name_longer_than_64_chars_is_still_reported()
    {
        var longName = new string('p', 70);
        var issues = All(Head + "x,normal,lip\n\n" +
            $"Preferences\nprefs.csv\nPreference,Value,\n{longName},20\n");
        Assert.Contains(issues, i => i.Severity == Severity.Warning && i.Message.Contains("after 64"));
    }

    [Fact]
    public void Comment_columns_past_J_may_exceed_64_chars()
    {
        var longComment = new string('c', 100); // well under the line limit
        var issues = All(Head + $"x,normal,lip,,,,,,,,{longComment}\n");
        Assert.Empty(issues.Where(i => i.Severity == Severity.Error));
    }

    [Fact]
    public void More_than_16_modes_warns_that_the_device_ignores_the_extras()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 17; i++)
        {
            sb.Append($"Profile Name,,Mode {i}\n");
            if (i == 0) sb.Append("game.csv\n");
            else sb.Append('\n');
            sb.Append("Outputs,Function,usb\nx,normal,lip\n\n");
        }
        var issues = All(sb.ToString());
        Assert.Contains(issues, i => i.Severity == Severity.Warning && i.Message.Contains("16 modes"));
    }

    [Fact]
    public void More_than_128_rows_in_one_mode_warns_that_the_device_ignores_the_extras()
    {
        var sb = new System.Text.StringBuilder(Head);
        for (int i = 0; i < 130; i++) sb.Append("x,normal,lip\n");
        var issues = All(sb.ToString());
        Assert.Contains(issues, i => i.Severity == Severity.Warning && i.Message.Contains("128"));
    }

    [Fact]
    public void Device_style_corpus_file_with_pref_row_has_no_errors()
    {
        var text = File.ReadAllText(Path.Combine("corpus", "device-style.csv"));
        var issues = All(text);
        Assert.Empty(issues.Where(i => i.Severity == Severity.Error));
    }

    // The binding loop and the preferences loop both end on `line_buffer[0]`
    // being \n or \r, nothing else. Every sheet after the first needs an EMPTY
    // line above it or its rows are eaten by the sheet above. The real
    // config.csv in the firmware tree is shaped exactly this way.
    static string[] SheetLines(string csv) =>
        csv.Replace("\r\n", "\n").Split('\n');

    // The first sheet sits right under the version header; every later one
    // needs the empty line, or the segment above never ends.
    static void AssertEverySheetHasAnEmptyLineAboveIt(string csv, string context = "")
    {
        var lines = SheetLines(csv);
        var first = true;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!Vocab.IsSheetKeyword(lines[i].Split(',')[0].Trim())) continue;
            if (first) { first = false; continue; }
            Assert.True(lines[i - 1].Length == 0,
                $"{context}line {i + 1} \"{lines[i]}\" needs an empty line above it, found \"{lines[i - 1]}\"");
        }
    }

    [Fact]
    public void Normalize_puts_an_empty_line_before_every_sheet_after_the_first()
    {
        var f = ProfileFile.Load(Head + "x,normal,lip\n" + Head.Replace("Left joy", "Mouse"));
        Assert.Equal(2, f.Document.Sheets.Count);

        f.NormalizeForDeviceCsv();

        AssertEverySheetHasAnEmptyLineAboveIt(f.ToCsvText());
        Assert.Equal(2, f.Document.Sheets.Count); // the separator is not a sheet
        Assert.Empty(f.Issues.Where(i => i.Severity == Severity.Error));
    }

    [Fact]
    public void Normalize_empties_a_separator_row_that_is_only_commas()
    {
        // A row of commas reads as a blank row to this app and to both
        // converters, but line_buffer[0] is ',' so the device runs straight
        // through it. Every corpus file written by hand looks like this.
        var f = ProfileFile.Load(File.ReadAllText(Path.Combine("corpus", "device-style.csv")));
        Assert.Contains(",,,,,,,,,", f.ToCsvText());

        f.NormalizeForDeviceCsv();

        AssertEverySheetHasAnEmptyLineAboveIt(f.ToCsvText());
        Assert.Equal(3, f.Document.Sheets.Count); // no rows gained or lost
    }

    [Fact]
    public void Normalize_fixes_the_shape_every_multi_sheet_edit_produces()
    {
        foreach (var (label, edit) in new (string, Action<ProfileFile>)[]
        {
            ("add a mode", f => f.AddModeSheet("Driving")),
            ("duplicate a mode", f => f.DuplicateMode(0, "Copy")),
            ("move a mode down", f => f.MoveMode(0, +1)),
        })
        {
            var f = ProfileFile.Load(File.ReadAllText(Path.Combine("corpus", "device-style.csv")));
            edit(f);
            f.NormalizeForDeviceCsv();
            AssertEverySheetHasAnEmptyLineAboveIt(f.ToCsvText(), $"after \"{label}\": ");
        }
    }

    [Fact]
    public void Normalize_is_idempotent_and_undoable_in_one_step()
    {
        var f = ProfileFile.Load(File.ReadAllText(Path.Combine("corpus", "device-style.csv")));
        var before = f.ToCsvText();

        f.NormalizeForDeviceCsv();
        var once = f.ToCsvText();
        Assert.NotEqual(before, once);

        f.NormalizeForDeviceCsv();
        Assert.Equal(once, f.ToCsvText());

        Assert.True(f.Undo());
        Assert.Equal(before, f.ToCsvText());
    }

    [Fact]
    public void An_imported_workbook_is_device_shaped_once_normalized()
    {
        using var stream = File.OpenRead(Path.Combine("corpus", "multi-tab.xlsx"));
        var f = ProfileFile.Load(Xlsx.ToCsv(stream));
        Assert.Equal(4, f.Document.Sheets.Count);

        f.NormalizeForDeviceCsv();

        AssertEverySheetHasAnEmptyLineAboveIt(f.ToCsvText());
        Assert.StartsWith("QuadStick Configuration,Version 1.5", f.ToCsvText());
        Assert.Equal(4, f.Document.Sheets.Count);
        Assert.Empty(f.Issues.Where(i => i.Severity == Severity.Error));
    }

    // search_for_keyword skips LEADING spaces (`while (isspace(*keyword))
    // ++keyword;`) and then runs strncmp against the whole word, so a trailing
    // space stops the match. The app trims every cell when it parses, so it
    // showed a working binding while the device threw the row or the input
    // away. One real profile in the public catalog does this today.
    [Fact]
    public void A_trailing_space_on_an_input_is_written_back_trimmed()
    {
        var f = ProfileFile.Load(Head + "left_2,force_off,mp_left_center_puff \n");

        Assert.Contains("left_2,force_off,mp_left_center_puff\r\n", f.ToCsvText());
    }

    [Fact]
    public void A_trailing_space_on_an_output_is_written_back_trimmed()
    {
        var f = ProfileFile.Load(Head + "left_2 ,normal,lip\n");

        Assert.Contains("left_2,normal,lip\r\n", f.ToCsvText());
    }

    [Fact]
    public void The_comments_columns_keep_the_spacing_the_user_typed()
    {
        // The device stops reading at column J, so columns K and beyond are the
        // user's own text and are written back byte for byte.
        var f = ProfileFile.Load(Head + "x,normal,lip,,,,,,,,  a note  \n");

        Assert.Contains("x,normal,lip,,,,,,,,  a note  \r\n", f.ToCsvText());
    }

    // next_word ends a field at any character that is not alphanumeric, '_',
    // '.', ' ' or '-'. So one cell holding "Aim (ADS)" becomes two fields to
    // the device and shifts every column after it along by one.
    [Theory]
    [InlineData("x,normal,Aim (ADS)", "C4")]
    [InlineData("x,normal,lip,on/off", "D4")]
    [InlineData("kb_a+b,normal,lip", "A4")]
    public void A_cell_the_device_would_split_in_two_is_reported(string row, string cell)
    {
        var issues = All(Head + row + "\n");

        Assert.Contains(issues, i => i.Cell == cell && i.Message.Contains("reads it as two"));
    }

    [Fact]
    public void An_ordinary_binding_row_is_not_reported_as_splittable()
    {
        // Underscores, dots, spaces and hyphens are all part of a word to the
        // device, so nothing in normal use should trip the check.
        Assert.DoesNotContain(All(Head + "left_trigger,delay_on 200,mp_left_sip_soft\n"),
            i => i.Message.Contains("reads it as two"));
    }

    // The binding loop ends only at `line_buffer[0] != '\n' && != '\r'`, which
    // is a line with nothing at all on it. A row of commas, or a note parked in
    // the comments columns, is a row the device reads and skips.
    [Fact]
    public void A_note_in_the_comments_columns_does_not_end_the_mode()
    {
        var f = ProfileFile.Load(Head +
            "x,normal,lip\n" +
            ",,,,,,,,,,Everything below here still counts\n" +
            "circle,normal,mp_center_sip\n");

        Assert.Equal(2, f.Document.Sheets[0].Bindings.Count);
        Assert.DoesNotContain(f.Issues, i => i.Message.Contains("appears after a blank row"));
    }

    [Fact]
    public void A_truly_empty_line_still_ends_the_mode()
    {
        var f = ProfileFile.Load(Head + "x,normal,lip\n\ncircle,normal,mp_center_sip\n");

        Assert.Single(f.Document.Sheets[0].Bindings);
        Assert.Contains(f.Issues, i => i.Message.Contains("appears after a blank row"));
    }

    // The loop's own i++ sits after a `continue` taken whenever the output cell
    // matches neither an output nor a preference keyword, so a blank or
    // misspelled output never uses one of the 128 slots.
    [Fact]
    public void Rows_the_device_skips_do_not_use_up_the_128_slots()
    {
        var rows = string.Concat(Enumerable.Repeat(",normal,\n", 40))
            + string.Concat(Enumerable.Repeat("x,normal,lip\n", 100));

        Assert.DoesNotContain(All(Head + rows), i => i.Message.Contains("first 128"));
    }

    [Fact]
    public void More_than_128_real_bindings_is_still_reported()
    {
        var rows = string.Concat(Enumerable.Repeat("x,normal,lip\n", 130));

        Assert.Contains(All(Head + rows), i => i.Message.Contains("first 128"));
    }

    // search_for_keyword_with_parameter matches on the LENGTH of each table
    // entry, so a function cell only has to START with a keyword. "toggled"
    // is toggle on the device, not normal.
    [Fact]
    public void A_function_that_starts_with_a_keyword_is_reported_as_that_keyword()
    {
        var issues = All(Head + "x,toggled,lip\n");

        var issue = Assert.Single(issues, i => i.Cell == "B4");
        Assert.Contains("toggle", issue.Message);
        Assert.DoesNotContain("falls back to \"normal\"", issue.Message);
    }

    [Fact]
    public void A_function_matching_no_keyword_at_all_still_falls_back_to_normal()
    {
        Assert.Contains(All(Head + "x,wobble,lip\n"),
            i => i.Cell == "B4" && i.Message.Contains("falls back to \"normal\""));
    }
}
