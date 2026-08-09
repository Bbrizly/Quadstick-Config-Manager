using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// The catalog is the only place QCM claims to know what a preference means. A
// wrong bound or a made up default here reaches a disabled user's hardware, so
// these tests police provenance as hard as they police shape.
public class PreferenceCatalogTests
{
    [Fact]
    public void Catalog_loads_offline_and_is_not_empty()
    {
        Assert.NotEmpty(PreferenceCatalog.All);
    }

    [Fact]
    public void Names_are_unique_with_ordinal_matching()
    {
        var names = PreferenceCatalog.All.Select(p => p.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    // The device reads its keywords case sensitively, so neither must we.
    [Fact]
    public void Lookup_is_case_sensitive()
    {
        Assert.True(PreferenceCatalog.TryGet("mouse_speed", out var found));
        Assert.Equal("mouse_speed", found.Name);
        Assert.False(PreferenceCatalog.TryGet("Mouse_Speed", out _));
    }

    [Fact]
    public void Unknown_names_return_false_and_never_throw()
    {
        Assert.False(PreferenceCatalog.TryGet("some_future_firmware_setting", out var missing));
        Assert.Null(missing);
        Assert.False(PreferenceCatalog.TryGet("", out _));
        Assert.False(PreferenceCatalog.TryGet(null!, out _));
    }

    [Fact]
    public void Every_entry_names_a_source()
    {
        foreach (var p in PreferenceCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Source), $"{p.Name} has no source.");
            Assert.False(string.IsNullOrWhiteSpace(p.Label), $"{p.Name} has no label.");
            Assert.Contains(p.Category, PreferenceCatalog.Categories);
        }
    }

    // The rule that matters: nothing claims a bound, default, option list or
    // risk note without saying where it came from.
    [Fact]
    public void Every_claim_carries_provenance()
    {
        foreach (var p in PreferenceCatalog.All)
        {
            var claims = p.Default is not null || p.Minimum.HasValue || p.Maximum.HasValue
                || p.Options.Count > 0 || p.Risk.Length > 0;
            if (claims)
                Assert.False(string.IsNullOrWhiteSpace(p.Source), $"{p.Name} claims metadata with no source.");
        }
    }

    [Fact]
    public void Text_entries_carry_no_guessed_bounds_default_or_options()
    {
        foreach (var p in PreferenceCatalog.All.Where(p => p.Editor == PreferenceEditor.Text))
        {
            Assert.False(p.Minimum.HasValue, $"{p.Name} is text but has a minimum.");
            Assert.False(p.Maximum.HasValue, $"{p.Name} is text but has a maximum.");
            Assert.Empty(p.Options);
            // bluetooth_remote_address is the one sourced empty default.
            if (p.Default is not null)
                Assert.Equal("", p.Default);
        }
    }

    [Fact]
    public void Toggles_only_ever_default_to_zero_or_one()
    {
        foreach (var p in PreferenceCatalog.All.Where(p => p.Editor == PreferenceEditor.Toggle))
            Assert.True(p.Default is null or "0" or "1", $"{p.Name} has a non binary default.");
    }

    [Fact]
    public void Choices_list_options_and_default_to_one_of_them()
    {
        foreach (var p in PreferenceCatalog.All.Where(p => p.Editor == PreferenceEditor.Choice))
        {
            Assert.NotEmpty(p.Options);
            if (p.Default is not null)
                Assert.Contains(p.Default, p.Options);
        }
    }

    [Fact]
    public void Integer_defaults_sit_inside_their_bounds()
    {
        foreach (var p in PreferenceCatalog.All.Where(p => p.Editor == PreferenceEditor.Integer))
        {
            if (p.Minimum.HasValue && p.Maximum.HasValue)
                Assert.True(p.Minimum.Value <= p.Maximum.Value, $"{p.Name} has minimum above maximum.");
            if (p.Default is null) continue;
            var n = int.Parse(p.Default, System.Globalization.CultureInfo.InvariantCulture);
            if (p.Minimum.HasValue) Assert.True(n >= p.Minimum.Value, $"{p.Name} default is below its minimum.");
            if (p.Maximum.HasValue) Assert.True(n <= p.Maximum.Value, $"{p.Name} default is above its maximum.");
        }
    }

    // A mode row can only set the preferences the firmware's keyword table
    // reaches from a mode sheet. Anything else is device wide and must never be
    // suggested there.
    [Fact]
    public void Every_mode_override_name_is_catalogued_as_an_override()
    {
        foreach (var name in Vocab.PreferenceOverrides)
        {
            Assert.True(PreferenceCatalog.TryGet(name, out var p), $"{name} is missing from the catalog.");
            Assert.True(p.ModeOverride, $"{name} is a mode override but the catalog says otherwise.");
        }
    }

