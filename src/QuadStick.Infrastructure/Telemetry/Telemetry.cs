using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using PostHog;
using PostHog.Api;
using QuadStick.Application.Settings;
using QuadStick.Application.Telemetry;
using QuadStick.Infrastructure.Settings;

namespace QuadStick.Infrastructure.Telemetry;

public static partial class Telemetry
{
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
        InstallFailure.HasErrors            => "has_errors",
        InstallFailure.NoProfile            => "no_profile",
        InstallFailure.NotAQuadstick        => "not_a_quadstick",
        InstallFailure.CancelledDevice      => "cancelled_device",
        InstallFailure.CancelledFolder      => "cancelled_folder",
        InstallFailure.CancelledDefault     => "cancelled_default",
        InstallFailure.CancelledPreferences => "cancelled_preferences",
        InstallFailure.IoError              => "io_error",
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

    public const int NoticeVersion = 1;
    const string Host = "https://us.i.posthog.com";
    public const int MaxFeedbackChars = 1000;

    static volatile bool _usage;
    static volatile PostHogClient? _client;
    static volatile string _distinctId = "";
    static readonly object Gate = new();
    static volatile string _appVersion = "0.0.0";
    static volatile bool _isDebugBuild;

    public static bool IsLive => _client is not null;
    static bool KillSwitch => Environment.GetEnvironmentVariable("QSCM_TELEMETRY") == "0";
    public static bool DisabledByEnvironment => KillSwitch;

    /// <summary>The executable supplies its identity. Infrastructure never
    /// derives product version from its own assembly.</summary>
    public static void ConfigureAppInfo(string version, bool isDebug)
    {
        _appVersion = string.IsNullOrWhiteSpace(version) ? "0.0.0" : version;
        _isDebugBuild = isDebug;
    }

    public static void ApplyConsent(int noticeVersion, bool usage)
    {
        lock (Gate)
        {
            _usage = !KillSwitch && noticeVersion >= NoticeVersion && usage;
            if (_usage) Start();
            else Stop();
        }
    }

    static void Start()
    {
        if (_client is not null || KillSwitch || TelemetryToken.Value.Length == 0) return;
        try
        {
            _client = new PostHogClient(Options.Create(new PostHogOptions
            {
                ProjectToken = TelemetryToken.Value,
                HostUrl = new Uri(Host),
                IsServer = false,
                BeforeSend = Scrub,
            }));
        }
        catch { _client = null; }
    }

    static void Stop()
    {
        var c = _client;
        _client = null;
        if (c is null) return;
        _ = Task.Run(async () => { try { await c.DisposeAsync(); } catch { } });
    }

    static bool Flush(TimeSpan wait)
    {
        var c = _client;
        if (c is null) return false;
        try { return Task.Run(() => c.FlushAsync()).Wait(wait); }
        catch { return false; }
    }

