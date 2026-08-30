using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// The panel down the left of the editor: which mode is open, what the window
// beside it is showing, and the parts that have nowhere to sit on the photo.
// It replaced a dropdown and two bands of loose buttons across the top, so
// these hold the things that dropdown used to guarantee.
public class SidebarTests
{
    // Driving, the preferences sheet, then Aiming: a mode is not the same
    // thing as a sheet, and the numbering has to survive one sitting between
    // two others.
    const string ThreeSheets =
        "Profile Name,,Driving\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "x,normal,lip\n" +
        "Preferences\n" +
        ",\n" +
        "Preference,Value,Units,Description\n" +
        "Sip_Puff_Threshold,20\n" +
        "Profile Name,,Aiming\n" +
        ",,\n" +
        "Outputs,Function,usb\n" +
        "circle,normal,right_sip\n";

    static MainWindow Open(string csv = ThreeSheets, bool deviceView = true)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.InterfaceScalePercent = 100;
        s.Model = "FPS";
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.Load(csv);
        file.Dirty = false; // else Close opens the save dialog and waits forever
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(deviceView);
        w.UpdateLayout();
        return w;
    }

    static Control Named(MainWindow w, string name) =>
        w.GetVisualDescendants().OfType<Control>().First(c => c.Name == name);

    static ToggleButton[] Rows(MainWindow w) =>
        ((StackPanel)Named(w, "ModeList")).Children.OfType<ToggleButton>().ToArray();

    static string TextOf(ToggleButton b) =>
        string.Concat(b.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

    // The device counts Profile Name segments and never reads the name, so the
    // list has to number modes and only modes: the preferences sheet sitting
    // second must not make the mode after it read as "3".
    [AvaloniaFact]
    public void Every_sheet_is_a_row_and_only_modes_are_numbered()
    {
        var w = Open();
        var rows = Rows(w);

        // Two modes, the preferences sheet, and the names table at the end.
        Assert.Equal(4, rows.Length);
        Assert.Equal("1: Driving", TextOf(rows[0]));
        Assert.Equal("Preferences", TextOf(rows[1]));
        Assert.Equal("2: Aiming", TextOf(rows[2]));
        Assert.True(rows[0].IsChecked, "nothing in the list says which mode is open");

        w.Close();
    }

    [AvaloniaFact]
    public void Pressing_a_row_opens_that_sheet()
    {
        var w = Open();
        Rows(w)[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        w.UpdateLayout();

        var rows = Rows(w);
        Assert.True(rows[2].IsChecked, "the list still points at the mode that was open before");
        Assert.False(rows[0].IsChecked);
        Assert.Null(w.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "DeviceHeaderMode"));

        w.Close();
    }

    // Pressing the open mode again used to leave the list with nothing checked:
    // a ToggleButton toggles itself, and the editor had no reason to rebuild.
    [AvaloniaFact]
    public void Pressing_the_open_mode_again_leaves_it_open()
    {
        var w = Open();
        Rows(w)[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        w.UpdateLayout();

        Assert.True(Rows(w)[0].IsChecked);
        w.Close();
    }

    [AvaloniaFact]
    public void The_plus_adds_a_mode_and_opens_it()
    {
        var w = Open();
        Named(w, "AddModeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        w.UpdateLayout();

        // Driving is open, so the new mode goes under Driving and Aiming
        // shifts to 3. The name only has to be one nobody else has; the number
        // beside it comes from where it sits, which is the only thing the
        // device reads.
        var rows = Rows(w);
        Assert.Equal(5, rows.Length);
        Assert.Equal("2: Mode 3", TextOf(rows[1]));
        Assert.True(rows[1].IsChecked, "the new mode was added and left unopened");
        Assert.Equal("3: Aiming", TextOf(rows[3]));

        w.OpenFile!.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void Modes_have_a_compact_independent_scroller_and_header_controls()
    {
        var w = Open();
        var listScroll = (ScrollViewer)Named(w, "ModeListScroll");
        var help = Named(w, "ModeHelpButton");
        var edit = Named(w, "ModesButton");
        var add = Named(w, "AddModeButton");

        // Modes get every bit of height left above Configuration, and keep a
        // useful three-row minimum rather than making Configuration scroll too.
        Assert.Equal(112, listScroll.MinHeight);
        Assert.True(listScroll.AllowAutoHide);
        // Two scrollers, and only two: this list, and the one around the whole
        // sidebar that catches a window too short to lay the panel out at all.
        // A third would mean some section had started scrolling on its own.
        var sidebar = Named(w, "EditorSidebar");
        var scrollers = sidebar.GetVisualDescendants().OfType<ScrollViewer>().ToList();
        Assert.Equal(2, scrollers.Count);
        Assert.Same(listScroll, scrollers.Last());
        Assert.Contains("modeRow", Rows(w)[0].Classes);

        // The word "Modes" is the explanatory target on the left; the edit
        // command then sits immediately before the add command on the right.
        var helpLeft = help.TranslatePoint(new Point(0, 0), w)!.Value.X;
        var editLeft = edit.TranslatePoint(new Point(0, 0), w)!.Value.X;
        var addLeft = add.TranslatePoint(new Point(0, 0), w)!.Value.X;
        Assert.True(helpLeft < editLeft && editLeft < addLeft);

        w.Close();
    }

    [AvaloniaFact]
    public void Selecting_a_mode_scrolls_that_mode_into_view()
    {
        var w = Open();
        try
        {
            // Build a list taller than the available Modes viewport.
            for (int i = 0; i < 12; i++)
            {
                Named(w, "AddModeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
            }
            w.UpdateLayout();

            var scroll = (ScrollViewer)Named(w, "ModeListScroll");
            scroll.Offset = new Vector(0, 0);
            var lastMode = Rows(w)[^2]; // the final row is Custom Names
            lastMode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.True(scroll.Offset.Y > 0, "the selected mode was left below the visible list");
        }
        finally
        {
            w.OpenFile!.Dirty = false;
            w.Close();
        }
    }

    // Everything the brief asked to move is in the panel, and nothing that
    // moved was left behind in the band across the top.
    [AvaloniaFact]
    public void The_modes_the_view_and_the_words_all_live_in_the_panel()
    {
        var w = Open();
        var sidebar = Named(w, "EditorSidebar");

        foreach (var name in new[]
        {
            "ModeList", "AddModeButton", "ModeHelpButton", "ModesButton",
            "DeviceViewButton", "RailViewButton", "ListViewButton",
            "ModelPicker", "LabelStyleButton", "CardViewButton", "DeviceHelpButton",
            "ZoneList",
        })
            Assert.Contains(sidebar, Named(w, name).GetVisualAncestors());

        w.Close();
    }

    [AvaloniaFact]
    public void The_view_choices_follow_the_plain_english_and_detailed_editor_controls()
    {
        var w = Open();
        double Top(string name) => Named(w, name).TranslatePoint(new Point(0, 0), w)!.Value.Y;

        var wordsBottom = Math.Max(
            Top("LabelStyleButton") + Named(w, "LabelStyleButton").Bounds.Height,
            Top("CardViewButton") + Named(w, "CardViewButton").Bounds.Height);
        Assert.True(wordsBottom <= Top("DeviceViewButton") + 1,
            "Device should appear below the Plain English and Detailed editor controls");
        Assert.True(wordsBottom <= Top("RailViewButton") + 1,
            "Parts should appear below the Plain English and Detailed editor controls");
        Assert.True(wordsBottom <= Top("ListViewButton") + 1,
            "Rows should appear below the Plain English and Detailed editor controls");

        w.Close();
    }

    // The panel is a fixed width, so anything in it that measures wider than it
    // is silently cut off: there is no sideways scrolling to reach it, and
    // there must not be.
    [AvaloniaFact]
    public void Nothing_in_the_panel_is_cut_off_by_its_edge()
    {
        var w = Open();
        var sidebar = Named(w, "EditorSidebar");
        double right = sidebar.Bounds.Width;
        Assert.True(right > 0, "the panel never laid out");

        foreach (var name in new[]
        {
            "AddModeButton", "ModeHelpButton", "ModesButton", "ModelPicker",
            "DeviceViewButton", "RailViewButton", "ListViewButton",
            "LabelStyleButton", "CardViewButton", "DeviceHelpButton",
        })
        {
            var c = Named(w, name);
            var corner = c.TranslatePoint(new Point(c.Bounds.Width, 0), sidebar)!.Value;
            Assert.True(corner.X <= right + 1,
                $"{name} runs past the edge of the panel: {corner.X:0} > {right:0}");
            Assert.True(c.Bounds.Height > 0, $"{name} never laid out");
        }

        w.Close();
    }

    // Rows view has its own list of every row, so the parts list would be a
    // second, dead navigation; the mode list and the view keys stay.
    [AvaloniaFact]
    public void Rows_view_keeps_the_modes_and_drops_the_parts_list()
    {
        var w = Open(deviceView: false);

        Assert.True(Named(w, "ModeList").IsVisible);
        Assert.True(Named(w, "EditorSidebar").IsVisible);
        Assert.False(Named(w, "ZoneList").IsVisible);
        // The machine in front of you is the same machine in either view.
        Assert.True(Named(w, "ModelPicker").IsVisible);
        Assert.True(Named(w, "DeviceHeaderStatus").IsVisible);

        w.Close();
    }
}
