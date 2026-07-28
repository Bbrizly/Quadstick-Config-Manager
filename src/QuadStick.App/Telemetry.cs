using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using PostHog;
using PostHog.Api;

namespace QuadStick.App;

// The complete list of things this app may report. There is no allowlist
// array and no Debug.Assert: a const string[] does not compile, and an assert
// vanishes from store builds, so the enforcement would fail open in exactly
// the builds that matter. A closed enum plus an exhaustive switch makes the
// compiler the check instead.
public enum TelemetryEvent
{
    AppLaunched, ProfileOpened, ProfileSaved,
    InstallAttempted, InstallSucceeded, InstallFailed,
    FeatureUsed, FeedbackSubmitted,
}

public enum ProfileSource { New, File, Library, Device, Sheets, Drive, Rescue }

public enum InstallFailure
{
    HasErrors, NoProfile, NotAQuadstick,
    CancelledDevice, CancelledFolder, CancelledDefault, IoError,
}

public enum AppFeature { SheetsImport, DriveBackup, DriveRestore, ShareLink }

public static partial class Telemetry
{
    // Each switch ends in a throwing arm, which is not the belt-and-braces it
    // looks like. Leaving the arm off does not make an unmapped member a
    // compile error: every named member is covered, so the compiler raises
    // CS8524 about a cast like (TelemetryEvent)8 instead, and
    // TreatWarningsAsErrors makes that fatal on its own. So the arm has to be
    // here, and EveryEnumMemberHasAWireName in the tests is what actually
    // catches a member added without a name. A throw rather than a default
    // string on purpose: Track swallows it and drops the event, where a "" or
    // a guessed name would quietly put a nameless event on the wire.
    internal static string Wire(TelemetryEvent e) => e switch
    {
        TelemetryEvent.AppLaunched       => "app_launched",
        TelemetryEvent.ProfileOpened     => "profile_opened",
        TelemetryEvent.ProfileSaved      => "profile_saved",
        TelemetryEvent.InstallAttempted  => "install_attempted",
        TelemetryEvent.InstallSucceeded  => "install_succeeded",
        TelemetryEvent.InstallFailed     => "install_failed",
        TelemetryEvent.FeatureUsed       => "feature_used",
        TelemetryEvent.FeedbackSubmitted => "feedback_submitted",
        _ => throw new ArgumentOutOfRangeException(nameof(e), e, "no wire name"),
    };

