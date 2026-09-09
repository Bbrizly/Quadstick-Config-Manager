using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace QuadStick.App.Tests;

// The seams another executable hosts this app through. Each one is load
// bearing for a separate program that this repo never builds and never sees,
// so a change that looks harmless here breaks something no test over there can
// catch first.
public class HostSeamTests
{    // The default has to stay the free app's own choice: a host that never sets
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
