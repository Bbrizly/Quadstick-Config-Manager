using System.Linq;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// Firmware 2373 added mp_right_mode_*: the right mouthpiece hole and the side
// tube used at the same time. The firmware calls the side tube the "mode" tube,
// and its sensor map gives the pairing both bits (DataFlow.c, 0b0011 for
// _MP_RIGHT_RIGHT_SIP, where the bits read Left, Center, Right, Mode).
//
// The app filed the four names under the Right hole because the name starts
// with mp_right, and then stripped that prefix and showed them as "mode sip".
// A user reading the Right hole's card would have been told a binding fires on
// a plain right-hole sip when it needs the side tube too, which is the kind of
// wrong label that leaves somebody unable to press a button they mapped.
public class ComboZoneTests
{
    [Theory]
    [InlineData("mp_right_mode_sip")]
    [InlineData("mp_right_mode_sip_soft")]
    [InlineData("mp_right_mode_puff")]
    [InlineData("mp_right_mode_puff_soft")]
    public void Right_hole_with_the_side_tube_is_a_combo(string input) =>
        Assert.Equal("combo", MainWindow.ZoneOf(input));

    // The combo arm is tested before the mp_right arm, so it has to match the
    // pairing and nothing else.
    [Theory]
    [InlineData("mp_right_sip")]
    [InlineData("mp_right_sip_soft")]
    [InlineData("mp_right_puff")]
    [InlineData("mp_right_puff_soft")]
    public void One_hole_on_its_own_stays_on_that_hole(string input) =>
        Assert.Equal("mp_right", MainWindow.ZoneOf(input));

    [Theory]
    [InlineData("lip")]
    [InlineData("lip_soft")]
    public void Lip_inputs_stay_in_the_lip_zone(string input) =>
        Assert.Equal("lip", MainWindow.ZoneOf(input));

    // The prefix list breaks on its first match, so "mp_right_" matching first
    // left the word "mode" in the label.
    [Theory]
    [InlineData("mp_right_mode_sip", "sip")]
    [InlineData("mp_right_mode_puff", "puff")]
    [InlineData("mp_right_mode_sip_soft", "soft sip")]
    [InlineData("mp_right_mode_puff_soft", "soft puff")]
    public void The_pairing_prefix_comes_off_whole(string input, string expected)
    {
        Assert.Equal(expected, MainWindow.StripInput(input, "combo"));
        Assert.DoesNotContain("mode", MainWindow.StripInput(input, "combo"));
    }

    // Every pairing strips to the same word, so the label has to name the parts
    // or the combo card and its dropdown read as a list of duplicates.
    [Fact]
    public void Each_pairing_names_its_own_parts()
    {
        Assert.Equal("Right + Side tube", MainWindow.ComboPair("mp_right_mode_sip"));
        Assert.Equal("Left + Right", MainWindow.ComboPair("mp_left_right_sip"));
        Assert.Equal("Right + Center", MainWindow.ComboPair("mp_right_center_sip"));
        Assert.Equal("Left + Center", MainWindow.ComboPair("mp_left_center_sip"));
        Assert.Equal("All three", MainWindow.ComboPair("mp_triple_sip"));
    }

    [Fact]
    public void Every_combo_input_gets_its_own_pairing_name()
    {
        var pairs = Vocab.Inputs.Where(i => MainWindow.ZoneOf(i) == "combo")
            .Select(MainWindow.ComboPair).Distinct().ToList();
        Assert.Equal(5, pairs.Count);
        Assert.DoesNotContain("Combo", pairs);
    }

    // Chips sit under a heading that already says "Combos", so they abbreviate.
    // R+S is the new one: R+C and L+R were already taken.
    [Fact]
    public void The_chip_abbreviates_the_pairing()
    {
        Assert.Equal("R+S soft sip", MainWindow.ChipLabel("mp_right_mode_sip_soft", "combo"));
        Assert.Equal("L+R puff", MainWindow.ChipLabel("mp_left_right_puff", "combo"));
        Assert.Equal("R+C sip", MainWindow.ChipLabel("mp_right_center_sip", "combo"));
        Assert.Equal("L+C sip", MainWindow.ChipLabel("mp_left_center_sip", "combo"));
        Assert.Equal("all 3 sip", MainWindow.ChipLabel("mp_triple_sip", "combo"));
    }

    [Fact]
    public void Every_combo_input_gets_its_own_chip()
    {
        var combos = Vocab.Inputs.Where(i => MainWindow.ZoneOf(i) == "combo").ToList();
        var chips = combos.Select(i => MainWindow.ChipLabel(i, "combo")).ToList();
        Assert.Equal(combos.Count, chips.Distinct().Count());
    }

    // increment_value and decrement_value step an output channel's analog value
    // and latch it (DataFlow.c:1793-1844). The switch that runs them is guarded
    // by `if (output_id <= KEYBOARD_RIGHT_GUI)`, and a setting's id is biased by
    // +1024, so they never touch a device setting. The app used to say they
    // nudged mouse speed, which is the one thing they cannot do: a row with a
    // setting name in the output column is read as a setting override and its
    // function cell is skipped unread.
    [Theory]
    [InlineData("increment_value")]
    [InlineData("decrement_value")]
    public void Stepping_a_value_is_about_an_output_not_a_setting(string function)
    {
        var said = MainWindow.FunctionExplain(function);
        Assert.DoesNotContain("setting", said);
        Assert.DoesNotContain("mouse speed", said);
        Assert.Contains("analog output", said);
    }
}
