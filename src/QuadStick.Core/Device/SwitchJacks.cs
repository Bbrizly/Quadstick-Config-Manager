using System.Globalization;

namespace QuadStick.Format;

/// <summary>Which physical socket on the back of the QuadStick a
/// <c>digital_in_N</c> name belongs to, and what a plug does when it lands
/// there.</summary>
/// <param name="Port">The socket, in the words somebody looking at the back
/// of the device would use.</param>
/// <param name="Position">Which half of that socket, once a splitter is in
/// it. Empty when the socket has only one.</param>
/// <param name="Lone">True when this is the channel a single plug lands on
/// with no splitter, so it is the one to offer first.</param>
public sealed record SwitchJack(string Channel, string Port, string Position, bool Lone)
{
    /// <summary>The whole thing in one phrase, for a list entry or a label.
    /// "Top jack, one switch" or "Top jack, splitter, second switch".</summary>
    public string Label =>
        Position.Length == 0 ? SwitchJacks.PortLabel(Port)
        : Lone ? string.Format(CultureInfo.CurrentCulture, Strings.Jack_PortOneSwitch, SwitchJacks.PortLabel(Port))
        : string.Format(CultureInfo.CurrentCulture, Strings.Jack_PortSplitterPosition, SwitchJacks.PortLabel(Port), Position);
}

/// <summary>
/// The back panel's switch sockets, mapped to the eight digital input channels.
/// </summary>
/// <remarks>
/// <para>Two different sources, and they answer different questions.</para>
/// <para>Firmware 2373 <c>Joystick/DataFlow.c:507</c> is the authority on what
/// each channel is physically wired to, and its own comments name the pins:
/// 1 and 2 are p0.3 RXD0 and p0.2 TXD0, 3 and 4 are p0.29 and p0.30 (the USB-A
/// data pins), 5 is p3.5 "mpaux", 6 is p0.23 "ai0 (mouthpiece)", and 7 and 8
/// are p4.28 and p4.29, the relay pins. That is why 5 and 6 sit with the
/// mouthpiece and not with the rear jacks.</para>
/// <para>Which socket on the case each pair comes out of is board wiring and is
/// not in the C at all. That half comes from Drew Redepenning, the clinician
/// who sets these up: the top socket is 7 and 8, the bottom is 1 and 2, the
/// middle is the lip switch and splits to 5 and 6, and a single plug with no
/// splitter lands on 8 at the top and 1 at the bottom. Anything here that the
/// firmware cannot confirm is labelled as the physical layout, never as
/// something the device said.</para>
/// </remarks>
public static class SwitchJacks
{
    // English, and staying English: this is how a socket is identified, in a
    // switch, in a test's InlineData and in the app's hotspot table. PortLabel
    // is what a person reads.
    public const string TopPort = "Top jack";
    public const string BottomPort = "Bottom jack";
    public const string LipPort = "Lip jack";
    public const string UsbDataPort = "USB-A data pins";

    /// <summary>The socket's name in the language the app is being read in.</summary>
    public static string PortLabel(string port) => port switch
    {
        TopPort => Strings.Jack_TopJack,
        BottomPort => Strings.Jack_BottomJack,
        LipPort => Strings.Jack_LipJack,
        UsbDataPort => Strings.Jack_USBADataPins,
        _ => port,
    };

    static readonly Dictionary<string, SwitchJack> ByChannel = new(StringComparer.Ordinal)
    {
        // A single plug lands here, so it is the first thing to offer.
        ["digital_in_8"] = new("digital_in_8", TopPort, Strings.Jack_FirstSwitch, Lone: true),
        ["digital_in_7"] = new("digital_in_7", TopPort, Strings.Jack_SecondSwitch, Lone: false),
        ["digital_in_1"] = new("digital_in_1", BottomPort, Strings.Jack_FirstSwitch, Lone: true),
        ["digital_in_2"] = new("digital_in_2", BottomPort, Strings.Jack_SecondSwitch, Lone: false),
        ["digital_in_5"] = new("digital_in_5", LipPort, Strings.Jack_FirstSwitch, Lone: true),
        ["digital_in_6"] = new("digital_in_6", LipPort, Strings.Jack_SecondSwitch, Lone: false),
        // p0.29 and p0.30 are the USB-A data lines read as plain inputs. No
        // socket of their own, and no claim invented for them.
        ["digital_in_3"] = new("digital_in_3", UsbDataPort, "", Lone: false),
        ["digital_in_4"] = new("digital_in_4", UsbDataPort, "", Lone: false),
    };

    /// <summary>Where a channel comes out, or null for a name that is not one
    /// of the eight.</summary>
    public static SwitchJack? For(string channel) =>
        ByChannel.TryGetValue((channel ?? "").Trim(), out var j) ? j : null;

    /// <summary>The sockets in the order they sit on the case, top to bottom,
    /// each with its channels. The USB-A pins come last: they are not a socket
    /// somebody plugs a switch into.</summary>
    public static readonly (string Port, string[] Channels)[] Ports =
    {
        (TopPort, new[] { "digital_in_8", "digital_in_7" }),
        (LipPort, new[] { "digital_in_5", "digital_in_6" }),
        (BottomPort, new[] { "digital_in_1", "digital_in_2" }),
        (UsbDataPort, new[] { "digital_in_3", "digital_in_4" }),
    };

    /// <summary>One sentence on how a socket behaves, for the card above the
    /// mappings. Says the default plainly, because getting it wrong means a
    /// switch that does nothing and no way to tell why.</summary>
    public static string Explain(string port) => port switch
    {
        TopPort => Strings.Jack_PlugOneSwitchIntoThe,
        BottomPort => Strings.Jack_PlugOneSwitchIntoThe2,
        LipPort => Strings.Jack_TheMiddleJackIsThe,
        UsbDataPort => Strings.Jack_TheUSBAPortS,
        _ => "",
    };

    /// <summary>The four names a joystick in the rear USB-A port arrives as,
    /// in the order a person thinks of them.</summary>
    /// <remarks>Firmware 2373 <c>input_keywords.h</c> carries all four, and
    /// <c>DataFlow.c handle_usb_inputs()</c> is what fills them. Its buttons
    /// are <c>usb_1_button_1</c> to <c>_16</c>, which the picker already
    /// offers; the four directions had no way in.</remarks>
    public static readonly string[] RearJoystick =
        { "usb_1_up", "usb_1_down", "usb_1_left", "usb_1_right" };
}