    [Fact]
    public void Catalog_only_names_are_not_mode_overrides()
    {
        var standalone = PreferenceCatalog.All
            .Where(p => !Vocab.PreferenceOverrides.Contains(p.Name))
            .ToList();

        Assert.NotEmpty(standalone);
        foreach (var p in standalone)
            Assert.False(p.ModeOverride, $"{p.Name} is standalone only but claims to be a mode override.");
    }

    // digital_out_1..4 match the output table on the device before the
    // preference table, so they are device settings only.
    [Theory]
    [InlineData("digital_out_1")]
    [InlineData("digital_out_2")]
    [InlineData("digital_out_3")]
    [InlineData("digital_out_4")]
    public void Digital_outputs_are_never_mode_overrides(string name)
    {
        Assert.True(PreferenceCatalog.TryGet(name, out var p));
        Assert.False(p.ModeOverride);
    }

    // The app models firmware 2373, so every name in that device's preference
    // table has an entry. A real preference must never reach the editor with
    // nothing known about it.
    [Fact]
    public void Every_firmware_2373_preference_is_catalogued()
    {
        foreach (var name in FirmwareOracle.Preferences)
            Assert.True(PreferenceCatalog.TryGet(name, out _),
                $"{name} is in firmware 2373 but not in the catalog.");
    }

    // And nothing the other way: the catalog never names a preference the
    // device does not have.
    [Fact]
    public void The_catalog_invents_no_preference_names()
    {
        foreach (var p in PreferenceCatalog.All)
            Assert.Contains(p.Name, FirmwareOracle.Preferences);
    }

    // Firmware 2373 added these four. Defaults are the device's own, from
    // Preferences.h, not a guess.
    [Theory]
    [InlineData("usb_1_dead_zone", PreferenceEditor.Integer, "20", "raw axis counts")]
    [InlineData("usb_2_dead_zone", PreferenceEditor.Integer, "20", "raw axis counts")]
    [InlineData("enable_usb_a_host", PreferenceEditor.Toggle, "1", "")]
    [InlineData("titan_two", PreferenceEditor.Toggle, "0", "")]
    public void Firmware_2373_names_are_catalogued(
        string name, PreferenceEditor editor, string def, string unit)
    {
        Assert.True(PreferenceCatalog.TryGet(name, out var p), $"{name} is missing from the catalog.");
        Assert.Equal(editor, p.Editor);
        Assert.Equal(def, p.Default);
        Assert.Equal(unit, p.Unit);
        Assert.True(p.ModeOverride, $"{name} is in the 2373 preference table, so a mode row reaches it.");
        Assert.Contains("2373", p.Source, StringComparison.Ordinal);
        // Someone on older firmware needs to know the name is new.
        Assert.Contains("firmware 2373", p.Description, StringComparison.Ordinal);
    }

    // The USB dead zone counts raw steps of a host controller's axis byte, not
    // percent, and nothing states a range, so none may be claimed.
    [Theory]
    [InlineData("usb_1_dead_zone")]
    [InlineData("usb_2_dead_zone")]
    public void USB_dead_zones_claim_no_range(string name)
    {
        Assert.True(PreferenceCatalog.TryGet(name, out var p));
        Assert.Null(p.Minimum);
        Assert.Null(p.Maximum);
    }

    // Firmware 2373 has sip_puff_delay_hard in its preference table, so a mode
    // row can set it, and the device's own default is 2000 ms, not QMP's 2400.
    [Fact]
    public void Hard_sip_puff_delay_is_a_mode_override_of_2000_ms()
    {
        Assert.True(PreferenceCatalog.TryGet("sip_puff_delay_hard", out var p));
        Assert.True(p.ModeOverride);
        Assert.Equal("2000", p.Default);
        Assert.Equal("milliseconds", p.Unit);
    }

    // Three defaults moved between firmware 1476 and 2373. The catalog follows
    // the device.
    [Fact]
    public void Firmware_2373_default_changes_are_carried()
    {
        Assert.True(PreferenceCatalog.TryGet("bluetooth_authentication_mode", out var auth));
        Assert.Equal("2", auth.Default);
        Assert.Contains("2", auth.Options);

        // Text, so the new default of 5 is recorded in prose rather than
        // written into anybody's file.
        Assert.True(PreferenceCatalog.TryGet("bluetooth_throttle", out var throttle));
        Assert.Equal(PreferenceEditor.Text, throttle.Editor);
        Assert.Null(throttle.Default);
        Assert.Contains("defaults to 5", throttle.Source, StringComparison.Ordinal);

        Assert.True(PreferenceCatalog.TryGet("enable_auto_zero", out var zero));
        Assert.Equal("0", zero.Default);
    }

