using System;
using System.IO;
using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

public class CrashReportTests
{
    [Theory]
    // macOS
    [InlineData("/Users/bassam/Documents/gta.csv", "/Users/<user>/Documents/gta.csv")]
    [InlineData("/Users/bassam", "/Users/<user>")]
    // Linux
    [InlineData("/home/fred/profiles/x.csv", "/home/<user>/profiles/x.csv")]
    // Windows, including the case difference that a single literal match misses
    [InlineData(@"C:\Users\Bassam\Desktop\a.csv", @"C:\Users\<user>\Desktop\a.csv")]
    [InlineData(@"c:\users\bassam\Desktop\a.csv", @"c:\users\<user>\Desktop\a.csv")]
    [InlineData(@"D:\Users\Someone\x", @"D:\Users\<user>\x")]
    // OneDrive-redirected profile
    [InlineData(@"C:\Users\Bassam\OneDrive\QuadStick\a.csv", @"C:\Users\<user>\OneDrive\QuadStick\a.csv")]
    // UNC
    [InlineData(@"\\server\home\bassam\a.csv", @"\\server\home\<user>\a.csv")]
    // Build-machine source paths, which are not the runtime user's home at all
    [InlineData("/Users/runner/work/qscm/src/App.cs", "/Users/<user>/work/qscm/src/App.cs")]
    public void SanitizesEveryHomePathShape(string input, string expected) =>
        Assert.Equal(expected, CrashReport.SanitizePath(input));

    [Theory]
    [InlineData("QuadStick.App.MainWindow.OpenInEditor()")]
    [InlineData("")]
    [InlineData("at QuadStick.Format.Parser.Parse(String text)")]
    public void LeavesNonPathTextAlone(string input) =>
        Assert.Equal(input, CrashReport.SanitizePath(input));

    [Fact]
    public void SanitizesEveryOccurrenceInOneString()
    {
        Assert.Equal(
            "copy /Users/<user>/a.csv to /Users/<user>/b.csv",
            CrashReport.SanitizePath("copy /Users/bassam/a.csv to /Users/bassam/b.csv"));
    }

    static Exception Thrown(Action a)
    {
        try { a(); } catch (Exception e) { return e; }
        throw new Xunit.Sdk.XunitException("expected a throw");
    }

    [Fact]
    public void KeepsTheTypeAndDropsTheMessage()
    {
        var ex = Thrown(() => throw new InvalidOperationException("cell B7 says left_sip"));
        var p = CrashReport.Build("ui-thread", ex);

        Assert.Equal("System.InvalidOperationException", p.Chain[0].Type);
        Assert.DoesNotContain("left_sip", CrashReport.ToJson(p));
        Assert.DoesNotContain("cell B7", CrashReport.ToJson(p));
    }

    [Fact]
    public void DropsMessagesFromEveryInnerException()
    {
        var inner = new IOException("C:/Users/bassam/secret-profile.csv is locked");
        var mid = new InvalidOperationException("wrapping SECRETMESSAGE", inner);
        var ex = Thrown(() => throw new ApplicationException("outer SECRETMESSAGE", mid));

        var json = CrashReport.ToJson(CrashReport.Build("task", ex));

        Assert.DoesNotContain("SECRETMESSAGE", json);
        Assert.DoesNotContain("secret-profile", json);
        Assert.DoesNotContain("bassam", json);
        // but the shape survives, so grouping still works
        Assert.Contains("System.IO.IOException", json);
        Assert.Contains("System.ApplicationException", json);
    }

    [Fact]
    public void FlattensAggregateExceptions()
    {
        var ex = new AggregateException(
            new InvalidOperationException("SECRETA"),
            new IOException("SECRETB"));

        var p = CrashReport.Build("task", ex);
        var json = CrashReport.ToJson(p);

        Assert.DoesNotContain("SECRETA", json);
        Assert.DoesNotContain("SECRETB", json);
        Assert.Contains("System.InvalidOperationException", json);
        Assert.Contains("System.IO.IOException", json);
    }

    [Fact]
    public void CarriesOnlyTheDisclosedEnvelope()
    {
        var p = CrashReport.Build("appdomain", Thrown(() => throw new Exception("x")));
        var json = CrashReport.ToJson(p);

        Assert.Contains("\"where\": \"appdomain\"", json);
        Assert.DoesNotContain(Environment.MachineName, json);
        Assert.DoesNotContain(Environment.UserName, json);
    }

    [Fact]
    public void SurvivesANullException()
    {
        // AppDomain hands us ExceptionObject as Exception, which can be null.
        var p = CrashReport.Build("appdomain", null);
        Assert.Empty(p.Chain);
        Assert.Equal("appdomain", p.Where);
    }

