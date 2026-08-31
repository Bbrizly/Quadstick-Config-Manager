using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

public class RescueTests
{
    // The tester opened the recovered work, left without saving, and the
    // "Unsaved work from last time" banner was still sitting on Home. Once
    // the offer is taken it must disappear for the rest of the session.
    [AvaloniaFact]
    public void Opening_recovered_work_clears_the_home_banner()
    {
        // RescueDirOverride is a static seam and CrashReportTests moves it, so
        // reading the real user rescue folder here made this race whichever
        // class xUnit happened to run alongside it. Own directory, own state.
        var prior = CrashGuard.RescueDirOverride;
        var rescue = Path.Combine(Path.GetTempPath(), "qscm-rescue-" + Guid.NewGuid().ToString("N"));
        CrashGuard.RescueDirOverride = rescue;
        try
        {
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        Directory.CreateDirectory(CrashGuard.RescueDir);
        File.WriteAllText(Path.Combine(CrashGuard.RescueDir, "autosave-draft.csv"),
            "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\nx,normal,lip\n");

        var w = new MainWindow();
        w.Show();
        w.UpdateLayout();
        var banner = w.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "HomeStatusText");
        Assert.True(banner.IsVisible); // the offer shows on launch

        Ui.Click(w.GetVisualDescendants().OfType<Button>()
            .First(b => b.Name == "RescueOpenButton"));

        Assert.False(banner.IsVisible); // taken: never announced again this session
        Assert.Empty(CrashGuard.PendingRescues()); // and the file on disk is spent

        // Replace the dirty recovered file so Close cannot open the save dialog.
        w.LoadProfile(ProfileFile.NewFromTemplate("clean.csv"));
        w.Close();
        }
        finally
        {
            CrashGuard.RescueDirOverride = prior;
            try { Directory.Delete(rescue, recursive: true); } catch { }
        }
    }
}
