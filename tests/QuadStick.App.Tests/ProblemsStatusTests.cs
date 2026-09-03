using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// The line under the profile name is the one sentence everybody reads before
// pressing Install, so it has to say what is actually true of this file.
public class ProblemsStatusTests
{
    static MainWindow Editor(string csv)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(ProfileFile.Load(csv));
        w.UpdateLayout();
        return w;
    }

    static string StatusLine(MainWindow w) =>
        string.Join(" ", w.GetVisualDescendants().OfType<ContentControl>()
            .Where(c => c.Name == "StatusHost")
            .SelectMany(c => c.GetVisualDescendants().OfType<TextBlock>())
            .Select(t => t.Text ?? ""));

    // "0 errors, 2 warnings. Errors block installing." told somebody their
    // profile was blocked when nothing was blocking it. A warning is a row the
    // device skips; the file installs.
    [AvaloniaFact]
    public void Warnings_alone_do_not_claim_the_install_is_blocked()
    {
        var w = Editor(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,left_sip\n");   // not a documented input: warning

        var line = StatusLine(w);
        Assert.Contains("warning", line);
        Assert.DoesNotContain("0 error", line);
        Assert.DoesNotContain("block", line);
        w.Close();
    }

    // The other half: one real error and the line has to say so, because that
    // one does stop the install.
    [AvaloniaFact]
    public void An_error_still_says_it_blocks_installing()
    {
        var w = Editor(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "mouse_speed,normal,fast\n" +   // a word where a number goes: error
            "x,normal,left_sip\n");        // and a warning beside it

        var line = StatusLine(w);
        Assert.Contains("1 error", line);
        Assert.Contains("Errors block installing", line);
        w.Close();
    }

    [AvaloniaFact]
    public void Expanded_problems_use_the_full_editor_width()
    {
        var w = Editor(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "mouse_speed,normal,fast\n");

        var toggle = w.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ProblemsToggle");
        var problems = w.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ProblemsListBorder");
        var workspace = w.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "EditorWorkspace");

        toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        w.UpdateLayout();

        Assert.True(problems.IsVisible);
        Assert.Equal(workspace.Bounds.Width, problems.Bounds.Width, precision: 1);
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "EditorSidebar"),
            problems.GetVisualAncestors());
        w.Close();
    }
}
