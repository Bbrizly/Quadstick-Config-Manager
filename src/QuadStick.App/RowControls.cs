using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace QuadStick.App;

// The controls every list of things in this app ends with: move it up, move it
// down, copy it, delete it.
//
// They used to be drawn twice. A row in the editor got the app's own chevron
// and trash icons; a mode in the Modes window got the typed characters
// "▲ ▼ ⧉ ✕" instead, which are a different size, a different weight and, on a
// machine missing the glyph, a box. The same job must not look like two
// different jobs depending on which screen it happens to be on, so both call
// in here and there is one place to change the answer.
//
// Everything is 40x40 (Button.icon): the click-target floor for a mouth stick
// or a head mouse.
public static class RowControls
{
    static PathIcon Glyph(string iconKey)
    {
        var icon = new PathIcon
        {
            Width = 16, Height = 16,
            Data = (Geometry)Application.Current!.FindResource(iconKey)!,
        };
        MainWindow.BindBrushTo(icon, IconElement.ForegroundProperty, "TextSecondary");
        return icon;
    }

    public static Button Icon(string iconKey, string spokenName)
    {
        var b = new Button { Classes = { "icon" }, Content = Glyph(iconKey) };
        AutomationProperties.SetName(b, spokenName);
        return b;
    }

    /// <summary>Move up or down. One chevron rotated, so the pair can never
    /// drift apart in weight or size the way two separate glyphs did.</summary>
    public static Button Move(bool up, string spokenName)
    {
        var b = Icon("IconChevron", spokenName);
        // The chevron points right; +90 turns it down, 180+90 turns it up.
        ((PathIcon)b.Content!).RenderTransform = new RotateTransform(up ? 270 : 90);
        b.VerticalAlignment = VerticalAlignment.Center;
        return b;
    }

    /// <summary>Delete. Red, because it is the one action here that loses
    /// work, and never red alone: it carries the trash shape too.</summary>
    public static Button Delete(string spokenName)
    {
        var b = Icon("IconDelete", spokenName);
        b.Classes.Add("danger");
        MainWindow.BindBrushTo((PathIcon)b.Content!, IconElement.ForegroundProperty, "Error");
        b.VerticalAlignment = VerticalAlignment.Center;
        return b;
    }
}
