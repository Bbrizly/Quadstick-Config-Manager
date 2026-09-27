using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

// The bridge is a separate process the app starts, reads and stops. What has
// to hold is the stop: a virtual pad left running after the app has gone is a
// controller somebody cannot let go of. The Rust side is tested on its own
// (crates/qs-bridge); these use a stand-in script so they run on any machine.
public class ControllerBridgeTests
{
    // The other half of qs-bridge's status_lines_are_the_protocol test.
    [Theory]
    [InlineData("{\"status\":\"waiting\"}", ControllerBridge.State.Waiting)]
    [InlineData("{\"status\":\"bridging\"}", ControllerBridge.State.Bridging)]
    [InlineData("{\"status\":\"no_access\"}", ControllerBridge.State.NoAccess)]
    [InlineData("{\"status\":\"no_virtual_pad\"}", ControllerBridge.State.NoVirtualPad)]
    [InlineData("{\"status\":\"unsupported\"}", ControllerBridge.State.Unsupported)]
    public void Each_status_line_is_read(string line, ControllerBridge.State want) =>
        Assert.Equal(want, ControllerBridge.Parse(line));

    // A newer bridge may say something this app has not learned. Ignoring it
    // keeps the last status true; guessing would not.
    [Theory]
    [InlineData("{\"status\":\"something_new\"}")]
    [InlineData("{\"state\":\"waiting\"}")]
    [InlineData("qs-bridge: waiting")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_ignored(string? line) => Assert.Null(ControllerBridge.Parse(line));

    // A stand-in bridge: says its first line, waits for stdin to close like the
    // real one, then leaves proof that it got to let go instead of being killed.
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    static (string Script, string Released) FakeBridge(string first, bool waitForStdin = true)
    {
        var dir = Directory.CreateTempSubdirectory("qs-bridge-test").FullName;
        var released = Path.Combine(dir, "released");
        var script = Path.Combine(dir, "qs-bridge");
        File.WriteAllText(script,
            "#!/bin/sh\n" +
            $"echo '{first}'\n" +
            (waitForStdin ? $"cat > /dev/null\necho yes > '{released}'\n" : "exit 1\n"));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (script, released);
    }

    static bool Eventually(Func<bool> done)
    {
        for (int i = 0; i < 250 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        return done();
    }

    [Fact]
    public void Stopping_closes_stdin_so_the_bridge_lets_go_itself()
    {
        if (OperatingSystem.IsWindows()) return;
        var (script, released) = FakeBridge("{\"status\":\"waiting\"}");
        var seen = new ConcurrentQueue<ControllerBridge.State>();
        var bridge = new ControllerBridge(script, seen.Enqueue);
        Assert.True(Eventually(() => seen.Contains(ControllerBridge.State.Waiting)));

        bridge.Dispose();

        Assert.True(File.Exists(released), "the bridge was killed before it could release the pad");
        Assert.DoesNotContain(ControllerBridge.State.Stopped, seen);
    }

    // A bridge that dies on its own has to say so, or the switch reads "on"
    // over a controller that is gone.
    [Fact]
    public void A_bridge_that_dies_says_it_stopped()
    {
        if (OperatingSystem.IsWindows()) return;
        var (script, _) = FakeBridge("{\"status\":\"waiting\"}", waitForStdin: false);
        var seen = new ConcurrentQueue<ControllerBridge.State>();
        using var bridge = new ControllerBridge(script, seen.Enqueue);
        Assert.True(Eventually(() => seen.Contains(ControllerBridge.State.Stopped)));
    }

    // Unsupported is the bridge's own last word, and "stopped" would bury it.
    [Fact]
    public void Unsupported_is_not_overwritten_by_stopped()
    {
        if (OperatingSystem.IsWindows()) return;
        var (script, _) = FakeBridge("{\"status\":\"unsupported\"}", waitForStdin: false);
        var seen = new ConcurrentQueue<ControllerBridge.State>();
        using var bridge = new ControllerBridge(script, seen.Enqueue);
        Assert.True(Eventually(() => seen.Contains(ControllerBridge.State.Unsupported)));
        Thread.Sleep(300);
        Assert.DoesNotContain(ControllerBridge.State.Stopped, seen);
    }

    static MainWindow OpenDevicePage()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.ControllerBridge = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.ShowDeviceSettingsForPreview("Preferences\nprefs.csv\nPreference,Value,Units,Description\n");
        w.UpdateLayout();
        return w;
    }

    static ToggleSwitch? Switch(MainWindow w) => w.GetVisualDescendants().OfType<ToggleSwitch>()
        .FirstOrDefault(t => AutomationProperties.GetName(t) == Strings.DevicePage_Bridge);

    // Where the bridge cannot run, a switch would promise something it cannot
    // do. The test machine has no bridge beside the app.
    [AvaloniaFact]
    public void No_switch_where_there_is_no_bridge()
    {
        ControllerBridge.PathOverride = null;
        var w = OpenDevicePage();
        Assert.Null(Switch(w));
        w.Close();
    }

    // The whole loop through the real page: on, it says what it is doing, and
    // closing the app stops the bridge the safe way.
    [AvaloniaFact]
    public void The_switch_runs_the_bridge_until_the_app_closes()
    {
        if (OperatingSystem.IsWindows()) return;
        var (script, released) = FakeBridge("{\"status\":\"bridging\"}");
        ControllerBridge.PathOverride = script;
        try
        {
            var w = OpenDevicePage();
            var toggle = Switch(w);
            Assert.NotNull(toggle);
            toggle!.IsChecked = true;
            Assert.True(Eventually(() => w.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => t.Text == Strings.DevicePage_BridgeOn && t.IsVisible)));
            Assert.True(Settings.Load().ControllerBridge);

            w.Close();
            Assert.True(File.Exists(released), "closing the app did not stop the bridge cleanly");
        }
        finally
        {
            ControllerBridge.PathOverride = null;
            var s = Settings.Load();
            s.ControllerBridge = false;
            Settings.Save(s);
        }
    }
}
