using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// Arabic is the one shipped language that reads right to left, so the window
// turns round with it. The device photo must not: its part markers sit at
// pixels measured off the image, and a mirrored canvas would put the label for
// the left mouthpiece hole on the right one. Someone maps their controller off
// those labels, so this is a wrong answer, not a cosmetic one.
public class ReadingDirectionTests
{
    static void InCulture(string tag, Action body)
    {
        var was = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(tag);
            body();
        }
        finally { CultureInfo.CurrentUICulture = was; }
    }

    static MainWindow Opened()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.NewFromTemplate("mygame.csv");
        file.Dirty = false; // else Close opens the save dialog and waits forever
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(true);
        w.UpdateLayout();
        return w;
    }

    [AvaloniaFact]
    public void Arabic_reads_right_to_left_and_the_others_do_not()
    {
        InCulture("ar", () => Assert.Equal(FlowDirection.RightToLeft, Localization.Direction));
        InCulture("en", () => Assert.Equal(FlowDirection.LeftToRight, Localization.Direction));
        InCulture("ja", () => Assert.Equal(FlowDirection.LeftToRight, Localization.Direction));
        InCulture("hi", () => Assert.Equal(FlowDirection.LeftToRight, Localization.Direction));
    }

    [AvaloniaFact]
    public void The_window_turns_round_in_arabic()
    {
        InCulture("ar", () =>
        {
            var w = Opened();
            Assert.Equal(FlowDirection.RightToLeft, w.FlowDirection);
            w.Close();
        });
    }

    [AvaloniaFact]
    public void The_device_photo_stays_left_to_right_in_arabic()
    {
        InCulture("ar", () =>
        {
            var w = Opened();
            var stage = w.GetVisualDescendants().OfType<Canvas>()
                .FirstOrDefault(c => c.Children.OfType<Image>().Any());
            Assert.NotNull(stage);
            Assert.Equal(FlowDirection.LeftToRight, stage!.FlowDirection);
            w.Close();
        });
    }
}
