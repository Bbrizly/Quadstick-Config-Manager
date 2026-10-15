using System.Globalization;
using System.Text;

namespace QuadStick.Format;

// The QuadStick's own text console, over Bluetooth (the RN-42 in SPP mode) or
// the 3.3V serial cable on the digital inputs 1&2 jack. Both arrive at the
// same parser, so this class takes any Stream and never knows which.
//
// Every rule here is read from Joystick/Console.c on firmware 2373:
// - A line ends at '\r' and only '\r' (poll_for_console_input, :607). A '\n'
//   inside a line is kept, which is how write_file,1 carries line breaks.
// - A backspace throws away everything before it on the line (:606), so each
//   command is sent after one, and half a line left over from anything else
//   cannot glue itself onto it.
// - The line buffer is 530 bytes (:151). A longer line is dropped without a
//   word (:618), so nothing sent here comes near it.
// - Every answer ends "\n>" (:540, :424, :562, :579). That prompt is the only
//   end-of-answer signal the device gives.
public sealed class DeviceConsole(Stream port)
{
    /// <summary>How long one answer may take. A file read over Bluetooth is
    /// the slow case: a few kilobytes at 115200 is well under a second.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    const string Prompt = "\n>";
    const string EndOfFile = "**END OF FILE**";   // Console.c:399

    // Room for the longest prefix, "write_file,1,", inside the 530 byte buffer
    // with plenty to spare for a multi-byte character at the edge.
    internal const int ChunkBytes = 480;

    /// <summary>Send one command and return everything the device said back,
    /// prompt included.</summary>
    public string Send(string command)
    {
        if (command.Contains('\r') || command.Contains('\b'))
            throw new ArgumentException("A console command cannot hold a carriage return or a backspace.", nameof(command));
        var bytes = Encoding.UTF8.GetBytes("\b" + command + "\r");
        port.Write(bytes, 0, bytes.Length);
        port.Flush();
        return ReadUntil(text => text.EndsWith(Prompt, StringComparison.Ordinal));
    }

