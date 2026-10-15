using System.Diagnostics;
using Xunit;

namespace QuadStick.App.Tests;

// The command a Mac user runs (or the app runs for them) must leave a login
// agent that sets the layout, set it for this session, and undo both. It runs
// for real here against a scratch HOME, with launchctl swapped for a recorder
// so the test machine's own session is never touched.
public class MacGamepadTests
{
    internal static (string Home, string Log) Scratch(bool launchctlFails = false)
    {
        // A space in HOME, because real ones have them ("Jane Doe").
        var home = Directory.CreateDirectory(Path.Combine(Directory.CreateTempSubdirectory("qcm-macpad").FullName, "Jane Doe")).FullName;
        var bin = Directory.CreateDirectory(Path.Combine(home, "bin")).FullName;
        var log = Path.Combine(home, "launchctl.log");
        var fake = Path.Combine(bin, "launchctl");
        File.WriteAllText(fake, $"#!/bin/sh\nprintf '%s|' \"$@\" >> '{log}'\necho >> '{log}'\n" + (launchctlFails ? "exit 1\n" : ""));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        MacGamepad.TestEnvironment = new() { ["HOME"] = home, ["PATH"] = bin + ":/usr/bin:/bin" };
        return (home, log);
    }

    static string Plist(string path, string key)
    {
        using var p = Process.Start(new ProcessStartInfo("/usr/bin/plutil", ["-extract", key, "raw", path])
            { RedirectStandardOutput = true })!;
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o.TrimEnd('\n');
    }

    // zsh is what Terminal opens; bash is what someone who changed it gets.
    [Theory]
    [InlineData("/bin/zsh")]
    [InlineData("/bin/bash")]
    public void The_command_sets_the_layout_now_and_at_every_login_and_undo_clears_both(string shell)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var (home, log) = Scratch();
        try
        {
            string value = string.Join("\n", MacGamepad.Mappings);
            Assert.True(MacGamepad.Run(MacGamepad.Command, shell));
            Assert.True(MacGamepad.IsOn);
            Assert.Equal(MacGamepad.Label, Plist(MacGamepad.AgentPath, "Label"));
            Assert.Equal("true", Plist(MacGamepad.AgentPath, "RunAtLoad"));
            Assert.Equal("/bin/launchctl", Plist(MacGamepad.AgentPath, "ProgramArguments.0"));
            Assert.Equal(value, Plist(MacGamepad.AgentPath, "ProgramArguments.3"));
            Assert.Equal($"setenv|SDL_GAMECONTROLLERCONFIG|{value}|", File.ReadAllText(log).TrimEnd('\n'));

            Assert.True(MacGamepad.Run(MacGamepad.UndoCommand, shell));
            Assert.False(MacGamepad.IsOn);
            Assert.EndsWith("unsetenv|SDL_GAMECONTROLLERCONFIG|", File.ReadAllText(log).TrimEnd('\n'));
        }
        finally
        {
            MacGamepad.TestEnvironment = null;
            Directory.Delete(Path.GetDirectoryName(home)!, true);
        }
    }

    // The layout is read off the firmware descriptor, so pin it: a stray edit
    // here moves a button for every Mac user at once.
    [Fact]
    public void Each_line_is_mode_0_with_the_firmware_layout()
    {
        Assert.Equal(2, MacGamepad.Mappings.Length);
        Assert.StartsWith("03000000d01600002b09000000000000,", MacGamepad.Mappings[0]);
        Assert.StartsWith("03000000d01600002b09000003000000,", MacGamepad.Mappings[1]);
        foreach (var line in MacGamepad.Mappings)
        {
            var fields = line.Split(',', StringSplitOptions.RemoveEmptyEntries).Skip(2).ToList();
            Assert.Equal(22, fields.Count);
            Assert.Equal(fields.Count, fields.Distinct().Count());
            Assert.Subset(fields.ToHashSet(), new HashSet<string>
            {
                "x:b0", "a:b1", "b:b2", "y:b3", "leftshoulder:b4", "rightshoulder:b5",
                "lefttrigger:b6", "righttrigger:b7", "back:b8", "start:b9",
                "leftstick:b10", "rightstick:b11", "guide:b12",
                "leftx:a0", "lefty:a1", "rightx:a2", "righty:a3",
                "dpup:h0.1", "dpright:h0.2", "dpdown:h0.4", "dpleft:h0.8", "platform:Mac OS X",
            });
        }
    }
}
