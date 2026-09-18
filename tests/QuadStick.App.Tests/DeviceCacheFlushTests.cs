using System.Runtime.CompilerServices;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// The QuadStick's drive keeps the last 4KB page it was sent in RAM and only
// writes it to flash about 1.5s later (FlashManager.c, Cache_timer = 12000,
// decremented in sound.c's Timer 1 interrupt). Nothing the host can send
// shortens that: SCSI.c answers no SYNCHRONIZE CACHE and no START STOP UNIT,
// and disk_read hands the dirty page straight back, so a readback passes on
// bytes that are not on the flash yet. Pull the device inside the window and
// the lost page is usually FAT or root directory, which breaks files nobody
// touched. Every path that writes to the device therefore has to wait before
// it says anything that reads as "done", because "done" is when people unplug.
public class DeviceCacheFlushTests
{
    [Fact]
    public void WaitCoversTheFirmwareCacheTimer()
    {
        Assert.True(Device.CacheFlushWait >= TimeSpan.FromMilliseconds(1500),
            $"CacheFlushWait is {Device.CacheFlushWait.TotalMilliseconds}ms, under the firmware's 1.5s flush delay.");
    }

    [Theory]
    [InlineData("InstallFlow.cs", "Device.Install(")]
    [InlineData("DeviceFilesWindow.cs", "Device.DeleteProfile(")]
    public void EveryDeviceWriteWaitsBeforeItReportsDone(string file, string call)
    {
        var source = File.ReadAllText(Path.Combine(SrcDir(), file));
        var wrote = source.IndexOf(call, StringComparison.Ordinal);
        Assert.True(wrote >= 0, $"{file} no longer calls {call}.");

        var waited = source.IndexOf("Task.Delay(Device.CacheFlushWait)", wrote, StringComparison.Ordinal);
        Assert.True(waited >= 0, $"{file} writes to the device and never waits out Device.CacheFlushWait.");

        // The wait has to come before the user is told it worked, not after.
        var told = source.IndexOf("Strings.Install_SafeToUnplug", wrote, StringComparison.Ordinal);
        Assert.True(told > waited, $"{file} says it is safe to unplug before waiting for the flush.");
    }

    static string SrcDir([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "QuadStick.App"));
}