    // A name the device parses and then throws away is still something the app
    // knows, so it has to say so instead of leaving the setting looking live.
    [Theory]
    [InlineData("usb_2_dead_zone")]
    [InlineData("enable_auto_zero")]
    public void Settings_the_device_ignores_say_so(string name)
    {
        Assert.True(PreferenceCatalog.TryGet(name, out var p));
        Assert.Contains("ignores", p.Description, StringComparison.Ordinal);
    }

    // Value 5 meant PC only, no joystick on firmware 1476 and means a Nintendo
    // Switch Pro Controller on 2373, so the same file behaves differently after
    // an update. The description names every value and says that out loud. It
    // stays text: a choice would raise an Error on any off list value and turn
    // valid files red.
    [Fact]
    public void USB_emulation_mode_documents_every_value()
    {
        Assert.True(PreferenceCatalog.TryGet("enable_DS3_emulation", out var p));
        Assert.Equal(PreferenceEditor.Text, p.Editor);
        Assert.Empty(p.Options);
        Assert.Null(p.Default);
        foreach (var meaning in new[]
                 {
                     "QuadStick native", "DualShock 3", "x360ce", "Xbox 360",
                     "Nintendo Switch Pro Controller", "no USB drive", "DS4 wireless",
                 })
            Assert.Contains(meaning, p.Description, StringComparison.Ordinal);
        Assert.Contains("1476", p.Description, StringComparison.Ordinal);
        Assert.NotEqual("", p.Risk);
    }

