using System;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using Avalonia.Interactivity;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// A row that keeps its output but loses its last input never fires again, and
// nothing in the finished file says so: the factory template ships twelve rows
// shaped exactly like that on purpose ("dpad_N,normal," and the rest), so a
// warning on the file would open every new profile with twelve complaints and
// still could not tell a placeholder from a mapping somebody just broke.
//
// Only the edit knows an input used to be there. So the edit says it.
public class GridConsequenceTests
{
    const string OneMapping =
        "Profile Name,,Left joy\r\ngame.csv\r\nPlayStation Outputs,Function,usb\r\n" +
        "left_trigger,normal,lip\r\n";

    static (MainWindow Owner, ProfileFile File, ImportReviewWindow Review) Open(string csv)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var owner = new MainWindow();
        owner.Show();
        Dispatcher.UIThread.RunJobs();
        owner.OpenDeviceProfile(ProfileFile.Load(csv));
        Dispatcher.UIThread.RunJobs();
        var file = owner.OpenFile!;
        var review = new ImportReviewWindow(owner, file, "Their sheet", Array.Empty<SkippedTab>());
        _ = review.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();
        review.UpdateLayout();
        return (owner, file, review);
    }

    static void Done(MainWindow owner, ImportReviewWindow review)
    {
        review.Close();
        if (owner.OpenFile is not null) owner.OpenFile.Dirty = false;
        owner.Close();
    }

    static void Advanced(ImportReviewWindow w)
    {
        var b = w.GetVisualDescendants().OfType<Button>()
            .First(x => (x.Content as string)?.Contains("dvanced") == true);
        b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
    }

    static Border GridHost(Window w) =>
        w.GetVisualDescendants().OfType<Border>().First(b =>
            (AutomationProperties.GetName(b) ?? "").StartsWith("Your spreadsheet."));

    static void GoTo(Window w, int row, int col)
    {
        GridHost(w).Focus();
        Dispatcher.UIThread.RunJobs();
        for (int i = 1; i < row; i++) w.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        for (int i = 0; i < col; i++) w.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
    }

    static bool Says(Window w, string fragment) =>
        w.GetVisualDescendants().OfType<TextBlock>().Any(t => (t.Text ?? "").Contains(fragment));

    [AvaloniaFact]
    public void Emptying_the_last_input_says_the_output_will_never_fire()
    {
        var (owner, file, review) = Open(OneMapping);
        Advanced(review);
        GoTo(review, 4, 2); // C4, the only input
        review.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        review.UpdateLayout();

        Assert.Empty(file.Document.Sheets[0].Bindings[0].Inputs);
        Assert.True(Says(review, "Nothing presses \"left_trigger\" now"),
            "the grid said nothing about the row it just killed");

        Done(owner, review);
    }

    [AvaloniaFact]
    public void Emptying_one_of_two_inputs_says_nothing_because_the_row_still_fires()
    {
        var (owner, file, review) = Open(
            "Profile Name,,Left joy\r\ngame.csv\r\nPlayStation Outputs,Function,usb\r\n" +
            "left_trigger,normal,lip,mp_center_sip\r\n");
        Advanced(review);
        GoTo(review, 4, 3); // D4, the second input
        review.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        review.UpdateLayout();

        Assert.Equal(new[] { "lip" }, file.Document.Sheets[0].Bindings[0].Inputs);
        Assert.False(Says(review, "Nothing presses"), "cried wolf on a row that still works");

        Done(owner, review);
    }

    [AvaloniaFact]
    public void Emptying_a_settings_value_leaves_the_wording_to_the_warning_that_already_covers_it()
    {
        // Column C on a settings row is a value, not an input. Saying "nothing
        // presses mouse_speed" would be nonsense, and the validator already
        // says the device will read the empty cell as 0.
        var (owner, file, review) = Open(
            "Profile Name,,Left joy\r\ngame.csv\r\nPlayStation Outputs,Function,usb\r\n" +
            "mouse_speed,,50\r\n");
        Advanced(review);
        GoTo(review, 4, 2);
        review.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        review.UpdateLayout();

        Assert.False(Says(review, "Nothing presses"));
        Assert.Contains(file.Issues, i => i.Cell == "C4" && i.Message.Contains("sets it to 0"));

        Done(owner, review);
    }

    // The import review is a one-shot window. The editors are where people
    // actually work, and taking the last input off a row there said nothing at
    // all: the row went on naming an output that nothing could press, and the
    // problems list could not tell it from the factory template's placeholders.
    static MainWindow OpenEditor(string csv, bool deviceView, out ProfileFile opened)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.Model = "FPS";
        s.DeviceCards = true;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        opened = ProfileFile.Load(csv);
        opened.Dirty = false;
        w.LoadProfile(opened);
        w.SetDeviceViewForPreview(deviceView);
        if (deviceView) w.SelectZoneForPreview("lip");
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    static bool WindowSays(Window w, string fragment)
    {
        w.UpdateLayout();
        return w.GetVisualDescendants().OfType<TextBlock>().Any(t => (t.Text ?? "").Contains(fragment));
    }

    // The remove control beside an input only appears once a row has more than
    // one. "none" is the device's own word for a blank, so a row left holding
    // only that presses nothing, and taking the real input off it is exactly
    // the edit worth a word about. It was the one edit that said nothing.
    [AvaloniaFact]
    public void List_View_says_when_an_edit_leaves_a_row_nothing_can_press()
    {
        var w = OpenEditor(
            "Profile Name,,Left joy\r\ngame.csv\r\nPlayStation Outputs,Function,usb\r\n" +
            "left_trigger,normal,lip,none\r\n", deviceView: false, out var f);

        w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "") == "Remove input 1 from row 4")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(WindowSays(w, "Nothing presses \"left_trigger\" now"));

        f.Dirty = false;
        w.Close();
    }

    // Device View is the view the app opens in, so a fix that only reached the
    // list would have been half a fix.
    [AvaloniaFact]
    public void Device_View_says_when_an_edit_leaves_a_row_nothing_can_press()
    {
        var w = OpenEditor(OneMapping, deviceView: true, out var f);

        // The inputs and their remove controls live inside the expanded card.
        w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Mapping 1:"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();

        w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Remove this input from mapping"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(WindowSays(w, "Nothing presses \"left_trigger\" now"));

        f.Dirty = false;
        w.Close();
    }
}