    string ReadUntil(Func<string, bool> done)
    {
        var got = new List<byte>();
        var buffer = new byte[1024];
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            int n;
            // A serial port with a ReadTimeout throws rather than returning
            // nothing. Either way it means no bytes yet, not a failure.
            try { n = port.Read(buffer, 0, buffer.Length); }
            catch (TimeoutException) { continue; }
            if (n == 0) { Thread.Sleep(10); continue; }
            got.AddRange(buffer.AsSpan(0, n).ToArray());
            var text = Encoding.UTF8.GetString(got.ToArray());
            if (done(text)) return text;
        }
        throw new TimeoutException(Strings.DeviceConsole_NoAnswer);
    }

    /// <summary>The firmware build number, or null if what answered is not a
    /// QuadStick. The probe: <c>build</c> prints FW_VERSION alone (Console.c:342).</summary>
    public int? Build()
    {
        var reply = Send("build");
        foreach (var line in Lines(reply))
            if (int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return v;
        return null;
    }

    /// <summary>Every preference and its live value (<c>pref</c>, Console.c:335).</summary>
    public Dictionary<string, int> Preferences()
    {
        var prefs = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in Lines(Send("pref")))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (int.TryParse(line[(colon + 1)..].Trim(), NumberStyles.AllowLeadingSign,
                             CultureInfo.InvariantCulture, out var v))
                prefs[line[..colon].Trim()] = v;
        }
        return prefs;
    }

    /// <summary>Change one preference in the device's memory only. prefs.csv is
    /// not touched, so unplugging the QuadStick or <see cref="Boot"/> puts the
    /// file's value back (Console.c:575 writes Preferences and PrefsBackup).</summary>
    public void SetPreference(string name, int value)
    {
        // A name the firmware does not know gets no answer at all (:570), so
        // silence here means refused, and the echo is the only proof it took.
        string reply;
        try { reply = Send(string.Create(CultureInfo.InvariantCulture, $"{name},{value}")); }
        catch (TimeoutException) { reply = ""; }
        if (!reply.Contains(string.Create(CultureInfo.InvariantCulture, $"{name}: {value}"), StringComparison.Ordinal))
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Strings.DeviceConsole_SettingRefused, name));
    }

    /// <summary>The file's text, or null if the device has no file by that name.</summary>
    public string? ReadFile(string name)
    {
        Write("read_file," + name);
        var reply = ReadUntil(text => text.EndsWith(Prompt, StringComparison.Ordinal)
                                    && (text.Contains(EndOfFile, StringComparison.Ordinal)
                                        || text.Contains("ErrorCode", StringComparison.Ordinal)));
        var end = reply.LastIndexOf(EndOfFile, StringComparison.Ordinal);
        return end < 0 ? null : reply[..end];
    }

    /// <summary>Create or replace a file: open, lines in chunks, close.</summary>
    public void WriteFile(string name, string text)
    {
        if (text.Contains('\r') || text.Contains('\b'))
            throw new ArgumentException("Line breaks must be bare \\n, and a backspace cannot be sent.", nameof(text));

        var open = Send("write_file,0," + name);
        if (open.Contains("ErrorCode", StringComparison.Ordinal))
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Strings.DeviceConsole_CouldNotOpen, name));

        foreach (var chunk in Chunks(text))
        {
            // write_file,1 answers with f_puts' count (:424), -1 on a failure.
            var reply = Send("write_file,1," + chunk);
            if (!Lines(reply).Any(l => int.TryParse(l, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Strings.DeviceConsole_WriteRefused, name));
        }

        // Two digit year: diskio.c:77 stores it as years since 2000.
        var now = DateTime.Now;
        Send(string.Create(CultureInfo.InvariantCulture,
            $"write_file,2,{now.Year % 100},{now.Month},{now.Day},{now.Hour},{now.Minute},{now.Second}"));
    }

    /// <summary>Make the device read a file now (Console.c:256).</summary>
    public void Load(string name)
    {
        var reply = Send("load," + name);
        if (!reply.Contains(" OK", StringComparison.Ordinal))
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Strings.DeviceConsole_CouldNotLoad, name));
    }

    public void Delete(string name) => Send("delete_file," + name);

    /// <summary>Zero the pressure sensors. Nobody may be touching the mouthpiece.</summary>
    public void Calibrate() => Send("calibrate");

    /// <summary>Restart the QuadStick. Everything set in memory goes; the files
    /// are read again. It does not answer: it is gone mid-reply.</summary>
    public void Boot() => Write("boot");

    void Write(string command)
    {
        var bytes = Encoding.UTF8.GetBytes("\b" + command + "\r");
        port.Write(bytes, 0, bytes.Length);
        port.Flush();
    }

    // Split on whole characters, never inside one, so a UTF-8 comment cell
    // reaches the file intact.
    internal static IEnumerable<string> Chunks(string text)
    {
        var sb = new StringBuilder();
        int bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            int n = rune.Utf8SequenceLength;
            if (bytes + n > ChunkBytes) { yield return sb.ToString(); sb.Clear(); bytes = 0; }
            sb.Append(rune.ToString());
            bytes += n;
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    static IEnumerable<string> Lines(string reply) =>
        reply.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Where(l => l != ">");

    // ---- sending a profile ----

    public sealed record SendResult(string Name, string? BackupPath);

    /// <summary>Install a profile over the console. Same gates as a drive
    /// install (<see cref="Device.InstallName"/>), then: back up what the
    /// device has, write, read it back, and only load it if it matches.</summary>
    public SendResult SendProfile(ProfileFile file, string backupDir,
                                  bool confirmDefaultCsv = false, bool confirmPreferencesCsv = false)
    {
        var name = Device.InstallName(file, confirmDefaultCsv, confirmPreferencesCsv);

        var toWrite = ProfileFile.Load(file.ToCsvText());
        toWrite.NormalizeForDeviceCsv();
        var text = toWrite.ToCsvText().Replace("\r\n", "\n");

        string? backup = null;
        if (ReadFile(name) is { } old)
            backup = Device.SaveBackup(name, old, backupDir);

        WriteFile(name, text);

        // f_gets and printf may each turn \n into \r\n on the way back, so the
        // comparison ignores carriage returns and nothing else.
        var back = ReadFile(name);
        if (back is null || back.Replace("\r", "") != text)
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture,
                Strings.DeviceConsole_ReadBackDiffers, name, backup ?? Strings.DeviceConsole_NoEarlierFile));

        Load(name);
        return new SendResult(name, backup);
    }

    // ---- measuring strength ----

    /// <summary>The file the strength test loads. Hard sip and hard puff on the
    /// centre tube both press X, and nothing soft is bound. With no soft input
    /// in use the soft latch timer never runs (DataFlow.c:319), so a breath
    /// that reaches the hard threshold always fires it.</summary>
    public const string StrengthTestName = "qcm_strength.csv";

    public const string StrengthTestProfile =
        "Profile Name,,Strength test,,,,,,,,\n" +
        StrengthTestName + ",,,,,,,,,,\n" +
        "PlayStation Outputs,Function,usb,,,,,,,,\n" +
        "x,normal,mp_center_puff,,,,,,,,\n" +
        "x,normal,mp_center_sip,,,,,,,,\n";

    /// <summary>The per-direction hard threshold. 0 in prefs.csv means "use
    /// sip_puff_threshold" (DataFlow.c:310-316), so setting it here overrides
    /// the shared one for that direction alone.</summary>
    public static string ThresholdPreference(bool puff) => puff ? "puff_threshold" : "sip_threshold";

    /// <summary>Its ceiling. Held at 100 for the test: the analog scale divides
    /// by maximum minus threshold (DataFlow.c:353).</summary>
    public static string MaximumPreference(bool puff) => puff ? "puff_maximum" : "sip_maximum";
}

public static class StrengthSweep
{
    // Percent of half-scale from the calibrated zero (DataFlow.c:294-297).
    // 95, not 100: at 100 the threshold meets the maximum held for the test.
    public const int Lowest = 5, Highest = 95;

    /// <summary>The highest threshold the person reaches, by binary search: 7
    /// tries cover 5 to 95. Lowest - 1 means they reached none of it.</summary>
    /// <remarks>ponytail: one try per step, so a weak breath on one try reads
    /// low. Retest the answer if that turns out to matter.</remarks>
    public static async Task<int> FindAsync(Func<int, Task<bool>> reaches, int lo = Lowest, int hi = Highest)
    {
        int best = lo - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (await reaches(mid)) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return best;
    }
}
