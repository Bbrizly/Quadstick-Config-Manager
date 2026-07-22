using Avalonia.Threading;
using QuadStick.Format;

namespace QuadStick.App;

// The safety net. If anything unexpected breaks, the user's work is written
// to a rescue file BEFORE anything else happens, and a crash log is kept so
// the problem can be reported and fixed. A disabled user must never lose an
// afternoon of mappings to a bug, and must never see the app just vanish.
public static class CrashGuard
{
    // Test seam, same pattern as Settings.Load(path): tests point this at a
    // temp directory instead of the real per-user ApplicationData folder.
    public static string? RescueDirOverride { get; set; }

    public static string RescueDir => RescueDirOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuadStickConfigManager", "rescue");

    public static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuadStickConfigManager", "crash-log.txt");

    /// <summary>Set by MainWindow so the net always knows what to rescue.</summary>
    public static Func<ProfileFile?>? CurrentFile { get; set; }

    public static void Install()
    {
        // Unhandled exceptions on the UI thread: rescue, log, and let the
        // app continue when possible instead of dying mid-click.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            TryRescue("ui-thread", e.Exception);
            e.Handled = true; // one broken click handler must not kill the app
        };

        // Anything else (background tasks, finalizers): rescue and log.
        // The process may still die, but the user's work is on disk first.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            TryRescue("appdomain", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            TryRescue("task", e.Exception);
            e.SetObserved();
        };
    }

    static void TryRescue(string where, Exception? ex)
    {
        // Two independent best-effort steps: a bad CsvFileName (e.g. the user
        // typed "bad?.csv") must not make the rescue write throw and take the
        // crash log down with it. Each step gets its own try.
        try
        {
            Directory.CreateDirectory(RescueDir);
            var file = CurrentFile?.Invoke();
            if (file is { Dirty: true })
            {
                var raw = Path.GetFileNameWithoutExtension(file.Document.CsvFileName ?? "profile");
                var name = Sanitize(raw);
                // Ticks, not just a second-resolution timestamp: a burst of
                // faulted background tasks can raise UnobservedTaskException
                // more than once in the same second, and each one must keep
                // its own snapshot rather than overwrite the last.
                var path = Path.Combine(RescueDir, $"{name}-rescued-{DateTime.Now:yyyyMMdd-HHmmss}-{DateTime.Now.Ticks % 10000}.csv");
                ProfileFile.WriteAtomic(path, file.ToCsvText());
            }
        }
        catch { /* rescue is best effort; fall through to the log */ }

        try
        {
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}: {ex}\n\n");
        }
        catch { /* the safety net must never itself throw */ }
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
        // Per-file catch: one locked/undeletable file must not stop the rest
        // from being cleaned up.
        foreach (var f in PendingRescues())
            try { File.Delete(f); } catch { /* best effort */ }
    }
}
