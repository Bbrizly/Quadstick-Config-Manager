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

    // Style.cs calls 48 a click-target floor for mouth stick and head mouse
    // users. The chrome shrank its own buttons to 40 and centred them in a
    // taller bar, so a third of the strip they sit in was inert: the tester
    // reported the top buttons "sometimes just don't work at all".
    [AvaloniaFact]
    public void EveryShellCommandIsBigEnoughToHit()
    {
        var w = Open();
        LoadClean(w);
        var floor = Style.Numbers["ControlHeight"];
        foreach (var name in NavButtons)
        {
            var b = w.FindControl<Button>(name)!;
            Assert.True(b.Bounds.Height >= floor,
                $"{name} is {b.Bounds.Height:0} tall, under the {floor:0} click-target floor");
        }
        w.Close();
    }

    // The appearance picker sets the height of the bar. A nav button shorter
    // than that is centred in it, and the strip left over above and below is
    // inert: a near miss lands on the bar and nothing happens.
    [AvaloniaFact]
    public void NoDeadStripAboveOrBelowTheShellCommands()
    {
        var w = Open();
        LoadClean(w);
        var row = w.FindControl<ComboBox>("AppearancePicker")!.Bounds.Height;
        foreach (var name in NavButtons)
        {
            var b = w.FindControl<Button>(name)!;
            Assert.True(b.Bounds.Height >= row - 0.5,
                $"{name} is {b.Bounds.Height:0} tall in a {row:0} row, leaving "
              + $"{row - b.Bounds.Height:0}px of the bar unclickable");
        }
        w.Close();
    }

    // Home and Settings live in the frame that never scrolls away. The editor
    // repeating them gave two buttons for one job and two places to look.
    [AvaloniaFact]
    public void TheEditorDoesNotRepeatAChromeCommand()
    {
        var w = Open();
        LoadClean(w);
        var editor = w.FindControl<DockPanel>("EditorView")!;
        var repeats = editor.GetVisualDescendants().OfType<Button>()
            .Select(b => b.Name)
            .Where(n => n is "HomeButton" or "EditorSettingsButton")
            .ToArray();
        Assert.Empty(repeats);
        w.Close();
    }

    // The chrome put Manage files and Community one click away from the open
    // editor for the first time. Opening a profile from either replaces the
    // one being edited, and these two were the only shell commands that did
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

    // Wrapping a window's existing content in the shared frame re-parented a
    // control the window still owned, which throws before anything is shown.
    // Every prompt and the quick guide went through that line, so with unsaved
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

    // A mouth stick or a switch can double-fire one press. Two prompts stacked
    // on the same question is two things to answer and no way to tell them
    // apart, so the second click has to find the first prompt already up.
    [AvaloniaTheory]
    [InlineData("ShellHomeButton")]
    [InlineData("ShellNewButton")]
    [InlineData("ShellDeviceButton")]
    [InlineData("ShellCommunityButton")]
    public void ADoublePressAsksToSaveOnlyOnce(string button)
    {
        var w = Open();
        var file = ProfileFile.NewFromTemplate("mygame.csv");
        w.LoadProfile(file);
        file.Dirty = true;
        w.UpdateLayout();

        var b = w.FindControl<Button>(button)!;
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, w.OwnedWindows.Count(o => o.Title == "Save your changes?"));

        foreach (var o in w.OwnedWindows.ToList()) o.Close();
        Dispatcher.UIThread.RunJobs();
        file.Dirty = false;
        w.Close();
    }
}
