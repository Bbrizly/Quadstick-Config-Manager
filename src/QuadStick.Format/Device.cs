using System.Globalization;

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
    //
    // The value and its timestamp travel together in one reference, because two
    // fields are read and written from two threads here: the Manage files window
    // does its scanning inside a Task.Run and Home does it on the UI thread. Two
    // separate fields could be read half updated, giving a fresh list a stale
    // time or the other way about. One record swapped in a single assignment
    // cannot be, and the list inside it is never mutated after it is built.
    sealed record Scan(List<string> Roots, DateTime AtUtc);

    static Scan? _cache;

    public static List<string> FindCandidatesCached()
    {
        if (_cache is { } seen && DateTime.UtcNow - seen.AtUtc < TimeSpan.FromSeconds(3))
            return seen.Roots;
        var fresh = new Scan(FindCandidates(), DateTime.UtcNow);
        _cache = fresh;
        return fresh.Roots;
    }

    // An explicit Refresh must not wait out the TTL. Drop the cache so the very
    // next cached lookup enumerates drives again.
    public static void InvalidateCandidateCache() => _cache = null;

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
        ProfileFile file, string deviceRoot, string backupDir,
        bool confirmDefaultCsv = false, bool confirmPreferencesCsv = false)
    {
        // Before the generic error dump, because this one has a single fix and
        // the user needs to read it. The device holds each root file name in a
        // 31 character slot, so a longer name copies onto the stick fine, then
        // cannot be opened, and runs into the next name in the device's own
        // list so the file after it reads as garbage too.
        var declared = file.Document.CsvFileName;
        if (declared is not null && SafeFileName.IsTooLongForDevice(declared))
            throw new InvalidOperationException(
                string.Format(CultureInfo.CurrentCulture, Strings.Device_DeclaredIsTooLongFor, declared, SafeFileName.MaxDeviceFileNameLength, declared.Length));

        if (file.HasErrors)
            throw new InvalidOperationException(
                Strings.Device_ThisProfileHasValidationErrors +
                string.Join("\n", file.Issues.Where(i => i.Severity == Severity.Error)));

        var name = file.Document.CsvFileName
            ?? throw new InvalidOperationException("The profile has no CSV filename in cell A2.");

        if (file.Document.IsDefaultConfig && !confirmDefaultCsv)
            throw new InvalidOperationException(
                Strings.Device_RefusingToOverwriteDefaultCsv);

        // prefs.csv is the device's own settings file, so it changes every
        // profile at once. The gate lives here and not in the window that calls
        // it, so no caller can write one by accident. A Preferences sheet inside
        // a normal game CSV is not this file and stays on the normal path.
        if (file.Document.IsDevicePreferences && !confirmPreferencesCsv)
            throw new InvalidOperationException(
                Strings.Device_RefusingToOverwritePrefsCsv);

        if (!IsInstallTarget(deviceRoot))
            throw new InvalidOperationException(
                Strings.Device_ThatFolderDoesNotLook);

        // The name comes out of cell A2 of a file that may have arrived from
        // anywhere: a download, a shared workbook, a community profile.
        // ValidateFileName already refuses separators, so the first test cannot
        // fire today. It is here because Path.Combine hands back the second
        // argument whole when it is rooted, and one character class check in
        // another assembly should not be the only thing standing between an
        // imported sheet and an arbitrary write target. DeleteProfile has had
        // the same guard since it was written.
        //
        // The other two are live: a control character passes ValidateFileName
        // and then throws out of File.WriteAllText, and a name Windows reserves
        // writes to a device instead of a file, so the readback comes back empty
        // and the user is told verification failed.
        if (name != Path.GetFileName(name) || name.Any(char.IsControl) || SafeFileName.IsReservedOnWindows(name))
            throw new InvalidOperationException(
                string.Format(CultureInfo.CurrentCulture, Strings.Device_NameIsNotAPlain, name));

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
            // Any failure, not just an IOException. The guard that matters is
            // the second one: the old file is gone, so whatever the exception
            // type was, the device is sitting there without the profile it had
            // a moment ago. A USB volume can raise UnauthorizedAccessException
            // on the same half-finished swap, and catching only IOException
            // left the user's working profile deleted with nothing put back.
            // A pulled stick makes File.Exists(target) false because the mount
            // point went away, not because the swap deleted anything. The old
            // profile is still on the device, untouched, so the copy-it-by-hand
            // message below would be a false alarm about the scariest thing the
            // app can tell someone. Say what actually happened instead.
            catch (Exception swap) when (backup != null && !File.Exists(target) && !Directory.Exists(deviceRoot))
            {
                // Careful about what this claims. The mount point going away
                // does not prove the old directory entry survived: the rename
                // may already have removed it before the volume disappeared. So
                // it says what is certainly true, keeps the backup, and asks the
                // user to look rather than promising them nothing happened.
                throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, Strings.Device_TheQuadStickWasDisconnectedWhile, name, backup, name), swap);
            }
            catch (Exception swap) when (backup != null && !File.Exists(target))
            {
                // Putting it back has to be as careful as putting it there. A
                // straight copy onto the profile's own path can be cut short by
                // the same full volume that broke the swap, and a half written
                // profile is worse than a missing one: the device reads until
                // the first blank line, so it loads a truncated file without
                // complaint and silently drops every binding past the cut, while
                // the message below swears the file is not there at all. Write
                // beside it and move it into place, so the profile either is the
                // old one or is not there, never something in between.
                var back = target + ".qscm-restore";
                try
                {
                    File.Copy(backup, back, overwrite: true);
                    File.Move(back, target, overwrite: true);
                }
                catch (Exception restore)
                {
                    // Best effort: a leftover partial copy is litter, but it
                    // must not be mistaken for the profile. The target is left
                    // alone because the move either put the old file there or
                    // never ran, and it is never a partial write.
                    try { if (File.Exists(back)) File.Delete(back); } catch { /* leave the stray copy */ }
                    throw new InvalidOperationException(
                        string.Format(CultureInfo.CurrentCulture, Strings.Device_WritingFailedMidSwapAnd, name, backup), restore);
                }
                // The cause travels with it. Now that any exception can land
                // here, dropping it would leave a crash report with nothing in
                // it about what actually broke.
                throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, Strings.Device_WritingFailedMidSwapThe, name), swap);
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
                Strings.Device_OnlyAPlainFileName);

        if (!fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only .csv profiles can be deleted.");

        if (string.Equals(fileName, "default.csv", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "prefs.csv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                string.Format(CultureInfo.CurrentCulture, Strings.Device_FileNameIsProtectedAndCannot, fileName));

        if (!IsInstallTarget(deviceRoot))
            throw new InvalidOperationException(
                Strings.Device_ThatFolderDoesNotLook);

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
                Strings.Device_ThatFileIsNotDirectly);

        if (!File.Exists(target))
            throw new InvalidOperationException(
                string.Format(CultureInfo.CurrentCulture, Strings.Device_FileNameIsNoLongerOn, fileName));

        // Backup first. If the copy throws, the source is still there and we
        // have deleted nothing.
        var backup = BackupExisting(target, backupDir);
        File.Delete(target);
        return new DeleteResult(target, backup);
    }

    // The order the device steps through files when you cycle profiles.
    // prefs.csv is settings, not a profile, so it is never selectable.
    // A QuadStick drive is FAT formatted, so macOS drops AppleDouble sidecars
    // like ._Racing.csv next to any file it copies there. They are metadata, not
    // profiles, and they must never reach the file list, the selection guide, or
    // delete. Dot files are hidden by every OS that writes them, so the name is
    // enough. ponytail: name check only, sniffing the AppleDouble magic bytes
    // would force every caller to open the file first.
    public static bool IsProfileFileName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
        && !fileName.StartsWith('.');

    public static IReadOnlyList<string> SelectionOrder(IEnumerable<string> fileNames)
    {
        var csv = fileNames
            .Where(IsProfileFileName)
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
