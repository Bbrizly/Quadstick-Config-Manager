using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// A profile opened from anywhere else on the computer used to leave no trace:
// Save wrote it back where it came from and Home only listed the library, so
// the only way back was the Open dialog again.
public class RecentsTests
{
    static string TempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"qcm-{tag}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string WriteProfile(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, ProfileFile.NewFromTemplate(name).ToCsvText());
        return path;
    }

    static MainWindow NewWindow()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.Recents.Clear(); // start from a known list, not whatever this machine had
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        return w;
    }

    static IEnumerable<string> CardNames(MainWindow w, string panel) =>
        w.GetVisualDescendants().OfType<WrapPanel>()
            .First(p => p.Name == panel)
            .GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? "");

    // The whole point: open a file from outside the library and it is still
    // one click away next time Home is on screen.
    [AvaloniaFact]
    public void A_file_opened_from_outside_the_library_gets_a_card()
    {
        var lib = TempDir("lib");
        var away = TempDir("away");
        var path = WriteProfile(away, "fromdesktop.csv");
        var old = MainWindow.LibraryDir;
        MainWindow.LibraryDir = lib;
        try
        {
            var w = NewWindow();
            w.OpenPathForPreview(path);
            Dispatcher.UIThread.RunJobs();
            w.ShowHomeForPreview();
            Dispatcher.UIThread.RunJobs();

            var section = w.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "RecentSection");
            Assert.True(section.IsVisible);
            Assert.Contains("fromdesktop", CardNames(w, "RecentCards"));
            // The folder is named so two files with the same name stay apart.
            Assert.Contains(CardNames(w, "RecentCards"), t => t.Contains(Path.GetFileName(away)));
            w.Close();
        }
        finally
        {
            MainWindow.LibraryDir = old;
            Directory.Delete(lib, recursive: true);
            Directory.Delete(away, recursive: true);
        }
    }

    // A library file already has a card. Listing it twice would just be noise,
    // and the section hides itself when nothing is left to show.
    [AvaloniaFact]
    public void A_library_file_never_doubles_up_in_recents()
    {
        var lib = TempDir("lib");
        var path = WriteProfile(lib, "mygame.csv");
        var old = MainWindow.LibraryDir;
        MainWindow.LibraryDir = lib;
        try
        {
            var w = NewWindow();
            w.OpenPathForPreview(path);
            Dispatcher.UIThread.RunJobs();
            w.ShowHomeForPreview();
            Dispatcher.UIThread.RunJobs();

            Assert.Contains("mygame", CardNames(w, "LibraryCards"));
            Assert.False(w.GetVisualDescendants().OfType<StackPanel>()
                .First(p => p.Name == "RecentSection").IsVisible);
            w.Close();
        }
        finally { MainWindow.LibraryDir = old; Directory.Delete(lib, recursive: true); }
    }

    // A recent whose file is gone (a temp folder the system wiped) drops off
    // instead of showing a card that opens onto an error.
    [AvaloniaFact]
    public void A_deleted_recent_drops_off_the_list()
    {
        var lib = TempDir("lib");
        var away = TempDir("away");
        var path = WriteProfile(away, "gone.csv");
        var old = MainWindow.LibraryDir;
        MainWindow.LibraryDir = lib;
        try
        {
            var w = NewWindow();
            w.OpenPathForPreview(path);
            Dispatcher.UIThread.RunJobs();
            File.Delete(path);
            w.ShowHomeForPreview();
            Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain("gone", CardNames(w, "RecentCards"));
            w.Close();
        }
        finally
        {
            MainWindow.LibraryDir = old;
            Directory.Delete(lib, recursive: true);
            Directory.Delete(away, recursive: true);
        }
    }

    // Newest first, no duplicates, and the list stops growing.
    [AvaloniaFact]
    public void Recents_are_newest_first_deduped_and_capped()
    {
        var lib = TempDir("lib");
        var away = TempDir("away");
        var old = MainWindow.LibraryDir;
        MainWindow.LibraryDir = lib;
        try
        {
            var w = NewWindow();
            for (int i = 0; i < 10; i++)
            {
                w.OpenPathForPreview(WriteProfile(away, $"p{i}.csv"));
                Dispatcher.UIThread.RunJobs();
            }
            w.OpenPathForPreview(Path.Combine(away, "p0.csv")); // already seen, must not appear twice
            Dispatcher.UIThread.RunJobs();
            w.ShowHomeForPreview();
            Dispatcher.UIThread.RunJobs();

            var names = CardNames(w, "RecentCards").Where(t => t.StartsWith('p')).ToList();
            Assert.Equal(8, names.Count);
            Assert.Equal("p0", names[0]);
            Assert.Single(names, "p0");
            w.Close();
        }
        finally
        {
            MainWindow.LibraryDir = old;
            Directory.Delete(lib, recursive: true);
            Directory.Delete(away, recursive: true);
        }
    }
}
