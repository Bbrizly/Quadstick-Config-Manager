using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

// The rules that came out of reading firmware 2373 against 1476. Every one of
// them exists because the device does something a file cannot show you.
public class Firmware2373RuleTests
{
    const string Head = "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\n";

    static List<Issue> Load(string csv) => ProfileFile.Load(csv).Issues;

    // "both" is a real channel on 2373, so it must stop being reported as a
    // word the device cannot match.
    [Fact]
    public void Both_is_a_channel_the_device_knows()
    {
        var issues = Load("Profile Name,,Solo\ngame.csv\nOutputs,Function,both\nx,normal,lip\n");

        Assert.DoesNotContain(issues, i => i.Message.Contains("does not match"));
    }

    // But an older QuadStick drops the Bluetooth half of that mode without a
    // word, so the app has to supply the word.
    [Fact]
    public void Both_says_it_needs_recent_firmware()
    {
        var issues = Load("Profile Name,,Solo\ngame.csv\nOutputs,Function,both\nx,normal,lip\n");

        var issue = Assert.Single(issues, i => i.Message.Contains("firmware from 2025"));
        Assert.Equal(Severity.Warning, issue.Severity);
        Assert.Contains("USB only", issue.Message);
    }

    [Fact]
    public void A_channel_the_device_really_cannot_match_is_still_reported()
    {
        var issues = Load("Profile Name,,Solo\ngame.csv\nOutputs,Function,wibble\nx,normal,lip\n");

        Assert.Contains(issues, i => i.Message.Contains("does not match"));
    }

    // A setting name is a legal output cell. The binding-slot count always knew
    // that; the output check did not, so a row the device reads perfectly was
    // called an undocumented name.
    [Theory]
    [InlineData("titan_two")]
    [InlineData("enable_usb_a_host")]
    [InlineData("usb_1_dead_zone")]
    public void A_setting_name_in_the_output_column_is_not_undocumented(string name)
    {
        var issues = Load(Head + $"{name},,1\n");

        Assert.DoesNotContain(issues, i => i.Message.Contains("not a documented output name"));
    }

    // Five settings the firmware parses, stores, and never reads.
    [Theory]
    [InlineData("enable_auto_zero", "1")]
    [InlineData("usb_2_dead_zone", "20")]
    [InlineData("joystick_warning", "400")]
    [InlineData("joystick_alarm", "500")]
    [InlineData("watchdog_disable", "1")]
    public void A_setting_the_device_ignores_says_so(string name, string value)
    {
        var issues = Load($"Preferences\nprefs.csv\nName,Value\n{name},{value}\n");

        var issue = Assert.Single(issues, i => i.Message.Contains("does nothing on current firmware"));
        Assert.Equal(Severity.Warning, issue.Severity);
        Assert.Contains(name, issue.Message);
    }

    // Same on a mode row, where the value lives in column C.
    [Fact]
    public void A_dead_setting_as_a_mode_override_says_so_too()
    {
        var issues = Load(Head + "enable_auto_zero,,1\n");

        Assert.Contains(issues, i =>
            i.Cell == "C4" && i.Message.Contains("does nothing on current firmware"));
    }

    // Two values the device still accepts and now acts on differently. The file
    // is unchanged and valid on both firmwares, so nothing in it can show that
    // updating the QuadStick changed what it does. Only the app can say it.
    //
    // enable_DS3_emulation 5 was "PC only, no joystick" on 1476
    // (Descriptors.c:1523) and is the Nintendo Switch Pro Controller on 2373
    // (Descriptors.c:1574), so the device stops being the controller the game
    // expects.
    [Fact]
    public void Emulation_mode_five_says_the_two_firmwares_disagree()
    {
        var issues = Load("Preferences\nprefs.csv\nName,Value\nenable_DS3_emulation,5\n");

        var issue = Assert.Single(issues, i => i.Message.Contains("means something different"));
        Assert.Equal(Severity.Warning, issue.Severity);
        Assert.Contains("Nintendo", issue.Message);
    }

    // A computer can only reach the QuadStick's files while the emulation it is
    // running declares a mass-storage interface, and four of the eight do not:
    // DS3_t (mode 1), NS_t (5), Mode6_t (6) and PS4_t (7) have no MS_Interface,
    // while PS3_t (0), X360CE_t (2), X360_t (3) and CM_t (4 on a computer) do.
    // Put one of the four in the file the device boots with and there is no way
    // to edit it back: recovery is the physical force-erase.
    [Theory]
    [InlineData("1")]
    [InlineData("5")]
    [InlineData("6")]
    [InlineData("7")]
    public void An_emulation_with_no_drive_cannot_go_in_the_device_preferences(string mode)
    {
        var issues = Load($"Preferences\nprefs.csv\nName,Value\nenable_DS3_emulation,{mode}\n");

        var issue = Assert.Single(issues, i => i.Message.Contains("access to the QuadStick's drive"));
        Assert.Equal(Severity.Error, issue.Severity);
        Assert.Contains("force-erase", issue.Message);
    }