    internal static string Wire(ProfileSource s) => s switch
    {
        ProfileSource.New     => "new",
        ProfileSource.File    => "file",
        ProfileSource.Library => "library",
        ProfileSource.Device  => "device",
        ProfileSource.Sheets  => "sheets",
        ProfileSource.Drive   => "drive",
        ProfileSource.Rescue  => "rescue",
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, "no wire name"),
    };

    internal static string Wire(InstallFailure f) => f switch
    {
        InstallFailure.HasErrors        => "has_errors",
        InstallFailure.NoProfile        => "no_profile",
        InstallFailure.NotAQuadstick    => "not_a_quadstick",
        InstallFailure.CancelledDevice  => "cancelled_device",
        InstallFailure.CancelledFolder  => "cancelled_folder",
        InstallFailure.CancelledDefault => "cancelled_default",
        InstallFailure.IoError          => "io_error",
        _ => throw new ArgumentOutOfRangeException(nameof(f), f, "no wire name"),
    };

    internal static string Wire(AppFeature f) => f switch
    {
        AppFeature.SheetsImport => "sheets_import",
        AppFeature.DriveBackup  => "drive_backup",
        AppFeature.DriveRestore => "drive_restore",
        AppFeature.ShareLink    => "share_link",
        _ => throw new ArgumentOutOfRangeException(nameof(f), f, "no wire name"),
    };

    // Bump to re-prompt when what gets sent materially changes.
    public const int NoticeVersion = 1;

    const string Host = "https://us.i.posthog.com";

    // The feedback box is the one place a user's own words are sent, and only
    // because they typed them into a box that says so. Capped so a pasted
    // profile cannot ride along inside it.
    public const int MaxFeedbackChars = 1000;

    // Telemetry owns its own state and never reads AppSettings. That object is
    // a mutable class with public fields, MainWindow replaces the whole
    // instance on reset, and background work already reaches into it. Handing
    // it to a background send invites a stale read or a torn swap; a one-way
    // push into volatile flags has neither problem.
    static volatile bool _usage;
    static volatile PostHogClient? _client;
    static volatile string _distinctId = "";
    static readonly object Gate = new();

    public static bool IsLive => _client is not null;

    static bool KillSwitch => Environment.GetEnvironmentVariable("QSCM_TELEMETRY") == "0";

    /// <summary>True when QSCM_TELEMETRY=0. There is nothing to consent to, so do not ask.</summary>
    public static bool DisabledByEnvironment => KillSwitch;

    /// <summary>The only way standing consent reaches the client.</summary>
    public static void ApplyConsent(int noticeVersion, bool usage)
    {
        lock (Gate)
        {
            _usage = !KillSwitch && noticeVersion >= NoticeVersion && usage;
            if (_usage) Start();
            else Stop();
        }
    }

    // Started lazily and only for a reason that already exists: standing usage
    // consent, or the user pressing Send on a crash report they can see. Never
    // from App.axaml.cs, never at startup. Before one of those two acts the SDK
    // is not loaded, so it cannot hook anything, buffer anything, or send.
    //
    // The empty-token check is the load-bearing one. Without the gitignored
    // TelemetryToken.Local.cs the token is "" and no client is ever built, so
    // a fork, a source build, and a test run cannot report even if every other
    // gate here were wrong.
    static void Start()
    {
        if (_client is not null || KillSwitch || TelemetryToken.Value.Length == 0) return;
        try
        {
            // The only public constructor takes IOptions<PostHogOptions>, not
            // PostHogOptions. Options.Create is the one-liner that wraps it.
            _client = new PostHogClient(Options.Create(new PostHogOptions
            {
                ProjectToken = TelemetryToken.Value,
                HostUrl      = new Uri(Host),
                IsServer     = false,   // defaults to true; this is a desktop app
                BeforeSend   = Scrub,
            }));
        }
        catch { _client = null; /* telemetry off for the session; the app continues */ }
    }

    static void Stop()
    {
        var c = _client;
        _client = null;
        if (c is null) return;
        // Fire and forget: a consent change must not block the UI thread on a
        // network queue. DisposeAsync, never the synchronous Dispose, which
        // blocks on the same work and can deadlock the dispatcher.
        _ = Task.Run(async () => { try { await c.DisposeAsync(); } catch { /* going away anyway */ } });
    }

    /// <summary>Release on exit. Deliberately does not block: losing one queued event beats hanging the close.</summary>
    public static void Shutdown()
    {
        lock (Gate) { _usage = false; Stop(); }
    }

    /// <summary>A random GUID, made once and persisted. Never derived from the machine or the user.</summary>
    /// <param name="settingsPath">Test seam, same shape as Settings.Load(path). Null means the real per-user file.</param>
    public static string InstallId(AppSettings s, string? settingsPath = null)
    {
        if (!string.IsNullOrEmpty(s.InstallId)) return s.InstallId;
        s.InstallId = Guid.NewGuid().ToString();
        // Not used for a single event until it is durable: sending under an ID
        // that never reached disk mints a fresh identity next launch, which
        // fragments the count and orphans data a deletion request cannot find.
        if (!Settings.TrySave(s, settingsPath)) s.InstallId = "";
        return s.InstallId;
    }

    /// <summary>Set once when consent is applied, so Track never touches AppSettings.</summary>
    public static void SetInstallId(string id) => _distinctId = id;

    static readonly HashSet<string> EnvelopeKeys =
        new(StringComparer.Ordinal) { "os", "os_version", "app_version", "is_debug" };

    // Allowed per event, not once globally. One global list would let any
    // event carry any allowed key, so a single wrong call site could put
    // feedback text on app_launched and the filter would wave it through.
    // Pairing the key with the event name closes that off.
    //
    // Rebuilt as an allowlist rather than filtered as a blocklist: a blocklist
    // fails open on the next SDK version, an allowlist fails closed.
    //
    // IMPORTANT, verified against 2.12.0 by capturing real events through
    // BeforeSend: the SDK adds four properties of its own that this filter
    // CANNOT remove, because they are attached after BeforeSend returns.
    //
    //   distinct_id       the install ID, which we send deliberately anyway
    //   $lib              "posthog-dotnet"
    //   $lib_version      "2.12.0"
    //   $geoip_disable    true
    //
    // They ship on every event no matter what this method does, so they are
    // disclosed in the privacy policy rather than pretended away.
    // $geoip_disable = true is good news: it tells PostHog not to derive a
    // location from the IP. $is_server does NOT appear, because IsServer is
    // set to false above.
    static bool Allowed(string eventName, string key) =>
        EnvelopeKeys.Contains(key) || (eventName, key) switch
        {
            ("profile_opened", "source")        => true,
            ("install_failed", "reason")        => true,
            ("feature_used", "feature")         => true,
            ("feedback_submitted", "text")      => true,
            ("$exception", "$exception_list")   => true,
            ("$exception", "$exception_type")   => true,
            _ => false,
        };

    // A crash report the user pressed Send on carries this marker, and it is
    // stripped here so it never reaches the wire. It exists because the
    // consent check below has to run inside BeforeSend, not only at Capture
    // time: withdrawing consent calls DisposeAsync, whose final queue drain
    // pushes already-queued usage events through this method. Checking _usage
    // here is what actually drops them. A per-crash send has no standing
    // consent to check, hence the marker.
    const string CrashConsentMarker = "__crash_consent";

    internal static CapturedEvent? Scrub(CapturedEvent e)
    {
        var crashConsent = e.Properties.Remove(CrashConsentMarker);
        if (!_usage && !crashConsent) return null;   // null means drop

        var kept = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (k, v) in e.Properties)
        {
            if (!Allowed(e.EventName, k)) continue;
            // $exception_list is allowed by key but not by content: the SDK's
            // own builder puts the message in .value and the user's source
            // lines in the frames. Rebuild it from the fields we disclose.
            kept[k] = k == "$exception_list" ? CleanExceptionList(v) : v;
        }
        return new CapturedEvent(e.EventName, e.DistinctId, kept, e.Timestamp);
    }

    static readonly HashSet<string> AllowedFrameKeys =
        new(StringComparer.Ordinal) { "platform", "lang", "function", "in_app" };

    // Anything that is not the exact list shape this app builds is dropped
    // rather than trusted, including the pre-serialized JSON string someone
    // might reasonably think works. A string here would reach PostHog as a
    // quoted string and never be parsed anyway.
    static object CleanExceptionList(object value)
    {
        if (value is not List<Dictionary<string, object>> list)
            return new List<Dictionary<string, object>>();

        var clean = new List<Dictionary<string, object>>();
        foreach (var item in list)
        {
            var frames = new List<Dictionary<string, object>>();
            if (item.TryGetValue("stacktrace", out var st)
                && st is Dictionary<string, object> stack
                && stack.TryGetValue("frames", out var fr)
                && fr is List<Dictionary<string, object>> rawFrames)
            {
                foreach (var f in rawFrames)
                {
                    var keptFrame = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var (k, v) in f)
                        if (AllowedFrameKeys.Contains(k)) keptFrame[k] = v;
                    frames.Add(keptFrame);
                }
            }

            clean.Add(new Dictionary<string, object>
            {
                ["type"] = item.TryGetValue("type", out var t) ? t : "Unknown",
                ["value"] = "",   // never the message
                ["mechanism"] = new Dictionary<string, object>
                {
                    ["type"] = "generic", ["handled"] = false, ["synthetic"] = false,
                },
                ["stacktrace"] = new Dictionary<string, object>
                {
                    ["type"] = "raw", ["frames"] = frames,
                },
            });
        }
        return clean;
    }

    internal static Dictionary<string, object> Envelope() => new(StringComparer.Ordinal)
    {
        ["os"] = OperatingSystem.IsWindows() ? "windows"
               : OperatingSystem.IsMacOS() ? "macos"
               : OperatingSystem.IsLinux() ? "linux" : "other",
        ["os_version"] = Truncate(RuntimeInformation.OSDescription, 80),
        ["app_version"] = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
        ["is_debug"] = IsDebug,
    };

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

