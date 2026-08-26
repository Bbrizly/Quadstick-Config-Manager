using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// Nothing on the hardware says which 3.5 mm jack is digital_in_8. A person
// wiring a switch for somebody else has to know, and getting it wrong means a
// switch that does nothing with no way to see why.
public class SwitchJackTests
{
    // The default a single plug lands on, per port. This is the fact Drew
    // named as the one people get wrong: both numbers are only in play once a
    // splitter is in the socket.
    [Theory]
    [InlineData("digital_in_8", SwitchJacks.TopPort, true)]
    [InlineData("digital_in_7", SwitchJacks.TopPort, false)]
    [InlineData("digital_in_1", SwitchJacks.BottomPort, true)]
    [InlineData("digital_in_2", SwitchJacks.BottomPort, false)]
    public void A_lone_switch_lands_on_8_at_the_top_and_1_at_the_bottom(
        string channel, string port, bool lone)
    {
        var jack = SwitchJacks.For(channel);
        Assert.NotNull(jack);
        Assert.Equal(port, jack!.Port);
        Assert.Equal(lone, jack.Lone);
    }

    // DataFlow.c:507 puts 5 on p3.5 "mpaux" and 6 on p0.23 "ai0 (mouthpiece)",
    // which is why the lip switch splits to those two and not to a rear pair.
    [Theory]
    [InlineData("digital_in_5")]
    [InlineData("digital_in_6")]
    public void The_lip_switch_splits_to_5_and_6(string channel) =>
        Assert.Equal(SwitchJacks.LipPort, SwitchJacks.For(channel)!.Port);

    // 3 and 4 are the USB-A data lines. They are real channels, so they must be
    // placed, but no socket may be invented for them.
    [Theory]
    [InlineData("digital_in_3")]
    [InlineData("digital_in_4")]
    public void The_usb_data_pins_are_not_a_socket(string channel)
    {
        var jack = SwitchJacks.For(channel);
        Assert.Equal(SwitchJacks.UsbDataPort, jack!.Port);
        Assert.Equal("", jack.Position);
    }

    // Every one of the eight has a home, or the picker's Switch jacks category
    // hides the ones it cannot place.
    [Fact]
    public void All_eight_channels_are_placed_exactly_once()
    {
        var placed = SwitchJacks.Ports.SelectMany(p => p.Channels).ToList();
        Assert.Equal(8, placed.Count);
        Assert.Equal(8, placed.Distinct(StringComparer.Ordinal).Count());
        for (int i = 1; i <= 8; i++)
            Assert.Contains($"digital_in_{i}", placed, StringComparer.Ordinal);
    }

    [Fact]
    public void A_name_that_is_not_a_jack_has_no_jack()
    {
        Assert.Null(SwitchJacks.For("lip"));
        Assert.Null(SwitchJacks.For("digital_in_9"));
        Assert.Null(SwitchJacks.For(""));
    }

    // The four directions a rear USB joystick arrives as. They are in the
    // firmware's input list, so they must survive validation.
    [Fact]
    public void The_rear_joystick_directions_are_real_inputs()
    {
        Assert.Equal(4, SwitchJacks.RearJoystick.Length);
        foreach (var name in SwitchJacks.RearJoystick)
            Assert.Contains(name, Vocab.Inputs, StringComparer.Ordinal);
    }

    // Each socket has to be able to say what plugging into it does, because
    // that sentence is what the editor prints.
    [Fact]
    public void Every_port_explains_itself()
    {
        foreach (var (port, _) in SwitchJacks.Ports)
            Assert.NotEqual("", SwitchJacks.Explain(port));
        Assert.Contains("digital_in_8", SwitchJacks.Explain(SwitchJacks.TopPort), StringComparison.Ordinal);
        Assert.Contains("digital_in_1", SwitchJacks.Explain(SwitchJacks.BottomPort), StringComparison.Ordinal);
    }
}
