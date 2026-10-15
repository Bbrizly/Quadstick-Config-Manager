using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// macOS and Linux hold writes to a FAT stick in memory for seconds, so every
// write to the device has to be pushed out before the unplug wait starts.
// Flush is swapped for a recorder, so these run one at a time. Other classes
// install in parallel, so the recorder keeps only calls under its own stick.
[Collection(nameof(DeviceFlushTests))]
public class DeviceFlushTests
{
    static List<(string Path, string? Text)> Record(string stick)
    {
        var calls = new List<(string, string?)>();
        Device.Flush = p =>
        {
            if (p.StartsWith(stick, StringComparison.Ordinal))
                lock (calls) calls.Add((p, File.Exists(p) ? File.ReadAllText(p) : null));
        };
        return calls;
    }

    static string Stick()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "default.csv"), "QuadStick Configuration File,\n,mygame.csv\n");
        return dir;
    }

    [Fact]
    public void Install_flushes_the_profile_once_it_is_in_place()
    {
        var stick = Stick();
        var calls = Record(stick);
        try
        {
            var result = Device.Install(ProfileFile.NewFromTemplate("mygame.csv"), stick,
                Directory.CreateTempSubdirectory().FullName);

            var (path, text) = Assert.Single(calls);
            Assert.Equal(result.InstalledPath, path);
            Assert.Equal(File.ReadAllText(result.InstalledPath), text);
        }
        finally { Device.Flush = Device.FlushToDevice; }
    }

    [Fact]
    public void Delete_flushes_so_the_removal_reaches_the_stick()
    {
        var stick = Stick();
        File.WriteAllText(Path.Combine(stick, "old.csv"), "x");
        var calls = Record(stick);
        try
        {
            var result = Device.DeleteProfile(stick, "old.csv", Directory.CreateTempSubdirectory().FullName);

            Assert.Equal((result.DeletedPath, (string?)null), Assert.Single(calls));
        }
        finally { Device.Flush = Device.FlushToDevice; }
    }

    [Fact]
    public void Prefs_restore_flushes_the_restored_file()
    {
        var stick = Stick();
        var snapshots = Directory.CreateTempSubdirectory().FullName;
        const string prefs = "QuadStick Preferences,\nversion,2\nenable_usb_comm,0\nmouse_speed,50\n";
        File.WriteAllText(Path.Combine(stick, PrefsGuard.FileName), prefs);
        Assert.True(PrefsGuard.TrySnapshot(stick, snapshots));
        File.WriteAllText(Path.Combine(stick, PrefsGuard.FileName), "broken");
        var calls = Record(stick);
        try
        {
            var target = PrefsGuard.Restore(stick, snapshots, Directory.CreateTempSubdirectory().FullName);

            Assert.Equal((target, (string?)prefs), Assert.Single(calls));
        }
        finally { Device.Flush = Device.FlushToDevice; }
    }

    [Fact]
    public void The_real_flush_runs_on_a_file_and_on_a_missing_one()
    {
        var path = Path.Combine(Stick(), "default.csv");
        Device.FlushToDevice(path);
        Device.FlushToDevice(path + ".gone");
        Assert.Equal("QuadStick Configuration File,\n,mygame.csv\n", File.ReadAllText(path));
    }
}