#if DEBUG
    const bool IsDebug = true;
#else
    const bool IsDebug = false;
#endif

    // Private on purpose. A public Track(event, key, value) would accept any
    // string under any name, which is the whole hole the closed enums exist to
    // shut. The typed methods below are the only way in.
    //
    // Synchronous, void-ish, and cannot throw. A local action never waits on
    // telemetry and a telemetry failure never reaches the user, the same rule
    // as DriveBackup.cs:10. The bool says whether the event was handed to a
    // live client, so a caller never tells the user "sent" when nothing was.
    static bool Track(TelemetryEvent e, string? key, string? value)
    {
        try
        {
            var c = _client;
            if (c is null || !_usage || _distinctId.Length == 0) return false;

            var props = Envelope();
            if (key is not null && value is not null) props[key] = value;

            // flags: null, not sendFeatureFlags: false. That older overload is
            // [Obsolete] in 2.12.0, which TreatWarningsAsErrors makes fatal.
            // This app has no feature flags, and a null snapshot is also what
            // keeps Capture from firing an extra /flags request per event.
            return c.Capture(_distinctId, Wire(e), props,
                             groups: null, flags: null, timestamp: null);
        }
        catch { return false; /* a dropped event is dropped */ }
    }

    public static bool Track(TelemetryEvent e) => Track(e, null, null);
    public static bool Track(TelemetryEvent e, ProfileSource s) => Track(e, "source", Wire(s));
    public static bool Track(TelemetryEvent e, InstallFailure f) => Track(e, "reason", Wire(f));
    public static bool Track(TelemetryEvent e, AppFeature f) => Track(e, "feature", Wire(f));

    /// <summary>Returns false when nothing was sent, so the UI never claims otherwise.</summary>
    public static bool SendFeedback(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return false;
        return Track(TelemetryEvent.FeedbackSubmitted, "text",
                     text.Length <= MaxFeedbackChars ? text : text[..MaxFeedbackChars]);
    }

    // PostHog's error tracking groups on $exception_list. The SDK builds this
    // itself in CaptureException, but that needs a live Exception and a live
    // client at crash time, and this design has neither: a crash writes a file
    // and sends nothing. So the structure is built here from the stored
    // payload, matching the shape the SDK emits (verified by capturing a real
    // CaptureException through BeforeSend on 2.12.0). Re-check on any bump.
    //
    // It MUST be a real List/Dictionary graph, never a pre-serialized JSON
    // string: Properties is Dictionary<string, object> and a string value
    // serializes as a quoted string, which error tracking will not parse.
    //
    // Deliberately absent from every frame: filename, abs_path, lineno, colno,
    // pre_context, context_line, post_context. The SDK populates all of those
    // and reads the developer's SOURCE FILE off disk to fill the context
    // fields. That is the single leakiest thing in the library, and building
    // the list by hand is what keeps it out.
    internal static Dictionary<string, object> ExceptionProperties(CrashPayload p)
    {
        var items = new List<Dictionary<string, object>>();

        foreach (var ex in p.Chain)
        {
            var frames = new List<Dictionary<string, object>>();
            foreach (var f in ex.Frames)
                frames.Add(new Dictionary<string, object>
                {
                    ["platform"] = "custom",
                    ["lang"]     = "dotnet",
                    ["function"] = f.Function,
                    ["in_app"]   = f.InApp,
                });

            items.Add(new Dictionary<string, object>
            {
                ["type"]  = ex.Type,
                ["value"] = "",   // the message is never read, so there is none
                ["mechanism"] = new Dictionary<string, object>
                {
                    ["type"] = "generic",
                    ["handled"] = false,   // these reached a top-level handler
                    ["synthetic"] = false,
                },
                ["stacktrace"] = new Dictionary<string, object>
                {
                    ["type"] = "raw",
                    ["frames"] = frames,
                },
            });
        }

        // The envelope comes from the stored report, not from today: the crash
        // may have happened on an older version, or on another OS build.
        var props = Envelope();
        props["$exception_list"] = items;
        props["$exception_type"] = p.Chain.Count > 0 ? p.Chain[0].Type : "Unknown";
        props["os"] = p.Os;
        props["os_version"] = p.OsVersion;
        props["app_version"] = p.App;
        props["is_debug"] = p.IsDebug;
        return props;
    }

    /// <summary>Send one stored report. Pressing Send is the consent, so this needs no standing flag.</summary>
    public static bool SendCrashReport(string json)
    {
        try
        {
            var payload = CrashReport.FromJson(json);
            // An empty chain would send an $exception with nothing to group on,
            // and the caller would then delete the file believing it landed.
            if (payload is null || payload.Chain.Count == 0 || _distinctId.Length == 0) return false;

            lock (Gate) { Start(); }
            var c = _client;
            if (c is null) return false;

            var props = ExceptionProperties(payload);
            props[CrashConsentMarker] = true;   // stripped again inside Scrub

            return c.Capture(_distinctId, "$exception", props,
                             groups: null, flags: null, timestamp: null);
        }
        catch { return false; /* a report that cannot be sent is dropped; the local log keeps it */ }
    }

    /// <summary>Test seam: what Track would send as the identity, so a test can prove a reset cleared it.</summary>
    internal static string DistinctIdForTest => _distinctId;

    /// <summary>Test seam: drop the client and the flags between tests.</summary>
    /// <param name="usage">Stands in for standing consent, so Scrub can be tested without a client.</param>
    internal static void ResetForTest(bool usage = false)
    {
        lock (Gate) { _usage = usage; _client = null; _distinctId = ""; }
    }
}
