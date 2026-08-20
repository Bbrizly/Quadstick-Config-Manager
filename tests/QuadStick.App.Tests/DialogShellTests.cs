using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

// The shared window frame draws its own close button. It used to take focus
// on open, so every prompt started on the control that means "cancel": Enter
// on "Save your changes?" answered Cancel and the click that raised it looked
// like it had done nothing.
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
