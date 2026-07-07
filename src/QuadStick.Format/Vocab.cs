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

        var fnNames = Set(root.GetProperty("functions"));
        FunctionArity = FunctionParams.Where(kv => fnNames.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    public static readonly IReadOnlySet<string> Inputs;
    public static readonly IReadOnlySet<string> OutputsPs3;
    public static readonly IReadOnlySet<string> OutputsXbox;
    public static readonly IReadOnlySet<string> KnownOutputs;

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

    /// <summary>The firmware's reader is stricter than IsSheetKeyword: it
    /// dispatches sheets by strncmp on the START of the raw line, case
    /// sensitively (Configuration.c, firmware 1476). A sheet whose A1 merely
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
    // (Configuration.c, 1476) tries preference_keywords when the output name
    // doesn't match, and reads the row as "set this preference for this mode".
    // digital_out_1..4 are excluded: they match output_keywords FIRST on the
    // device, so they are outputs there, never preference overrides.
    public static readonly IReadOnlySet<string> PreferenceOverrides = new HashSet<string>(StringComparer.Ordinal)
    {
        "sip_puff_threshold_soft", "sip_puff_threshold", "sip_puff_maximum",
        "sip_puff_delay_soft", "joystick_deflection_minimum",
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
        "enable_usb_a_device", "enable_swap_inputs", "enable_select_files",
        "enable_DS3_emulation", "enable_auto_zero", "enable_left_side_tube",
        "enable_usb_comm", "enable_rumble", "bluetooth_throttle",
        "bluetooth_remote_address", "bluetooth_remote_adapter",
    };

    // Input names present in the firmware's own keyword table (1476) but
    // absent from the current validation endpoint. Old profiles use them and
    // the device parses them; the app accepts them with a warning.
    public static readonly IReadOnlySet<string> LegacyInputs = new HashSet<string>(StringComparer.Ordinal)
    { "push", "lip_soft", "right_sip_long", "right_puff_long", "bluetooth_status" };

    // "none" is a real input keyword on the device, equivalent to leaving
    // the cell blank.
    public const string NoneInput = "none";

    public static readonly IReadOnlySet<string> Channels =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "none", "usb", "bluetooth", "usb bluetooth", "bluetooth usb" };
}
