using Avalonia;
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

// The compact card is a shape, not a wording: the same mapping, with the two
// names on one line and the behavior underneath. What it must not do is say
// one thing on screen and another out loud, which is how the setting rows
// went wrong.
public sealed class CompactCardTests : IDisposable
{
    // The settings file is shared across the run, so a test that leaves one of
    // these on hands the next one a view it never asked for. Rows View as
    // cards is the one that bit: a preferences test read its own rows for a
    // ComboBox and found sentence cards instead.
    public void Dispose()
    {
        var s = Settings.Load();
        s.CompactCards = false;
        s.RowCards = false;
        Settings.Save(s);
    }

    static ProfileFile OneTurboMapping() => ProfileFile.Load(
        "Profile Name,,Solo\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "circle,turbo,lip\n");

    // "soft puff" is the widest input word on the mouthpiece, and the one the
    // first render of this layout drew straight through the "to" beside it.
    static ProfileFile OneWideInput() => ProfileFile.Load(
        "Profile Name,,Solo\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "touchpad,normal,,mp_left_soft_puff\n");

    // Rows View, where the card has the window's width. The single line is
    // the wide form, so this is where it can be seen at all.
    static MainWindow OpenWide(bool compact, ProfileFile file)
    {
        var s = Settings.Load();
        s.Model = "FPS";
        s.TutorialSeen = true;
        s.RowCards = true;
        s.CardSentenceStyle = "PressWhen";
        s.CompactCards = compact;
        s.RememberWindow = false;
        Settings.Save(s);

        var w = new MainWindow();
        w.Show();
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    static MainWindow OpenOnLip(bool compact, string sentenceStyle, ProfileFile file, string zone = "lip")
    {
        var s = Settings.Load();
        s.Model = "FPS";
        s.TutorialSeen = true;
        s.DeviceCards = true;
        s.RowCards = false;
        s.CardSentenceStyle = sentenceStyle;
        s.CompactCards = compact;
        s.RememberWindow = false;
        Settings.Save(s);

        var w = new MainWindow();
        w.Show();
        w.LoadProfile(file);
        w.SelectZoneForPreview(zone);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        return w;
    }

    static string Spoken(MainWindow w) => w.GetVisualDescendants().OfType<Button>()
        .Select(b => AutomationProperties.GetName(b) ?? "")
        .First(n => n.StartsWith("Mapping 1:"));

    // The switch is off: the card reads the long way round, as it always has.
    [AvaloniaFact]
    public void Off_the_card_still_reads_press_output_when_you_input()
    {
        var file = OneTurboMapping();
        var w = OpenOnLip(compact: false, "PressWhen", file);

        Assert.Equal("Mapping 1: press Circle when you lip, as turbo. Press Enter to edit.", Spoken(w));

        file.Dirty = false;
        w.Close();
    }

    // On, and with the words setting still on the output-first sentence: the
    // card is drawn input to output, so it has to be spoken that way too.
    [AvaloniaFact]
    public void Compact_speaks_input_to_output_whatever_the_sentence_style_says()
    {
        var file = OneTurboMapping();
        var w = OpenOnLip(compact: true, "PressWhen", file);

        Assert.Equal("Mapping 1: lip to Circle, as turbo. Press Enter to edit.", Spoken(w));

        file.Dirty = false;
        w.Close();
    }

    // The point of the shape: the two names share a line, and the behavior is
    // under them rather than beside them. Read off the laid-out card, because
    // a column count would still pass if the pills landed anywhere.
    [AvaloniaFact]
    public void Compact_puts_the_two_names_on_one_line_and_the_behavior_under_them()
    {
        var file = OneTurboMapping();
        var w = OpenWide(compact: true, file);

        var card = w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Mapping 1:"));
        double MiddleOf(Control c) =>
            c.TranslatePoint(new Point(0, c.Bounds.Height / 2), card)!.Value.Y;
        double Word(string text) => MiddleOf(card.GetVisualDescendants()
            .OfType<TextBlock>().Single(t => t.Text == text));
        // circle draws as its controller face button, so the output pill has a
        // picture where the other two have words.
        double output = MiddleOf(card.GetVisualDescendants().OfType<Viewbox>()
            .Single(v => AutomationProperties.GetName(v) == "Circle"));

        Assert.Equal(Word("lip"), output, 1.0);
        Assert.True(Word("turbo") > Word("lip") + 1,
            $"turbo sits at {Word("turbo")}, the names at {Word("lip")}");

        file.Dirty = false;
        w.Close();
    }

    // Nothing may be drawn over anything else. The compact line gives the
    // input a share of the card, and a wide input has to wrap inside it rather
    // than run under the word that follows it.
    [AvaloniaFact]
    public void A_wide_input_stays_inside_its_share_of_the_compact_line()
    {
        var file = OneWideInput();
        var w = OpenWide(compact: true, file);

        var card = w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Mapping 1:"));
        var input = card.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == "soft puff");
        var joiner = card.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == "to");

        double inputRight = input.TranslatePoint(new Point(input.Bounds.Width, 0), card)!.Value.X;
        double joinerLeft = joiner.TranslatePoint(new Point(0, 0), card)!.Value.X;
        Assert.True(inputRight <= joinerLeft,
            $"the input ends at {inputRight}, \"to\" starts at {joinerLeft}");

        file.Dirty = false;
        w.Close();
    }

    // The sidebar key carries the setting both ways and says which way it is.
    [AvaloniaFact]
    public void The_sidebar_key_shows_the_state_and_rebuilds_the_cards()
    {
        var file = OneTurboMapping();
        var w = OpenOnLip(compact: false, "PressWhen", file);

        var key = w.GetVisualDescendants().OfType<ToggleButton>()
            .Single(t => t.Name == "FormatToggleButton");
        Assert.True(key.IsVisible);
        Assert.False(key.IsChecked);
        Assert.Equal("Mappings read as a full sentence. Switch to one line each.",
            AutomationProperties.GetName(key));

        Ui.Click(key);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        Assert.True(key.IsChecked);
        Assert.Equal("Mappings are on one line. Switch to the full sentence.",
            AutomationProperties.GetName(key));
        Assert.Equal("Mapping 1: lip to Circle, as turbo. Press Enter to edit.", Spoken(w));

        file.Dirty = false;
        w.Close();
    }
}
