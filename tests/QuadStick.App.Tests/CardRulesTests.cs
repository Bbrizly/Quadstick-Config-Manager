using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// A sentence card is a table: an outline round the sentence, and a hairline
// between every pair of neighbouring cells. Without the rules a run of cards
// is phrases floating in a box, which is what the tester read them as.
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
}
