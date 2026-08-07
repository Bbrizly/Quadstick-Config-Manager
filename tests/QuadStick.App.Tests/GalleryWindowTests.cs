using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

// A style or token missing from the gallery is one nobody looks at, which is
// where the drift starts. So the list is never written down twice: classes are
// read off Application.Styles, tokens off Palette.
public class GalleryWindowTests
{
    static GalleryWindow Open()
    {
        var w = new GalleryWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    // Class names out of every selector the app styles, e.g. "Button.primary"
    // and "WrapPanel.toolbar > :is(Control)" both yield their class.
    static HashSet<string> StyledClasses()
    {
        var found = new HashSet<string>();
        foreach (var s in Application.Current!.Styles.OfType<Avalonia.Styling.Style>())
            foreach (Match m in Regex.Matches(s.Selector?.ToString() ?? "", @"\.([A-Za-z][A-Za-z0-9]*)"))
                found.Add(m.Groups[1].Value);
        return found;
    }

    [AvaloniaFact]
    public void The_gallery_is_reachable_only_by_asking_for_it()
    {
        Assert.IsType<MainWindow>(App.WindowFor(null));
        Assert.IsType<MainWindow>(App.WindowFor(Array.Empty<string>()));
        Assert.IsType<MainWindow>(App.WindowFor(new[] { "some-profile.csv" }));
        Assert.IsType<GalleryWindow>(App.WindowFor(new[] { "--gallery" }));
    }

    [AvaloniaFact]
    public void Every_styled_class_in_the_app_has_a_specimen()
    {
        var w = Open();
        var shown = w.GetVisualDescendants().OfType<StyledElement>()
            .SelectMany(c => c.Classes).ToHashSet();

        var missing = StyledClasses().Where(c => !shown.Contains(c)).OrderBy(c => c).ToList();
        Assert.True(missing.Count == 0,
            "No specimen for: " + string.Join(", ", missing)
            + ". Add one to GalleryWindow, or the style is one nobody can look at.");

        w.Close();
    }

    [AvaloniaFact]
    public void Every_colour_token_is_on_the_page_with_its_name_and_its_hex()
    {
        var w = Open();
        var text = w.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? "").ToHashSet();

        foreach (var (key, hex) in Palette.Light)
        {
            Assert.Contains(key, text);
            Assert.Contains(hex.ToUpperInvariant(), text);
        }

        w.Close();
    }

    // The readout is what you paste, so it must list only real edits. Every hex
    // box fires a change event when first drawn, which reported the lot as edited.
    [AvaloniaFact]
    public void A_gallery_nobody_has_touched_reports_no_edits()
    {
        var w = Open();
        var readout = w.GetVisualDescendants().OfType<TextBox>().First(t => t.IsReadOnly);

        // The headings the readout writes above a block of edits.
        Assert.DoesNotContain("// Palette.", readout.Text);
        Assert.DoesNotContain("// Style.cs", readout.Text);
        Assert.Contains("Nothing changed yet", readout.Text);

        w.Close();
    }

    // The point of the workbench: a number turned here moves the real controls,
    // not a drawing of them. Without this the sliders are decoration.
    [AvaloniaFact]
    public void A_turned_knob_moves_the_specimens_and_prints_what_to_paste()
    {
        var w = Open();
        var slider = w.GetVisualDescendants().OfType<Slider>()
            .First(s => (Avalonia.Automation.AutomationProperties.GetName(s) ?? "").StartsWith("ControlRadius"));
        var button = w.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == "Save");

        try
        {
            slider.Value = 14;
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();

            Assert.Equal(new CornerRadius(14), button.CornerRadius);
            // And it says what to write down, because the gallery never edits
            // the source files itself.
            var readout = w.GetVisualDescendants().OfType<TextBox>().First(t => t.IsReadOnly);
            Assert.Contains("[\"ControlRadius\"] = 14", readout.Text);
        }
        finally
        {
            Style.Set("ControlRadius", Style.Numbers["ControlRadius"]);
            Dispatcher.UIThread.RunJobs();
            w.Close();
        }
    }
}
