using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// File names on the stick set the order the profile switch walks, so people
// number them and then cannot tell "3.csv" from "4.csv". The name inside the
// file says which is which, and until now only the window title showed it.
public class ProfileTitleTests
{
    const string WithHeader =
        "QuadStick Configuration,Version 1.5,142Em6Lyr8zT0f3RNI1rjBw92MQWpehcOAuvU6sHzxds,Grand Theft Auto\n" +
        "Profile Name,,Driving\n3.csv\nGamepad,Function,\nbutton_a,,sip\n";

    const string NoHeader = "Profile Name,,Elden Ring\n4.csv\nGamepad,Function,\nbutton_a,,sip\n";

    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qcm-title-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static MainWindow NewWindow()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.Recents.Clear();
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        return w;
    }

    static IEnumerable<string> CardTexts(MainWindow w) =>
        w.GetVisualDescendants().OfType<WrapPanel>()
            .First(p => p.Name == "LibraryCards")
            .GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? "");

    [AvaloniaFact]
    public void A_library_card_shows_the_name_inside_the_file_next_to_the_file_name()
    {
        var lib = TempDir();
        File.WriteAllText(Path.Combine(lib, "3.csv"), WithHeader);
        File.WriteAllText(Path.Combine(lib, "4.csv"), NoHeader);
        var old = MainWindow.LibraryDir;
        MainWindow.LibraryDir = lib;
        try
        {
            var w = NewWindow();
            w.ShowHomeForPreview();
            Dispatcher.UIThread.RunJobs();

            var texts = CardTexts(w).ToList();
            Assert.Contains("3", texts); // the file name still leads the card
            Assert.Contains(texts, t => t.StartsWith("Grand Theft Auto · "));
            // No version header: the first mode's name is the closest thing the
            // file has to a title, and it is still better than "4".
            Assert.Contains(texts, t => t.StartsWith("Elden Ring · "));
            w.Close();
        }
        finally { MainWindow.LibraryDir = old; Directory.Delete(lib, recursive: true); }
    }

    // The other half of telling twenty numbered files apart: which push of the
    // profile switch lands on this one. The Manage files window has always had
    // that list; Home showed nothing, so the count had to be done by hand.
    [AvaloniaFact]
    public void A_device_card_carries_its_number_in_the_profile_switch_order()
    {
        var stick = TempDir();
        foreach (var f in new[] { "default.csv", "B21.csv", "prefs.csv" })
            File.WriteAllText(Path.Combine(stick, f), $"Profile Name,,Solo\n{f}\n");
        var lib = TempDir();
        var old = MainWindow.LibraryDir;
        MainWindow.LibraryDir = lib;
        try
        {
            var w = NewWindow();
            w.FindDeviceRoots = () => new[] { stick };
            w.ShowHomeForPreview();
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();

            var texts = w.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "DeviceCards")
                .GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            // default.csv is always the first file the switch reaches, however
            // late its name sorts, and prefs.csv is settings rather than a
            // profile so it is never in the count. The cards are laid out in
            // that order too: numbers that jump about read as a bug.
            var headings = texts.Where(t => !t.Contains("mode sheet")).ToList();
            Assert.Equal(new[] { "1. default", "2. B21", "prefs" }, headings);
            w.Close();
        }
        finally
        {
            MainWindow.LibraryDir = old;
            Directory.Delete(lib, recursive: true);
            Directory.Delete(stick, recursive: true);
        }
    }

    // The title is a second name, not a replacement: repeating the file name
    // back would just take up the line that says what is in the file.
    [Fact]
    public void A_title_that_only_repeats_the_file_name_is_left_out()
    {
        var doc = Parser.Parse("Profile Name,,mygame\nmygame.csv\n").Doc;
        Assert.Equal("", MainWindow.TitleNote(doc, "/somewhere/MyGame.csv"));
        Assert.Equal("mygame · ", MainWindow.TitleNote(doc, "/somewhere/3.csv"));
    }

    // The sheet title beats the first mode's name when the file carries both,
    // the same way the window title picks.
    [Fact]
    public void The_sheet_title_wins_over_the_first_mode_name()
    {
        Assert.Equal("Grand Theft Auto", Parser.Parse(WithHeader).Doc.Title);
        Assert.Equal("Elden Ring", Parser.Parse(NoHeader).Doc.Title);
        Assert.Equal("", Parser.Parse("Preferences\n").Doc.Title);
    }
}
