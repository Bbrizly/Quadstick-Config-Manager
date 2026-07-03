using QuadStick.Format;
using Xunit;

public class InstallCleanupTests
{
    [Fact]
    public void Install_LeavesNoTempFile_AndWritesTarget()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "default.csv"), "QuadStick Configuration File,\n,mygame.csv\n");
        var file = ProfileFile.NewFromTemplate("mygame.csv");
        var backups = Directory.CreateTempSubdirectory().FullName;

        var result = Device.Install(file, dir, backups);

        Assert.Empty(Directory.GetFiles(dir, "*.qscm-tmp"));
        Assert.True(File.Exists(result.InstalledPath));
    }

    // Forces a genuine mid-swap throw with no backup available (first install of this
    // filename), by making the target path an existing directory: File.Exists(target)
    // is false so the backup step is skipped, the tmp write/readback succeed, but
    // File.Move(tmp, target) throws because target is a directory. The restore-from-
    // backup catch clause requires backup != null, so it doesn't apply here — this is
    // exactly the case the try/finally must still catch to avoid orphaning the tmp file.
    [Fact]
    public void Install_move_failure_with_no_backup_still_cleans_up_tmp()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "default.csv"), "QuadStick Configuration File,\n,mygame.csv\n");
        var backups = Directory.CreateTempSubdirectory().FullName;

        // Target exists as a directory, not a file, so File.Exists(target) is false
        // (no backup taken) but File.Move onto it will still throw.
        Directory.CreateDirectory(Path.Combine(dir, "mygame.csv"));

        var file = ProfileFile.NewFromTemplate("mygame.csv");

        Assert.ThrowsAny<Exception>(() => Device.Install(file, dir, backups));
        Assert.Empty(Directory.GetFiles(dir, "*.qscm-tmp"));
    }
}
