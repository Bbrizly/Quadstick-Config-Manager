using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

// The sandboxed Mac build can only read a QuadStick the person picked, and a
// bookmark is what carries that pick to the next launch. The sandbox itself
// cannot run here, so this proves the Foundation calls: a bookmark made for a
// folder resolves back to it, and one that is junk is left alone.
public class MacDriveAccessTests
{
    [Fact]
    public void A_remembered_drive_resolves_back_to_the_same_folder()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var old = Settings.PathOverride;
        var dir = Directory.CreateTempSubdirectory("qcm-drive").FullName;
        Settings.PathOverride = Path.Combine(dir, "settings.json");
        try
        {
            var stick = Directory.CreateDirectory(Path.Combine(dir, "QUAD STICK")).FullName;
            Assert.True(MacDriveAccess.Remember(stick));
            Assert.True(MacDriveAccess.Remember(stick + "/"));
            var lines = File.ReadAllLines(MacDriveAccess.StorePath);
            Assert.Single(lines);
            // macOS hands back /private/var for /var, so compare the folder itself.
            var back = MacDriveAccess.Resolve(lines[0], out _)!;
            Assert.EndsWith(stick, back, StringComparison.Ordinal);
            Assert.True(Directory.Exists(back));

            Assert.Null(MacDriveAccess.Resolve("not a bookmark", out _));
            Assert.Null(MacDriveAccess.Resolve(Convert.ToBase64String(new byte[] { 1, 2, 3 }), out _));
            MacDriveAccess.Restore();
        }
        finally { Settings.PathOverride = old; }
    }
}

// What a person on the Mac App Store build sees: a plugged-in stick the sandbox
// will not let the app read, and one button that fixes it for good.
public class StoreDriveButtonTests
{
    static MainWindow Home(Func<IReadOnlyList<string>> roots)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow { FindDeviceRoots = roots };
        w.Show();
        w.ShowHomeForPreview();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return w;
    }

    static Avalonia.Controls.Button? Choose(MainWindow w) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w).OfType<Avalonia.Controls.Button>()
            .FirstOrDefault(b => b.Content as string == Strings.Install_ChooseTheQuadStickDrive && b.IsEffectivelyVisible);

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void A_normal_build_never_shows_the_button()
    {
        var w = Home(Array.Empty<string>);
        Assert.Null(Choose(w));
        w.Close();
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task Choosing_the_drive_once_lists_it_and_remembers_it()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var oldPath = Settings.PathOverride;
        var dir = Directory.CreateTempSubdirectory("qcm-store").FullName;
        var stick = Directory.CreateDirectory(Path.Combine(dir, "QUADSTICK")).FullName;
        File.WriteAllText(Path.Combine(stick, "default.csv"), "Profile Name,,Solo\n");
        MacGamepad.Sandboxed = true;
        try
        {
            bool granted = false;
            var w = Home(() => granted ? new[] { stick } : Array.Empty<string>());
            Settings.PathOverride = Path.Combine(dir, "settings.json");

            // A folder that is not a QuadStick is refused, in words, and nothing is kept.
            w.PickDriveFolder = () => Task.FromResult<string?>(dir);
            Assert.False(await w.ChooseDriveAsync(_ => { }));
            Assert.False(File.Exists(MacDriveAccess.StorePath));

            w.PickDriveFolder = () => { granted = true; return Task.FromResult<string?>(stick); };
            Assert.NotNull(Choose(w));
            Choose(w)!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            for (int i = 0; i < 50 && Choose(w) is not null; i++) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }

            Assert.Null(Choose(w));   // the stick is listed now, so the offer is gone
            Assert.Single(File.ReadAllLines(MacDriveAccess.StorePath));
            w.Close();
        }
        finally
        {
            MacGamepad.Sandboxed = false;
            Settings.PathOverride = oldPath;
        }
    }
}
