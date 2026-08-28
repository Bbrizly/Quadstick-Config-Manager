using System.Text.Json;

namespace QuadStick.Format;

// Legal input/output/function names from validation.json (validation.quadstick.com).
public static class Vocab
{
    static Vocab()
    {
        using var s = typeof(Vocab).Assembly.GetManifestResourceStream("ValidationJson")
            ?? throw new InvalidOperationException("Embedded validation.json missing.");
        using var doc = JsonDocument.Parse(s);
        var root = doc.RootElement;

        static HashSet<string> Set(JsonElement e) =>
            e.EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);

        Inputs = Set(root.GetProperty("inputs"));
        OutputsPs3 = Set(root.GetProperty("outputs_ps3"));
        OutputsXbox = Set(root.GetProperty("outputs_xbox"));
        var known = new HashSet<string>(OutputsPs3, StringComparer.Ordinal);
        known.UnionWith(OutputsXbox);
        KnownOutputs = known;
        KnownOutputsLoose = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);

        var fnNames = Set(root.GetProperty("functions"));
        FunctionArity = FunctionParams.Where(kv => fnNames.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        FunctionsInFirmwareOrder = FirmwareFunctionOrder.Where(FunctionArity.ContainsKey).ToArray();
    }

    // function_keywords[] in preference_keywords.h, in the order the firmware
    // lists it. search_for_keyword_with_parameter walks the table in order and
    // compares only the first strlen(entry) characters, so a cell that starts
    // with one of these gets that function whatever follows it. Order decides
    // the winner when two entries share a prefix.
    static readonly string[] FirmwareFunctionOrder =
    {
        "normal", "toggle", "repeat", "pulse", "duty", "greater_than",
        "less_than", "force_off", "delayed_latch", "delay_off", "delay_on", "tap",
        // Added by firmware 2373, at the end of the table. Safe there because no
        // entry in the 14 is a prefix of any other, so the order cannot decide
        // anything for these two. The table has room for 15: the function code
        // is unpacked as `binding.function & 0x0F` in DataFlow.c.
        "increment_value", "decrement_value",
    };

    public static readonly IReadOnlyList<string> FunctionsInFirmwareOrder;

    public static readonly IReadOnlySet<string> Inputs;
    public static readonly IReadOnlySet<string> OutputsPs3;
    public static readonly IReadOnlySet<string> OutputsXbox;
    public static readonly IReadOnlySet<string> KnownOutputs;

    // The same outputs, matched loosely, for UI rules that hold a name a human
    // typed against a token. Never for validating a file: the device itself
    // reads its keywords case-sensitively, so KnownOutputs is the truth there.
    public static readonly IReadOnlySet<string> KnownOutputsLoose;

    public static readonly IReadOnlyDictionary<string, (int Min, int Max)> FunctionArity;

    static readonly Dictionary<string, (int Min, int Max)> FunctionParams = new(StringComparer.Ordinal)
    {
        ["normal"] = (0, 0),
        ["toggle"] = (0, 0),
        ["repeat"] = (0, 2),
        ["pulse"] = (0, 2),
        ["duty"] = (0, 1),
        ["greater_than"] = (0, 2),
        ["less_than"] = (0, 1),
        ["force_off"] = (0, 1),
        ["delayed_latch"] = (0, 1),
        ["delay_off"] = (0, 1),
        ["delay_on"] = (0, 2),
        ["tap"] = (0, 2),
        ["increment_value"] = (0, 2),
        ["decrement_value"] = (0, 2),
    };

    /// <summary>A1 must contain "Profile", or be Preferences / Infrared.</summary>
    public static bool IsSheetKeyword(string a1) =>
        a1.Contains("Profile", StringComparison.OrdinalIgnoreCase)
        || a1.Trim().Equals("Preferences", StringComparison.OrdinalIgnoreCase)
        || a1.Trim().Equals("Infrared", StringComparison.OrdinalIgnoreCase);

    /// <summary>The file's own first line, which is not a sheet: "QuadStick
    /// Configuration,Version 1.5,&lt;sheet id&gt;,&lt;name&gt;". A worksheet whose A1
    /// says this is a whole profile written flat, not a mode.</summary>
    public static bool IsFileHeader(string a1) =>
        a1.TrimStart().StartsWith(FileHeaderKeyword, StringComparison.OrdinalIgnoreCase);

    public const string FileHeaderKeyword = "QuadStick Configuration";

    /// <summary>The firmware's reader is stricter than IsSheetKeyword: it
    /// dispatches sheets by strncmp on the START of the raw line, case
    /// sensitively (Configuration.c, firmware 2373). A sheet whose A1 merely
    /// CONTAINS "Profile" is silently skipped by the device.</summary>
    public static bool FirmwareAcceptsSheetKeyword(string rawA1) =>
        rawA1.StartsWith("Profile", StringComparison.Ordinal)
        || rawA1.StartsWith("Preferences", StringComparison.Ordinal)
        || rawA1.StartsWith("Infrared", StringComparison.Ordinal);

    public static SheetType KeywordToType(string a1) =>
        a1.Contains("Profile", StringComparison.OrdinalIgnoreCase) ? SheetType.ProfileName
        : a1.Trim().Equals("Preferences", StringComparison.OrdinalIgnoreCase) ? SheetType.Preferences
        : SheetType.Infrared;

    public static bool IsKnownOutput(string name) => KnownOutputs.Contains(name);

    // Preference names legal in a mode sheet's output column: the firmware
    // (Configuration.c, 2373) tries preference_keywords when the output name
    // doesn't match, and reads the row as "set this preference for this mode".
    // digital_out_1..4 are excluded: they match output_keywords FIRST on the
    // device, so they are outputs there, never preference overrides.
    public static readonly IReadOnlySet<string> PreferenceOverrides = new HashSet<string>(StringComparer.Ordinal)
    {
        "sip_puff_threshold_soft", "sip_puff_threshold", "sip_puff_maximum",
        "sip_puff_delay_soft", "sip_puff_delay_hard",
        // Sip and puff split apart, so a sip and a puff can have their own
        // thresholds. Zero on any of these means the matching sip_puff_ setting
        // still applies to that direction.
        "sip_threshold_soft", "sip_threshold", "sip_maximum",
        "puff_threshold_soft", "puff_threshold", "puff_maximum",
        "joystick_deflection_minimum",
        "joystick_deflection_maximum", "joystick_warning", "joystick_alarm",
        "joystick_D_Pad_inner", "joystick_D_Pad_outer",
        "joystick_dead_zone_shape", "anti_dead_zone", "volume", "brightness",
        "watchdog_disable", "bluetooth_device_mode",
        "bluetooth_authentication_mode", "bluetooth_connection_mode",
        "lip_position_minimum", "lip_position_maximum", "mouse_speed",
        "mouse_response_curve", "debug",
        "deflection_multiplier_up", "deflection_multiplier_down",
        "deflection_multiplier_left", "deflection_multiplier_right",
        "usb_1_multiplier_right", "usb_1_multiplier_left",
        "usb_1_multiplier_down", "usb_1_multiplier_up",
        "usb_2_multiplier_right", "usb_2_multiplier_left",
        "usb_2_multiplier_down", "usb_2_multiplier_up",
        "usb_1_dead_zone", "usb_2_dead_zone",
        "enable_usb_a_device", "enable_usb_a_host", "enable_swap_inputs",
        "enable_select_files",
        "enable_DS3_emulation", "enable_auto_zero", "enable_left_side_tube",
        "enable_usb_comm", "enable_rumble", "bluetooth_throttle",
        "bluetooth_remote_address", "bluetooth_remote_adapter", "titan_two",
    };

    // A mode row whose output cell is a preference name sets that preference
    // for the mode instead of binding a button, so its column C is a value and
    // not an input. The function cell plays no part in deciding this.
    //
    // This used to carve out increment_value and decrement_value, on the theory
    // that such a row adjusts the setting live. Firmware 2373 settled it and the
    // theory was wrong. Configuration.c takes the preference branch on the
    // output NAME alone and then skips the function cell unconditionally:
    //
    //     binding.output += 1024;
    //     next_word(line_buffer, &k);                        // skip "function"
    //     binding.function = atoi(next_word(line_buffer, &k));
    //
    // There is no function lookup on that path in either firmware, so
    // "mouse_speed,increment_value 5,right_sip" sets mouse_speed to
    // atoi("right_sip"), which is 0, on a 2025 device exactly as on a 2017 one.
    //
    // The two functions are real, they just belong on real outputs: they step an
    // output channel's analog value 0..1023 and latch it (DataFlow.c), so
    // "left_joy_up,increment_value 5,mp_center_puff" raises the stick 5% a puff
    // and holds it. The switch that runs them is guarded by
    // `if (output_id <= KEYBOARD_RIGHT_GUI)`, which a preference id (biased by
    // +1024) can never satisfy.
    //
    // Every place that has to tell the two apart calls this.
    public static bool IsPreferenceOverride(string output, string function) =>
        PreferenceOverrides.Contains(output);

    // Input names present in the firmware's own keyword table but absent from
    // the current validation endpoint. "Legacy" is about the endpoint, not the
    // device: 2373 still has all five, so a profile using one still works and
    // the app accepts it with a warning rather than telling its owner to change
    // a name their QuadStick answers to.
    public static readonly IReadOnlySet<string> LegacyInputs = new HashSet<string>(StringComparer.Ordinal)
    { "push", "lip_soft", "right_sip_long", "right_puff_long", "bluetooth_status" };

    // The same story on the output side. The firmware's output table still has
    // these two unaxed aliases in 2373; the current validation endpoint only
    // lists the axed forms (gyroscope_x_cw and friends). A profile using one
    // works on the device, so telling its owner to replace it would be wrong.
    public static readonly IReadOnlySet<string> LegacyOutputs = new HashSet<string>(StringComparer.Ordinal)
    { "gyroscope_cw", "gyroscope_ccw" };

    // "none" is a real input keyword on the device, equivalent to leaving
    // the cell blank.
    public const string NoneInput = "none";

    /// <summary>True when a row still names an output but nothing left on it
    /// can make the device fire it.
    ///
    /// The validator cannot say this and never will: the factory template ships
    /// twelve rows shaped exactly like this on purpose ("dpad_N,normal," and the
    /// rest), so a finished file holding one is indistinguishable from a correct
    /// one. Only the edit knows an input used to be there, so only the edit can
    /// mention it. The import review has said so since it was written; the two
    /// editors, where people actually live, said nothing at all.
    ///
    /// "none" is the device's own word for a blank and a word it has never heard
    /// of is skipped, so a row holding only those never fired and losing them
    /// costs nothing worth announcing. A settings row is left alone too: its
    /// column C is a value, not an input, and emptying it already has its own
    /// warning saying the device reads 0.</summary>
    public static bool NothingFiresIt(Binding b) =>
        b.Output.Trim().Length > 0
        && !IsPreferenceOverride(b.Output, b.Function)
        && !b.Inputs.Any(i => i != NoneInput && (Inputs.Contains(i) || LegacyInputs.Contains(i)));

    // connections_keywords[] in the firmware, and the device matches it the way
    // it matches every other keyword: the whole word, case sensitively, with usb
    // as the fallback. So "Bluetooth" and "usb bluetooth" are not two ways of
    // saying something, they are two ways of getting usb without being told.
    //
    // "both" is new in 2373, where the channel is a bitmask (USB 1, BLUETOOTH 2,
    // USB_AND_BLUETOOTH 3) and the mode runs on either. A 2017 device does not
    // know the word, so it falls back to usb and, because that firmware tests
    // the channel with == rather than a mask, the mode loses Bluetooth with
    // nothing said. See BothChannelNeedsNewFirmware in the validator.
    public static readonly IReadOnlySet<string> Channels =
        new HashSet<string>(StringComparer.Ordinal) { "none", "usb", "bluetooth", "both" };
}
