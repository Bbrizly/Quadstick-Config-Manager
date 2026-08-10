using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// One sentence used to cover an empty download, a sheet of somebody's notes,
// and a tab this app had itself pushed. The user could not tell which they had,
// and the one that read worst was their own share link.
public class SheetsImportErrorTests
{
    static IReadOnlyList<SkippedTab> None => Array.Empty<SkippedTab>();

    [Fact]
    public void An_empty_download_says_empty_and_says_to_wait()
    {
        var message = MainWindow.NoProfileTab("   \r\n", None);

        Assert.Contains("came back empty", message);
        Assert.Contains("wait", message);
    }

    [Fact]
    public void A_sheet_of_something_else_quotes_its_A1()
    {
        var message = MainWindow.NoProfileTab("Shopping list,eggs\r\nmilk\r\n", None);

        Assert.Contains("\"Shopping list\"", message);
    }

    [Fact]
    public void A_blank_A1_is_named_as_blank_rather_than_quoted_as_nothing()
    {
        var message = MainWindow.NoProfileTab(",eggs\r\nmilk\r\n", None);

        Assert.Contains("cell A1 is empty", message);
    }

    // The tabs are the whole answer when there are any: the user is looking at
    // a workbook whose tabs all got passed over.
    [Fact]
    public void Passed_over_tabs_are_named()
    {
        var skipped = new[]
        {
            new SkippedTab("Dpad", Array.Empty<string[]>()),
            new SkippedTab("Reference Card", Array.Empty<string[]>(), SkippedTabKind.Helper),
        };

        var message = MainWindow.NoProfileTab("", skipped);

        Assert.Contains("\"Dpad\"", message);
        Assert.Contains("\"Reference Card\"", message);
    }

    // A1 can hold a paragraph somebody pasted over it. Enough of it to
    // recognise, not the whole thing in the status line.
    [Fact]
    public void A_pasted_paragraph_in_A1_is_cut_short()
    {
        var message = MainWindow.NoProfileTab(new string('x', 400) + ",b\r\n", None);

        Assert.Contains("...", message);
        Assert.True(message.Length < 250, $"message is {message.Length} characters");
    }
}
