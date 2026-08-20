namespace QuadStick.Format;

public enum ModeLight { Off, Red, Blue, Purple }

/// <summary>
/// What the QuadStick's five lights show while a mode is the active one.
/// </summary>
/// <remarks>
/// Ported from the device, not from documentation. FW 2373
/// <c>Joystick/DataFlow.c</c>, <c>update_active_config_leds()</c>:
/// <code>
/// scratch = config_led_range_patterns[active_mode / 5];
/// LEDs_ChangeLEDs((active_mode == 15 ? 0 : (0b10000100000 >> (active_mode % 5))) | scratch, 0b1111111111);
/// </code>
/// Five two-colour LEDs. Bits 0-4 are the red halves, bits 5-9 the blue ones,
/// and both halves lit is the purple everyone recognises. Bit 0 is the
/// rightmost light: <c>BSP/MCB2300/bsp_MCB2300.c</c> orders the pin table
/// <c>{RED_5, RED_4, RED_3, RED_2, RED_1}</c>, "use rightmost LSB order for
/// most natural display", over LEDs it names left to right 1 to 5.
/// <c>active_mode</c> is 1-based there, the same number this app shows.
/// </remarks>
public static class ModeLights
{
    static readonly int[] RangePatterns =
        { 0, 0b0000100001, 0b0000100000, 0b0000000001, 0b1111100000, 0b1111100001, 0b0000011111 };

    /// Past this the firmware indexes off the end of its own table, so there is
    /// no pattern to show. Say that rather than invent one.
    public const int HighestMode = 34;

    /// The five lights left to right, or null when the firmware has no pattern.
    public static ModeLight[]? For(int mode)
    {
        if (mode < 1 || mode > HighestMode) return null;

        int bits = ((mode == 15 ? 0 : 0b10000100000 >> (mode % 5)) | RangePatterns[mode / 5])
                   & 0b1111111111; // the device's own active mask: ten bits, five lights
        var lights = new ModeLight[5];
        for (int bit = 0; bit < 5; bit++)
        {
            bool red = (bits & (1 << bit)) != 0;
            bool blue = (bits & (1 << (bit + 5))) != 0;
            lights[4 - bit] = (red, blue) switch // bit 0 is the rightmost light
            {
                (true, true) => ModeLight.Purple,
                (true, false) => ModeLight.Red,
                (false, true) => ModeLight.Blue,
                _ => ModeLight.Off,
            };
        }
        return lights;
    }

    /// The same pattern in words. A colour on its own is not a cue everyone can
    /// read, so nothing in the app shows these lights without this line.
    public static string Describe(ModeLight[] lights)
    {
        var groups = lights
            .Select((light, i) => (Light: light, Number: i + 1))
            .Where(x => x.Light != ModeLight.Off)
            .GroupBy(x => x.Light)
            .Select(g => (g.Count() == 1 ? "light " : "lights ")
                         + Series(g.Select(x => x.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList())
                         + " " + g.Key.ToString().ToLowerInvariant())
            .ToList();
        return groups.Count == 0 ? "no lights" : string.Join(", ", groups);
    }

    static string Series(List<string> parts) =>
        parts.Count == 1 ? parts[0]
        : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
}
