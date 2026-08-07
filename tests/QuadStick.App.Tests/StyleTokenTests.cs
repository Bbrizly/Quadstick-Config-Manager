using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

// Style.cs is only the one place the look is decided while every token it lists
// is registered and reaches the controls. Both halves are pinned here.
public class StyleTokenTests
{
    [AvaloniaFact]
    public void Every_token_is_registered_under_its_own_name()
    {
        foreach (var key in Style.Numbers.Keys)
        {
            Assert.True(Application.Current!.TryFindResource(key, out var v), key);
            Assert.IsType<double>(v);
        }
        foreach (var key in Style.Paddings.Keys)
        {
            Assert.True(Application.Current!.TryFindResource(key, out var v), key);
            Assert.IsType<Thickness>(v);
        }
        // Radii are offered twice: as a number to do arithmetic with, and as
        // the corner a style setter can actually take.
        foreach (var key in Style.Numbers.Keys.Where(k => k.EndsWith("Radius")))
            Assert.IsType<CornerRadius>(Application.Current!.FindResource(key + "Corner"));
    }

    [AvaloniaFact]
    public void Turning_a_token_moves_a_control_that_is_already_on_screen()
    {
        var button = new Button { Content = "Save" };
        var w = new Window { Content = button };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        Assert.Equal(new CornerRadius(Style.Numbers["ControlRadius"]), button.CornerRadius);
        Assert.Equal(Style.Numbers["ControlHeight"], button.MinHeight);

        try
        {
            Style.Set("ControlRadius", 13);
            Style.Set("ControlHeight", 61);
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();

            Assert.Equal(new CornerRadius(13), button.CornerRadius);
            Assert.Equal(61, button.MinHeight);
        }
        finally
        {
            // Resources are the application's, not this window's, so a token
            // left turned would follow every test after this one.
            Style.Set("ControlRadius", Style.Numbers["ControlRadius"]);
            Style.Set("ControlHeight", Style.Numbers["ControlHeight"]);
            Dispatcher.UIThread.RunJobs();
            w.Close();
        }
    }
}