    // No entry may still say a name is missing from the device's table when
    // firmware 2373 has it. These sentences used to drive modeOverride.
    [Fact]
    public void No_entry_claims_a_catalogued_name_is_absent()
    {
        foreach (var p in PreferenceCatalog.All)
        {
            var prose = $"{p.Source} {p.Description} {p.Risk}";
            Assert.DoesNotContain("absent from firmware", prose, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("has no such name", prose, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("enable_DS3_emulation")]
    [InlineData("enable_usb_a_device")]
    [InlineData("debug")]
    public void Disputed_settings_stay_raw(string name)
    {
        Assert.True(PreferenceCatalog.TryGet(name, out var p));
        Assert.Equal(PreferenceEditor.Text, p.Editor);
        Assert.Null(p.Default);
        Assert.Empty(p.Options);
    }

    [Theory]
    [InlineData("enable_DS3_emulation")]
    [InlineData("enable_usb_a_device")]
    [InlineData("watchdog_disable")]
    public void Risky_settings_say_why(string name)
    {
        Assert.True(PreferenceCatalog.TryGet(name, out var p));
        Assert.NotEqual("", p.Risk);
    }

    [Fact]
    public void Audited_QMP_control_metadata_is_carried_exactly()
    {
        Assert.True(PreferenceCatalog.TryGet("sip_puff_threshold_soft", out var soft));
        Assert.Equal(PreferenceEditor.Integer, soft.Editor);
        Assert.Equal("8", soft.Default);
        Assert.Equal(5, soft.Minimum);
        Assert.Equal(100, soft.Maximum);

        Assert.True(PreferenceCatalog.TryGet("mouse_response_curve", out var curve));
        Assert.Equal(PreferenceEditor.Choice, curve.Editor);
        Assert.Equal(new[] { "0", "1", "2" }, curve.Options);

        Assert.True(PreferenceCatalog.TryGet("bluetooth_device_mode", out var bt));
        Assert.Equal(
            new[] { "none", "keyboard", "game_pad", "mouse", "combo", "joystick", "ssp" },
            bt.Options);

        // Computed from the four direction sliders, so QMP gives it no range.
        Assert.True(PreferenceCatalog.TryGet("deflection_multiplier_up", out var mult));
        Assert.Equal(PreferenceEditor.Integer, mult.Editor);
        Assert.Equal("140", mult.Default);
        Assert.Null(mult.Minimum);
        Assert.Null(mult.Maximum);
    }

    [Fact]
    public void Entries_are_grouped_in_category_display_order()
    {
        var seen = new List<string>();
        foreach (var p in PreferenceCatalog.All)
            if (seen.Count == 0 || seen[^1] != p.Category)
                seen.Add(p.Category);

        Assert.Equal(PreferenceCatalog.Categories, seen);
    }

    static string Entry(string body) => "[{" + body + "}]";

    const string Good =
        "\"name\":\"a\",\"label\":\"A\",\"category\":\"Advanced\",\"editor\":\"text\",\"source\":\"s\"";

    [Fact]
    public void Well_formed_fixture_parses()
    {
        var one = Assert.Single(PreferenceCatalog.Parse(Entry(Good)));
        Assert.Equal("a", one.Name);
        Assert.Equal(PreferenceEditor.Text, one.Editor);
        Assert.False(one.ModeOverride);
        Assert.Equal("", one.Risk);
    }

    [Theory]
    // Not an array.
    [InlineData("{}")]
    // Missing or empty required fields.
    [InlineData("[{\"label\":\"A\",\"category\":\"Advanced\",\"editor\":\"text\",\"source\":\"s\"}]")]
    [InlineData("[{\"name\":\"a\",\"category\":\"Advanced\",\"editor\":\"text\",\"source\":\"s\"}]")]
    [InlineData("[{\"name\":\"a\",\"label\":\"A\",\"editor\":\"text\",\"source\":\"s\"}]")]
    [InlineData("[{\"name\":\"a\",\"label\":\"A\",\"category\":\"Advanced\",\"editor\":\"text\"}]")]
    [InlineData("[{\"name\":\"a\",\"label\":\"A\",\"category\":\"Advanced\",\"editor\":\"text\",\"source\":\"\"}]")]
    // Unknown category and unknown editor kind.
    [InlineData("[{\"name\":\"a\",\"label\":\"A\",\"category\":\"Nowhere\",\"editor\":\"text\",\"source\":\"s\"}]")]
    [InlineData("[{\"name\":\"a\",\"label\":\"A\",\"category\":\"Advanced\",\"editor\":\"slider\",\"source\":\"s\"}]")]
    // A typo in a field name must not be swallowed.
    [InlineData("[{\"name\":\"a\",\"label\":\"A\",\"category\":\"Advanced\",\"editor\":\"text\",\"source\":\"s\",\"minimun\":1}]")]
    public void Malformed_metadata_throws(string json)
    {
        Assert.Throws<InvalidOperationException>(() => PreferenceCatalog.Parse(json));
    }

    [Fact]
    public void Duplicate_names_throw()
    {
        var json = "[{" + Good + "},{" + Good + "}]";
        Assert.Throws<InvalidOperationException>(() => PreferenceCatalog.Parse(json));
    }

    [Theory]
    // Bounds on something that is not an integer.
    [InlineData("\"name\":\"a\",\"label\":\"A\",\"category\":\"Advanced\",\"editor\":\"text\",\"source\":\"s\",\"minimum\":1")]
    // Minimum above maximum.
    [InlineData("\"name\":\"a\",\"label\":\"A\",\"category\":\"Advanced\",\"editor\":\"integer\",\"source\":\"s\",\"minimum\":9,\"maximum\":2")]
    // Default outside the bounds.
    [InlineData("\"name\":\"a\",\"label\":\"A\",\"category\":\"Advanced\",\"editor\":\"integer\",\"source\":\"s\",\"minimum\":0,\"maximum\":5,\"default\":\"9\"")]
    // Integer default that is not a whole number.
    [InlineData("\"name\":\"a\",\"label\":\"A\",\"category\":\"Advanced\",\"editor\":\"integer\",\"source\":\"s\",\"default\":\"lots\"")]
    // Bounds that are not whole numbers.
    [InlineData("\"name\":\"a\",\"label\":\"A\",\"category\":\"Advanced\",\"editor\":\"integer\",\"source\":\"s\",\"minimum\":1.5")]
    public void Invalid_bounds_throw(string body)
    {
        Assert.Throws<InvalidOperationException>(() => PreferenceCatalog.Parse(Entry(body)));
    }

    [Theory]
    // A choice with no options at all.
    [InlineData("\"name\":\"a\",\"label\":\"A\",\"category\":\"Advanced\",\"editor\":\"choice\",\"source\":\"s\"")]
    // A choice with an empty option list.
    [InlineData("\"name\":\"a\",\"label\":\"A\",\"category\":\"Advanced\",\"editor\":\"choice\",\"source\":\"s\",\"options\":[]")]
    // Options on something that is not a choice.
    [InlineData("\"name\":\"a\",\"label\":\"A\",\"category\":\"Advanced\",\"editor\":\"text\",\"source\":\"s\",\"options\":[\"x\"]")]
    // A default that is not one of the options.
    [InlineData("\"name\":\"a\",\"label\":\"A\",\"category\":\"Advanced\",\"editor\":\"choice\",\"source\":\"s\",\"options\":[\"x\"],\"default\":\"y\"")]
    // A toggle that does not serialize as 0 or 1.
    [InlineData("\"name\":\"a\",\"label\":\"A\",\"category\":\"Advanced\",\"editor\":\"toggle\",\"source\":\"s\",\"default\":\"yes\"")]
    public void Invalid_options_throw(string body)
    {
        Assert.Throws<InvalidOperationException>(() => PreferenceCatalog.Parse(Entry(body)));
    }
}
