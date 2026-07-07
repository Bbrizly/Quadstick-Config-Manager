using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

public class CorpusTests
{
    static string Load(string name) => File.ReadAllText(Path.Combine("corpus", name));

    [Fact]
    public void Default_profile_parses_clean()
    {
        var (doc, parseIssues) = Parser.Parse(Load("default.csv"));
        var issues = parseIssues.Concat(Validator.Validate(doc)).ToList();

        Assert.Single(doc.Sheets);
        Assert.Equal("default.csv", doc.CsvFileName);
        Assert.True(doc.IsDefaultConfig);
        Assert.Equal("Left Joystick", doc.Sheets[0].ModeName);
        Assert.Equal("usb", doc.Sheets[0].Channel);
        Assert.Equal(32, doc.Sheets[0].Bindings.Count);
        // The only acceptable finding is the default.csv caution.
        var unexpected = issues.Where(i => i.Severity == Severity.Error).ToList();
        Assert.Empty(unexpected);
        Assert.Contains(issues, i => i.Message.Contains("default.csv"));
    }

    [Fact]
    public void Gta_profile_parses_clean_with_duplicate_outputs_and_repeat()
    {
        var (doc, parseIssues) = Parser.Parse(Load("gta-mode1.csv"));
        var issues = parseIssues.Concat(Validator.Validate(doc)).ToList();

        Assert.Equal("gta.csv", doc.CsvFileName);
        Assert.Empty(issues.Where(i => i.Severity == Severity.Error));
        Assert.Equal(2, doc.Sheets[0].Bindings.Count(b => b.Output == "select"));
        Assert.Contains(doc.Sheets[0].Bindings, b => b.Function == "repeat");
    }

    [Fact]
    public void Csv_round_trip_is_stable()
    {
        var text = Load("default.csv");
        var once = Csv.Write(Csv.Parse(text));
        var twice = Csv.Write(Csv.Parse(once));
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Csv_quotes_a_bare_carriage_return_so_it_round_trips()
    {
        // A lone '\r' (no '\n') used to slip out unquoted, corrupting the
        // written CSV's row structure for any reader that treats '\r' as a
        // row break on its own (old Mac line endings).
        var grid = new List<string[]> { new[] { "a\rb", "c" } };
        var written = Csv.Write(grid);
        var reparsed = Csv.Parse(written);
        Assert.Equal("a\rb", reparsed[0][0]);
    }
}

public class ValidatorGoldenTests
{
    static List<Issue> All(string csv)
    {
        var (doc, parseIssues) = Parser.Parse(csv);
        return parseIssues.Concat(Validator.Validate(doc)).ToList();
    }

    [Fact]
    public void A1_must_START_with_the_sheet_keyword_because_the_firmware_dispatches_on_the_line_start()
    {
        // QMP's validator accepts A1 that merely CONTAINS "Profile", but the
        // firmware (Configuration.c, 1476) strncmp's the START of the line,
        // case sensitively, and silently skips a sheet that fails.
        var bad = All("My Cool Setup,,Left joy\ngame.csv\nOutputs,Function,usb\nx,normal,lip\n");
        Assert.Contains(bad, i => i.Severity == Severity.Error && i.Cell == "A1");

        // Contains-but-not-starts: parsed as a sheet (QMP would), but errored
        // so it can never be installed in a form the device throws away.
        var contains = All("My Cool Profile,,Left joy\ngame.csv\nOutputs,Function,usb\nx,normal,lip\n");
        Assert.Contains(contains, i => i.Severity == Severity.Error && i.Cell == "A1" && i.Message.Contains("START"));

        var good = All("Profile Name,,Left joy\ngame.csv\nOutputs,Function,usb\nx,normal,lip\n");
        Assert.Empty(good.Where(i => i.Severity == Severity.Error));
    }

