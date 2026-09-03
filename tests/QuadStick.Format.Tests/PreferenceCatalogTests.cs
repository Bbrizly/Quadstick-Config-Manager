using System.Linq;
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
        // Someone on an older QuadStick needs to know the name is new. The
        // firmware number is evidence for whoever maintains the catalog and
        // lives in Source; the person reading the app is told in their words.
        Assert.Contains("Newer QuadSticks", p.Description, StringComparison.Ordinal);
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

        // A whole number with no bounds: the device reads it with atoi, and no
        // source states a range, so the default is recorded and nothing else.
        Assert.True(PreferenceCatalog.TryGet("bluetooth_throttle", out var throttle));
        Assert.Equal(PreferenceEditor.Integer, throttle.Editor);
        Assert.Equal("5", throttle.Default);
        Assert.Null(throttle.Minimum);
        Assert.Null(throttle.Maximum);
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
    // an update. The description names every value and says that out loud.
    //
    // It used to stay text, because a choice raises an Error on any off list
    // value and that would turn a valid file for a newer QuadStick red. It is a
    // choice now, with firmwareMayAddMore carrying that concern instead: the
    // eight known values get plain-language names, and a ninth is a warning.
    // People asked for the mode by console name, not by number.
    [Fact]
    public void USB_emulation_mode_documents_every_value()
    {
        Assert.True(PreferenceCatalog.TryGet("enable_DS3_emulation", out var p));
        Assert.Equal(PreferenceEditor.Choice, p.Editor);
        Assert.Equal(new[] { "0", "1", "2", "3", "4", "5", "6", "7" }, p.Options);
        Assert.True(p.FirmwareMayAddMore);
        // The meanings are on the options themselves, which is where somebody
        // picking one reads them. Listing all eight again in the description
        // was the same text twice, once in a place nobody could act on.
        var listed = string.Join(" | ", p.OptionLabels);
        foreach (var meaning in new[]
                 {
                     "QuadStick", "DualShock 3", "x360ce", "Xbox 360",
                     "Nintendo Switch Pro Controller", "no USB drive", "wireless",
                 })
            Assert.Contains(meaning, listed, StringComparison.Ordinal);
        // A number means something else on an older QuadStick, and that is a
        // warning, so it is in the risk note where a warning belongs.
        Assert.Contains("older QuadSticks", p.Risk, StringComparison.Ordinal);
    }

    // The console name is the thing somebody is choosing; the number is only
    // how the device spells it. Every option carries one.
    [Fact]
    public void USB_emulation_mode_names_every_value_in_plain_words()
    {
        Assert.True(PreferenceCatalog.TryGet("enable_DS3_emulation", out var p));
        Assert.Equal(p.Options.Count, p.OptionLabels.Count);
        Assert.Equal("Nintendo Switch Pro Controller, no USB drive", p.LabelForOption("5"));
        // Mode 4 also carries the name QMP puts on it, because that is the
        // phrase somebody arrives with. Joystick.c:625 calls the same value
        // "boot in PS4 mode" in the firmware's own comment.
        Assert.Equal("DualShock 4, for a PS4 (QMP's Boot in PS4 Mode)", p.LabelForOption("4"));
        // A value the catalog has no word for reads back as itself.
        Assert.Equal("8", p.LabelForOption("8"));
    }

    // Drew's whole reason for asking was to stop opening QMP, so somebody
    // arrives holding QMP's words for these settings. The catalog has to answer
    // to those words, not only to the file's token.
    //
    // They live in their own field rather than inside the description. "QMP
    // calls this the joystick's range of motion" was half of a sentence that
    // was supposed to say what the setting does, on a screen that already had
    // too many words on it.
    [Theory]
    [InlineData("sip_puff_threshold", "high threshold")]
    [InlineData("sip_puff_delay_soft", "Low Threshold Delay")]
    [InlineData("titan_two", "Titan 2 PS4 flag")]
    [InlineData("enable_usb_a_host", "USB-A Host Mode")]
    [InlineData("enable_DS3_emulation", "Boot in PS4 Mode")]
    public void A_setting_answers_to_the_name_QMP_shows(string name, string qmpWords)
    {
        Assert.True(PreferenceCatalog.TryGet(name, out var p));
        Assert.Contains(qmpWords, p.AlsoCalled, StringComparison.Ordinal);
    }

    // "Joystick sensitivity" is not a setting. QMP-4 spells it as four range
    // sliders and a dead zone, and its own label says outright that larger
    // numbers are less sensitive. Somebody arriving with that word has to land
    // somewhere, so every control it collapses into carries it.
    [Theory]
    [InlineData("joystick_deflection_maximum")]
    [InlineData("joystick_deflection_minimum")]
    [InlineData("deflection_multiplier_up")]
    [InlineData("deflection_multiplier_down")]
    [InlineData("deflection_multiplier_left")]
    [InlineData("deflection_multiplier_right")]
    public void Every_joystick_range_control_says_which_way_is_less_sensitive(string name)
    {
        Assert.True(PreferenceCatalog.TryGet(name, out var p));
        Assert.Contains("sensitive", p.Description, StringComparison.OrdinalIgnoreCase);
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

    // These two were text while what their values MEAN was unsettled, and the
    // editor got dragged along with it. The two questions are separate.
    // Configuration.c:653-681 hands every preference but the two bluetooth
    // keyword tables and bluetooth_remote_address to atoi, so a whole number
    // is what the device reads either way. What stays unclaimed is the range
    // and the list of values, and that is what this pins.
    [Theory]
    [InlineData("enable_usb_a_device")]
    [InlineData("debug")]
    public void Disputed_settings_claim_no_range_and_no_option_list(string name)
    {
        Assert.True(PreferenceCatalog.TryGet(name, out var p));
        Assert.Equal(PreferenceEditor.Integer, p.Editor);
        Assert.Null(p.Minimum);
        Assert.Null(p.Maximum);
        Assert.Empty(p.Options);
    }

    // The one preference the device keeps as a string. Everything else on this
    // page is a number, so a plain text box anywhere else is a bug.
    [Fact]
    public void Only_the_bluetooth_address_is_free_text()
    {
        var text = PreferenceCatalog.All
            .Where(p => p.Editor == PreferenceEditor.Text)
            .Select(p => p.Name)
            .ToArray();
        Assert.Equal(new[] { "bluetooth_remote_address" }, text);
    }

    // A setting with no words under it is a control nobody can act on, and
    // QMP's answer to that was a tooltip. Every one of the sixty one says
    // what it does.
    [Fact]
    public void Every_setting_says_what_it_does()
    {
        foreach (var p in PreferenceCatalog.All)
            Assert.False(string.IsNullOrWhiteSpace(p.Description),
                $"{p.Name} has no description.");
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

    // Drew Redepenning, who sets QuadSticks up for patients, asked for four
    // groups that open on the settings he actually changes. Thirteen joystick
    // settings buried the two, and the names of two of them described the
    // firmware rather than the control.
    [Theory]
    [InlineData("Joystick", "joystick_deflection_maximum", "joystick_deflection_minimum")]
    [InlineData("Sip and puff", "sip_puff_threshold_soft", "sip_puff_threshold",
                "sip_puff_delay_soft", "sip_puff_delay_hard")]
    [InlineData("Bluetooth", "bluetooth_device_mode", "bluetooth_authentication_mode",
                "bluetooth_connection_mode")]
    [InlineData("USB and compatibility", "enable_usb_a_host", "enable_DS3_emulation", "titan_two")]
    public void A_group_opens_on_the_settings_people_actually_change(string category, params string[] expected)
    {
        var open = PreferenceCatalog.All
            .Where(d => d.Category == category && !d.Advanced)
            .Select(d => d.Name)
            .ToArray();
        // Order matters: it is the order they appear down the group.
        Assert.Equal(expected, open);
    }

    // Every other category is one short list already, so nothing in it folds.
    [Fact]
    public void Only_the_four_crowded_groups_fold_anything()
    {
        var folded = PreferenceCatalog.All.Where(d => d.Advanced).Select(d => d.Category).Distinct();
        Assert.Equal(
            new[] { "Joystick", "Sip and puff", "Bluetooth", "USB and compatibility" }.OrderBy(x => x),
            folded.OrderBy(x => x));
    }

    // "Joystick full deflection" and "Hard sip/puff threshold" are the
    // firmware's words. The second one is actively misleading: it is the
    // threshold the ordinary sip and puff inputs use, not a special hard one.
    [Theory]
    [InlineData("joystick_deflection_maximum", "Joystick sensitivity")]
    [InlineData("sip_puff_threshold", "Normal sip/puff threshold")]
    public void The_two_confusing_names_read_the_way_people_say_them(string name, string label)
    {
        Assert.True(PreferenceCatalog.TryGet(name, out var def));
        Assert.Equal(label, def.Label);
    }

    // Four bare numbers in a dropdown told nobody anything. The meanings are
    // Table 2-3 of the RN-42's command reference, which is what Bluetooth.c:267
    // sends them to as its SA setting.
    [Fact]
    public void Every_bluetooth_pairing_number_says_what_it_does()
    {
        Assert.True(PreferenceCatalog.TryGet("bluetooth_authentication_mode", out var def));
        Assert.Equal(new[] { "0", "1", "2", "4" }, def.Options);
        Assert.Equal(def.Options.Count, def.OptionLabels.Count);
        foreach (var o in def.Options)
            Assert.NotEqual(o, def.LabelForOption(o));
    }

    // The side tube's long sip is how somebody reaches their other game files,
    // and that is the sentence a clinician needs beside this delay.
    [Fact]
    public void The_hard_delay_says_it_is_how_you_switch_game_files()
    {
        Assert.True(PreferenceCatalog.TryGet("sip_puff_delay_hard", out var def));
        Assert.Contains("side tube", def.Description);
        Assert.Contains("game file", def.Description);
    }
}
