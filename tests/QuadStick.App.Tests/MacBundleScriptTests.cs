using System.Diagnostics;
using Xunit;

namespace QuadStick.App.Tests;

// scripts/make-macos-app.sh now takes the bundle's names instead of hardcoding
// them, because a second program built on this app needs its own identifier or
// the two collide in LaunchServices. The defaults have to stay exactly what
// this app has always shipped, and "exactly" is a thing to check rather than
// to be careful about.
public class MacBundleScriptTests
{
    static string Repo() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    static string Run(string publish, string appPath, params (string Key, string Value)[] env)
    {
        var script = Path.Combine(Repo(), "scripts", "make-macos-app.sh");
        var psi = new ProcessStartInfo("/bin/bash", $"\"{script}\" \"{publish}\" 1.2.3 \"{appPath}\"")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Repo(),
        };
        foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
        return File.ReadAllText(Path.Combine(appPath, "Contents", "Info.plist"));
    }

    static string Publish(string dir, string exe)
    {
        var publish = Path.Combine(dir, "publish");
        Directory.CreateDirectory(publish);
        File.WriteAllText(Path.Combine(publish, exe), "#!/bin/sh\n");
        return publish;
    }

    // Nothing to check on Windows: the script is bash and only the mac
    // package job ever runs it. CI tests on Linux, where it does run.
    [Fact]
    public void The_defaults_are_what_this_app_has_always_shipped()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("qscm-bundle-").FullName;
        try
        {
            var plist = Run(Publish(dir, "QuadStickConfigManager"), Path.Combine(dir, "Out.app"));
            Assert.Contains("<key>CFBundleName</key><string>Quadstick: Config Manager</string>", plist, StringComparison.Ordinal);
            Assert.Contains("<key>CFBundleDisplayName</key><string>Quadstick: Config Manager</string>", plist, StringComparison.Ordinal);
            Assert.Contains("<key>CFBundleIdentifier</key><string>com.bbrizly.quadstickconfigmanager</string>", plist, StringComparison.Ordinal);
            Assert.Contains("<key>CFBundleExecutable</key><string>QuadStickConfigManager</string>", plist, StringComparison.Ordinal);
            Assert.Contains("<key>CFBundleShortVersionString</key><string>1.2.3</string>", plist, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_second_program_gets_its_own_identity()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("qscm-bundle-").FullName;
        try
        {
            var plist = Run(Publish(dir, "SomethingElse"), Path.Combine(dir, "Other.app"),
                ("QSCM_EXE", "SomethingElse"),
                ("QSCM_ID", "com.example.somethingelse"),
                ("QSCM_NAME", "Something Else"));
            Assert.Contains("<key>CFBundleIdentifier</key><string>com.example.somethingelse</string>", plist, StringComparison.Ordinal);
            Assert.Contains("<key>CFBundleExecutable</key><string>SomethingElse</string>", plist, StringComparison.Ordinal);
            Assert.Contains("<key>CFBundleDisplayName</key><string>Something Else</string>", plist, StringComparison.Ordinal);
            // And it did not quietly keep the free app's identifier as well.
            Assert.DoesNotContain("com.bbrizly.quadstickconfigmanager", plist, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }
}
