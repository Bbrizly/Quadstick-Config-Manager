using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

// The shared window frame every secondary window in the app wears. It used to
// draw a close button of its own, a few pixels from the operating system's, so
// on macOS every window asked to be closed twice: the red dot top left and an
// x top right. It also used to take focus on open, so every prompt started on
// the control that means "cancel": Enter on "Save your changes?" answered
// Cancel and the click that raised it looked like it had done nothing.
public class DialogShellTests
{
    static Window Wrap(Control content, string title)
    {
        var w = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight };
        w.Content = MainWindow.DialogShell(w, content);
        return w;
    }

    [AvaloniaFact]
    public void FocusStartsOnTheDialogsOwnDefaultAction()
    {
        var save = new Button { Content = "Save", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var w = Wrap(new StackPanel { Children = { save, cancel } }, "Save your changes?");
        w.Show();
        w.UpdateLayout();

        Assert.True(save.IsFocused,
            "the prompt opened with focus somewhere else, so Enter does not answer it");
        w.Close();
    }

    [AvaloniaFact]
    public void FocusStartsInTheBoxWhenTheDialogAsksForTyping()
    {
        var box = new TextBox();
        var save = new Button { Content = "Save template", IsDefault = true };
        var w = Wrap(new StackPanel { Children = { box, save } }, "Save as template");
        w.Show();
        w.UpdateLayout();

        Assert.True(box.IsFocused, "typing the name would have gone nowhere");
        w.Close();
    }

    // One close control per window, and it is the operating system's.
    [AvaloniaFact]
    public void TheFrameDrawsNoCloseButtonOfItsOwn()
    {
        var w = Wrap(new Button { Content = "Done", IsCancel = true }, "Modes");
        w.Show();
        w.UpdateLayout();

        Assert.DoesNotContain(w.GetVisualDescendants().OfType<Button>(),
            b => (AutomationProperties.GetName(b) ?? "").StartsWith("Close ")
                 || (b.Content as string) == "\u00d7");
        w.Close();
    }

    // Focus still has to land inside the window, or Escape never reaches it.
    [AvaloniaFact]
    public void FocusStaysInsideAWindowWithNothingToFocus()
    {
        var w = Wrap(new TextBlock { Text = "Nothing to press." }, "Notice");
        w.Show();
        w.UpdateLayout();

        var focused = w.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.IsFocused);
        Assert.NotNull(focused);
        w.Close();
    }
}
