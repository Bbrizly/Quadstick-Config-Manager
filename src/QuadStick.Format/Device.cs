namespace QuadStick.Format;

// Find a mounted QuadStick (root has default.csv) and install profiles.
// Backup → write .qscm-tmp → read back → rename. default.csv needs explicit OK.
public static class Device
{
    public static List<string> FindCandidates()
    {
        var found = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                if (d.DriveType is not (DriveType.Removable or DriveType.Fixed)) continue;
                if (d.DriveType == DriveType.Fixed && !IsMacExternal(d)) continue;
                if (IsInstallTarget(d.RootDirectory.FullName))
                    found.Add(d.RootDirectory.FullName);
            }
            catch (IOException) { /* unreadable volume: skip */ }
            catch (UnauthorizedAccessException) { /* no permission: skip */ }
        }
        return found;
    }

    // macOS mounts USB sticks under /Volumes as "fixed" drives.
    static bool IsMacExternal(DriveInfo d) =>
        OperatingSystem.IsMacOS() && d.RootDirectory.FullName.StartsWith("/Volumes/");

    public static bool IsInstallTarget(string deviceRoot) =>
        File.Exists(Path.Combine(deviceRoot, "default.csv"));

    public sealed record InstallResult(string InstalledPath, string? BackupPath);

    public static InstallResult Install(
        ProfileFile file, string deviceRoot, string backupDir, bool confirmDefaultCsv = false)
    {
        if (file.HasErrors)
            throw new InvalidOperationException(
                "This profile has validation errors and cannot be installed:\n" +
                string.Join("\n", file.Issues.Where(i => i.Severity == Severity.Error)));

        var name = file.Document.CsvFileName
            ?? throw new InvalidOperationException("The profile has no CSV filename in cell A2.");

        if (file.Document.IsDefaultConfig && !confirmDefaultCsv)
            throw new InvalidOperationException(
                "Refusing to overwrite default.csv without explicit confirmation. " +
                "A wrong default.csv can disable flash-drive access.");

        if (!IsInstallTarget(deviceRoot))
            throw new InvalidOperationException(
                "That folder does not look like a QuadStick drive (no default.csv at its root). " +
                "Pick the USB volume that appears when the device is plugged in.");

        var target = Path.Combine(deviceRoot, name);

        // 1. Backup any existing file.
        string? backup = null;
        if (File.Exists(target))
        {
            Directory.CreateDirectory(backupDir);
            backup = Path.Combine(backupDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{name}");
            File.Copy(target, backup, overwrite: false);
        }

        // Clone so the open editor isn't touched.
        var toWrite = ProfileFile.Load(file.ToCsvText());
        toWrite.EnsureVersionHeader();
        var text = toWrite.ToCsvText();
        var tmp = target + ".qscm-tmp";
        File.WriteAllText(tmp, text);
        if (File.ReadAllText(tmp) != text)
        {
            File.Delete(tmp);
            throw new InvalidOperationException("Readback verification failed; the device was not modified.");
        }
        try
        {
            File.Move(tmp, target, overwrite: true);
        }
        catch (IOException) when (backup != null && !File.Exists(target))
        {
            // The swap died between delete and rename. Put the old file back
            // so the device is never left without the profile.
            File.Copy(backup, target, overwrite: true);
            if (File.Exists(tmp)) File.Delete(tmp);
            throw new InvalidOperationException(
                $"Writing failed mid-swap; the previous version of {name} was restored from backup. The device is unchanged.");
        }
        return new InstallResult(target, backup);
    }

    public static string DefaultBackupDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "QuadStickBackups");
}
