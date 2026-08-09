using System.Text.Json;

namespace QuadStick.Format.Tests;

/// <summary>What the QuadStick actually ends up with when it reads a file.
///
/// This is a line by line transcription of the device's own reader, not a
/// description of it: next_word, search_for_keyword,
/// search_for_keyword_with_parameter and the three segment loaders from
/// Joystick/Configuration.c, plus f_gets from Joystick/fatfs/ff.c
/// (FW_VERSION 2373). Its keyword tables are corpus/firmware-2373.json, dumped
/// straight out of the firmware headers.
///
/// Moving from 1476 to 2373 needed no re-transcription: every function below is
/// byte for byte the same in both sources, and all four limits are unchanged.
/// 2373 only grew the keyword tables, which is why corpus/firmware-1476.json is
/// still here for The_new_firmware_took_nothing_away to check against.
///
/// Nothing in here may be "improved". If it disagrees with the app, the app is
/// what changes. If the firmware changes, re-dump the tables and re-transcribe
/// the functions below, and let the tests tell you what moved.</summary>
public static class FirmwareOracle
{
    public sealed record Binding(string Output, string Function, IReadOnlyList<string> Inputs);

    public sealed record Mode(string Channel, IReadOnlyList<Binding> Bindings);

    // InputOutputChannels.h and Configuration.h.
    public const int MaxKeywordLength = 64;
    public const int MaxBindings = 128;
    public const int MaxProfiles = 16;
    const int LineBuffer = 1024;

    public static readonly IReadOnlyList<string> Outputs;
    public static readonly IReadOnlyList<string> Inputs;
    public static readonly IReadOnlyList<string> Preferences;
    public static readonly IReadOnlyList<string> Functions;
    public static readonly IReadOnlyList<string> Connections;

    static FirmwareOracle()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine("corpus", "firmware-2373.json")));
        static string[] List(JsonElement e, string name) =>
            e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).ToArray();
        Outputs = List(doc.RootElement, "outputs");
        Inputs = List(doc.RootElement, "inputs");
        Preferences = List(doc.RootElement, "preferences");
        Functions = List(doc.RootElement, "functions");
        Connections = List(doc.RootElement, "connections");
    }

    // ff.c f_gets, with _USE_STRFUNC 1 (Joystick/fatfs/ffconf.h), so '\r' is
    // NOT stripped: it keeps at most len-1 characters, stops after '\n', and
    // hands whatever is left back on the next call. A line longer than 1023
    // characters therefore arrives as two.
    public static List<string> ReadLines(string text)
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

    /// <summary>next_word. Returns null for the firmware's NULL, and leaves
    /// <paramref name="index"/> alone when it does, exactly as the C does.</summary>
    public static string? NextWord(string line, ref int index)
    {
        if (index == -1) return null; // end of line detected last time
        int start = index;
        for (int i = 0; i < MaxKeywordLength; i++)
        {
            char c = start + i < line.Length ? line[start + i] : '\0';
            if (IsWordChar(c)) continue;
            if (c == '\0') { index = -1; return line[start..(start + i)]; }
            index = start + i + 1;
            return line[start..(start + i)];
        }
        return null; // 64 characters with no separator: NULL, index untouched
    }

    // "any character not alphanumeric, or in: '_. -' is a separator character".
    // isalnum on a non-ASCII byte is not defined for this build, so anything
    // outside ASCII counts as a separator here.
    static bool IsWordChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ' ' or '-';

    /// <summary>search_for_keyword: skips LEADING whitespace, then compares the
    /// whole word. A trailing space is part of the word and stops the match.
    /// Returns null for the caller's default_value.</summary>
    public static string? Match(string? word, IReadOnlyList<string> table)
    {
        if (word is null) return null;
        var keyword = word.TrimStart();
        foreach (var entry in table)
            if (string.Equals(entry, keyword, StringComparison.Ordinal))
                return entry;
        return null;
    }

    /// <summary>search_for_keyword_with_parameter: compares only the first
    /// strlen(entry) characters, in table order, so a cell only has to START
    /// with a keyword. Null means the caller's default, which the binding loop
    /// passes as 0, "normal".</summary>
    public static string? MatchWithParameter(string? word, IReadOnlyList<string> table)
    {
        if (word is null) return null;
        var keyword = word.TrimStart();
        foreach (var entry in table)
            if (keyword.StartsWith(entry, StringComparison.Ordinal))
                return entry;
        return null;
    }

    /// <summary>Load_Configuration_File and the three segment loaders. Returns
    /// one Mode per Profile segment the device actually loads.</summary>
    public static List<Mode> Read(string fileText)
    {
        var modes = new List<Mode>();
        var lines = ReadLines(fileText);
        int at = 0;

        // "look for QuadStick as first word or bailout"
        if (lines.Count == 0 || !lines[0].StartsWith("QuadStick", StringComparison.Ordinal))
            return modes;
        at = 1;

        while (at < lines.Count)
        {
            var line = lines[at++];
            // strncmp against the START of the raw line, case sensitively,
            // Preferences before Profile before Infrared.
            if (line.StartsWith("Preferences", StringComparison.Ordinal)) SkipSegment(lines, ref at);
            else if (line.StartsWith("Infrared", StringComparison.Ordinal)) SkipSegment(lines, ref at);
            else if (line.StartsWith("Profile", StringComparison.Ordinal))
            {
                var mode = ReadSegment(lines, ref at, modes.Count);
                if (mode is not null) modes.Add(mode);
            }
            // anything else: "Unrecognized segment", one line, skipped
        }
        return modes;
    }

    // The Preferences and Infrared loaders skip two lines and then run the same
    // "until a line that starts with \n or \r" loop. Only their line count
    // matters here, so that the Profile segments after them line up.
    static void SkipSegment(List<string> lines, ref int at)
    {
        at += 2;
        while (at < lines.Count && !EndsSegment(lines[at])) at++;
    }

    static bool EndsSegment(string line) => line.Length == 0 || line[0] == '\n' || line[0] == '\r';

    static Mode? ReadSegment(List<string> lines, ref int at, int profileIndex)
    {
        if (at + 1 >= lines.Count) return null; // "Configuration file too short!"
        at++;                                   // the filename row, skipped whole
        var labels = lines[at++];               // the third line carries the channel

        int k = 0;
        NextWord(labels, ref k);                // skip "Output or Function"
        NextWord(labels, ref k);                // skip "Function"
        // connections_keywords, defaulting to USB, exactly like the rest of the
        // tables. It used to be written out by hand here, which is how the
        // oracle stayed on the 2017 list of three after everything else moved
        // to 2373 and its fourth word, "both".
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
                    // No output matched, so try a preference name instead.
                    output = Match(keyword, Preferences);
                    if (output is null) continue; // the `continue` that skips i++
                    NextWord(line, ref j);        // skip the function cell
                    function = NextWord(line, ref j) ?? "";  // atoi of the value
                    bindings.Add(new Binding(output, function, Array.Empty<string>()));
                    i++;
                    continue;
                }
                function = MatchWithParameter(NextWord(line, ref j), Functions) ?? "normal";

                // Eight input cells, read in reverse and then shifted down to
                // drop every one the device did not recognize.
                var slots = new string?[8];
                for (int l = 0; l < 8; l++)
                    slots[7 - l] = Match(NextWord(line, ref j), Inputs);
                var inputs = slots.Where(s => s is not null && s != "none").Select(s => s!).ToList();
                inputs.Reverse(); // back into the order the file lists them

                bindings.Add(new Binding(output, function, inputs));
            }
            i++;
        }
        return new Mode(channel, bindings);
    }
}
