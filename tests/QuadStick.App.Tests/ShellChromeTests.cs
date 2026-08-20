using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// The persistent chrome is the only navigation that never scrolls away, so a
// broken label or a stuck tab state is a screen reader dead end.
public class ShellChromeTests
{
    static readonly string[] NavButtons =
    {
        "ShellHomeButton", "ShellNewButton", "ShellOpenButton",
        "ShellDeviceButton", "ShellCommunityButton",
    };

    static MainWindow Open()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.UpdateLayout();
        return w;
    }

    static void LoadClean(MainWindow w)
    {
        var file = ProfileFile.NewFromTemplate("mygame.csv");
        file.Dirty = false; // else Close waits on the save dialog forever
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();
    }

    // Labels hide at narrow widths, so the accessible name is the only thing
    // left saying what the icon does.
    [AvaloniaFact]
    public void EveryShellCommandSaysWhatItIs()
    {
        var w = Open();
        foreach (var name in NavButtons)
        {
            var b = w.FindControl<Button>(name);
            Assert.NotNull(b);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(b!)), name);
        }
        w.Close();
    }

    // The chrome is the only thing on screen that says which page you are on,
    // and it is not colour alone: the tab carries the state class both ways.
    [AvaloniaFact]
    public void HomeTabMarksItselfOnceAndClearsInTheEditor()
    {
        var w = Open();
        var home = w.FindControl<Button>("ShellHomeButton")!;

        for (var i = 0; i < 3; i++)
        {
            LoadClean(w);
            Assert.DoesNotContain("active", home.Classes);
            w.ShowHomeForPreview();
            w.UpdateLayout();
            Assert.Equal(1, home.Classes.Count(c => c == "active"));
        }
        w.Close();
    }

    // work open the top buttons did nothing at all.
    [AvaloniaFact]
    public void TheQuickGuideOpens()
    {
        var w = Open();
        LoadClean(w);
        w.FindControl<Button>("HelpButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(w.OwnedWindows, o => o.Title == "Quick guide");
        foreach (var o in w.OwnedWindows.ToList()) o.Close();
        w.Close();
    }

    // not ask first, so unsaved work went without a word.
    [AvaloniaFact]
    public void EveryShellCommandAsksBeforeLeavingUnsavedWork()
    {
        var w = Open();
        var file = ProfileFile.NewFromTemplate("mygame.csv");
        w.LoadProfile(file);
        file.Dirty = true;
        w.UpdateLayout();

        foreach (var name in NavButtons)
        {
            w.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var opened = w.OwnedWindows.ToList();
            Assert.True(opened.Count == 1 && opened[0].Title == "Save your changes?",
                $"{name} left the editor without asking: opened "
              + (opened.Count == 0 ? "nothing" : string.Join(", ", opened.Select(o => o.Title))));
            foreach (var o in opened) o.Close();
            Dispatcher.UIThread.RunJobs();
        }

        file.Dirty = false; // else Close waits on the save dialog forever
        w.Close();
    }
}
