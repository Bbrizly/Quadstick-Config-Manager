namespace QuadStick.Format;

// Keeps a spare copy of the device's own prefs.csv, and answers whether the one
// on the drive is still a file the device would accept.
//
// Why this is needed at all: a profile that changes usb emulation mode makes the
// firmware disconnect and re-enumerate itself so the host sees the new
// controller type (Configuration.c:320-362). An ordinary switch between a game
// profile and a mouse profile therefore surprise-removes the drive from Windows,
// twice a session. Windows repairs the volume afterwards and drops whatever it
// cannot reconcile, and prefs.csv is what that keeps landing on. Nothing here
// prevents that. It only means the user has a copy to put back instead of
// hunting for one.
public static class PrefsGuard
{
    public const string FileName = "prefs.csv";

    // One copy, not a history. The user wants the last known good file back, and
    // a list of dated ones is a decision nobody has the information to make.
    // ponytail: a single snapshot is shared by two QuadSticks plugged in at
    // once; key it by drive if anyone ever hits that.
    const string SnapshotName = "prefs-good.csv";

    public enum State
    {
        // No prefs.csv on the drive. Normal: the device has built-in defaults
        // and plenty of people never make one.
        Missing,
        // The device would load it.
        Healthy,
        // It is there and the device would refuse it.
        Broken,
    }

    public static string SnapshotPath(string snapshotDir) => Path.Combine(snapshotDir, SnapshotName);

    // The firmware's test, not one of ours. Load_Preferences_File opens the
    // file, requires "QuadStick" as the first nine characters, then requires
    // three more lines before it reads any setting ("Prefs.csv too short").
    // Deliberately nothing else: prefs.csv holds settings this app does not
    // know, and calling one of those broken would send somebody to replace a
    // working file.
    public static State Check(string deviceRoot)
    {
        var path = Path.Combine(deviceRoot, FileName);
        string[] lines;
        try
        {
            if (!File.Exists(path)) return State.Missing;
            lines = File.ReadAllLines(path);
        }
        // A file whose directory entry chkdsk could not repair throws on open,
        // usually IOException carrying 0x80070570. That is the whole case this
        // class exists for, so it is Broken, never an exception the caller has
        // to think about.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return State.Broken;
        }

        if (lines.Length < 4) return State.Broken;
        return lines[0].StartsWith("QuadStick", StringComparison.Ordinal) ? State.Healthy : State.Broken;
    }

    public static bool HasSnapshot(string snapshotDir) => File.Exists(SnapshotPath(snapshotDir));

    public static DateTime? SnapshotTaken(string snapshotDir)
    {
        try { return HasSnapshot(snapshotDir) ? File.GetLastWriteTime(SnapshotPath(snapshotDir)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // Called wherever the app has just looked at a device. Only a file the
    // device would load is ever copied, so a broken one can never overwrite the
    // spare. Best effort by design: failing to take a copy must not break
    // whatever the user was actually doing.
    public static bool TrySnapshot(string deviceRoot, string snapshotDir)
    {
        if (Check(deviceRoot) != State.Healthy) return false;
        try
        {
            Directory.CreateDirectory(snapshotDir);
            var tmp = SnapshotPath(snapshotDir) + ".qscm-tmp";
            File.Copy(Path.Combine(deviceRoot, FileName), tmp, overwrite: true);
            File.Move(tmp, SnapshotPath(snapshotDir), overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Puts the spare back, byte for byte. Not through Install: that re-serializes
    // the file, and a restore has to hand back exactly what was taken, including
    // the settings this app does not model.
    //
    // Whatever is on the device is copied to backupDir first even though it is
    // the broken one. It is still the only record of what the user had, and a
    // half-repaired file can hold settings the spare predates.
    public static string Restore(string deviceRoot, string snapshotDir, string backupDir)
    {
        var snapshot = SnapshotPath(snapshotDir);
        if (!File.Exists(snapshot))
            throw new InvalidOperationException(Strings.Prefs_NoSavedCopyOfPrefs);

        var target = Path.Combine(deviceRoot, FileName);
        if (File.Exists(target))
        {
            Directory.CreateDirectory(backupDir);
            var stamp = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}";
            var keep = Path.Combine(backupDir, $"{stamp}-broken-{FileName}");
            // The copy can fail on exactly the file this is here to replace, and
            // refusing the restore because the unreadable file is unreadable
            // would strand the user. Try, then carry on.
            try { File.Copy(target, keep, overwrite: false); } catch { /* it is the broken one */ }
        }

        // Beside it, then into place: the device must never see a half written
        // prefs.csv, because it reads until the first blank line and would load
        // a truncated one without complaint.
        var tmp = target + ".qscm-tmp";
        try
        {
            File.Copy(snapshot, tmp, overwrite: true);
            File.Move(tmp, target, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* leave the stray temp */ }
        }
        return target;
    }
}
