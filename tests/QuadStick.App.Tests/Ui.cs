using Avalonia.Controls;
using Avalonia.Interactivity;
using Xunit;

namespace QuadStick.App.Tests;

// Raising ClickEvent by hand fires on a control a real user could never press:
// disabled, hidden, or walled off behind a disabled ancestor. v1.7.0 shipped a
// tutorial whose own Back / Skip / Next were dead, and the test that clicked
// them passed. Every click in this suite goes through here so a test can only
// press what a person could press.
static class Ui
{
    public static void Click(Control c)
    {
        Assert.True(c.IsEffectivelyVisible, $"{Name(c)} is not on screen");
        Assert.True(c.IsEffectivelyEnabled, $"{Name(c)} is disabled");
        ClickEvenIfDisabled(c);
    }

    // Only for a test whose point is that pressing anyway does nothing. Every
    // other click goes through Click, which a disabled control fails.
    public static void ClickEvenIfDisabled(Control c) =>
        c.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static string Name(Control c) =>
        Avalonia.Automation.AutomationProperties.GetName(c) is { Length: > 0 } n ? n
        : c.Name is { Length: > 0 } x ? x
        : c.GetType().Name;
}
