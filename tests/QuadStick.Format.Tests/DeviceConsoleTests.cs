using System.Text;
using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

public class DeviceConsoleTests
{
    static ProfileFile Profile(string name = "game.csv", string comment = "") => ProfileFile.Load(
        $"Profile Name,,Game,,,,,,,,Comments\n{name},,,,,,,,,,\n" +
        "PlayStation Outputs,Function,usb,,,,,,,,\n" +
        $"x,normal,mp_center_puff,,,,,,,,{comment}\n" +
        "circle,normal,mp_center_sip,,,,,,,,\n");

    static string TempDir() => Path.Combine(Path.GetTempPath(), "qcm-console-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_profile_lands_byte_for_byte_and_is_loaded_last()
    {
        var device = new FakeQuadStick();
        device.Files["game.csv"] = "the old one\n";
        var dir = TempDir();
        try
        {
            var result = new DeviceConsole(device).SendProfile(Profile(), dir);

            var expected = ProfileFile.Load(Profile().ToCsvText());
            expected.NormalizeForDeviceCsv();
            Assert.Equal(expected.ToCsvText().Replace("\r\n", "\n"), device.Files["game.csv"]);
            // Kept as it came off the device, carriage returns and all.
            Assert.Equal("the old one\n", File.ReadAllText(result.BackupPath!).Replace("\r", ""));
            Assert.Equal("load,game.csv", device.Commands[^1]);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_file_that_reads_back_wrong_is_never_loaded()
    {
        var device = new FakeQuadStick { CorruptWrites = true };
        var dir = TempDir();
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new DeviceConsole(device).SendProfile(Profile(), dir));
            Assert.Contains("game.csv", ex.Message);
            Assert.DoesNotContain(device.Commands, c => c.StartsWith("load,", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Default_csv_needs_the_same_yes_over_the_console_as_on_a_drive()
    {
        var device = new FakeQuadStick();
        Assert.Throws<InvalidOperationException>(() =>
            new DeviceConsole(device).SendProfile(Profile("default.csv"), TempDir()));
        Assert.Empty(device.Commands);
    }

    [Fact]
    public void A_long_profile_never_overruns_the_530_byte_line_and_keeps_its_accents()
    {
        // Enough rows to need many chunks, and a comment with multi-byte
        // characters so a chunk edge lands inside one somewhere.
        var csv = new StringBuilder("Profile Name,,Game,,,,,,,,Comments\nlong.csv,,,,,,,,,,\nPlayStation Outputs,Function,usb,,,,,,,,\n");
        for (int i = 0; i < 60; i++) csv.Append("x,normal,mp_center_puff,,,,,,,,é è ü 日本 ").Append(i).Append('\n');
        var device = new FakeQuadStick();
        var dir = TempDir();
        try
        {
            new DeviceConsole(device).SendProfile(ProfileFile.Load(csv.ToString()), dir);
            Assert.InRange(device.LongestLine, 1, 529);
            Assert.Contains("日本 59", device.Files["long.csv"]);
            Assert.Equal("load,long.csv", device.Commands[^1]);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Chunks_join_back_to_the_text_and_each_fits()
    {
        var text = string.Concat(Enumerable.Repeat("aé日😀\n", 400));
        var chunks = DeviceConsole.Chunks(text).ToList();
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, c => Assert.InRange(Encoding.UTF8.GetByteCount(c), 1, DeviceConsole.ChunkBytes));
    }

    [Fact]
    public void A_setting_the_firmware_does_not_know_is_reported_not_assumed()
    {
        var device = new FakeQuadStick();
        var console = new DeviceConsole(device) { Timeout = TimeSpan.FromMilliseconds(200) };
        console.SetPreference("puff_threshold", 45);
        Assert.Equal(45, device.Prefs["puff_threshold"]);
        Assert.Equal(45, console.Preferences()["puff_threshold"]);
        // The firmware answers an unknown name with nothing at all (:570).
        var ex = Assert.Throws<InvalidOperationException>(() => console.SetPreference("puff_threshhold", 45));
        Assert.Contains("puff_threshhold", ex.Message);
    }

    [Fact]
    public void Nothing_on_the_port_times_out_with_a_reason()
    {
        var console = new DeviceConsole(new FakeQuadStick { Silent = true }) { Timeout = TimeSpan.FromMilliseconds(150) };
        var ex = Assert.Throws<TimeoutException>(() => console.Build());
        Assert.Contains("digital_in_1", ex.Message);
    }

    [Fact]
    public void Half_a_line_left_on_the_port_does_not_eat_the_next_command()
    {
        // Somebody's terminal, or a send cut off by a dropped Bluetooth link,
        // leaves bytes with no carriage return. The backspace in front of each
        // command is what throws them away (Console.c:606).
        var device = new FakeQuadStick();
        var junk = Encoding.UTF8.GetBytes("read_fi");
        device.Write(junk, 0, junk.Length);
        var console = new DeviceConsole(device) { Timeout = TimeSpan.FromMilliseconds(300) };
        Assert.Equal(2373, console.Build());
    }

    [Fact]
    public void Build_identifies_a_QuadStick()
    {
        Assert.Equal(2373, new DeviceConsole(new FakeQuadStick()).Build());
    }

    [Fact]
    public void The_strength_test_profile_is_what_the_firmware_reads_it_as()
    {
        // Sent the way every profile is sent, so read what that path writes.
        var device = new FakeQuadStick();
        var dir = TempDir();
        try { new DeviceConsole(device).SendProfile(ProfileFile.Load(DeviceConsole.StrengthTestProfile), dir); }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        var modes = FirmwareOracle.Read(device.Files[DeviceConsole.StrengthTestName]);
        var mode = Assert.Single(modes);
        Assert.All(mode.Bindings, b => Assert.Equal("x", b.Output));
        Assert.Equal(new[] { "mp_center_puff", "mp_center_sip" }, mode.Bindings.Select(b => Assert.Single(b.Inputs)));
        // Nothing soft: a bound soft input is what lets the latch block a hard one.
        Assert.DoesNotContain(mode.Bindings, b => b.Inputs.Any(i => i.EndsWith("_soft", StringComparison.Ordinal)));
        var file = ProfileFile.Load(DeviceConsole.StrengthTestProfile);
        Assert.False(file.HasErrors, string.Join("\n", file.Issues));
        Assert.Equal(DeviceConsole.StrengthTestName, file.Document.CsvFileName);
    }

    [Fact]
    public void The_threshold_preferences_are_real_firmware_words()
    {
        foreach (var puff in new[] { true, false })
        {
            Assert.Contains(DeviceConsole.ThresholdPreference(puff), FirmwareOracle.Preferences);
            Assert.Contains(DeviceConsole.MaximumPreference(puff), FirmwareOracle.Preferences);
        }
    }

    [Fact]
    public async Task The_sweep_finds_every_strength_exactly_in_seven_breaths()
    {
        for (int strength = StrengthSweep.Lowest - 1; strength <= StrengthSweep.Highest; strength++)
        {
            int tries = 0;
            var found = await StrengthSweep.FindAsync(t => { tries++; return Task.FromResult(t <= strength); });
            Assert.Equal(strength, found);
            Assert.InRange(tries, 1, 7);
        }
    }
}
