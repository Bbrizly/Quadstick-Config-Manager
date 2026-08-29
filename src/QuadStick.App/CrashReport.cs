using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace QuadStick.App;

public sealed record CrashFrame(string Function, bool InApp);

public sealed record CrashException(string Type, IReadOnlyList<CrashFrame> Frames);

// Everything that may be sent about a crash, and nothing else. If a field is
// not on this record it cannot reach the wire, which is the point.
public sealed record CrashPayload(
    int Schema,
    string Where,
    string App,
    string Os,
    string OsVersion,
    bool IsDebug,
    string Utc,
    IReadOnlyList<CrashException> Chain);

// Turns an exception into something safe to send. Pure and SDK-free on
// purpose: this is the privacy-critical half of telemetry, and it must be
// reviewable and testable without a network stack anywhere near it.
public static partial class CrashReport
{
    public const int SchemaVersion = 1;

    // Match the shape of a home directory, not one literal string from
    // Environment.SpecialFolder.UserProfile. That literal misses Windows case
    // differences, UNC paths, redirected OneDrive profiles, and the build
    // machine's own /Users/runner, which is not the runtime user's home at all
    // and would otherwise sail through untouched.
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
        App: Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
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

    // AggregateException hides real faults behind one wrapper, and inner
    // exceptions are usually where the actual bug is. Both get flattened into
    // one ordered list so grouping sees the same shape every time.
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

    // The message is never read. Not scrubbed, not truncated, not looked at:
    // Exception.Message can hold a cell value or a custom output name and no
    // regex can tell one from ordinary prose.
    static IReadOnlyList<CrashFrame> Frames(Exception ex)
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
        catch { /* a stack we cannot read is not a reason to lose the report */ }
        return frames;
    }

    // Test seam, same pattern as CrashGuard.RescueDirOverride.
    public static string? PendingDirOverride { get; set; }

    public static string PendingDir => PendingDirOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuadStickConfigManager", "pending-reports");

    // A crash loop must not become a nag loop, or fill a disk.
    public const int MaxPending = 5;
    public const int MaxAgeDays = 30;

    // Called from inside a crash handler. It must never throw, and it must
    // never be slow: one small file, no network, no SDK.
    public static void Write(string where, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(PendingDir);
            var name = $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json";
            var final = Path.Combine(PendingDir, name);
            // Write beside the target and rename into place. A second crash
            // mid-write would otherwise leave a truncated file that parses as
            // nothing and shows the user an empty report.
            var tmp = final + ".tmp";
            File.WriteAllText(tmp, ToJson(Build(where, ex)));
            File.Move(tmp, final, overwrite: true);
            Trim();
        }
        catch { /* the safety net must never itself throw */ }
    }

    /// <summary>Reports waiting to be asked about, oldest first. Expired and surplus files are deleted here.</summary>
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

    /// <summary>Throw away everything waiting. Used by a reset and by "stop asking".</summary>
    public static void Discard()
    {
        try
        {
            // Half-written files too. They hold the same contents and nothing
            // else would ever remove them, so a reset that left one behind
            // would not be the clean slate it promises.
            foreach (var f in Directory.GetFiles(PendingDir, "crash-*"))
                try { File.Delete(f); } catch { /* one locked file must not stop the rest */ }
        }
        catch { /* nothing to discard */ }
    }

    /// <summary>Throw away one report, the one the user was actually shown and agreed to send.</summary>
    public static void Discard(string path)
    {
        try { File.Delete(path); } catch { /* it stays, and the next launch asks again */ }
    }

    static void Trim()
    {
        try
        {
            // A crash between the write and the rename leaves a .tmp that the
            // cap and the expiry would otherwise never see, so it is swept
            // rather than kept forever. Only once it is far too old to be a
            // write still in flight, though: a second process can be part way
            // through its own .tmp right now, and deleting that one under it
            // makes its File.Move fail and loses the report it was saving.
            var inFlight = DateTime.UtcNow.AddMinutes(-5);
            var files = Directory.GetFiles(PendingDir, "crash-*")
                .Where(f => !f.EndsWith(".tmp", StringComparison.Ordinal)
                            || File.GetLastWriteTimeUtc(f) < inFlight)
                .OrderBy(File.GetLastWriteTimeUtc).ToList();

            var cutoff = DateTime.UtcNow.AddDays(-MaxAgeDays);
            foreach (var f in files.ToList())
                if (File.GetLastWriteTimeUtc(f) < cutoff)
                {
                    try { File.Delete(f); files.Remove(f); } catch { /* best effort */ }
                }

            // Oldest first, so the survivors are the most recent crashes.
            foreach (var f in files.Take(Math.Max(0, files.Count - MaxPending)))
                try { File.Delete(f); } catch { /* best effort */ }
        }
        catch { /* trimming is best effort */ }
    }

    public static string ToJson(CrashPayload p) =>
        JsonSerializer.Serialize(p, CrashJsonContext.Default.CrashPayload);

    // Caps for anything read back off disk. Nothing this app writes comes
    // close to them: a .NET stack is tens of frames, a type name is tens of
    // characters, and a chain is a handful of exceptions.
    public const int MaxChain = 20;
    public const int MaxFrames = 200;
    public const int MaxNameChars = 300;

    // What comes back from disk is not what was written. A pending file is an
    // ordinary file in the user's own folder, so anything on the machine can
    // edit it, and every string in it goes on the wire once Send is pressed.
    // Trusting the record shape alone would make PRIVACY.md's promise that
    // file paths are never sent depend on nobody having touched the file.
    //
    // So the whole payload is rebuilt here: counts clamped, strings clamped,
    // and function and type names held to the characters a .NET identifier
    // actually uses. A path, a URL, or a token cannot survive that.
    public static CrashPayload? FromJson(string json)
    {
        try
        {
            if (json.Length > 512 * 1024) return null;   // ours are a few KB
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

    // Namespace, type, method, and the shapes the runtime adds around them:
    // generics, local functions, lambdas, explicit interface implementations.
    // The hyphen is here for the three Where labels ("ui-thread"), which are
    // not identifiers. Anything else becomes '_'.
    //
    // What this buys, and only this: the separators are gone. No slash,
    // backslash, colon, space, or @ survives, so a file path, a URL with a
    // scheme, and an email address all come out as runs of underscores.
    //
    // It is NOT a secret filter, and it cannot be one. An OAuth token, a Sheets
    // ID, and a bare filename are letters, digits, dots and hyphens, which is
    // exactly what a namespace-qualified method name is: no character rule can
    // tell them apart. Nothing this app writes puts such a string here, so the
    // only way one arrives is someone hand-editing a pending report before
    // pressing Send, which is their own data and their own choice. If that ever
    // needs to be stopped, match the frame against a real identifier grammar
    // and drop what fails, do not widen this filter.
    static string Identifier(string? s) =>
        string.IsNullOrEmpty(s) ? ""
            : new string(Truncate(s, MaxNameChars)
                .Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '+' or '-' or '<' or '>'
                             or '`' or '[' or ']' or ',' or '|' ? c : '_').ToArray());

    // OS descriptions are prose ("Darwin 25.5.0 Darwin Kernel Version ..."),
    // so they keep spaces and punctuation but lose control characters.
    static string Printable(string? s) =>
        string.IsNullOrEmpty(s) ? ""
            : new string(s.Where(c => !char.IsControl(c)).ToArray());
}

// Source-generated so the report survives trimming, matching how AppSettings
// is handled in Theme.cs.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CrashPayload))]
internal partial class CrashJsonContext : JsonSerializerContext { }
