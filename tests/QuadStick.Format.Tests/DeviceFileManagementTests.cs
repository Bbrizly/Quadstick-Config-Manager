using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// Delete removes a file from a disabled user's device and cannot be undone from
// the device, so these tests care about one thing above all: nothing is removed
// unless every rule passed and a backup already exists on disk.
public class DeviceFileManagementTests
{
    static string NewDeviceRoot()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "default.csv"), "QuadStick Configuration File,\n");
        return dir;
    }

    static string NewBackupDir() => Directory.CreateTempSubdirectory().FullName;

    static string WriteProfile(string root, string name, string text = "profile\n")
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Theory]
    [InlineData("default.csv")]
    [InlineData("Default.CSV")]
    [InlineData("DEFAULT.csv")]
    [InlineData("prefs.csv")]
    [InlineData("Prefs.Csv")]
    [InlineData("PREFS.CSV")]
    public void Protected_names_are_rejected_before_any_backup_or_delete(string name)
    {
        var root = NewDeviceRoot();
        var backups = Path.Combine(Directory.CreateTempSubdirectory().FullName, "backups");
        var target = WriteProfile(root, name);

        var ex = Assert.Throws<InvalidOperationException>(
            () => Device.DeleteProfile(root, name, backups));

        Assert.Contains("protected", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(target), "the protected file must still be on the device");
        Assert.False(Directory.Exists(backups), "no backup work may start for a protected name");
    }

    [Fact]
    public void Path_traversal_is_rejected()
    {
        var root = NewDeviceRoot();
        var backups = NewBackupDir();
        var outside = Path.Combine(Directory.GetParent(root)!.FullName, "outside.csv");
        File.WriteAllText(outside, "outside\n");

        Assert.Throws<InvalidOperationException>(
            () => Device.DeleteProfile(root, "../outside.csv", backups));

        Assert.True(File.Exists(outside), "a file outside the device root must survive");
        Assert.Empty(Directory.GetFiles(backups));
    }

    [Fact]
    public void Subdirectory_target_is_rejected()
    {
        var root = NewDeviceRoot();
        var backups = NewBackupDir();
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        var nested = WriteProfile(root, Path.Combine("sub", "game.csv"));

        Assert.Throws<InvalidOperationException>(
            () => Device.DeleteProfile(root, Path.Combine("sub", "game.csv"), backups));

        Assert.True(File.Exists(nested));
        Assert.Empty(Directory.GetFiles(backups));
    }

    [Fact]
    public void Absolute_path_is_rejected()
    {
        var root = NewDeviceRoot();
        var backups = NewBackupDir();
        var target = WriteProfile(root, "game.csv");

        Assert.Throws<InvalidOperationException>(
            () => Device.DeleteProfile(root, target, backups));

        Assert.True(File.Exists(target));
        Assert.Empty(Directory.GetFiles(backups));
    }

    [Fact]
    public void Non_csv_file_is_rejected()
    {
        var root = NewDeviceRoot();
        var backups = NewBackupDir();
        var target = WriteProfile(root, "readme.txt");

        Assert.Throws<InvalidOperationException>(
            () => Device.DeleteProfile(root, "readme.txt", backups));

        Assert.True(File.Exists(target));
        Assert.Empty(Directory.GetFiles(backups));
    }

    [Fact]
    public void Root_without_default_csv_is_rejected()
    {
        var notADevice = Directory.CreateTempSubdirectory().FullName;
        var backups = NewBackupDir();
        var target = WriteProfile(notADevice, "game.csv");

        Assert.Throws<InvalidOperationException>(
            () => Device.DeleteProfile(notADevice, "game.csv", backups));

        Assert.True(File.Exists(target));
        Assert.Empty(Directory.GetFiles(backups));
    }

    [Fact]
    public void Missing_file_is_rejected()
    {
        var root = NewDeviceRoot();
        var backups = NewBackupDir();

        Assert.Throws<InvalidOperationException>(
            () => Device.DeleteProfile(root, "gone.csv", backups));

        Assert.Empty(Directory.GetFiles(backups));
    }

    // Backup failure is injected by pointing backupDir at a path whose parent is
    // an existing file, so Directory.CreateDirectory cannot make it.
    [Fact]
    public void Backup_failure_leaves_the_source_file_untouched()
    {
        var root = NewDeviceRoot();
        var target = WriteProfile(root, "game.csv", "keep me\n");

        var blocker = Path.Combine(Directory.CreateTempSubdirectory().FullName, "not-a-dir");
        File.WriteAllText(blocker, "x");
        var backups = Path.Combine(blocker, "backups");

        Assert.ThrowsAny<IOException>(() => Device.DeleteProfile(root, "game.csv", backups));

        Assert.True(File.Exists(target), "no backup means no delete");
        Assert.Equal("keep me\n", File.ReadAllText(target));
    }

    [Fact]
    public void Successful_delete_backs_up_and_removes_only_the_exact_target()
    {
        var root = NewDeviceRoot();
        var backups = NewBackupDir();
        var target = WriteProfile(root, "game.csv", "game body\n");
        var neighbour = WriteProfile(root, "other.csv", "other body\n");

        var result = Device.DeleteProfile(root, "game.csv", backups);

        Assert.Equal(target, result.DeletedPath);
        Assert.False(File.Exists(target));
        Assert.True(File.Exists(result.BackupPath));
        Assert.Equal("game body\n", File.ReadAllText(result.BackupPath));
        Assert.Equal(backups, Path.GetDirectoryName(result.BackupPath));
        Assert.EndsWith("game.csv", result.BackupPath, StringComparison.Ordinal);

        Assert.True(File.Exists(neighbour), "other profiles must be untouched");
        Assert.True(File.Exists(Path.Combine(root, "default.csv")), "default.csv must be untouched");
    }

    [Fact]
    public void Two_deletes_of_the_same_name_keep_two_distinct_backups()
    {
        var root = NewDeviceRoot();
        var backups = NewBackupDir();

        WriteProfile(root, "game.csv", "first\n");
        var first = Device.DeleteProfile(root, "game.csv", backups);

        WriteProfile(root, "game.csv", "second\n");
        var second = Device.DeleteProfile(root, "game.csv", backups);

        Assert.NotEqual(first.BackupPath, second.BackupPath);
        Assert.Equal("first\n", File.ReadAllText(first.BackupPath));
        Assert.Equal("second\n", File.ReadAllText(second.BackupPath));
        Assert.Equal(2, Directory.GetFiles(backups).Length);
    }

    [Fact]
    public void Install_and_delete_share_one_backup_naming_rule()
    {
        var root = NewDeviceRoot();
        var backups = NewBackupDir();
        File.WriteAllText(Path.Combine(root, "mygame.csv"), "old install\n");

        var installed = Device.Install(ProfileFile.NewFromTemplate("mygame.csv"), root, backups);
        Assert.NotNull(installed.BackupPath);
        Assert.Equal("old install\n", File.ReadAllText(installed.BackupPath!));

        var deleted = Device.DeleteProfile(root, "mygame.csv", backups);

        Assert.Equal(backups, Path.GetDirectoryName(installed.BackupPath));
        Assert.Equal(backups, Path.GetDirectoryName(deleted.BackupPath));
        Assert.EndsWith("-mygame.csv", installed.BackupPath!, StringComparison.Ordinal);
        Assert.EndsWith("-mygame.csv", deleted.BackupPath, StringComparison.Ordinal);
        Assert.NotEqual(installed.BackupPath, deleted.BackupPath);
    }

    [Fact]
    public void SelectionOrder_puts_default_first_drops_prefs_and_sorts_the_rest()
    {
        var names = new[]
        {
            "Zelda.csv", "prefs.csv", "apex.csv", "default.csv", "Battlefield.csv", "cod.CSV",
        };

        Assert.Equal(
            new[] { "default.csv", "apex.csv", "Battlefield.csv", "cod.CSV", "Zelda.csv" },
            Device.SelectionOrder(names));
    }

    [Fact]
    public void SelectionOrder_matches_protected_names_case_insensitively()
    {
        var names = new[] { "game.csv", "PREFS.CSV", "Default.csv", "notes.txt" };

        Assert.Equal(new[] { "Default.csv", "game.csv" }, Device.SelectionOrder(names));
    }

    [Fact]
    public void SelectionOrder_of_nothing_is_empty()
    {
        Assert.Empty(Device.SelectionOrder(Array.Empty<string>()));
        Assert.Empty(Device.SelectionOrder(new[] { "prefs.csv" }));
    }

    // Found on a real FAT volume, not in a temp directory. A QuadStick drive is
    // FAT, so macOS leaves ._Racing.csv beside every file it copies there. Those
    // sidecars are binary metadata. Listing them would put phantom entries in the
    // LED guide and offer ._prefs.csv for deletion, since it does not match the
    // exact protected name.
    [Fact]
    public void SelectionOrder_ignores_the_sidecar_files_macos_writes_to_fat_drives()
    {
        var names = new[]
        {
            "default.csv", "._default.csv", "._prefs.csv", "._Racing.csv", "Racing.csv",
        };

        Assert.Equal(new[] { "default.csv", "Racing.csv" }, Device.SelectionOrder(names));
    }

    [Theory]
    [InlineData("Racing.csv", true)]
    [InlineData("default.csv", true)]
    [InlineData("prefs.csv", true)]
    [InlineData("._Racing.csv", false)]
    [InlineData("._prefs.csv", false)]
    [InlineData(".hidden.csv", false)]
    [InlineData("notes.txt", false)]
    [InlineData("", false)]
    public void IsProfileFileName_accepts_only_visible_csv_names(string name, bool expected)
    {
        Assert.Equal(expected, Device.IsProfileFileName(name));
    }

    [Theory]
    [InlineData(1, "purple", "grey", "grey", "grey", "grey")]
    [InlineData(5, "grey", "grey", "grey", "grey", "purple")]
    [InlineData(10, "grey", "grey", "grey", "grey", "blue")]
    [InlineData(15, "grey", "grey", "grey", "grey", "red")]
    [InlineData(19, "grey", "grey", "grey", "purple", "red")]
    [InlineData(20, "blue", "blue", "blue", "blue", "blue")]
    [InlineData(26, "purple", "blue", "blue", "blue", "purple")]
    [InlineData(30, "red", "red", "red", "red", "purple")]
    [InlineData(31, "purple", "red", "red", "red", "red")]
    [InlineData(32, "red", "purple", "red", "red", "red")]
    public void LedPattern_rows_match_the_audited_table(
        int fileNumber, string a, string b, string c, string d, string e)
    {
        Assert.Equal(new[] { a, b, c, d, e }, Device.LedPattern(fileNumber));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(33)]
    [InlineData(int.MaxValue)]
    public void LedPattern_outside_the_table_is_empty(int fileNumber)
    {
        Assert.Empty(Device.LedPattern(fileNumber));
    }

    [Fact]
    public void LedPattern_always_returns_five_known_colours()
    {
        var colours = new[] { "purple", "grey", "blue", "red" };
        for (int n = 1; n <= 32; n++)
        {
            var row = Device.LedPattern(n);
            Assert.Equal(5, row.Count);
            Assert.All(row, c => Assert.Contains(c, colours));
        }
    }

    [Fact]
    public void LedPattern_result_cannot_change_the_table()
    {
        var row = (IList<string>)Device.LedPattern(1);

        Assert.Throws<NotSupportedException>(() => row[0] = "chartreuse");
        Assert.Equal("purple", Device.LedPattern(1)[0]);
    }

    [Fact]
    public void Invalidating_the_cache_forces_the_next_lookup_to_enumerate()
    {
        var first = Device.FindCandidatesCached();
        var cached = Device.FindCandidatesCached();
        Assert.Same(first, cached);

        Device.InvalidateCandidateCache();
        var fresh = Device.FindCandidatesCached();

        Assert.NotSame(first, fresh);
    }
}
