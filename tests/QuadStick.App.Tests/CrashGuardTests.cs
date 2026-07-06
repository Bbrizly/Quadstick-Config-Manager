using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

public class CrashGuardTests
{
    // RescueDirOverride is static/shared, so each test points it at its own
    // temp dir and resets it in `finally` to avoid bleeding into other tests.
    static void WithRescueDir(Action<string> body)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var prior = CrashGuard.RescueDirOverride;
        CrashGuard.RescueDirOverride = dir;
        try { body(dir); }
        finally { CrashGuard.RescueDirOverride = prior; }
    }

    [Fact]
    public void PendingRescues_lists_newest_first()
    {
        WithRescueDir(dir =>
        {
            var older = Path.Combine(dir, "a-rescued-1.csv");
            var newer = Path.Combine(dir, "b-rescued-2.csv");
            File.WriteAllText(older, "old");
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));
            File.WriteAllText(newer, "new");
            File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

            var pending = CrashGuard.PendingRescues();

            Assert.Equal(new[] { newer, older }, pending);
        });
    }

    [Fact]
    public void DiscardRescues_removes_every_pending_file()
    {
        WithRescueDir(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "a-rescued-1.csv"), "a");
            File.WriteAllText(Path.Combine(dir, "b-rescued-2.csv"), "b");

            CrashGuard.DiscardRescues();

            Assert.Empty(CrashGuard.PendingRescues());
        });
    }
}
