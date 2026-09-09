using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
    // Changing the language rebuilds the window, and the rebuild hands the new
    // window the main-window role. Under a host that role belongs to the host,
    // and an editor taking it closes the host's own screen when the editor
    // closes. The lifetime cannot be installed headlessly, so the decision is
    // driven directly.
    [AvaloniaFact]
    public void A_rebuilt_editor_leaves_a_hosts_main_window_alone()
    {
        var w = new MainWindow();
        var next = new MainWindow();
        var host = new Window();
        var desktop = new ClassicDesktopStyleApplicationLifetime { MainWindow = host };

        w.HandOverMainWindow(desktop, next);

        Assert.Same(host, desktop.MainWindow);
    }

    // The other half: the free app IS the main window, so the rebuild still has
    // to carry the role over or closing the old window shuts the app down.
    [AvaloniaFact]
    public void A_rebuilt_editor_still_takes_over_the_apps_own_main_window()
    {
        var w = new MainWindow();
        var next = new MainWindow();
        var desktop = new ClassicDesktopStyleApplicationLifetime { MainWindow = w };

        w.HandOverMainWindow(desktop, next);

        Assert.Same(next, desktop.MainWindow);
    }
    // Changing the language throws the editor away and builds another. A host
    // holding the old one hears no more saves, so both the subscription and
    // the news of the swap have to cross over. Without this a clinic records
    // snapshots until the day somebody picks French, and then silently stops.
    //
    // Switched away and back rather than switched once: the second rebuild is
    // what proves the subscriptions keep travelling, and it puts the process
    // back in its own language through the app's own path. Doing that in a
    // finally after an await does not work, because the save hops threads and
    // CurrentUICulture is per thread, so the restore lands on the wrong one and
    // the rest of the suite runs in French.
    [AvaloniaFact]
    public async Task A_rebuilt_editor_still_belongs_to_its_host()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.Language = Localization.FollowSystem;
        Settings.Save(s);

        var dir = Directory.CreateTempSubdirectory("qscm-hostseam-").FullName;
        var path = Path.Combine(dir, "game.csv");
        File.WriteAllText(path, Solo().ToCsvText());

        var w = new MainWindow();
        w.Show();
        var told = new List<MainWindow>();
        string? said = null;
        w.ProfileSaved += p => said = p;
        w.EditorReplaced += n => told.Add(n);

        MainWindow? last = null;
        try
        {
            w.OpenPath(path);
            var french = w.SetLanguage("fr");
            last = french.SetLanguage(Localization.FollowSystem);

            Assert.Equal(new[] { french, last }, told);     // the host was told, twice
            Assert.True(await last.SaveProfileAsync());
            Assert.Equal(path, said);                       // and still hears saves
        }
        finally
        {
            var s2 = Settings.Load();
            s2.Language = Localization.FollowSystem;
            Settings.Save(s2);
            if (last?.OpenFile is { } f) f.Dirty = false;
            last?.Close();
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
