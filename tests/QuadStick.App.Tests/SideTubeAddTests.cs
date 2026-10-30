using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// Drew Redepenning: "a lot of people will forget to map that and get stuck in
// their new mode once they upload it".
public class SideTubeAddTests
{
    const string Csv =
        "QuadStick Configuration,Version 1.5,,game\n" +
        "Profile Name,,Walking\n\nOutput or Function,Function,usb\nincrement_mode,normal,lip\n\n" +
        "Profile Name,,Open\n\nOutput or Function,Function,usb\nx,normal,lip\n\n" +
        "Profile Name,,Taken\n\nOutput or Function,Function,usb\nx,normal,right_sip\n\n";

    static MainWindow Open(string csv, string model)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        var s = Settings.Load();
        s.Model = model;
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(ProfileFile.Load(csv));
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    static string StatusText(MainWindow w) => string.Join(" ",
        w.GetVisualDescendants().OfType<ContentControl>().First(c => c.Name == "StatusHost")
            .GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

    static void Press(MainWindow w)
    {
        Ui.Click(w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "AddModeButton"));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void The_plus_gives_the_new_mode_and_a_stranded_one_the_side_tube_and_says_so()
    {
        var w = Open(Csv, "FPS");
        try
        {
            Press(w);

            var file = w.OpenFile!;
            var added = file.Document.Sheets[1];
            Assert.Equal(new[] { "increment_mode", "decrement_mode" }, added.Bindings.Select(b => b.Output));
            Assert.Equal(new[] { "x", "increment_mode", "decrement_mode" },
                file.Document.Sheets[2].Bindings.Select(b => b.Output));

            var status = StatusText(w);
            Assert.Contains(Strings.Modes_ModeAddedSideTube, status);
            Assert.Contains(string.Format(CultureInfo.CurrentCulture, Strings.Modes_SideTubeGiven_one, 1), status);
            // Walking, Mode 4, Open, Taken: Taken is the fourth, by position.
            Assert.Contains(string.Format(CultureInfo.CurrentCulture, Strings.Modes_StillStuck_one, 4), status);

            Assert.True(file.Undo());
            Assert.Equal(3, file.Document.Sheets.Count);
            Assert.Equal(new[] { "x" }, file.Document.Sheets[1].Bindings.Select(b => b.Output));
        }
        finally
        {
            w.OpenFile!.Dirty = false;
            w.Close();
        }
    }

    [AvaloniaFact]
    public void Nothing_stranded_and_nothing_changed_says_only_the_new_mode()
    {
        var w = Open(
            "QuadStick Configuration,Version 1.5,,game\n" +
            "Profile Name,,Walking\n\nOutput or Function,Function,usb\nincrement_mode,normal,lip\n\n", "Original");
        try
        {
            Press(w);
            Assert.Equal(Strings.Modes_ModeAddedSideTube, StatusText(w));
        }
        finally
        {
            w.OpenFile!.Dirty = false;
            w.Close();
        }
    }

    // A Singleton has one mouthpiece tube and no side tube at all, so rows on
    // right_sip would be a way out that is not on the device.
    [AvaloniaFact]
    public void A_Singleton_gets_an_empty_mode_and_no_rows_anywhere()
    {
        var w = Open(Csv, "Singleton");
        try
        {
            var other = w.OpenFile!.Document.Sheets[1].Bindings.Count;
            Press(w);

            var file = w.OpenFile!;
            Assert.Empty(file.Document.Sheets[1].Bindings);
            Assert.Equal(other, file.Document.Sheets[2].Bindings.Count);
            Assert.Equal(Strings.Modes_ModeAdded, StatusText(w));
        }
        finally
        {
            w.OpenFile!.Dirty = false;
            var s = Settings.Load();
            s.Model = "FPS";
            Settings.Save(s);
            w.Close();
        }
    }

    static string Modes(int count) =>
        "QuadStick Configuration,Version 1.5,,game\n" + string.Concat(Enumerable.Range(1, count)
            .Select(i => $"Profile Name,,M{i}\n\nOutput or Function,Function,usb\nincrement_mode,normal,lip\n\n"));

    [AvaloniaFact]
    public void A_mode_past_the_limit_does_not_claim_the_side_tube_reaches_it()
    {
        var w = Open(Modes(16), "FPS");
        try
        {
            // Add after the last mode, so the new one lands at number 17.
            w.SelectSheetForPreview(15);
            Press(w);

            var status = StatusText(w);
            Assert.DoesNotContain(Strings.Modes_ModeAddedSideTube, status);
            Assert.Contains(QuadStick.Format.Strings.Issue_TheDeviceSupports16Modes, status);
        }
        finally
        {
            w.OpenFile!.Dirty = false;
            w.Close();
        }
    }

    [AvaloniaFact]
    public void A_mode_in_the_middle_says_the_last_one_fell_past_the_limit()
    {
        var w = Open(Modes(16), "FPS");
        try
        {
            w.SelectSheetForPreview(0);
            Press(w);

            var status = StatusText(w);
            Assert.Contains(Strings.Modes_ModeAddedSideTube, status);
            Assert.Contains(Strings.Modes_PushedPastLimit, status);
        }
        finally
        {
            w.OpenFile!.Dirty = false;
            w.Close();
        }
    }
}
