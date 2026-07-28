using System;
using System.IO;
using Avalonia.Headless.XUnit;
using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

// The plan's versions of these two performed the behaviour themselves instead
// of calling the app, so they passed before the code existed and could never
// have caught a missing hook. These drive the real methods on a real window.
public class TelemetryConsentUiTests
{
    static MainWindow ShownWindow()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        return w;
    }

    [AvaloniaFact]
    public void ResetSettingsSilencesTelemetryAndDropsTheInstallId()
    {
        var w = ShownWindow();
        try
        {
            w.CurrentSettings.UsageAnalytics = true;
            w.CurrentSettings.TelemetryNoticeVersion = Telemetry.NoticeVersion;
            w.CurrentSettings.InstallId = "11111111-2222-3333-4444-555555555555";
            Telemetry.SetInstallId(w.CurrentSettings.InstallId);
            Assert.NotEqual("", Telemetry.DistinctIdForTest);

            w.ResetSettings();   // the real method, not a re-implementation

            Assert.False(w.CurrentSettings.UsageAnalytics);
            Assert.Equal(0, w.CurrentSettings.TelemetryNoticeVersion);
            Assert.Equal("", w.CurrentSettings.InstallId);
            Assert.False(Telemetry.IsLive);
            // The one assertion a fresh AppSettings cannot satisfy on its own,
            // so this fails if the reset stops telling Telemetry about it.
            Assert.Equal("", Telemetry.DistinctIdForTest);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void ResetSettingsThrowsAwayAnyPendingCrashReport()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qscm-reset-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        CrashReport.PendingDirOverride = dir;
        var w = ShownWindow();
        try
        {
            CrashReport.Write("task", new InvalidOperationException("x"));
            Assert.NotEmpty(CrashReport.Pending());

            w.ResetSettings();

            // A reset that left a crash report on disk would offer to send it
            // under a brand new identity the user never agreed to.
            Assert.Empty(CrashReport.Pending());
        }
        finally { w.Close(); CrashReport.PendingDirOverride = null; Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public void SayingNoLeavesEverythingOffButRecordsThatTheNoticeWasShown()
    {
        var w = ShownWindow();
        try
        {
            w.ApplyTelemetryAnswer(usage: false);

            Assert.False(w.CurrentSettings.UsageAnalytics);
            Assert.Equal(Telemetry.NoticeVersion, w.CurrentSettings.TelemetryNoticeVersion);
            Assert.False(Telemetry.IsLive);
            // Someone who said no never gets an identifier at all.
            Assert.Equal("", w.CurrentSettings.InstallId);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void APendingReportIsNotOfferedOnceTheUserHasSaidStopAsking()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qscm-ask-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        CrashReport.PendingDirOverride = dir;
        var w = ShownWindow();
        try
        {
            CrashReport.Write("task", new InvalidOperationException("x"));
            w.CurrentSettings.AskAboutCrashes = false;

            // Returns without ever building a dialog. If it built one this
            // would hang the headless run, which is the assertion.
            Assert.True(w.OfferPendingCrashReportAsync().IsCompleted);
            Assert.NotEmpty(CrashReport.Pending());   // and the report is kept
        }
        finally { w.Close(); CrashReport.PendingDirOverride = null; Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public void NothingIsOfferedWhenThereIsNoPendingReport()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qscm-none-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        CrashReport.PendingDirOverride = dir;
        var w = ShownWindow();
        try
        {
            w.CurrentSettings.AskAboutCrashes = true;
            Assert.True(w.OfferPendingCrashReportAsync().IsCompleted);
        }
        finally { w.Close(); CrashReport.PendingDirOverride = null; Directory.Delete(dir, true); }
    }
}
