using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// A3 of a sheet names a convention ("XBox Outputs"), and the picker used to
// take that as the list of outputs the file was allowed to use. It is a label
// carried over from whatever template a file was copied from, so a profile
// whose rows all said right_2 under an XBox header could not be given right_2
// again: the picker hid it and the search called it no match.
//
// right_2 and right_trigger are one output. output_keywords.h files the second
// under "// aliases" and both reach RIGHT_2. So the list is whole now, and
// which of the two spellings a reader sees is the reader's own setting.
public sealed class PickerVocabularyTests : IDisposable
{
    readonly string _wasGrouping = Settings.Load().PickerGrouping;
    readonly string _wasVocabulary = Settings.Load().PickerVocabulary;

    public void Dispose()
    {
        var s = Settings.Load();
        s.PickerGrouping = _wasGrouping;
        s.PickerVocabulary = _wasVocabulary;
        Settings.Save(s);
    }

    // The header says Xbox and every row says PlayStation. Both halves of that
    // are real: this is the file shape the bug was reported on.
    const string Csv =
        "Profile Name,,Solo\n" +
        "game.csv\n" +
        "XBox Outputs,Function,usb\n" +
        "right_2,normal,lip\n";

    static MainWindow Open(string vocabulary)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.PickerGrouping = "Flat"; // one level, so every option is on screen at once
        s.PickerVocabulary = vocabulary;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(ProfileFile.Load(Csv));
        w.SetDeviceViewForPreview(false);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    static Control OpenPicker(MainWindow w)
    {
        var button = w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Output for row 4"));
        var flyout = (Flyout)button.Flyout!;
        flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        var content = (Control)flyout.Content!;
        content.UpdateLayout();
        return content;
    }

    // List View names each option by its raw token, which is what the file
    // gets, so this is the list of writable outputs as the picker offers them.
    static string[] Options(Control root) => root.GetVisualDescendants().OfType<Button>()
        .Where(b => b is not RadioButton)
        .Select(b => AutomationProperties.GetName(b) ?? "")
        .ToArray();

    [AvaloniaFact]
    public void XboxHeaderStillOffersPlaystationOutputs()
    {
        var w = Open("All");
        var options = Options(OpenPicker(w));
        Assert.Contains("right_2", options);
        Assert.Contains("right_trigger", options);
    }

    [AvaloniaFact]
    public void PlaystationHidesTheXboxSpellingOnly()
    {
        var w = Open("PlayStation");
        var options = Options(OpenPicker(w));
        Assert.Contains("left_1", options);
        Assert.DoesNotContain("left_bumper", options);
        // One name, no second spelling: never filtered by either choice.
        Assert.Contains("kb_v", options);
        Assert.Contains("start", options);
    }

    [AvaloniaFact]
    public void XboxHidesThePlaystationSpellingOnly()
    {
        var w = Open("Xbox");
        var options = Options(OpenPicker(w));
        Assert.Contains("left_bumper", options);
        Assert.DoesNotContain("left_1", options);
        Assert.Contains("kb_v", options);
        Assert.Contains("start", options);
    }

    // Filtering the list must never filter away what the row already holds,
    // or the cell reads one output and its own picker denies it exists.
    [AvaloniaFact]
    public void TheRowsOwnValueSurvivesEveryVocabulary()
    {
        var w = Open("Xbox");
        Assert.Contains("right_2", Options(OpenPicker(w)));
    }

    // The row is out of the layout for now (MainWindow.VocabularyFilterUi).
    // Everything under it still works, so this pins the off state rather than
    // letting the tests below quietly stop covering anything.
    [AvaloniaFact]
    public void TheChoiceIsNotInTheLayoutYet()
    {
        Assert.False(MainWindow.VocabularyFilterUi);
        var w = Open("All");
        Assert.Empty(OpenPicker(w).GetVisualDescendants().OfType<RadioButton>());
    }

    [AvaloniaFact]
    public void TheChoiceIsRemembered()
    {
        var w = Open("All");
        w.SetPickerVocabulary("PlayStation");
        Assert.Equal("PlayStation", Settings.Load().PickerVocabulary);
    }

    [Fact]
    public void EveryPairIsTwoNamesForOneFirmwareOutput()
    {
        // Both halves are legal tokens, so neither choice can hide an output
        // the other keeps.
        foreach (var (ps, xbox) in OutputCatalog.VocabularyPairs)
        {
            Assert.Contains(ps, Vocab.AllOutputs);
            Assert.Contains(xbox, Vocab.AllOutputs);
        }
        Assert.Equal(13, OutputCatalog.VocabularyPairs.Length);
    }
}