    [Fact]
    public void Bad_filename_is_an_error()
    {
        var issues = All("Profile Name,,Left joy\nmy game\nOutputs,Function,usb\nx,normal,lip\n");
        Assert.Contains(issues, i => i.Severity == Severity.Error && i.Cell == "A2");
    }

    [Fact]
    public void Content_after_blank_row_is_an_error_because_the_device_reads_blanks_as_sheet_breaks()
    {
        var issues = All("Profile Name,,Left joy\ngame.csv\nOutputs,Function,usb\nx,normal,lip\n,,\ncircle,normal,lip\n");
        Assert.Contains(issues, i => i.Severity == Severity.Error && i.Message.Contains("after a blank row"));
    }

    [Fact]
    public void Unknown_input_is_an_error()
    {
        var issues = All("Profile Name,,Left joy\ngame.csv\nOutputs,Function,usb\nx,normal,left_sip\n");
        Assert.Contains(issues, i => i.Severity == Severity.Error && i.Message.Contains("left_sip"));
    }

    [Fact]
    public void Invalid_input_points_at_its_real_column_not_always_C()
    {
        // "lip" (C, valid) then "bogus" (D, invalid): the issue must land on D4
        // so Fix First and the highlight reach the bad input, not the first one.
        var issues = All("Profile Name,,L\ng.csv\nOutputs,Function,usb\nx,normal,lip,bogus\n");
        Assert.Contains(issues, i => i.Severity == Severity.Error && i.Message.Contains("bogus") && i.Cell == "D4");
    }

    [Fact]
    public void Decimal_function_params_are_accepted_regardless_of_OS_locale()
    {
        // On a comma-decimal culture, a current-culture parse would reject "2.5".
        var prior = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            Assert.Empty(All("Profile Name,,L\ng.csv\nOutputs,Function,usb\nx,repeat 2.5,lip\n")
                .Where(i => i.Severity == Severity.Error));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = prior; }
    }

    [Fact]
    public void Single_parameter_function_forms_are_legal_per_the_manual()
    {
        // "tap 500", "delay_on 500 1", "repeat 4": all documented usage.
        Assert.Empty(All("Profile Name,,L\ng.csv\nOutputs,Function,usb\nx,tap 500,lip\n")
            .Where(i => i.Severity == Severity.Error));
        Assert.Empty(All("Profile Name,,L\ng.csv\nOutputs,Function,usb\nx,delay_on 500 1,lip\n")
            .Where(i => i.Severity == Severity.Error));
        Assert.Empty(All("Profile Name,,L\ng.csv\nOutputs,Function,usb\nx,repeat 4,lip\n")
            .Where(i => i.Severity == Severity.Error));
    }

    [Fact]
    public void Unknown_function_is_an_error_and_bad_params_are_errors()
    {
        Assert.Contains(All("Profile Name,,L\ng.csv\nOutputs,Function,usb\nx,blink,lip\n"),
            i => i.Severity == Severity.Error && i.Message.Contains("blink"));
        Assert.Contains(All("Profile Name,,L\ng.csv\nOutputs,Function,usb\nx,repeat fast,lip\n"),
            i => i.Severity == Severity.Error && i.Message.Contains("fast"));
        Assert.Contains(All("Profile Name,,L\ng.csv\nOutputs,Function,usb\nx,repeat 5 2000 9,lip\n"),
            i => i.Severity == Severity.Error && i.Message.Contains("at most"));
        Assert.Empty(All("Profile Name,,L\ng.csv\nOutputs,Function,usb\nx,repeat 5 2000,lip\n")
            .Where(i => i.Severity == Severity.Error));
    }

    [Fact]
    public void Editing_default_csv_warns_about_bricking()
    {
        var issues = All("Profile Name,,L\ndefault.csv\nOutputs,Function,usb\nx,normal,lip\n");
        Assert.Contains(issues, i => i.Severity == Severity.Warning && i.Message.Contains("force-erase"));
    }
}
