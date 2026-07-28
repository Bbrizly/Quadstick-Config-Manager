using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

public class TelemetryTests
{
    [Fact]
    public void NewSettingsAreSilentByDefault()
    {
        var s = new AppSettings();
        Assert.Equal(0, s.TelemetryNoticeVersion);
        Assert.False(s.UsageAnalytics);
        Assert.True(s.AskAboutCrashes);
        Assert.Equal("", s.InstallId);
    }

    [Fact]
    public void SettingsFileFromBeforeThisFeatureLoadsSilent()
    {
        // A real v1.5 settings file: no telemetry keys at all.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, """{"Model":"FPS","Theme":"Dark","TutorialSeen":true}""");
        try
        {
            var s = Settings.Load(path);
            Assert.Equal(0, s.TelemetryNoticeVersion);
            Assert.False(s.UsageAnalytics);
            Assert.Equal("", s.InstallId);
            Assert.True(s.TutorialSeen);   // proves the file really was read
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TelemetryFieldsRoundTripThroughJson()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            Assert.True(Settings.TrySave(new AppSettings
            {
                TelemetryNoticeVersion = 1,
                UsageAnalytics = true,
                AskAboutCrashes = false,
                InstallId = "abc-123",
            }, path));

            var s = Settings.Load(path);
            Assert.Equal(1, s.TelemetryNoticeVersion);
            Assert.True(s.UsageAnalytics);
            Assert.False(s.AskAboutCrashes);
            Assert.Equal("abc-123", s.InstallId);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(TelemetryEvent.AppLaunched, "app_launched")]
    [InlineData(TelemetryEvent.ProfileOpened, "profile_opened")]
    [InlineData(TelemetryEvent.ProfileSaved, "profile_saved")]
    [InlineData(TelemetryEvent.InstallAttempted, "install_attempted")]
    [InlineData(TelemetryEvent.InstallSucceeded, "install_succeeded")]
    [InlineData(TelemetryEvent.InstallFailed, "install_failed")]
    [InlineData(TelemetryEvent.FeatureUsed, "feature_used")]
    [InlineData(TelemetryEvent.FeedbackSubmitted, "feedback_submitted")]
    public void EventWireNames(TelemetryEvent e, string wire) => Assert.Equal(wire, Telemetry.Wire(e));

    [Theory]
    [InlineData(ProfileSource.New, "new")]
    [InlineData(ProfileSource.Rescue, "rescue")]
    [InlineData(ProfileSource.Sheets, "sheets")]
    public void SourceWireNames(ProfileSource s, string wire) => Assert.Equal(wire, Telemetry.Wire(s));

    [Fact]
    public void EveryEnumMemberHasAWireName()
    {
        // An unmapped member must be a compile error, but if someone adds one
        // with a throwing default this catches it before a user does.
        foreach (var e in Enum.GetValues<TelemetryEvent>())
            Assert.False(string.IsNullOrWhiteSpace(Telemetry.Wire(e)));
        foreach (var s in Enum.GetValues<ProfileSource>())
            Assert.False(string.IsNullOrWhiteSpace(Telemetry.Wire(s)));
        foreach (var f in Enum.GetValues<InstallFailure>())
            Assert.False(string.IsNullOrWhiteSpace(Telemetry.Wire(f)));
        foreach (var f in Enum.GetValues<AppFeature>())
            Assert.False(string.IsNullOrWhiteSpace(Telemetry.Wire(f)));
    }

    [Fact]
    public void WireNamesAreUnique()
    {
        var all = Enum.GetValues<TelemetryEvent>().Select(Telemetry.Wire).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void NoticeVersionZeroInitialisesNothing()
    {
        Telemetry.ResetForTest();
        Telemetry.ApplyConsent(0, usage: true);   // usage on but never told
        Assert.False(Telemetry.IsLive);
        Assert.False(Telemetry.Track(TelemetryEvent.AppLaunched));   // silent no-op
        Assert.False(Telemetry.IsLive);
    }

    [Fact]
    public void UsageOffInitialisesNothing()
    {
        Telemetry.ResetForTest();
        Telemetry.ApplyConsent(Telemetry.NoticeVersion, usage: false);
        Assert.False(Telemetry.IsLive);
    }

    [Fact]
    public void KillSwitchBeatsEverything()
    {
        // The module initializer already set this; set it again so the test
        // states its own precondition instead of inheriting one.
        Environment.SetEnvironmentVariable("QSCM_TELEMETRY", "0");
        Telemetry.ResetForTest();
        Telemetry.ApplyConsent(Telemetry.NoticeVersion, usage: true);
        Assert.False(Telemetry.IsLive);
    }

    [Fact]
    public void InstallIdIsAGuidStableAcrossCalls()
    {
        // Always pass a path. Without it this writes the developer's real
        // settings.json, and a test run must never touch the live file.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var s = new AppSettings();
            var first = Telemetry.InstallId(s, path);

            Assert.True(Guid.TryParse(first, out _));
            Assert.Equal(first, Telemetry.InstallId(s, path));
            Assert.Equal(first, s.InstallId);
            // never derived from the machine or the user
            Assert.DoesNotContain(Environment.MachineName, first);
            Assert.DoesNotContain(Environment.UserName, first);

            // and it is durable, which is what lets an event use it
            Assert.Equal(first, Settings.Load(path).InstallId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AnInstallIdThatCouldNotBeSavedIsNotUsed()
    {
        var s = new AppSettings();
        // An unwritable path: the ID must come back empty rather than be used
        // for events under an identity that will not survive the next launch.
        Assert.Equal("", Telemetry.InstallId(s, "/this/path/cannot/exist/\0bad.json"));
        Assert.Equal("", s.InstallId);
    }

    [Fact]
    public void ScrubKeepsOnlyDisclosedProperties()
    {
        Telemetry.ResetForTest(usage: true);
        var e = new PostHog.Api.CapturedEvent(
            "app_launched", "install-1",
            new Dictionary<string, object>
            {
                ["os"] = "macos",
                ["app_version"] = "1.6.0",
                ["$is_server"] = true,
                ["something_new_the_sdk_added"] = "surprise",
                ["$ip"] = "1.2.3.4",
                ["machine"] = Environment.MachineName,
            },
            DateTimeOffset.UtcNow);

        var scrubbed = Telemetry.Scrub(e)!;

        Assert.True(scrubbed.Properties.ContainsKey("os"));
        Assert.True(scrubbed.Properties.ContainsKey("app_version"));
        Assert.False(scrubbed.Properties.ContainsKey("$is_server"));
        Assert.False(scrubbed.Properties.ContainsKey("something_new_the_sdk_added"));
        Assert.False(scrubbed.Properties.ContainsKey("$ip"));
        Assert.False(scrubbed.Properties.ContainsKey("machine"));
    }

    [Fact]
    public void APropertyAllowedOnOneEventIsStrippedFromAnother()
    {
        Telemetry.ResetForTest(usage: true);
        // The allowlist is per event. Feedback text on app_launched is the
        // exact shape a single wrong call site would produce, and one global
        // list would let it through.
        var e = new PostHog.Api.CapturedEvent(
            "app_launched", "install-1",
            new Dictionary<string, object> { ["text"] = "SECRETMESSAGE", ["os"] = "macos" },
            DateTimeOffset.UtcNow);

        var scrubbed = Telemetry.Scrub(e)!;

        Assert.False(scrubbed.Properties.ContainsKey("text"));
        Assert.True(scrubbed.Properties.ContainsKey("os"));

        // and the same key on the event that declares it does survive
        var ok = new PostHog.Api.CapturedEvent(
            "feedback_submitted", "install-1",
            new Dictionary<string, object> { ["text"] = "the sip sensor is too sensitive" },
            DateTimeOffset.UtcNow);
        Assert.True(Telemetry.Scrub(ok)!.Properties.ContainsKey("text"));
    }

    [Fact]
    public void ScrubStripsTheMessageEvenFromAHandBuiltExceptionList()
    {
        Telemetry.ResetForTest(usage: true);
        // Defence in depth. This design never calls CaptureException, but if
        // anyone ever does, the SDK puts the message in $exception_message AND
        // in every $exception_list[i].value, and reads the source file off
        // disk into pre_context/context_line/post_context. Allowing
        // $exception_list wholesale would pass all of that straight through.
        var e = new PostHog.Api.CapturedEvent(
            "$exception", "install-1",
            new Dictionary<string, object>
            {
                ["$exception_message"] = "SECRETMESSAGE",
                ["$exception_list"] = new List<Dictionary<string, object>>
                {
                    new()
                    {
                        ["type"] = "System.IOException",
                        ["value"] = "SECRETMESSAGE",
                        ["stacktrace"] = new Dictionary<string, object>
                        {
                            ["frames"] = new List<Dictionary<string, object>>
                            {
                                new()
                                {
                                    ["function"] = "Foo",
                                    ["abs_path"] = "/Users/bassam/App.cs",
                                    ["context_line"] = "var secret = profile.Cell(7);",
                                },
                            },
                        },
                    },
                },
            },
            DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(Telemetry.Scrub(e)!.Properties);

        Assert.DoesNotContain("SECRETMESSAGE", json);
        Assert.DoesNotContain("abs_path", json);
        Assert.DoesNotContain("context_line", json);
        Assert.DoesNotContain("bassam", json);
        Assert.Contains("System.IOException", json);   // grouping still works
    }

    [Fact]
    public void AStringExceptionListIsRejectedNotPassedThrough()
    {
        Telemetry.ResetForTest(usage: true);
        // A pre-serialized string is not the supported shape. It is dropped to
        // an empty list rather than forwarded, because PostHog would receive a
        // quoted string and never parse it, and because trusting the value
        // would mean trusting whatever else was inside it.
        var e = new PostHog.Api.CapturedEvent(
            "$exception", "install-1",
            new Dictionary<string, object>
            {
                ["$exception_list"] = "[{\"type\":\"System.IOException\",\"value\":\"SECRETMESSAGE\"}]",
                ["$exception_message"] = "SECRETMESSAGE",
            },
            DateTimeOffset.UtcNow);

        var scrubbed = Telemetry.Scrub(e)!;

        Assert.False(scrubbed.Properties.ContainsKey("$exception_message"));
        var list = Assert.IsType<List<Dictionary<string, object>>>(scrubbed.Properties["$exception_list"]);
        Assert.Empty(list);
        Assert.DoesNotContain("SECRETMESSAGE", JsonSerializer.Serialize(scrubbed.Properties));
    }

    [Fact]
    public void FeedbackIsCappedAndRefusesToClaimAnUnsentSend()
    {
        Telemetry.ResetForTest();
        // Nothing is live, so it must report failure rather than let the UI
        // say "sent" and throw the user's text away.
        Assert.False(Telemetry.SendFeedback("the sip sensor is too sensitive"));
        Assert.False(Telemetry.SendFeedback("   "));
        Assert.False(Telemetry.SendFeedback(new string('x', Telemetry.MaxFeedbackChars * 2)));
    }

    [Fact]
    public void ExceptionPropertiesCarryTheTypeAndFramesButNoMessage()
    {
        Exception ex;
        try { throw new InvalidOperationException("SECRETMESSAGE"); }
        catch (Exception e) { ex = e; }

        var payload = CrashReport.Build("ui-thread", ex);
        var props = Telemetry.ExceptionProperties(payload);

        // A real object graph, never a pre-serialized string: a string value
        // would reach PostHog as a quoted string and never be parsed.
        var list = Assert.IsType<List<Dictionary<string, object>>>(props["$exception_list"]);
        Assert.Equal("System.InvalidOperationException", list[0]["type"]);
        Assert.Equal("", list[0]["value"]);
        Assert.False(props.ContainsKey("$exception_message"));

        var json = JsonSerializer.Serialize(props);
        Assert.DoesNotContain("SECRETMESSAGE", json);
        // the leaky frame fields the SDK would have added are not here
        foreach (var leaky in new[] { "abs_path", "pre_context", "context_line", "post_context", "lineno" })
            Assert.DoesNotContain(leaky, json);

        // the envelope rides along and nothing else does
        Assert.True(props.ContainsKey("os"));
        Assert.True(props.ContainsKey("app_version"));
    }

    [Fact]
    public void ExceptionPropertiesSurviveScrubUnchanged()
    {
        Telemetry.ResetForTest(usage: true);
        // What ExceptionProperties builds has to get through BeforeSend, or
        // the crash report arrives empty. This is the round trip.
        Exception ex;
        try { throw new InvalidOperationException("SECRETMESSAGE"); }
        catch (Exception e) { ex = e; }

        var props = Telemetry.ExceptionProperties(CrashReport.Build("ui-thread", ex));
        var scrubbed = Telemetry.Scrub(
            new PostHog.Api.CapturedEvent("$exception", "install-1", props, DateTimeOffset.UtcNow))!;

        var list = Assert.IsType<List<Dictionary<string, object>>>(scrubbed.Properties["$exception_list"]);
        Assert.NotEmpty(list);
        Assert.Equal("System.InvalidOperationException", list[0]["type"]);
        Assert.True(scrubbed.Properties.ContainsKey("$exception_type"));
        Assert.True(scrubbed.Properties.ContainsKey("os"));

        var frames = Assert.IsType<List<Dictionary<string, object>>>(
            ((Dictionary<string, object>)list[0]["stacktrace"])["frames"]);
        Assert.NotEmpty(frames);
        Assert.DoesNotContain("SECRETMESSAGE", JsonSerializer.Serialize(scrubbed.Properties));
    }

    [Fact]
    public void SendingACrashReportWithNoConsentDoesNotThrow()
    {
        // Pressing Send IS the consent. It must not require the usage toggle,
        // and it must report honestly that nothing went out.
        Telemetry.ResetForTest();
        Telemetry.ApplyConsent(Telemetry.NoticeVersion, usage: false);
        Assert.False(Telemetry.IsLive);

        Assert.False(Telemetry.SendCrashReport("not valid json"));
        Assert.False(Telemetry.SendCrashReport(
            CrashReport.ToJson(CrashReport.Build("task", new IOException("x")))));
    }

    [Fact]
    public void AQueuedUsageEventIsDroppedAfterConsentIsWithdrawn()
    {
        // Withdrawing consent disposes the client, and disposal drains the
        // queue through BeforeSend. Without the check inside Scrub, events
        // already queued would go out after the user said stop.
        Telemetry.ResetForTest(usage: false);

        var queued = new PostHog.Api.CapturedEvent(
            "app_launched", "install-1",
            new Dictionary<string, object> { ["os"] = "macos" },
            DateTimeOffset.UtcNow);

        Assert.Null(Telemetry.Scrub(queued));
    }

    [Fact]
    public void AConsentedCrashReportStillGoesOutWithUsageOff()
    {
        // The other half: pressing Send is its own consent, so a crash report
        // must survive the same drain that drops usage events.
        Telemetry.ResetForTest(usage: false);

        var props = Telemetry.ExceptionProperties(
            CrashReport.Build("task", new IOException("SECRETMESSAGE")));
        props["__crash_consent"] = true;

        var scrubbed = Telemetry.Scrub(
            new PostHog.Api.CapturedEvent("$exception", "install-1", props, DateTimeOffset.UtcNow));

        Assert.NotNull(scrubbed);
        Assert.True(scrubbed!.Properties.ContainsKey("$exception_list"));
        // and the marker itself never reaches the wire
        Assert.False(scrubbed.Properties.ContainsKey("__crash_consent"));
        Assert.DoesNotContain("SECRETMESSAGE", JsonSerializer.Serialize(scrubbed.Properties));
    }
}
