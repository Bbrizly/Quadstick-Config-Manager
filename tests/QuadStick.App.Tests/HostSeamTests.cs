using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// The seams another executable hosts this app through. Each one is load
// bearing for a separate program that this repo never builds and never sees,
// so a change that looks harmless here breaks something no test over there can
// catch first.
public class HostSeamTests
{
    static ProfileFile Solo() => ProfileFile.Load(
        "Profile Name,,Solo\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "mouse_left,normal,lip\n");

    // The default has to stay the free app's own choice: a host that never sets
    // StartWindow must be indistinguishable from one that does not exist.
    [AvaloniaFact]
    public void The_app_opens_the_window_it_always_did()
    {
        Assert.IsType<MainWindow>(App.StartWindow(null));
        Assert.IsType<GalleryWindow>(App.StartWindow(new[] { "--gallery" }));
    }

    [AvaloniaFact]
    public void A_host_can_open_its_own_window_instead()
    {
        var was = App.StartWindow;
        try
        {
            var host = new Window();
            App.StartWindow = _ => host;
            Assert.Same(host, App.StartWindow(null));
            Assert.Same(host, App.StartWindow(new[] { "--gallery" }));
        }
        finally { App.StartWindow = was; }
    }
    // A save is the only moment a host can record what the file looked like,
    // and nothing else tells it one happened.
    [AvaloniaFact]
    public async Task A_save_says_where_it_landed()
    {
        var dir = Directory.CreateTempSubdirectory("qscm-hostseam-").FullName;
        var path = Path.Combine(dir, "game.csv");
        File.WriteAllText(path, Solo().ToCsvText());

        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        string? said = null;
        w.ProfileSaved += p => said = p;
        try
        {
            w.OpenPath(path);
            Assert.True(await w.SaveProfileAsync());
            Assert.Equal(path, said);
        }
        finally
        {
            w.OpenFile!.Dirty = false;
            w.Close();
            Directory.Delete(dir, true);
        }
    }
    // The three statics a host redirects so a session's files land in its own
    // folder instead of the free app's. Settable and public is the whole
    // contract; this fails to compile if one of them stops being either.
    [AvaloniaFact]
    public void A_host_can_redirect_every_path_the_app_writes_to()
    {
        var settingsWas = Settings.PathOverride;
        var rescueWas = CrashGuard.RescueDirOverride;
        var libraryWas = MainWindow.LibraryDir;
        var dir = Directory.CreateTempSubdirectory("qscm-hostseam-").FullName;
        try
        {
            Settings.PathOverride = Path.Combine(dir, "settings.json");
            CrashGuard.RescueDirOverride = Path.Combine(dir, "rescue");
            MainWindow.LibraryDir = Path.Combine(dir, "profiles");

            Assert.StartsWith(dir, Settings.DefaultPath);
            Assert.StartsWith(dir, CrashGuard.RescueDir);
            Assert.StartsWith(dir, CrashGuard.CrashLogPath);
            Assert.StartsWith(dir, MainWindow.TemplatesDir);
        }
        finally
        {
            Settings.PathOverride = settingsWas;
            CrashGuard.RescueDirOverride = rescueWas;
            MainWindow.LibraryDir = libraryWas;
            Directory.Delete(dir, true);
        }
    }}
