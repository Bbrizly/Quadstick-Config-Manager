using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using QuadStick.Format.Tests;
using Xunit;

namespace QuadStick.App.Tests;

// The strength test end to end against the fake console, with a person who
// breathes when the window tells them to and reaches a known strength. The
// button the gamepad reports is the firmware's rule: it fires when the
// threshold set for that direction is at or under what they can reach.
public class DeviceLinkWindowTests
{
    sealed class Person(FakeQuadStick device, DeviceLinkWindow window, int puff, int sip)
    {
        public LiveState Now()
        {
            var asked = window.PromptText;
            bool fires =
                (asked.Contains("Puff", StringComparison.Ordinal) && device.Prefs["puff_threshold"] <= puff) ||
                (asked.Contains("Sip", StringComparison.Ordinal) && device.Prefs["sip_threshold"] <= sip);
            return new LiveState(0, 0, fires ? new[] { 1 } : Array.Empty<int>(), "QuadStick",
                new HashSet<string>(), true);
        }
    }

    static (DeviceLinkWindow, FakeQuadStick, string) Open(int puff = 62, int sip = 37)
    {
        var device = new FakeQuadStick();
        var w = new DeviceLinkWindow(new MainWindow())
        {
            BreathWindow = TimeSpan.FromMilliseconds(150),
            SettleTime = TimeSpan.FromMilliseconds(1),
            BackupDir = Path.Combine(Path.GetTempPath(), "qcm-link-" + Guid.NewGuid().ToString("N")),
        };
        w.ConsoleForTest = new DeviceConsole(device) { Timeout = TimeSpan.FromMilliseconds(300) };
        var person = new Person(device, w, puff, sip);
        w.Live = person.Now;
        return (w, device, w.BackupDir);
    }

    [AvaloniaFact]
    public async Task The_strength_test_finds_the_persons_puff_and_sip_then_puts_everything_back()
    {
        var (w, device, dir) = Open(puff: 62, sip: 37);
        try
        {
            await w.MeasureNowAsync();

            Assert.Equal((62, 37), w.LastStrength);
            Assert.Contains("62%", w.StatusText);
            Assert.Contains("37%", w.StatusText);
            // Both ceilings held at 100 so the threshold never meets them.
            Assert.Contains("puff_maximum,100", device.Commands);
            Assert.Contains("sip_maximum,100", device.Commands);
            // Calibrated before the first breath.
            Assert.True(device.Commands.IndexOf("calibrate") < device.Commands.FindIndex(c => c.StartsWith("puff_threshold,", StringComparison.Ordinal)));
            // Seven breaths a direction, no more.
            Assert.InRange(device.Commands.Count(c => c.StartsWith("puff_threshold,", StringComparison.Ordinal)), 1, 7);
            Assert.InRange(device.Commands.Count(c => c.StartsWith("sip_threshold,", StringComparison.Ordinal)), 1, 7);
            // The test file is gone and the restart is the last thing sent.
            Assert.DoesNotContain(DeviceConsole.StrengthTestName, device.Files.Keys);
            Assert.Equal("boot", device.Commands[^1]);
            Assert.Equal("", w.PromptText);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task Someone_who_reaches_nothing_reads_as_under_the_bottom_not_zero()
    {
        var (w, device, dir) = Open(puff: 0, sip: 0);
        try
        {
            await w.MeasureNowAsync();
            Assert.Equal((StrengthSweep.Lowest - 1, StrengthSweep.Lowest - 1), w.LastStrength);
            Assert.Contains($"under {StrengthSweep.Lowest}%", w.StatusText);
            Assert.Equal("boot", device.Commands[^1]);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task Stop_still_restarts_the_QuadStick()
    {
        var (w, device, dir) = Open();
        try
        {
            var run = w.MeasureNowAsync();
            // Stop as soon as the first breath is asked for.
            var give = DateTime.UtcNow.AddSeconds(10);
            while ((w.PromptText.Length == 0 || w.PromptText.StartsWith("Hands", StringComparison.Ordinal))
                   && DateTime.UtcNow < give)
                await Task.Delay(5);
            Assert.StartsWith("Puff", w.PromptText);
            w.StopForTest();
            await run;

            Assert.Null(w.LastStrength);
            Assert.Equal(Strings.Link_Cancelled, w.StatusText);
            Assert.Equal("boot", device.Commands[^1]);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task Without_a_USB_reading_nothing_is_sent_to_the_QuadStick()
    {
        var (w, device, dir) = Open();
        w.Live = () => null;
        await w.MeasureNowAsync();
        Assert.Equal(Strings.Link_NeedsUsb, w.StatusText);
        Assert.Empty(device.Commands);
        Assert.False(Directory.Exists(dir));
    }

    [AvaloniaFact]
    public async Task Sending_a_profile_reports_where_the_old_one_went()
    {
        var (w, device, dir) = Open();
        device.Files["game.csv"] = "old\n";
        try
        {
            await w.SendFileAsync(ProfileFile.Load(
                "Profile Name,,Game,,,,,,,,\ngame.csv,,,,,,,,,,\nPlayStation Outputs,Function,usb,,,,,,,,\nx,normal,mp_center_puff,,,,,,,,\n"));
            Assert.Equal("load,game.csv", device.Commands[^1]);
            Assert.Contains("game.csv", w.StatusText);
            Assert.Contains(dir, w.StatusText);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public void The_device_page_has_a_named_way_in()
    {
        var w = new MainWindow();
        w.Show();
        w.ShowDeviceSettingsForPreview();
        var button = w.GetVisualDescendants().OfType<Button>()
            .SingleOrDefault(b => AutomationProperties.GetName(b) == Strings.Link_PageButtonName);
        Assert.NotNull(button);
    }
}
