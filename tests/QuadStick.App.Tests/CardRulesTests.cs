using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// Cards and diagram callouts are tables: an outline, and a hairline between
// every pair of neighbouring cells. Without the rules they are phrases
// floating in a box, which is what the tester read them as.
public class CardRulesTests
{
    static ProfileFile OneLipMapping() => ProfileFile.Load(
        "Profile Name,,Solo\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "x,normal,lip\n");

    static MainWindow OpenOnLip()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.DeviceCards = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(OneLipMapping());
        w.SelectZoneForPreview("lip");
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    [AvaloniaFact]
    public void Sentence_card_cells_are_ruled_like_a_table()
    {
        var w = OpenOnLip();
        var card = w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Mapping 1:"));

        // The grid holding the sentence sits inside its own outlined box.
        var grid = card.GetVisualDescendants().OfType<Grid>().First(g => g.Children.Count > 1);
        var box = Assert.IsType<Border>(grid.Parent);
        Assert.Equal(1, box.BorderThickness.Left);
        Assert.Equal(1, box.BorderThickness.Top);
        Assert.Equal(1, box.BorderThickness.Right);
        Assert.Equal(1, box.BorderThickness.Bottom);

        // Every cell is a Border, and the ones with a neighbour to the right
        // or below carry the rule that separates them.
        var cells = grid.Children.OfType<Border>().ToList();
        Assert.Equal(grid.Children.Count, cells.Count);
        // Spans count: a cell reaching the last column is an edge cell, so
        // the sentence style and the panel width cannot change the answer.
        int lastCol = cells.Max(c => Grid.GetColumn(c) + Grid.GetColumnSpan(c) - 1);
        int lastRow = cells.Max(c => Grid.GetRow(c) + Grid.GetRowSpan(c) - 1);
        Assert.Contains(cells, c => c.BorderThickness.Right > 0);
        foreach (var c in cells)
        {
            Assert.Equal(Grid.GetColumn(c) + Grid.GetColumnSpan(c) - 1 < lastCol ? 1 : 0, c.BorderThickness.Right);
            Assert.Equal(Grid.GetRow(c) + Grid.GetRowSpan(c) - 1 < lastRow ? 1 : 0, c.BorderThickness.Bottom);
            // No doubled lines: a cell never draws its neighbour's edge too.
            Assert.Equal(0, c.BorderThickness.Left);
            Assert.Equal(0, c.BorderThickness.Top);
        }

        w.Close();
    }

    // The diagram callouts carry the same rules: one down the gutter between
    // the gesture and its action, one above every row after the first, and one
    // under the part's name.
    [AvaloniaFact]
    public void Diagram_callout_rows_are_ruled_like_a_table()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.Model = "FPS";
        s.RememberWindow = false;
        Settings.Save(s);

        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "circle,normal,mp_center_puff\n" +
            "square,normal,mp_center_sip\n");
        file.Dirty = false;
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(true);
        w.UpdateLayout();

        var callout = w.GetVisualDescendants().OfType<ToggleButton>()
            .First(b => (AutomationProperties.GetName(b) ?? "")
                .StartsWith("Center mouthpiece hole", StringComparison.Ordinal));

        // Four gestures, so three rules across and one down the gutter.
        var table = callout.GetVisualDescendants().OfType<Grid>()
            .First(g => g.ColumnDefinitions.Count == 3 && g.RowDefinitions.Count == 4);
        var rules = table.Children.OfType<Border>().ToList();
        Assert.Equal(3, rules.Count(r => r.Height == 1 && Grid.GetColumnSpan(r) == 3));
        Assert.Equal(1, rules.Count(r => r.Width == 1 && Grid.GetColumn(r) == 1
                                         && Grid.GetRowSpan(r) == 4));

        // And the name above the table is ruled off from it.
        var under = Assert.IsType<Border>(table.Parent);
        Assert.Equal(1, under.BorderThickness.Top);
        Assert.Equal(0, under.BorderThickness.Bottom);

        w.Close();
    }
}
