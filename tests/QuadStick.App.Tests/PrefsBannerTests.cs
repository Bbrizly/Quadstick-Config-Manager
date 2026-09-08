using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// A profile switch that changes usb emulation mode makes the QuadStick
// disconnect and re-enumerate its own drive, so Windows repairs the volume and
// prefs.csv is what the repair keeps eating. Home takes a spare copy whenever it
// sees a good one, and offers it back when it does not.
public sealed class PrefsBannerTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qcm-prefs-ui-" + Guid.NewGuid().ToString("N")[..8]);

    const string Good = "QuadStick Configuration,Version 0.5\nPreferences,,\nprefs.csv,,,,\nPreference,Value,\nenable_DS3_emulation,0,\n";

    string Drive(string? prefs)
    {
        var root = Path.Combine(_tmp, "QUADSTICK");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "default.csv"), "Profile Name,,Solo\ndefault.csv\n");
        if (prefs is not null) File.WriteAllText(Path.Combine(root, "prefs.csv"), prefs);
        return root;
    }

    string Backups => Path.Combine(_tmp, "backups");

    MainWindow HomeWith(string root)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);

        var w = new MainWindow { FindDeviceRoots = () => new[] { root }, BackupRoot = () => Backups };
        w.Show();
        w.ShowHomeForPreview();
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    static StackPanel Cards(MainWindow w) =>
        w.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "DeviceCards");

    static List<string> Lines(MainWindow w) =>
        Cards(w).GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();

    static Button? PutBack(MainWindow w) =>
        Cards(w).GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => (b.Content as string)?.StartsWith("Put back the copy", StringComparison.Ordinal) == true);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    [AvaloniaFact]
    public void AGoodPrefsFileSaysNothingAndIsCopied()
    {
        var root = Drive(Good);
        var w = HomeWith(root);

        Assert.DoesNotContain(Lines(w), l => l.Contains("settings file", StringComparison.Ordinal));
        Assert.True(PrefsGuard.HasSnapshot(Path.Combine(Backups, "prefs")));
    }

    [AvaloniaFact]
    public void ADamagedPrefsFileIsNamedOnScreen()
    {
        var w = HomeWith(Drive(""));
        Assert.Contains(Lines(w), l => l == Strings.Shell_PrefsBroken);
    }

    [AvaloniaFact]
    public void WithNoSpareItSaysSoRatherThanOfferingAButton()
    {
        var w = HomeWith(Drive("wrecked"));
        Assert.Contains(Lines(w), l => l == Strings.Shell_PrefsNoCopy);
        Assert.Null(PutBack(w));
    }

    // The whole point: the spare taken from a healthy visit is what comes back
    // after the damage, without the user touching Error checking or the file.
    [AvaloniaFact]
    public void TheSpareTakenEarlierGoesBackOnOneClick()
    {
        var root = Drive(Good);
        var w = HomeWith(root);                       // healthy visit takes the copy

        File.WriteAllText(Path.Combine(root, "prefs.csv"), ""); // the repair eats it
        w.ShowHomeForPreview();
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        var put = PutBack(w);
        Assert.NotNull(put);

        put!.Command?.Execute(null);
        put.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Good, File.ReadAllText(Path.Combine(root, "prefs.csv")));
        Assert.Equal(PrefsGuard.State.Healthy, PrefsGuard.Check(root));
    }

    // A file the device would refuse must never become the spare, or the button
    // hands back the damage.
    [AvaloniaFact]
    public void ADamagedFileIsNeverCopiedOverTheSpare()
    {
        var root = Drive(Good);
        HomeWith(root);
        File.WriteAllText(Path.Combine(root, "prefs.csv"), "");

        var w = HomeWith(root);
        Assert.NotNull(PutBack(w));
        Assert.Equal(Good, File.ReadAllText(PrefsGuard.SnapshotPath(Path.Combine(Backups, "prefs"))));
    }
}
