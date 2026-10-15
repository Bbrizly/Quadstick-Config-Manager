using System;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// Clicking a mouthpiece hole on the photo narrows the list beside it to that
// hole, because a hole is a zone of its own. The switch jacks are not: four
// sockets share one zone, so clicking the top jack left the bottom jack's
// mappings in the list and the picture and the list disagreed. Same for the
// five hole pairings. Drew reported it on 2026-09-18.
public class ZoneNarrowingTests
{
    // Two sockets and two pairings, so narrowing has something to drop.
    const string Csv =
        "Profile Name,,Solo\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "x,normal,digital_in_8\n" +
        "circle,normal,digital_in_1\n" +
        "square,normal,mp_left_center_sip\n" +
        "triangle,normal,mp_left_right_sip\n";

    static MainWindow Open(string input)
    {
        var s = Settings.Load();
        s.Model = "FPS";
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.DeviceCards = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(ProfileFile.Load(Csv));
        w.PickInputForPreview(input);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    // An output is drawn as controller art, not as its token, so the words on
    // the panel are not enough: the spoken names are where a row says what it
    // presses.
    static string PanelText(MainWindow w)
    {
        var panel = w.GetVisualDescendants().OfType<Control>()
            .First(c => c.Name == "ZoneDetailPanel");
        return string.Join(" ", panel.GetVisualDescendants().OfType<Control>()
            .Select(c => (c as TextBlock)?.Text ?? AutomationProperties.GetName(c) ?? ""));
    }

    [AvaloniaTheory]
    [InlineData("digital_in_8", "digital_in_7", "digital_in_2")]
    [InlineData("digital_in_1", "digital_in_2", "digital_in_7")]
    [InlineData("mp_left_center_sip", "mp_left_center_puff", "mp_left_right_puff")]
    [InlineData("mp_left_right_sip", "mp_left_right_puff", "mp_left_center_puff")]
    public void Picking_a_part_drops_the_rest_of_the_zone(string input, string kept, string dropped)
    {
        var w = Open(input);
        var text = PanelText(w);
        Assert.Contains(kept, text, StringComparison.Ordinal);
        Assert.DoesNotContain(dropped, text, StringComparison.Ordinal);
        w.Close();
    }

    // Hiding rows silently is how a mapping gets reported missing, so the
    // panel has to say it is narrowed and offer the whole zone back.
    [AvaloniaFact]
    public void The_panel_says_it_is_narrowed_and_undoes_it()
    {
        var w = Open("digital_in_8");
        Assert.Contains("Top jack only", PanelText(w), StringComparison.Ordinal);
        var back = w.GetVisualDescendants().OfType<Button>()
            .First(b => AutomationProperties.GetName(b) == "Show every mapping on this part of the device");
        back.Command?.Execute(null);
        ((Avalonia.Interactivity.Interactive)back).RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        var text = PanelText(w);
        Assert.Contains("digital_in_2", text, StringComparison.Ordinal);
        Assert.Contains("digital_in_7", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Top jack only", text, StringComparison.Ordinal);
        w.Close();
    }
}