    // default.csv is the other file the device comes up on, so the same rule.
    [Fact]
    public void An_emulation_with_no_drive_cannot_go_in_default_csv()
    {
        var issues = Load("Profile Name,,Solo\ndefault.csv\nOutputs,Function,usb\nenable_DS3_emulation,,6\n");

        var issue = Assert.Single(issues, i => i.Message.Contains("access to the QuadStick's drive"));
        Assert.Equal(Severity.Error, issue.Severity);
        Assert.Equal("C4", issue.Cell);
    }

    // In a game profile it is survivable: the device boots back into
    // default.csv and the files come back. Worth saying, not worth blocking,
    // because playing on a Switch is exactly what mode 5 is for.
    [Fact]
    public void An_emulation_with_no_drive_is_only_a_warning_in_a_game_profile()
    {
        var issues = Load(Head + "enable_DS3_emulation,,5\n");

        var issue = Assert.Single(issues, i => i.Message.Contains("access to the QuadStick's drive"));
        Assert.Equal(Severity.Warning, issue.Severity);
        Assert.DoesNotContain(issues, i => i.Severity == Severity.Error);
    }

    // The four that keep the drive say nothing at all, including 4, which
    // answers a computer with CM_t and a PS4 with PS4_t.
    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("4")]
    public void An_emulation_that_keeps_the_drive_is_not_worth_a_word(string mode)
    {
        var issues = Load($"Preferences\nprefs.csv\nName,Value\nenable_DS3_emulation,{mode}\n");

        Assert.DoesNotContain(issues, i => i.Message.Contains("access to the QuadStick's drive"));
    }

    // USB emulation has gained values with almost every firmware, so a number
    // this app has never heard of is more likely a QuadStick newer than the app
    // than a mistake. It is written out untouched and the install is not blocked.
    [Fact]
    public void An_emulation_value_the_app_does_not_know_still_installs()
    {
        var issues = Load("Preferences\nprefs.csv\nName,Value\nenable_DS3_emulation,8\n");

        var issue = Assert.Single(issues, i => i.Message.Contains("not a value this app knows"));
        Assert.Equal(Severity.Warning, issue.Severity);
        Assert.DoesNotContain(issues, i => i.Severity == Severity.Error);
    }

    // joystick_deflection_minimum 0 meant no dead zone on 1476
    // (DataFlow.c:978, a plain multiply). 2373 substitutes 129 raw counts
    // instead (DataFlow.c:997), so a stick set to move at the slightest touch
    // gains a dead zone the file never asked for.
    [Fact]
    public void A_zero_dead_zone_says_the_two_firmwares_disagree()
    {
        var issues = Load("Preferences\nprefs.csv\nName,Value\njoystick_deflection_minimum,0\n");

        var issue = Assert.Single(issues, i => i.Message.Contains("means something different"));
        Assert.Equal(Severity.Warning, issue.Severity);
        Assert.Contains("129", issue.Message);
    }

    // Same on a mode row, where the value lives in column C.
    [Fact]
    public void A_value_the_firmwares_disagree_on_says_so_on_a_mode_row_too()
    {
        var issues = Load(Head + "enable_DS3_emulation,,5\n");

        Assert.Contains(issues, i =>
            i.Cell == "C4" && i.Message.Contains("means something different"));
    }

    // Only the one value changed meaning. Every other value means what it always
    // meant, and warning on those would train people to ignore the warning.
    [Theory]
    [InlineData("enable_DS3_emulation", "4")]
    [InlineData("joystick_deflection_minimum", "8")]
    public void A_value_the_two_firmwares_agree_on_is_not_worth_a_word(string name, string value)
    {
        var issues = Load($"Preferences\nprefs.csv\nName,Value\n{name},{value}\n");

        Assert.DoesNotContain(issues, i => i.Message.Contains("means something different"));
    }

    // Writing 0 asks for nothing, which is what the device does anyway.
    [Fact]
    public void A_dead_setting_left_at_zero_is_not_worth_a_word()
    {
        var issues = Load("Preferences\nprefs.csv\nName,Value\nenable_auto_zero,0\n");

        Assert.DoesNotContain(issues, i => i.Message.Contains("does nothing on current firmware"));
    }

    // The catalog and Vocab have to agree in BOTH directions, or the editor
    // offers a name in a mode sheet that the device will not honor there.
    // PreferenceCatalogTests holds the Vocab-to-catalog half. This is the other
    // one: a setting the catalog calls standalone-only must not be in the set
    // the mode-sheet picker draws from.
    //
    // Only digital_out_1..4 are standalone-only today, and they are excluded
    // for a second reason as well: they match output_keywords first on the
    // device, so a mode sheet reads them as outputs and never as settings.
    [Fact]
    public void A_standalone_only_setting_is_never_a_mode_override()
    {
        var standaloneOnly = PreferenceCatalog.All.Where(d => !d.ModeOverride).Select(d => d.Name).ToList();

        Assert.NotEmpty(standaloneOnly); // the rule would be vacuous otherwise
        foreach (var name in standaloneOnly)
            Assert.False(Vocab.PreferenceOverrides.Contains(name),
                $"{name} is standalone only in the catalog but Vocab offers it as a mode override.");
    }

    // The per-direction thresholds have to keep their order for the same reason
    // the shared ones do, and for one worse one: the firmware divides by the
    // gap between them on every scan, with no guard.
    [Fact]
    public void Two_equal_sip_thresholds_are_reported_as_a_divide_by_zero()
    {
        var issues = Load(
            "Preferences\nprefs.csv\nName,Value\nsip_threshold_soft,40\nsip_threshold,40\n");

        var issue = Assert.Single(issues, i => i.Message.Contains("divide by zero"));
        Assert.Equal(Severity.Warning, issue.Severity);
        Assert.Contains("sip", issue.Message);
    }

    [Fact]
    public void Sip_thresholds_out_of_order_are_reported()
    {
        var issues = Load(
            "Preferences\nprefs.csv\nName,Value\nsip_threshold,60\nsip_maximum,50\n");

        Assert.Contains(issues, i => i.Message.Contains("thresholds run into each other"));
    }

    // A zero means "use the shared value", so the comparison has to be made
    // against that, not against the zero.
    [Fact]
    public void A_zero_falls_back_to_the_shared_value_before_comparing()
    {
        // puff_threshold_soft is 0, so the effective soft threshold is the
        // shared 40, which collides with the puff-only hard threshold of 40.
        var issues = Load(
            "Preferences\nprefs.csv\nName,Value\nsip_puff_threshold_soft,40\npuff_threshold_soft,0\npuff_threshold,40\n");

        Assert.Contains(issues, i => i.Message.Contains("divide by zero"));
    }

    // A mode sets preferences from its own rows too, and the device applies
    // them going into the mode. The divide by zero does not care which sheet
    // the numbers came from, so neither can the check.
    [Fact]
    public void Two_equal_sip_thresholds_set_by_a_mode_are_reported_too()
    {
        var issues = Load(Head + "sip_threshold_soft,,40\nsip_threshold,,40\n");

        var issue = Assert.Single(issues, i => i.Message.Contains("divide by zero"));
        Assert.StartsWith("C", issue.Cell); // a mode row keeps its value in column C
    }

    [Fact]
    public void A_mode_that_orders_its_thresholds_properly_passes()
    {
        var issues = Load(Head + "sip_threshold_soft,,10\nsip_threshold,,40\n");

        Assert.DoesNotContain(issues, i => i.Message.Contains("run into each other"));
    }

    // Naming a row the file does not have sends the reader somewhere they
    // cannot go, so the message has to name whichever row carries the number.
    [Fact]
    public void The_warning_names_the_row_that_really_holds_the_value()
    {
        var issues = Load(
            "Preferences\nprefs.csv\nName,Value\nsip_puff_threshold_soft,40\npuff_threshold,40\n");

        var issue = Assert.Single(issues, i => i.Message.Contains("divide by zero"));
        Assert.Contains("sip_puff_threshold_soft", issue.Message);
        Assert.Contains("sip_puff_threshold_soft", issue.Fix);
        // puff_threshold_soft is not in the file, so it must not be the thing
        // the reader is told to change.
        Assert.DoesNotContain("\"puff_threshold_soft\"", issue.Fix);
    }

    // The other value may be sitting on the device, where this app cannot see
    // it, and guessing about it would be worse than saying nothing.
    [Fact]
    public void One_threshold_on_its_own_says_nothing()
    {
        var issues = Load("Preferences\nprefs.csv\nName,Value\nsip_threshold,40\n");

        Assert.DoesNotContain(issues, i => i.Message.Contains("run into each other"));
        Assert.DoesNotContain(issues, i => i.Message.Contains("divide by zero"));
    }

    [Fact]
    public void Sip_and_puff_thresholds_that_are_far_enough_apart_pass()
    {
        var issues = Load(
            "Preferences\nprefs.csv\nName,Value\nsip_threshold_soft,10\nsip_threshold,40\nsip_maximum,70\n");

        Assert.DoesNotContain(issues, i => i.Message.Contains("run into each other"));
    }

    // Restarting the device is legal, and on the push switch it can leave the
    // QuadStick in its firmware loader until somebody unplugs it.
    [Fact]
    public void Resetting_the_device_from_push_names_the_firmware_loader()
    {
        var issues = Load(Head + "reset_quadstick,normal,push\n");

        var issue = Assert.Single(issues, i => i.Message.Contains("firmware loader"));
        Assert.Equal(Severity.Warning, issue.Severity);
    }

    [Fact]
    public void Resetting_the_device_from_anything_else_is_a_milder_word()
    {
        var issues = Load(Head + "reset_quadstick,normal,mp_center_sip\n");

        Assert.Contains(issues, i => i.Message.Contains("restarts the QuadStick"));
        Assert.DoesNotContain(issues, i => i.Message.Contains("firmware loader"));
    }

    // 2373 gates the USB mouse and keyboard reports on the channel and leaves
    // the gamepad ungated, so half a Bluetooth mode quietly stops on a cable.
    // Four profiles in the catalog are a "Mouse Mode" on bluetooth.
    [Theory]
    [InlineData("bluetooth")]
    [InlineData("none")]
    public void A_mode_off_usb_says_its_mouse_and_keyboard_rows_go_quiet(string channel)
    {
        var issues = Load($"Profile Name,,Solo\ngame.csv\nOutputs,Function,{channel}\n"
            + "mouse_left,normal,mp_left_sip\nkb_a,normal,lip\n");

        var issue = Assert.Single(issues, i => i.Message.Contains("mouse and keyboard over USB"));
        Assert.Equal(Severity.Warning, issue.Severity);
        Assert.Equal("C3", issue.Cell);
        Assert.Contains("2 mouse or keyboard rows, the first on row 4", issue.Message);
    }

    [Theory]
    [InlineData("usb")]
    [InlineData("both")]
    public void A_mode_that_keeps_usb_says_nothing_about_it(string channel)
    {
        var issues = Load($"Profile Name,,Solo\ngame.csv\nOutputs,Function,{channel}\n"
            + "mouse_left,normal,mp_left_sip\n");

        Assert.DoesNotContain(issues, i => i.Message.Contains("mouse and keyboard over USB"));
    }

    [Fact]
    public void A_bluetooth_mode_with_no_mouse_or_keyboard_row_says_nothing()
    {
        var issues = Load("Profile Name,,Solo\ngame.csv\nOutputs,Function,bluetooth\nx,normal,lip\n");

        Assert.DoesNotContain(issues, i => i.Message.Contains("mouse and keyboard over USB"));
    }

    // mouse_speed is a preference name, not a mouse output. The device takes
    // the preference branch on it and never reaches the HID gate.
    [Fact]
    public void A_mouse_speed_row_is_not_counted_as_a_mouse_binding()
    {
        var issues = Load("Profile Name,,Solo\ngame.csv\nOutputs,Function,bluetooth\nmouse_speed,,40\n");

        Assert.DoesNotContain(issues, i => i.Message.Contains("mouse and keyboard over USB"));
    }

    // The app was built on the 2017 list, so it told a real user his QuadStick
    // had no setting called "puff_threshold" and that he had been running on
    // defaults for months. 2373 added the six split sip and puff settings and
    // his rows were fine. A wrong name is the worst thing this app can say: it
    // sends somebody to change a file that was already right.
    [Theory]
    [InlineData("sip_threshold_soft")]
    [InlineData("sip_threshold")]
    [InlineData("sip_maximum")]
    [InlineData("puff_threshold_soft")]
    [InlineData("puff_threshold")]
    [InlineData("puff_maximum")]
    public void A_split_sip_or_puff_setting_is_a_real_name_and_draws_no_warning(string name)
    {
        var issues = Load($"Preferences\nprefs.csv\nName,Value\n{name},40\n");

        // Row 4 is the setting. Nothing at all to say about it.
        Assert.Empty(issues.Where(i => i.Cell.EndsWith('4')));
    }

    [Fact]
    public void An_ordinary_binding_says_nothing_about_restarting()
    {
        var issues = Load(Head + "x,normal,push\n");

        Assert.DoesNotContain(issues, i => i.Message.Contains("restarts the QuadStick"));
    }
}
