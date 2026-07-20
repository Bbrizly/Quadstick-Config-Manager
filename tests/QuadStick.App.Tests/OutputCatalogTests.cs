using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// The Press picker's categories must be provably complete: every legal
// output token lands in exactly one listed category, and the prefix
// families (kb_, mouse_, ir_, xac_, dpad_) never leak anywhere else.
public class OutputCatalogTests
{
    [Fact]
    public void Every_known_output_lands_in_a_listed_category()
    {
        foreach (var t in Vocab.KnownOutputs)
        {
            var (cat, sub) = OutputCatalog.Classify(t);
            Assert.Contains(cat, OutputCatalog.CategoryOrder);
            if (OutputCatalog.SubOrder.TryGetValue(cat, out var subs))
                Assert.Contains(sub, subs);
            else
                Assert.Equal("", sub);
        }
    }

    [Fact]
    public void Prefix_families_stay_in_their_category()
    {
        foreach (var t in Vocab.KnownOutputs)
        {
            var (cat, _) = OutputCatalog.Classify(t);
            if (t.StartsWith("kb_")) Assert.Equal("Keyboard", cat);
            if (t.StartsWith("mouse_")) Assert.Equal("Mouse", cat);
            if (t.StartsWith("ir_")) Assert.Equal("TV remote", cat);
            if (t.StartsWith("xac_")) Assert.Equal("Xbox Adaptive Controller", cat);
            if (t.StartsWith("dpad_")) Assert.Equal("Controller", cat);
        }
    }

    [Theory]
    [InlineData("circle", "Controller", "Buttons")]
    [InlineData("A", "Controller", "Buttons")]
    [InlineData("left_trigger", "Controller", "Buttons")]
    [InlineData("touch", "Controller", "Buttons")]
    [InlineData("left_joy_up", "Controller", "Thumbsticks")]
    [InlineData("right_stick", "Controller", "Thumbsticks")]
    [InlineData("dpad_NE", "Controller", "D-pad")]
    [InlineData("kb_a", "Keyboard", "Letters")]
    [InlineData("kb_f", "Keyboard", "Letters")]
    [InlineData("kb_7", "Keyboard", "Numbers")]
    [InlineData("kb_f12", "Keyboard", "Function keys")]
    [InlineData("kb_keypad_5", "Keyboard", "Number pad")]
    [InlineData("kb_space", "Keyboard", "Space, Enter, arrows")]
    [InlineData("kb_left_arrow", "Keyboard", "Space, Enter, arrows")]
    [InlineData("kb_left_shift", "Keyboard", "Modifier keys")]
    [InlineData("kb_printscreen", "Keyboard", "Other keys")]
    [InlineData("mouse_left_button", "Mouse", "")]
    [InlineData("ir_play", "TV remote", "")]
    [InlineData("xac_left_A", "Xbox Adaptive Controller", "")]
    [InlineData("increment_mode", "Mode switching", "")]
    [InlineData("load_file", "Mode switching", "")]
    [InlineData("bluetooth_throttle", "Device settings", "")]
    [InlineData("sip_puff_threshold", "Device settings", "")]
    public void Spot_checks(string token, string cat, string sub)
        => Assert.Equal((cat, sub), OutputCatalog.Classify(token));

    [Fact]
    public void Keyboard_letter_number_and_function_groups_are_complete()
    {
        var kb = Vocab.KnownOutputs.Where(t => t.StartsWith("kb_"))
            .Select(t => OutputCatalog.Classify(t).Sub).ToList();
        Assert.Equal(26, kb.Count(s => s == "Letters"));
        Assert.Equal(10, kb.Count(s => s == "Numbers"));
        Assert.Equal(24, kb.Count(s => s == "Function keys"));
    }
}
