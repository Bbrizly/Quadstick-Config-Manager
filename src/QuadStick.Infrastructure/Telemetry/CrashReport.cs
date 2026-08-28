using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using QuadStick.Application.Telemetry;

namespace QuadStick.Infrastructure.Telemetry;

// Turns an exception into something safe to send. Pure and SDK-free on
// purpose: this is the privacy-critical half of telemetry, and it must be
// reviewable and testable without a network stack anywhere near it. The neutral
// payload DTOs live in Application so the telemetry provider can consume them
// without depending on the Avalonia assembly.
public static partial class CrashReport
{
    public const int SchemaVersion = 1;

    [GeneratedRegex(
        @"(?<prefix>(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/])?(?:Users|home)[\\/])(?<user>[^\\/]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex HomePath();

    public static string SanitizePath(string s) =>
        string.IsNullOrEmpty(s) ? s
            : HomePath().Replace(s, m => m.Groups["prefix"].Value + "<user>");

    public static CrashPayload Build(string where, Exception? ex) => new(
        Schema: SchemaVersion,
        Where: where,
        App: Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0",
        Os: OperatingSystem.IsWindows() ? "windows"
          : OperatingSystem.IsMacOS() ? "macos"
          : OperatingSystem.IsLinux() ? "linux" : "other",
        OsVersion: Truncate(RuntimeInformation.OSDescription, 80),
        IsDebug: IsDebugBuild,
        Utc: DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        Chain: Flatten(ex).Select(Describe).ToList());

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

#if DEBUG
    const bool IsDebugBuild = true;
#else
    const bool IsDebugBuild = false;
#endif

    static IEnumerable<Exception> Flatten(Exception? ex)
    {
        if (ex is null) yield break;
        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.Flatten().InnerExceptions)
                foreach (var e in Flatten(inner)) yield return e;
            yield break;
        }
        yield return ex;
        foreach (var e in Flatten(ex.InnerException)) yield return e;
    }

    static CrashException Describe(Exception ex) =>
        new(ex.GetType().FullName ?? "Unknown", Frames(ex));

    // Exception.Message is deliberately never read. It can contain profile
    // values or user-provided names, and no scrubber can distinguish those
    // reliably from ordinary prose.
    static List<CrashFrame> Frames(Exception ex)
    {
        var frames = new List<CrashFrame>();
        try
        {
            foreach (var f in new StackTrace(ex, fNeedFileInfo: false).GetFrames())
            {
                var m = f.GetMethod();
                if (m is null) continue;
                var type = m.DeclaringType?.FullName ?? "";
                frames.Add(new CrashFrame(
                    SanitizePath($"{type}.{m.Name}"),
                    type.StartsWith("QuadStick.", StringComparison.Ordinal)));
            }
        }
        catch { }
        return frames;
    }

    public static string? PendingDirOverride { get; set; }

    public static string PendingDir => PendingDirOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuadStickConfigManager", "pending-reports");

    public const int MaxPending = 5;
    public const int MaxAgeDays = 30;

    public static void Write(string where, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(PendingDir);
            var name = $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json";
            var final = Path.Combine(PendingDir, name);
            var tmp = final + ".tmp";
            File.WriteAllText(tmp, ToJson(Build(where, ex)));
            File.Move(tmp, final, overwrite: true);
            Trim();
        }
        catch { }
    }

    public static IReadOnlyList<string> Pending()
    {
        try
        {
            if (!Directory.Exists(PendingDir)) return Array.Empty<string>();
            Trim();
            return Directory.GetFiles(PendingDir, "crash-*.json")
                .OrderBy(File.GetLastWriteTimeUtc)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    public static void Discard()
    {
        try
        {
            foreach (var f in Directory.GetFiles(PendingDir, "crash-*"))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    public static void Discard(string path)
    {
        try { File.Delete(path); } catch { }
    }

    static void Trim()
    {
        try
        {
            var inFlight = DateTime.UtcNow.AddMinutes(-5);
            var files = Directory.GetFiles(PendingDir, "crash-*")
                .Where(f => !f.EndsWith(".tmp", StringComparison.Ordinal)
                            || File.GetLastWriteTimeUtc(f) < inFlight)
                .OrderBy(File.GetLastWriteTimeUtc).ToList();

            var cutoff = DateTime.UtcNow.AddDays(-MaxAgeDays);
            foreach (var f in files.ToList())
                if (File.GetLastWriteTimeUtc(f) < cutoff)
                {
                    try { File.Delete(f); files.Remove(f); } catch { }
                }

            foreach (var f in files.Take(Math.Max(0, files.Count - MaxPending)))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    public static string ToJson(CrashPayload p) =>
        JsonSerializer.Serialize(p, CrashJsonContext.Default.CrashPayload);

    public const int MaxChain = 20;
    public const int MaxFrames = 200;
    public const int MaxNameChars = 300;

    public static CrashPayload? FromJson(string json)
    {
        try
        {
            if (json.Length > 512 * 1024) return null;
            var p = JsonSerializer.Deserialize(json, CrashJsonContext.Default.CrashPayload);
            if (p is null || p.Schema != SchemaVersion) return null;

            var chain = new List<CrashException>();
            foreach (var ex in (p.Chain ?? []).Take(MaxChain))
            {
                var frames = (ex.Frames ?? [])
                    .Take(MaxFrames)
                    .Select(f => new CrashFrame(Identifier(f.Function), f.InApp))
                    .ToList();
                chain.Add(new CrashException(Identifier(ex.Type), frames));
            }

            return p with
            {
                Where = Identifier(p.Where),
                App = Identifier(p.App),
                Os = Identifier(p.Os),
                OsVersion = Truncate(Printable(p.OsVersion), 80),
                Chain = chain,
            };
        }
        catch { return null; }
    }

    static string Identifier(string? s) =>
        string.IsNullOrEmpty(s) ? ""
            : new string(Truncate(s, MaxNameChars)
                .Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '+' or '-' or '<' or '>'
                             or '`' or '[' or ']' or ',' or '|' ? c : '_').ToArray());

    static string Printable(string? s) =>
        string.IsNullOrEmpty(s) ? ""
            : new string(s.Where(c => !char.IsControl(c)).ToArray());
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CrashPayload))]
internal partial class CrashJsonContext : JsonSerializerContext { }
