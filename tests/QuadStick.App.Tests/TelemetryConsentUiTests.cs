using System;
using System.IO;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    // A TabControl only builds the selected tab, so the box does not exist as a
    // visual until Advanced is showing. Selecting it is what a user does to
    // reach the setting anyway.
    static CheckBox UsageBox(MainWindow w)
    {
        w.ShowSettingsPage();
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        var tabs = w.GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => (string?)t.Header == "Advanced");
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        return w.GetVisualDescendants().OfType<CheckBox>()
            .First(c => AutomationProperties.GetName(c) == "Share anonymous usage data");
    }

    // The box used to read its own answer back off CurrentSettings, which
    // ApplyTelemetryAnswer had already forced to false. So a failed save while
    // turning it OFF left the box showing off, the file still saying true, and
    // telemetry back on at the next launch with nobody told. The box must show
    // what is stored, because that is what decides the next launch.
    [AvaloniaFact]
    public void AFailedSaveWhileTurningUsageOffLeavesTheBoxShowingWhatIsStored()
    {
        var w = ShownWindow();
        // In memory only. The handler reads its starting point off
        // CurrentSettings, and persisting usage=true here would leave it on in
        // the shared settings file for whatever test runs next.
        w.CurrentSettings.UsageAnalytics = true;
        w.CurrentSettings.TelemetryNoticeVersion = Telemetry.NoticeVersion;

        var usage = UsageBox(w);
        Assert.True(usage.IsChecked);

        try
        {
            Settings.FailSavesForTest = true;
            usage.IsChecked = false;             // the user unchecks it

            Assert.True(usage.IsChecked);        // reverted, so the surprise is visible now
            Assert.False(Telemetry.IsLive);      // and nothing is sent in the meantime
        }
        finally
        {
            Settings.FailSavesForTest = false;
            w.Close();
        }
    }

    // The same revert, run against the oscillation it replaced. Assigning
    // IsChecked re-enters the handler, and an earlier attempt assigned the
    // negation each time, which has no fixed point: on a settings file that
    // keeps failing to write it recursed until the stack overflowed.
    [AvaloniaFact]
    public void RevertingTheUsageBoxSettlesInsteadOfRecursing()
    {
        var w = ShownWindow();
        // In memory only. The handler reads its starting point off
        // CurrentSettings, and persisting usage=true here would leave it on in
        // the shared settings file for whatever test runs next.
        w.CurrentSettings.UsageAnalytics = true;
        w.CurrentSettings.TelemetryNoticeVersion = Telemetry.NoticeVersion;

        var usage = UsageBox(w);

        try
        {
            Settings.FailSavesForTest = true;
            for (var i = 0; i < 50; i++)         // a stack overflow kills the run, it does not fail it
            {
                usage.IsChecked = false;
                Assert.True(usage.IsChecked);
            }
        }
        finally
        {
            Settings.FailSavesForTest = false;
            w.Close();
        }
    }
}
