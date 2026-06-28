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
}

public class ValidatorGoldenTests
{
    static List<Issue> All(string csv)
    {
        var (doc, parseIssues) = Parser.Parse(csv);
        return parseIssues.Concat(Validator.Validate(doc)).ToList();
    }

    [Fact]
    public void Wrong_A1_keyword_is_an_error_but_containing_Profile_is_legal()
    {
        // QMP's actual rule: A1 must CONTAIN "Profile" or equal Preferences/Infrared.
        var bad = All("My Cool Setup,,Left joy\ngame.csv\nOutputs,Function,usb\nx,normal,lip\n");
        Assert.Contains(bad, i => i.Severity == Severity.Error && i.Cell == "A1");

        var good = All("My Cool Profile,,Left joy\ngame.csv\nOutputs,Function,usb\nx,normal,lip\n");
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
    public void Unknown_function_is_an_error_and_bad_params_are_errors()
    {
        Assert.Contains(All("Profile Name,,L\ng.csv\nOutputs,Function,usb\nx,blink,lip\n"),
            i => i.Severity == Severity.Error && i.Message.Contains("blink"));
        Assert.Contains(All("Profile Name,,L\ng.csv\nOutputs,Function,usb\nx,repeat fast,lip\n"),
            i => i.Severity == Severity.Error && i.Message.Contains("fast"));
        Assert.Contains(All("Profile Name,,L\ng.csv\nOutputs,Function,usb\nx,repeat 5,lip\n"),
            i => i.Severity == Severity.Error && i.Message.Contains("both parameters"));
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
