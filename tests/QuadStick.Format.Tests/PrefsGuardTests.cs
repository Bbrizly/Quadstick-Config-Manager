using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// The device's own reader is the standard here: Load_Preferences_File wants
// "QuadStick" in the first nine characters and three more lines after it, and
// refuses anything else ("Prefs.csv too short"). These pin that, and pin that a
// file the device would refuse can never overwrite the spare copy.
public class PrefsGuardTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "qscm-prefs-" + Guid.NewGuid().ToString("N"));
    string Drive => Path.Combine(_dir, "drive");
    string Snapshots => Path.Combine(_dir, "snapshots");
    string Backups => Path.Combine(_dir, "backups");

    public PrefsGuardTests() => Directory.CreateDirectory(Drive);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } GC.SuppressFinalize(this); }

    const string Good = "QuadStick Configuration,Version 0.5\nPreferences,,\nprefs.csv,,,,\nPreference,Value,\nenable_DS3_emulation,2,\n";

    void Write(string text) => File.WriteAllText(Path.Combine(Drive, "prefs.csv"), text);

    [Fact]
    public void NoFileIsMissingNotBroken()
        => Assert.Equal(PrefsGuard.State.Missing, PrefsGuard.Check(Drive));

    [Fact]
    public void AFileTheDeviceWouldLoadIsHealthy()
    {
        Write(Good);
        Assert.Equal(PrefsGuard.State.Healthy, PrefsGuard.Check(Drive));
    }

    // What chkdsk leaves: the entry survives, the contents do not.
    [Theory]
    [InlineData("")]
    [InlineData("\0\0\0\0\0\0\0\0\0\0\0\0")]
    [InlineData("QuadStick Configuration,Version 0.5\n")]
    [InlineData("QuadStick Configuration\nPreferences,,\nprefs.csv,,,,\n")]
    [InlineData("Preferences,,\nprefs.csv,,,,\nPreference,Value,\nenable_DS3_emulation,2,\n")]
    public void AFileTheDeviceWouldRefuseIsBroken(string text)
    {
        Write(text);
        Assert.Equal(PrefsGuard.State.Broken, PrefsGuard.Check(Drive));
    }

    [Fact]
    public void ABrokenFileNeverOverwritesTheSpare()
    {
        Write(Good);
        Assert.True(PrefsGuard.TrySnapshot(Drive, Snapshots));

        Write(""); // the corruption arrives
        Assert.False(PrefsGuard.TrySnapshot(Drive, Snapshots));
        Assert.Equal(Good, File.ReadAllText(PrefsGuard.SnapshotPath(Snapshots)));
    }

    [Fact]
    public void RestorePutsBackTheExactBytesAndKeepsTheBrokenOne()
    {
        Write(Good);
        PrefsGuard.TrySnapshot(Drive, Snapshots);
        Write("wrecked");

        PrefsGuard.Restore(Drive, Snapshots, Backups);

        Assert.Equal(Good, File.ReadAllText(Path.Combine(Drive, "prefs.csv")));
        Assert.Equal(PrefsGuard.State.Healthy, PrefsGuard.Check(Drive));
        var kept = Directory.GetFiles(Backups, "*-broken-prefs.csv");
        Assert.Single(kept);
        Assert.Equal("wrecked", File.ReadAllText(kept[0]));
    }

    [Fact]
    public void RestoreWorksWhenChkdskDeletedTheFileOutright()
    {
        Write(Good);
        PrefsGuard.TrySnapshot(Drive, Snapshots);
        File.Delete(Path.Combine(Drive, "prefs.csv"));

        PrefsGuard.Restore(Drive, Snapshots, Backups);
        Assert.Equal(Good, File.ReadAllText(Path.Combine(Drive, "prefs.csv")));
    }

    [Fact]
    public void RestoreWithNoSpareSaysSoAndLeavesTheDeviceAlone()
    {
        Write("wrecked");
        Assert.Throws<InvalidOperationException>(() => PrefsGuard.Restore(Drive, Snapshots, Backups));
        Assert.Equal("wrecked", File.ReadAllText(Path.Combine(Drive, "prefs.csv")));
    }

    [Fact]
    public void RestoreLeavesNoTempBehind()
    {
        Write(Good);
        PrefsGuard.TrySnapshot(Drive, Snapshots);
        Write("wrecked");
        PrefsGuard.Restore(Drive, Snapshots, Backups);
        Assert.Empty(Directory.GetFiles(Drive, "*.qscm-tmp*"));
    }
}