    static async Task<bool> FlushAsync(TimeSpan wait)
    {
        var c = _client;
        if (c is null) return false;
        try
        {
            var flush = c.FlushAsync();
            if (await Task.WhenAny(flush, Task.Delay(wait)).ConfigureAwait(false) != flush) return false;
            await flush.ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    static readonly TimeSpan ShutdownFlush = TimeSpan.FromSeconds(2);
    static readonly TimeSpan SendFlush = TimeSpan.FromSeconds(5);

    public static void Shutdown()
    {
        Flush(ShutdownFlush);
        lock (Gate) { _usage = false; Stop(); }
    }

    /// <summary>A random GUID, made once and persisted. The Infrastructure
    /// settings store is used directly here; no presentation Settings facade is
    /// referenced from this assembly.</summary>
    public static string InstallId(AppSettings s, string? settingsPath = null)
    {
        if (!string.IsNullOrEmpty(s.InstallId)) return s.InstallId;
        s.InstallId = Guid.NewGuid().ToString();
        if (!new JsonAppSettingsStore(settingsPath).TrySave(s)) s.InstallId = "";
        return s.InstallId;
    }

    public static void SetInstallId(string id) => _distinctId = id;

    static readonly HashSet<string> EnvelopeKeys =
        new(StringComparer.Ordinal) { "os", "os_version", "app_version", "is_debug" };

    static bool Allowed(string eventName, string key) =>
        EnvelopeKeys.Contains(key) || (eventName, key) switch
        {
            ("profile_opened", "source") => true,
            ("install_failed", "reason") => true,
            ("feature_used", "feature") => true,
            ("feedback_submitted", "text") => true,
            ("$exception", "$exception_list") => true,
            ("$exception", "$exception_type") => true,
            _ => false,
        };

    const string CrashConsentMarker = "__crash_consent";

    internal static CapturedEvent? Scrub(CapturedEvent e)
    {
        var crashConsent = e.Properties.Remove(CrashConsentMarker);
        if (!_usage && !crashConsent) return null;

        var kept = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (k, v) in e.Properties)
        {
            if (!Allowed(e.EventName, k)) continue;
            kept[k] = k == "$exception_list" ? CleanExceptionList(v) : v;
        }
        return new CapturedEvent(e.EventName, e.DistinctId, kept, e.Timestamp);
    }

    static readonly HashSet<string> AllowedFrameKeys =
        new(StringComparer.Ordinal) { "platform", "lang", "function", "in_app" };

    static List<Dictionary<string, object>> CleanExceptionList(object value)
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
                ["value"] = "",
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
        ["app_version"] = _appVersion,
        ["is_debug"] = _isDebugBuild,
    };

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    static bool Track(TelemetryEvent e, string? key, string? value)
    {
        try
        {
            var c = _client;
            if (c is null || !_usage || _distinctId.Length == 0) return false;
            var props = Envelope();
            if (key is not null && value is not null) props[key] = value;
            return c.Capture(_distinctId, Wire(e), props,
                             groups: null, flags: null, timestamp: null);
        }
        catch { return false; }
    }

    public static bool Track(TelemetryEvent e) => Track(e, null, null);
    public static bool Track(TelemetryEvent e, ProfileSource s) => Track(e, "source", Wire(s));
    public static bool Track(TelemetryEvent e, InstallFailure f) => Track(e, "reason", Wire(f));
    public static bool Track(TelemetryEvent e, AppFeature f) => Track(e, "feature", Wire(f));

    public static async Task<bool> SendFeedbackAsync(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return false;
        if (!Track(TelemetryEvent.FeedbackSubmitted, "text",
                   text.Length <= MaxFeedbackChars ? text : text[..MaxFeedbackChars])) return false;
        return await FlushAsync(SendFlush);
    }

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
                    ["lang"] = "dotnet",
                    ["function"] = f.Function,
                    ["in_app"] = f.InApp,
                });

            items.Add(new Dictionary<string, object>
            {
                ["type"] = ex.Type,
                ["value"] = "",
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

        var props = Envelope();
        props["$exception_list"] = items;
        props["$exception_type"] = p.Chain.Count > 0 ? p.Chain[0].Type : "Unknown";
        props["os"] = p.Os;
        props["os_version"] = p.OsVersion;
        props["app_version"] = p.App;
        props["is_debug"] = p.IsDebug;
        return props;
    }

    public static async Task<bool> SendCrashReportAsync(string json)
    {
        try
        {
            var payload = CrashReport.FromJson(json);
            if (payload is null || payload.Chain.Count == 0 || _distinctId.Length == 0) return false;

            lock (Gate) { Start(); }
            var c = _client;
            if (c is null) return false;

            var props = ExceptionProperties(payload);
            props[CrashConsentMarker] = true;
            if (!c.Capture(_distinctId, "$exception", props,
                           groups: null, flags: null, timestamp: null)) return false;
            return await FlushAsync(SendFlush);
        }
        catch { return false; }
    }

    internal static string DistinctIdForTest => _distinctId;

    internal static void ResetForTest(bool usage = false)
    {
        lock (Gate)
        {
            _usage = usage;
            _client = null;
            _distinctId = "";
            _appVersion = "0.0.0";
            _isDebugBuild = false;
        }
    }
}
