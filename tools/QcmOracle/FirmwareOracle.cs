using System.Text.Json;

namespace QuadStick.Oracle;

/// <summary>
/// Test-only transcription of the QuadStick firmware's profile reader.
/// Keep behavior aligned with tests/QuadStick.Format.Tests/FirmwareOracle.cs;
/// do not "improve" this parser independently of the firmware oracle tests.
/// </summary>
internal static class FirmwareOracle
{
    internal sealed record Binding(string Output, string Function, IReadOnlyList<string> Inputs);
    internal sealed record Mode(string Channel, IReadOnlyList<Binding> Bindings);

    internal const int MaxKeywordLength = 64;
    internal const int MaxBindings = 128;
    internal const int MaxProfiles = 16;
    const int LineBuffer = 1024;

    static readonly IReadOnlyList<string> Outputs;
    static readonly IReadOnlyList<string> Inputs;
    static readonly IReadOnlyList<string> Preferences;
    static readonly IReadOnlyList<string> Functions;
    static readonly IReadOnlyList<string> Connections;

    static FirmwareOracle()
    {
        var root = Program.FindRepoRoot();
        var table = Path.Combine(root, "tests", "QuadStick.Format.Tests", "corpus", "firmware-2373.json");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(table));
        static string[] List(JsonElement e, string name) =>
            e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).ToArray();
        Outputs = List(doc.RootElement, "outputs");
        Inputs = List(doc.RootElement, "inputs");
        Preferences = List(doc.RootElement, "preferences");
        Functions = List(doc.RootElement, "functions");
        Connections = List(doc.RootElement, "connections");
    }

    internal static List<string> ReadLines(string text)
    {
        var lines = new List<string>();
        int n = 0;
        var buffer = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            buffer.Append(c);
            n++;
            if (c == '\n' || n == LineBuffer - 1)
            {
                lines.Add(buffer.ToString());
                buffer.Clear();
                n = 0;
            }
        }
        if (buffer.Length > 0) lines.Add(buffer.ToString());
        return lines;
    }

    internal static string? NextWord(string line, ref int index)
    {
        if (index == -1) return null;
        int start = index;
        for (int i = 0; i < MaxKeywordLength; i++)
        {
            char c = start + i < line.Length ? line[start + i] : '\0';
            if (IsWordChar(c)) continue;
            if (c == '\0') { index = -1; return line[start..(start + i)]; }
            index = start + i + 1;
            return line[start..(start + i)];
        }
        return null;
    }

    static bool IsWordChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ' ' or '-';

    internal static string? Match(string? word, IReadOnlyList<string> table)
    {
        if (word is null) return null;
        var keyword = word.TrimStart();
        foreach (var entry in table)
            if (string.Equals(entry, keyword, StringComparison.Ordinal)) return entry;
        return null;
    }

    internal static string? MatchWithParameter(string? word, IReadOnlyList<string> table)
    {
        if (word is null) return null;
        var keyword = word.TrimStart();
        foreach (var entry in table)
            if (keyword.StartsWith(entry, StringComparison.Ordinal)) return entry;
        return null;
    }

    internal static List<Mode> Read(string fileText)
    {
        var modes = new List<Mode>();
        var lines = ReadLines(fileText);
        if (lines.Count == 0 || !lines[0].StartsWith("QuadStick", StringComparison.Ordinal))
            return modes;
        int at = 1;
        while (at < lines.Count)
        {
            var line = lines[at++];
            if (line.StartsWith("Preferences", StringComparison.Ordinal)) SkipSegment(lines, ref at);
            else if (line.StartsWith("Infrared", StringComparison.Ordinal)) SkipSegment(lines, ref at);
            else if (line.StartsWith("Profile", StringComparison.Ordinal))
            {
                var mode = ReadSegment(lines, ref at, modes.Count);
                if (mode is not null) modes.Add(mode);
            }
        }
        return modes;
    }

    static void SkipSegment(List<string> lines, ref int at)
    {
        at = Math.Min(lines.Count, at + 2);
        while (at < lines.Count && !EndsSegment(lines[at])) at++;
    }

    static bool EndsSegment(string line) => line.Length == 0 || line[0] == '\n' || line[0] == '\r';

    static Mode? ReadSegment(List<string> lines, ref int at, int profileIndex)
    {
        if (at + 1 >= lines.Count) return null;
        at++;
        var labels = lines[at++];

        int k = 0;
        NextWord(labels, ref k);
        NextWord(labels, ref k);
        var channel = Match(NextWord(labels, ref k), Connections) ?? "usb";

        var bindings = new List<Binding>();
        int i = 0;
        while (at < lines.Count && !EndsSegment(lines[at]) && i < MaxBindings)
        {
            var line = lines[at++];
            int j = 0;
            var keyword = NextWord(line, ref j);
            if (keyword is not null && profileIndex < MaxProfiles)
            {
                var output = Match(keyword, Outputs);
                string function;
                if (output is null)
                {
                    output = Match(keyword, Preferences);
                    if (output is null) continue;
                    NextWord(line, ref j);
                    function = NextWord(line, ref j) ?? "";
                    bindings.Add(new Binding(output, function, Array.Empty<string>()));
                    i++;
                    continue;
                }
                function = MatchWithParameter(NextWord(line, ref j), Functions) ?? "normal";
                var slots = new string?[8];
                for (int l = 0; l < 8; l++) slots[7 - l] = Match(NextWord(line, ref j), Inputs);
                var inputs = slots.Where(s => s is not null && s != "none").Select(s => s!).ToList();
                inputs.Reverse();
                bindings.Add(new Binding(output, function, inputs));
            }
            i++;
        }
        return new Mode(channel, bindings);
    }
}
