using Avalonia.Threading;
using QuadStick.Format;

namespace QuadStick.App;

// The safety net. If anything unexpected breaks, the user's work is written
// to a rescue file BEFORE anything else happens, and a crash log is kept so
// the problem can be reported and fixed.
public static class CrashGuard
{
    public static string? RescueDirOverride { get; set; }

    public static string RescueDir => RescueDirOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuadStickConfigManager", "rescue");

    public static string CrashLogPath => RescueDirOverride is { } dir
        ? Path.Combine(dir, "crash-log.txt")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QuadStickConfigManager", "crash-log.txt");

    const long MaxCrashLogBytes = 1024 * 1024;

    /// <summary>Set by MainWindow so the net always knows what to rescue.</summary>
    public static Func<ProfileFile?>? CurrentFile { get; set; }

    public static void Install()
    {
        // A genuinely unhandled UI exception means we no longer know which
        // editor/device invariants survived. Rescue first, but do NOT mark it
        // handled: continuing with Install/Delete enabled is less safe than a
        // controlled process failure followed by opening the rescued profile.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            TryRescue("ui-thread", e.Exception);
            e.Handled = false;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            TryRescue("appdomain", e.ExceptionObject as Exception);

        // Unobserved task exceptions are raised after the task is already dead;
        // recording them does not resume the failed operation, so observing the
        // GC notification is safe and avoids a second process-level failure.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            TryRescue("task", e.Exception);
            e.SetObserved();
        };
    }

    static void TryRescue(string where, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(RescueDir);
            var file = CurrentFile?.Invoke();
            if (file is { Dirty: true })
            {
                var raw = Path.GetFileNameWithoutExtension(file.Document.CsvFileName ?? "profile");
                var name = Sanitize(raw);
                var path = Path.Combine(RescueDir,
                    $"{name}-rescued-{DateTime.Now:yyyyMMdd-HHmmss}-{DateTime.Now.Ticks % 10000}.csv");
                ProfileFile.WriteAtomic(path, file.ToCsvText());
            }
        }
        catch { /* rescue is best effort; fall through to the log */ }

        AppendCrashLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}: {ex}\n\n");

        try
        {
            CrashReport.Write(where, ex);
        }
        catch { /* the safety net must never itself throw */ }
    }

    /// <summary>Test seam: drives the same path the three hooks drive.</summary>
    internal static void ReportForTest(string where, Exception? ex) => TryRescue(where, ex);

    /// <summary>Record something that was caught and handled. The log only: no
    /// rescue copy and no crash report, because nothing actually crashed.</summary>
    public static void Note(Exception ex, string where) =>
        AppendCrashLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] handled, {where}: {ex}\n\n");

    static void AppendCrashLog(string text)
    {
        try
        {
            var directory = Path.GetDirectoryName(CrashLogPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            if (File.Exists(CrashLogPath) && new FileInfo(CrashLogPath).Length >= MaxCrashLogBytes)
            {
                var old = CrashLogPath + ".old";
                try { File.Move(CrashLogPath, old, overwrite: true); }
                catch { try { File.Delete(CrashLogPath); } catch { } }
            }

            File.AppendAllText(CrashLogPath, text);
        }
        catch { /* diagnostics must never become the crash */ }
    }

    static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length == 0 ? "profile" : name;
    }

    /// <summary>Rescue files waiting from a previous crash, newest first.</summary>
    public static IReadOnlyList<string> PendingRescues()
    {
        try
        {
            return Directory.Exists(RescueDir)
                ? Directory.GetFiles(RescueDir, "*.csv").OrderByDescending(File.GetLastWriteTime).ToArray()
                : Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    public static void DiscardRescues()
    {
        foreach (var f in PendingRescues())
            try { File.Delete(f); } catch { /* best effort */ }
    }
}
