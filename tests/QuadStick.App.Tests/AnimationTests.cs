using System;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// Regression guards for the delete/add motion. Avalonia 11.1's built-in
// TransformAnimator accepts a translate animation and then silently animates
// nothing; the app shipped with an invisible "slide" until a clock-driven
// probe proved Y never moved. The fix pins a custom InterpolatingAnimator
// and runs the animation on the TranslateTransform itself.
//
// Two layers, because the headless clock only reliably advances animations
// started from test context (app-started ones need real rendering):
//  1. the app's own Between() helper demonstrably produces mid-flight values
//  2. the delete path demonstrably wires slide transforms onto survivors
public class AnimationTests
{
    static (MainWindow, ProfileFile) OpenWithProfile(string name)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.NewFromTemplate(name);
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();
        return (w, file);
    }

    [AvaloniaFact]
    public void The_slide_helper_really_moves_values_on_a_ticking_clock()
    {
        var (w, file) = OpenWithProfile("anim-helper.csv");
        var rowsPanel = w.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "RowsPanel");
        var row = rowsPanel.Children[1];
        var lift = new TranslateTransform();
        row.RenderTransform = lift;
        _ = MainWindow.Between(TranslateTransform.YProperty, 56, 0).RunAsync(lift);

        bool sawMidFlight = false;
        for (int i = 0; i < 10 && !sawMidFlight; i++)
        {
            System.Threading.Thread.Sleep(20);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            if (lift.Y > 0.5) sawMidFlight = true;
        }

        file.Dirty = false; // else Close opens the save dialog and waits forever
        w.Close();
        Assert.True(sawMidFlight,
            "Between() never produced a mid-flight value: the pinned animator is not animating (the old TransformAnimator silent no-op is back)");
    }

    [AvaloniaFact]
    public void Deleting_a_row_wires_the_slide_onto_every_survivor()
    {
        var (w, file) = OpenWithProfile("anim-delete.csv");
        var rowsPanel = w.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "RowsPanel");
        int rowsBefore = rowsPanel.Children.Count - 1; // child 0 is the header

        var del = w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Delete row "));
        del.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        int rowsAfter = rowsPanel.Children.Count - 1;
        int withSlide = rowsPanel.Children.Count(c => c.RenderTransform is TranslateTransform);

        file.Dirty = false; // else Close opens the save dialog and waits forever
        w.Close();
        Assert.Equal(rowsBefore - 1, rowsAfter); // the delete itself stays synchronous
        // The gap-close animates at most the first 30 rows below the gap.
        int expected = Math.Min(rowsAfter, 30);
        Assert.True(withSlide >= expected,
            $"only {withSlide} of {rowsAfter} surviving rows carry a slide transform (expected {expected}): AnimateGapClose is not wired to the delete path");
    }
}
