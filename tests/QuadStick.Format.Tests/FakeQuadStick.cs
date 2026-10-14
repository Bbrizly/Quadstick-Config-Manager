using System.Text;

namespace QuadStick.Format.Tests;

// A QuadStick console in memory, answering the way Joystick/Console.c does on
// firmware 2373. Each answer below names the line it copies, so a test that
// passes here passes against the parser the device runs, not a guess at it.
public sealed class FakeQuadStick : Stream
{
    public readonly Dictionary<string, string> Files = new(StringComparer.Ordinal);
    public readonly Dictionary<string, int> Prefs = new(StringComparer.Ordinal)
    {
        ["sip_puff_threshold"] = 60, ["puff_threshold"] = 0, ["sip_threshold"] = 0,
        ["puff_maximum"] = 0, ["sip_maximum"] = 0,
    };
    public readonly List<string> Commands = new();
    public int LongestLine;
    public bool Silent;              // nothing on the other end of the port
    public bool CorruptWrites;       // flash that drops the last byte of a chunk
    public bool CrLfOnRead = true;   // printf turning \n into \r\n

    readonly List<byte> _line = new();
    readonly Queue<byte> _out = new();
    string? _writing;
    readonly StringBuilder _written = new();

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = 0;
        while (n < count && _out.Count > 0) buffer[offset + n++] = _out.Dequeue();
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        for (int i = offset; i < offset + count; i++)
        {
            byte b = buffer[i];
            if ((b == '\r' || b == '\n') && _line.Count == 0) continue;   // :601
            if (b == 0x08) { _line.Clear(); continue; }                     // :606
            if (b != '\r') { _line.Add(b); continue; }                      // :607
            if (_line.Count > 529) { _line.Clear(); continue; }             // :598, dropped
            LongestLine = Math.Max(LongestLine, _line.Count);
            var line = Encoding.UTF8.GetString(_line.ToArray());
            _line.Clear();
            if (!Silent) Answer(line);
        }
    }

    void Say(string text) { foreach (var b in Encoding.UTF8.GetBytes(text)) _out.Enqueue(b); }

    void Answer(string line)
    {
        Commands.Add(line);
        var comma = line.IndexOf(',');
        var word = comma < 0 ? line : line[..comma];
        var rest = comma < 0 ? "" : line[(comma + 1)..];
        switch (word)
        {
            case "build": Say("2373\n"); break;                                      // :342
            case "calibrate": Say("\nCalibrated.  Hope you weren't touching it.\n"); break;
            case "pref":                                                             // :336-339
                Say("\nCurrent Preference values\n\n");
                foreach (var (k, v) in Prefs) Say($" {k}: {v}\n");
                break;
            case "load":                                                             // :259-266
                Say($"Console load command: {rest}\n");
                Say(Files.ContainsKey(rest) ? $"\nLoaded configuration file {rest} OK" : "\n*** File ErrorCode: 4 ***");
                break;
            case "read_file":                                                        // :381-400
                if (!Files.TryGetValue(rest, out var text)) { Say("Open file ErrorCode: 4\n"); break; }
                Say(CrLfOnRead ? text.Replace("\n", "\r\n") : text);
                Say("**END OF FILE**\n");
                break;
            case "write_file":                                                       // :401-438
                int cut = rest.IndexOf(',');
                var mode = cut < 0 ? rest : rest[..cut];
                var arg = cut < 0 ? "" : rest[(cut + 1)..];
                if (mode == "0") { _writing = arg; _written.Clear(); break; }
                if (mode == "1")
                {
                    _written.Append(CorruptWrites ? arg[..^1] : arg);
                    Say($"{Encoding.UTF8.GetByteCount(arg)}\n>");
                    return;                                                          // :425, no blip
                }
                Files[_writing!] = _written.ToString();
                Say($"{_written.Length} bytes written\n");
                break;
            case "delete_file": Files.Remove(rest); Say($"Deleted: {rest}\n"); break;
            case "boot": return;                                                     // resets, never answers
            default:
                // A preference name: :568-580. Anything else is ignored.
                if (Prefs.ContainsKey(word))
                {
                    Prefs[word] = int.Parse(rest);
                    Say($"\n{word,25}: {rest}\n>");
                }
                return;
        }
        Say("\n>");                                                                  // :540
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => 0;
    public override long Position { get => 0; set { } }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => 0;
    public override void SetLength(long value) { }
}

