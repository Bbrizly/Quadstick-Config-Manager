using System.Linq;
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

// The tour is the first thing a new user meets, and it points at one control at
// a time. A step whose control is not on screen is a dead end for exactly the
// person the tour exists for.
public class TutorialTourTests
{
    static MainWindow Open()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;   // the tour is started by hand below, not on open
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.UpdateLayout();
        return w;
    }

    static Button TourButton(MainWindow w, string spokenName) =>
        w.GetVisualDescendants().OfType<Button>()
            .First(b => AutomationProperties.GetName(b) == spokenName);

    static void Click(Button b)
    {
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    // Step 4 opens a profile and switches to the editor. Back from it put the
    // step text and the spotlight on "New profile", a Home control, while the
    // editor was still the page on screen.
    [AvaloniaFact]
    public void BackFromTheDeviceStepReturnsToHome()
    {
        var w = Open();
        w.StartTutorial();
        Dispatcher.UIThread.RunJobs();

        var next = TourButton(w, "Next step");
        for (var i = 0; i < 3; i++) Click(next);      // Welcome, Appearance, New profile, Your QuadStick
        Assert.True(w.FindControl<DockPanel>("EditorView")!.IsVisible);

        Click(TourButton(w, "Back to the previous step"));
        w.UpdateLayout();

        Assert.True(w.FindControl<Control>("HomeView")!.IsVisible);
        Assert.True(w.FindControl<Button>("HomeNewButton")!.IsEffectivelyVisible);

        Click(TourButton(w, "Skip the tutorial"));
        w.Close();
    }
}
