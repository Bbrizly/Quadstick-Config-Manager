using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

// The app opens filling the screen, and keeps filling it until somebody
// un-maximizes and closes. The third test is the one that protects everything
// else: RememberWindow off is the plain default window that the screenshot
// tool and every other test in this project measure against, so maximizing
// must never reach it.
public sealed class WindowFillsScreenTests : IDisposable
{
    readonly AppSettings _saved = Settings.Load();

    // These tests write real settings, and RememberWindow on means closing a
    // window writes more. Put back what was there or the next test reads them.
    public void Dispose() => Settings.Save(_saved);

    static MainWindow Open(Action<AppSettings> set)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        set(s);
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.UpdateLayout();
        return w;
    }

    [AvaloniaFact]
    public void AFreshInstallOpensMaximized()
    {
        var w = Open(s => { s.RememberWindow = true; s.WinMax = true; s.WinW = null; s.WinH = null; });
        Assert.Equal(WindowState.Maximized, w.WindowState);
        w.Close();
    }

    [AvaloniaFact]
    public void AWindowLeftSmallComesBackSmall()
    {
        var w = Open(s =>
        {
            s.RememberWindow = true;
            s.WinMax = false;
            s.WinW = 1000; s.WinH = 700;
        });
        Assert.Equal(WindowState.Normal, w.WindowState);
        Assert.Equal(1000, w.Width);
        w.Close();
    }

    [AvaloniaFact]
    public void RememberWindowOffLeavesThePlainDefaultWindow()
    {
        var w = Open(s => { s.RememberWindow = false; s.WinMax = true; });
        Assert.Equal(WindowState.Normal, w.WindowState);
        w.Close();
    }
}
