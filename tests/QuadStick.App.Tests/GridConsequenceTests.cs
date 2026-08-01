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
}
