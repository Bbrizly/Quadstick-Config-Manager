namespace QuadStick.Format;

// Find a mounted QuadStick (root has default.csv) and install profiles.
// Backup → write .qscm-tmp → read back → rename. default.csv needs explicit OK.
public static class Device
{
    // Display-only wrapper. RefreshEditor and the Home cards call this many
    // times per user action (save → refresh → undo …); a live scan each time
    // enumerates every drive and stats default.csv on the UI thread, which a
    // spun-down USB stick can stall on. A short TTL collapses the burst.
    // ponytail: 3s cache, tune up if a freshly plugged device is slow to show
    // on Home. Install always uses the live FindCandidates() below.
    static List<string>? _cache;
    static DateTime _cacheAtUtc;
    public static List<string> FindCandidatesCached()
    {
        if (_cache is not null && DateTime.UtcNow - _cacheAtUtc < TimeSpan.FromSeconds(3))
            return _cache;
        _cache = FindCandidates();
        _cacheAtUtc = DateTime.UtcNow;
        return _cache;
    }

    // An explicit Refresh must not wait out the TTL. Drop the cache so the very
    // next cached lookup enumerates drives again.
    public static void InvalidateCandidateCache()
    {
        _cache = null;
        _cacheAtUtc = default;
    }

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
        OperatingSystem.IsMacOS() && d.RootDirectory.FullName.StartsWith("/Volumes/", StringComparison.Ordinal);

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
            backup = BackupExisting(target, backupDir);

        // Clone so the open editor isn't touched.
        var toWrite = ProfileFile.Load(file.ToCsvText());
        toWrite.NormalizeForDeviceCsv();
        var text = toWrite.ToCsvText();
        var tmp = target + ".qscm-tmp";
        try
        {
            File.WriteAllText(tmp, text);
            if (File.ReadAllText(tmp) != text)
                throw new InvalidOperationException("Readback verification failed; the device was not modified.");

            try
            {
                File.Move(tmp, target, overwrite: true);
            }
            catch (IOException) when (backup != null && !File.Exists(target))
            {
                // The swap died between delete and rename. Put the old file back
                // so the device is never left without the profile.
                File.Copy(backup, target, overwrite: true);
                throw new InvalidOperationException(
                    $"Writing failed mid-swap; the previous version of {name} was restored from backup. The device is unchanged.");
            }
        }
        finally
        {
            // Whatever threw between the write and the successful move, never leave
            // a stray .qscm-tmp on the device. A successful File.Move already
            // consumed it, so this is a no-op on the happy path. Best-effort: a
            // failure to delete the temp must not mask the real install error.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* leave the stray temp */ }
        }
        return new InstallResult(target, backup);
    }

    // The one backup primitive. Install and DeleteProfile both go through it so
    // there is a single naming rule and a single place that can fail.
    // Millisecond stamp plus a counter: two backups of the same name in the
    // same instant must both survive, never throw, never overwrite.
    static string BackupExisting(string path, string backupDir)
    {
        Directory.CreateDirectory(backupDir);
        var name = Path.GetFileName(path);
        var stamp = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}";
        var backup = Path.Combine(backupDir, $"{stamp}-{name}");
        for (int n = 2; File.Exists(backup); n++)
            backup = Path.Combine(backupDir, $"{stamp}-{n}-{name}");
        File.Copy(path, backup, overwrite: false);
        return backup;
    }

    public sealed record DeleteResult(string DeletedPath, string BackupPath);

    // Deleting from someone's device is irreversible, so every rule lives here
    // and not in the window that calls it. The UI cannot pass its way around
    // any of these checks. Order matters: a protected name is refused before
    // anything is copied or removed.
    public static DeleteResult DeleteProfile(string deviceRoot, string fileName, string backupDir)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName))
            throw new InvalidOperationException(
                "Only a plain file name on the device root can be deleted, not a path.");

        if (!fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only .csv profiles can be deleted.");

        if (string.Equals(fileName, "default.csv", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "prefs.csv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{fileName} is protected and cannot be deleted. " +
                "Removing it can leave the device unusable.");

        if (!IsInstallTarget(deviceRoot))
            throw new InvalidOperationException(
                "That folder does not look like a QuadStick drive (no default.csv at its root). " +
                "Pick the USB volume that appears when the device is plugged in.");

        // Belt and braces after the file-name check: resolve both paths and
        // prove the target still sits directly in the device root.
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(deviceRoot));
        var target = Path.GetFullPath(Path.Combine(root, fileName));
        var targetDir = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(target) ?? string.Empty);
        var compare = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(targetDir, root, compare))
            throw new InvalidOperationException(
                "That file is not directly on the device root, so it was not deleted.");

        if (!File.Exists(target))
            throw new InvalidOperationException(
                $"{fileName} is no longer on the device. Refresh the list and try again.");

        // Backup first. If the copy throws, the source is still there and we
        // have deleted nothing.
        var backup = BackupExisting(target, backupDir);
        File.Delete(target);
        return new DeleteResult(target, backup);
    }

    // The order the device steps through files when you cycle profiles.
    // prefs.csv is settings, not a profile, so it is never selectable.
    public static IReadOnlyList<string> SelectionOrder(IEnumerable<string> fileNames)
    {
        var csv = fileNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Where(n => n.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            .Where(n => !string.Equals(n, "prefs.csv", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var ordered = new List<string>();
        var first = csv.FirstOrDefault(n => string.Equals(n, "default.csv", StringComparison.OrdinalIgnoreCase));
        if (first is not null) ordered.Add(first);
        ordered.AddRange(csv
            .Where(n => !string.Equals(n, "default.csv", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        return ordered;
    }

    // The audited QuadStick Manager Program table, copied as data. Five lights,
    // left to right, for file numbers 1 to 32. There is no rule to infer here
    // and nothing past 32 is documented, so nothing is extrapolated.
    const string P = "purple", G = "grey", B = "blue", R = "red";
    static readonly string[][] _ledPatterns =
    [
        [P, G, G, G, G], [G, P, G, G, G], [G, G, P, G, G], [G, G, G, P, G],
        [G, G, G, G, P], [P, G, G, G, P], [G, P, G, G, P], [G, G, P, G, P],
        [G, G, G, P, P], [G, G, G, G, B], [P, G, G, G, B], [G, P, G, G, B],
        [G, G, P, G, B], [G, G, G, P, B], [G, G, G, G, R], [P, G, G, G, R],
        [G, P, G, G, R], [G, G, P, G, R], [G, G, G, P, R], [B, B, B, B, B],
        [P, B, B, B, B], [B, P, B, B, B], [B, B, P, B, B], [B, B, B, P, B],
        [B, B, B, B, P], [P, B, B, B, P], [B, P, B, B, P], [B, B, P, B, P],
        [B, B, B, P, P], [R, R, R, R, P], [P, R, R, R, R], [R, P, R, R, R],
    ];

    public static IReadOnlyList<string> LedPattern(int fileNumber) =>
        fileNumber >= 1 && fileNumber <= _ledPatterns.Length
            ? [.. _ledPatterns[fileNumber - 1]] // copy so the table stays read-only
            : [];

    public static string DefaultBackupDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "QuadStickBackups");
}
