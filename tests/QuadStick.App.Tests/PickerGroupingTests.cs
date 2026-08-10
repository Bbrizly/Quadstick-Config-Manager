using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// The output picker files a token under a category and then a group inside it.
// That is right for someone meeting the QuadStick's vocabulary for the first
// time and wrong for someone who has typed "circle" a hundred times, and it
// was the only way the picker worked.
//
// What is pinned here is how many levels a pick costs, not how the list looks.
public sealed class PickerGroupingTests : IDisposable
{
    readonly string _wasGrouping = Settings.Load().PickerGrouping;

    public void Dispose()
    {
        var s = Settings.Load();
        s.PickerGrouping = _wasGrouping;
        Settings.Save(s);
    }

    const string Csv =
        "Profile Name,,Solo\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "circle,normal,lip\n";

    static MainWindow Open(string grouping)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.PickerGrouping = grouping;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(ProfileFile.Load(Csv));
        w.SetDeviceViewForPreview(false);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    // The output cell for the first binding, opened. The flyout's own content
    // is the tree under test; a flyout popup lives outside the window's visual
    // tree, so it is reached through the button that owns it.
    static Control OpenPicker(MainWindow w)
    {
        var button = w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Output for row 4"));
        var flyout = (Flyout)button.Flyout!;
        flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        var content = (Control)flyout.Content!;
        content.UpdateLayout();
        return content;
    }

    static string[] Buttons(Control root) => root.GetVisualDescendants().OfType<Button>()
        .Select(b => AutomationProperties.GetName(b) ?? "")
        .ToArray();

    static void Press(Control root, string startsWith)
    {
        var b = root.GetVisualDescendants().OfType<Button>()
            .First(x => (AutomationProperties.GetName(x) ?? "").StartsWith(startsWith));
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
    }

    // Two levels down to a button: Controller, then Buttons, then circle.
    [AvaloniaFact]
    public void Detailed_puts_a_group_inside_a_category()
    {
        var w = Open("Detailed");
        var picker = OpenPicker(w);

        Press(picker, "Controller,");
        Assert.Contains(Buttons(picker), b => b.StartsWith("Buttons,") && b.Contains("Opens this category"));

        w.Close();
    }

    // One level: the category lists every output under it, groups and all.
    [AvaloniaFact]
    public void Wide_lists_a_categorys_outputs_without_a_second_level()
    {
        var w = Open("Wide");
        var picker = OpenPicker(w);

        Press(picker, "Controller,");
        var names = Buttons(picker);
        Assert.DoesNotContain(names, b => b.Contains("Opens this category"));
        Assert.Contains("circle", names);
        // A d-pad output lives in a different group of the same category, so
        // it proves the whole category came through and not just one group.
        Assert.Contains(names, b => b.StartsWith("dpad_"));

        w.Close();
    }

    // No levels at all.
    [AvaloniaFact]
    public void Flat_is_one_list_with_no_categories()
    {
        var w = Open("Flat");
        var picker = OpenPicker(w);

        var names = Buttons(picker);
        Assert.DoesNotContain(names, b => b.Contains("Opens this category"));
        Assert.Contains("circle", names);
        Assert.Contains(names, b => b.StartsWith("kb_"));

        w.Close();
    }

    // Detailed stays the default, so nobody's picker changes under them.
    [Fact]
    public void Detailed_is_the_default()
    {
        Assert.Equal("Detailed", new AppSettings().PickerGrouping);
        Assert.Equal("Detailed", MainWindow.PickerGroupings[0]);
    }
}
