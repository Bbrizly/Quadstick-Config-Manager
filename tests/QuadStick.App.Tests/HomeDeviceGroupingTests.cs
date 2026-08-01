using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

// Home used to flatten every mounted QuadStick into one alphabetical list, so
// with two plugged in there was nothing saying which stick a profile came from.
public sealed class HomeDeviceGroupingTests : IDisposable
{
    readonly List<string> _roots = new();

    // A drive the app will accept: default.csv is what marks a QuadStick.
    string Drive(string name, params string[] profiles)
    {
        var root = Path.Combine(Path.GetTempPath(), "qcm-home-" + Guid.NewGuid().ToString("N")[..8], name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "default.csv"), "Profile Name,,Solo\ndefault.csv\n");
        foreach (var p in profiles)
            File.WriteAllText(Path.Combine(root, p), "Profile Name,,Solo\n" + p + "\n");
        _roots.Add(root);
        return root;
    }

    static MainWindow HomeWith(params string[] roots)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);

        var w = new MainWindow { FindDeviceRoots = () => roots };
        w.Show();
        w.ShowHomeForPreview();
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    static StackPanel Cards(MainWindow w) =>
        w.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "DeviceCards");

    static List<string> Headings(MainWindow w) =>
        Cards(w).Children.OfType<TextBlock>().Select(t => t.Text ?? "").ToList();

    [AvaloniaFact]
    public void TwoQuadSticks_GetAHeadingEach_WithTheirOwnCards()
    {
        var a = Drive("QUADSTICK", "aaa.csv");
        var b = Drive("QUADSTICK2", "zzz.csv");

        var w = HomeWith(a, b);

        var headings = Headings(w);
        Assert.Equal(2, headings.Count);
        Assert.Contains(headings, h => h.Contains(a));
        Assert.Contains(headings, h => h.Contains(b));

        // Two card groups, one per drive, and neither holds the other's file.
        var groups = Cards(w).Children.OfType<WrapPanel>().ToList();
        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.NotEmpty(g.Children));
    }

    [AvaloniaFact]
    public void OneQuadStick_GetsNoHeading()
    {
        var only = Drive("QUADSTICK", "aaa.csv", "zzz.csv");

        var w = HomeWith(only);

        // A heading over the only group is noise, so the single-device screen
        // must look exactly as it did before grouping existed.
        Assert.Empty(Headings(w));
        Assert.Single(Cards(w).Children.OfType<WrapPanel>());
    }

    [AvaloniaFact]
    public void AnUnreadableDrive_DropsOff_AndTheOtherStillShows()
    {
        var good = Drive("QUADSTICK", "aaa.csv");
        var gone = Path.Combine(Path.GetTempPath(), "qcm-home-missing-" + Guid.NewGuid().ToString("N")[..8]);

        var w = HomeWith(gone, good);

        // The missing drive contributes nothing, so no heading is needed for
        // the one that is left.
        Assert.Empty(Headings(w));
        Assert.Single(Cards(w).Children.OfType<WrapPanel>());
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(Path.GetDirectoryName(root)!, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
