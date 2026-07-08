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

    [Fact]
    public void Preference_name_with_increment_value_is_a_live_binding_and_validates_inputs()
    {
        var ok = All(Head + "mouse_speed,increment_value 5,right_sip\n");
        Assert.Empty(ok.Where(i => i.Severity == Severity.Error));

        var bad = All(Head + "mouse_speed,increment_value 5,not_an_input\n");
        Assert.Contains(bad, i => i.Severity == Severity.Error && i.Message.Contains("not_an_input"));
    }

    // A Preferences sheet is not a mode sheet: the value lives in column B,
    // not C (Fred Davison, 2026-07-08). These guard that distinction.
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
    public void Non_numeric_parameter_is_still_an_error()
    {
        var issues = All(Head + "x,repeat fast,lip\n");
        Assert.Contains(issues, i => i.Severity == Severity.Error && i.Message.Contains("fast"));
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
    public void Cell_longer_than_the_64_char_keyword_limit_is_an_error()
    {
        var longName = new string('x', 70);
        var issues = All(Head + $"{longName},normal,lip\n");
        Assert.Contains(issues, i => i.Severity == Severity.Error && i.Message.Contains("64"));
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
}
