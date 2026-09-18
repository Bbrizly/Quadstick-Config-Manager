using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

// Note is the only record that a caught bug happened at all. It was writing
// into a folder that does not exist until something crashes hard, and its own
// catch swallowed the failure, so on most machines every handled error went
// nowhere.
public class CrashGuardNoteTests
{
    [Fact]
    public void A_handled_error_reaches_the_log_on_a_machine_that_has_never_crashed()
    {
        var was = CrashGuard.RescueDirOverride;
        var dir = Path.Combine(Directory.CreateTempSubdirectory("qscm-note-").FullName, "never-made");
        try
        {
            CrashGuard.RescueDirOverride = dir;
            Assert.False(Directory.Exists(dir));

            CrashGuard.Note(new InvalidOperationException("the thing broke"), "a test");

            Assert.True(File.Exists(CrashGuard.CrashLogPath));
            var text = File.ReadAllText(CrashGuard.CrashLogPath);
            Assert.Contains("the thing broke", text, StringComparison.Ordinal);
            Assert.Contains("handled, a test", text, StringComparison.Ordinal);
        }
        finally
        {
            CrashGuard.RescueDirOverride = was;
            try { Directory.Delete(Path.GetDirectoryName(dir)!, true); } catch (IOException) { }
        }
    }
}