    [Fact]
    public void SanitizesPathsInsideFrames()
    {
        var p = CrashReport.Build("ui-thread", Thrown(() => throw new Exception("x")));
        foreach (var f in p.Chain[0].Frames)
        {
            Assert.DoesNotContain("/Users/" + Environment.UserName, f.Function);
            Assert.DoesNotContain(@"\Users\" + Environment.UserName, f.Function);
        }
    }

    static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "qscm-crash-" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void WritesOneFilePerCrash()
    {
        var dir = TempDir();
        CrashReport.PendingDirOverride = dir;
        try
        {
            CrashReport.Write("ui-thread", new InvalidOperationException("x"));
            CrashReport.Write("task", new IOException("y"));
            Assert.Equal(2, CrashReport.Pending().Count);
        }
        finally { CrashReport.PendingDirOverride = null; Directory.Delete(dir, true); }
    }

    [Fact]
    public void KeepsAtMostFiveOldestFirst()
    {
        var dir = TempDir();
        CrashReport.PendingDirOverride = dir;
        try
        {
            for (var i = 0; i < 8; i++)
                CrashReport.Write("task", new InvalidOperationException("x"));

            Assert.Equal(CrashReport.MaxPending, CrashReport.Pending().Count);
        }
        finally { CrashReport.PendingDirOverride = null; Directory.Delete(dir, true); }
    }

    [Fact]
    public void DropsReportsOlderThanThirtyDays()
    {
        var dir = TempDir();
        CrashReport.PendingDirOverride = dir;
        try
        {
            CrashReport.Write("task", new InvalidOperationException("x"));
            var f = CrashReport.Pending()[0];
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-(CrashReport.MaxAgeDays + 1)));

            Assert.Empty(CrashReport.Pending());
            Assert.False(File.Exists(f));   // expired means deleted, not hidden
        }
        finally { CrashReport.PendingDirOverride = null; Directory.Delete(dir, true); }
    }

    [Fact]
    public void DiscardRemovesEverything()
    {
        var dir = TempDir();
        CrashReport.PendingDirOverride = dir;
        try
        {
            CrashReport.Write("task", new InvalidOperationException("x"));
            CrashReport.Write("task", new InvalidOperationException("y"));
            CrashReport.Discard();
            Assert.Empty(CrashReport.Pending());
        }
        finally { CrashReport.PendingDirOverride = null; Directory.Delete(dir, true); }
    }

    [Fact]
    public void WriteNeverThrowsOnABadDirectory()
    {
        CrashReport.PendingDirOverride = "/this/path/cannot/be/created/\0bad";
        try
        {
            CrashReport.Write("task", new InvalidOperationException("x"));   // must not throw
            Assert.Empty(CrashReport.Pending());
        }
        finally { CrashReport.PendingDirOverride = null; }
    }

    [Fact]
    public void WrittenFileRoundTripsAndStillHidesTheMessage()
    {
        var dir = TempDir();
        CrashReport.PendingDirOverride = dir;
        try
        {
            CrashReport.Write("ui-thread", new InvalidOperationException("SECRETMESSAGE"));
            var text = File.ReadAllText(CrashReport.Pending()[0]);

            Assert.DoesNotContain("SECRETMESSAGE", text);
            Assert.Equal("ui-thread", CrashReport.FromJson(text)!.Where);
        }
        finally { CrashReport.PendingDirOverride = null; Directory.Delete(dir, true); }
    }

    [Fact]
    public void APartlyWrittenFileIsNeverListed()
    {
        // The rename is what makes this true: a .tmp left by a second crash
        // mid-write does not match crash-*.json, so nobody is ever shown it.
        var dir = TempDir();
        CrashReport.PendingDirOverride = dir;
        try
        {
            File.WriteAllText(Path.Combine(dir, "crash-20260728-000000-abc.json.tmp"), "{\"sch");
            Assert.Empty(CrashReport.Pending());
        }
        finally { CrashReport.PendingDirOverride = null; Directory.Delete(dir, true); }
    }

    [Fact]
    public void ACrashOnTheTaskSchedulerLeavesAPendingReport()
    {
        var dir = TempDir();
        var rescue = TempDir();
        CrashReport.PendingDirOverride = dir;
        CrashGuard.RescueDirOverride = rescue;
        try
        {
            CrashGuard.ReportForTest("task", new InvalidOperationException("SECRETMESSAGE"));

            var pending = CrashReport.Pending();
            Assert.Single(pending);
            Assert.DoesNotContain("SECRETMESSAGE", File.ReadAllText(pending[0]));

            // The crash log is the one place the raw message is deliberately
            // kept, so it has to land in the temp dir, never the real one.
            Assert.Equal(Path.Combine(rescue, "crash-log.txt"), CrashGuard.CrashLogPath);
            Assert.Contains("SECRETMESSAGE", File.ReadAllText(CrashGuard.CrashLogPath));
        }
        finally
        {
            CrashReport.PendingDirOverride = null;
            CrashGuard.RescueDirOverride = null;
            Directory.Delete(dir, true);
            Directory.Delete(rescue, true);
        }
    }
}
