using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// The preference editor writes device strings. A value it changes on the
// user's behalf is not a cosmetic bug: a QuadStick is how these users move,
// speak and play, and a setting silently clamped or reformatted can leave them
// with hardware that no longer answers. Every test here is about that.
public class PreferenceUiTests
{
    static string[] Said(MainWindow w) =>
        w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();

    static MainWindow ShowPrefs(string rows, out ProfileFile file)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        file = ProfileFile.Load("Preferences\nprefs.csv\nPreference,Value,Units,Description\n" + rows);
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();
        return w;
    }

    static T Cell<T>(MainWindow w, int row) where T : Control =>
        w.GetVisualDescendants().OfType<T>()
            .First(c => AutomationProperties.GetName(c) == $"Setting value for row {row}");

    static bool Has<T>(MainWindow w, int row) where T : Control =>
        w.GetVisualDescendants().OfType<T>()
            .Any(c => AutomationProperties.GetName(c) == $"Setting value for row {row}");

    [AvaloniaFact]
    public void A_toggle_setting_writes_exactly_zero_or_one()
    {
        var w = ShowPrefs("enable_swap_inputs,0\n", out var file);

        var box = Cell<CheckBox>(w, 4);
        Assert.False(box.IsChecked);

        box.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("1", file.GetCell(4, 1));

        box.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("0", file.GetCell(4, 1));

        file.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void A_choice_setting_writes_the_exact_device_token()
    {
        var w = ShowPrefs("bluetooth_device_mode,none\n", out var file);

        var combo = Cell<ComboBox>(w, 4);
        Assert.Equal("none", combo.SelectedItem?.ToString());

        combo.SelectedIndex = 2; // none, keyboard, game_pad
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("game_pad", file.GetCell(4, 1));

        file.Dirty = false;
        w.Close();
    }

    // People pick a console, not a number. The row shows the console name with
    // the device's own number beside it, and what lands in the file is the
    // number on its own: a label reaching the file would be read as 0.
    [AvaloniaFact]
    public void A_choice_setting_shows_plain_words_and_still_writes_the_number()
    {
        var w = ShowPrefs("enable_DS3_emulation,0\n", out var file);

        var combo = Cell<ComboBox>(w, 4);
        var shown = combo.ItemsSource!.Cast<object>().Select(o => o.ToString()!).ToList();
        Assert.Equal("QuadStick (PC and PS3) (0)", shown[0]);
        int xbox360 = shown.FindIndex(t => t.Contains("Xbox 360 controller"));
        Assert.True(xbox360 >= 0);

        combo.SelectedIndex = xbox360;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("3", file.GetCell(4, 1));

        file.Dirty = false;
        w.Close();
    }

    // prefs.csv is what the device boots into, so a mode with no mass-storage
    // interface would take its drive away and the only way back is the physical
    // force-erase. Those four are not in the list at all, and the row says so
    // rather than leaving a short list looking like a bug.
    [AvaloniaFact]
    public void A_file_the_device_boots_into_offers_no_mode_that_hides_the_drive()
    {
        var w = ShowPrefs("enable_DS3_emulation,0\n", out var file);

        var shown = Cell<ComboBox>(w, 4).ItemsSource!.Cast<object>().Select(o => o.ToString()!).ToList();
        Assert.Equal(4, shown.Count);
        foreach (var hidden in new[] { "(1)", "(5)", "(6)", "(7)" })
            Assert.DoesNotContain(shown, t => t.EndsWith(hidden, StringComparison.Ordinal));
        foreach (var safe in new[] { "(0)", "(2)", "(3)", "(4)" })
            Assert.Contains(shown, t => t.EndsWith(safe, StringComparison.Ordinal));

        Assert.Contains(Said(w), t => t.Contains("not offered here"));

        file.Dirty = false;
        w.Close();
    }

    // A game profile is survivable: the device boots back into default.csv and
    // the files come back, so all eight stay on offer there.
    [AvaloniaFact]
    public void A_game_profile_still_offers_every_emulation_mode()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.Load(
            "Profile Name\ngame.csv\nOutput or Function,Function,Input\n"
            + "Preferences\n\nPreference,Value,Units,Description\nenable_DS3_emulation,0\n");
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();
        w.SelectSheetForPreview(1);
        w.UpdateLayout();

        Assert.Equal(8, Cell<ComboBox>(w, 7).ItemsSource!.Cast<object>().Count());

        file.Dirty = false;
        w.Close();
    }

    // A number is a device string, not a display string. Under a number format
    // that spells a minus sign differently, the cell must still get the plain
    // ASCII form the firmware parses.
    [AvaloniaFact]
    public void A_number_setting_writes_plain_digits_whatever_the_local_number_format_is()
    {
        var before = CultureInfo.CurrentCulture;
        var odd = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        odd.NumberFormat.NegativeSign = "~";
        odd.NumberFormat.NumberGroupSeparator = ".";
        CultureInfo.CurrentCulture = odd;
        try
        {
            Assert.Equal("~5", (-5).ToString(CultureInfo.CurrentCulture)); // the culture really is odd

            var w = ShowPrefs("deflection_multiplier_up,140\n", out var file);
            var spinner = Cell<NumericUpDown>(w, 4);
            Assert.Equal(140m, spinner.Value);

            spinner.Value = -5m;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("-5", file.GetCell(4, 1));

            spinner.Value = 1300m;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("1300", file.GetCell(4, 1));

            file.Dirty = false;
            w.Close();
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    // The one rule that matters. A number past the official manager's range, a
    // choice token the catalog has never seen, and a number written in an odd
    // spelling all keep the plain text box, and none of them is touched.
    [AvaloniaFact]
    public void A_value_the_control_cannot_show_stays_raw_and_unchanged()
    {
        var w = ShowPrefs(
            "volume,150\n" +
            "mouse_response_curve,9\n" +
            "brightness,007\n" +
            "enable_swap_inputs,yes\n", out var file);

        foreach (var row in new[] { 4, 5, 6, 7 })
        {
            Assert.False(Has<NumericUpDown>(w, row));
            Assert.False(Has<ComboBox>(w, row));
            Assert.False(Has<CheckBox>(w, row));
            Assert.True(Has<AutoCompleteBox>(w, row));
        }

        Assert.Equal("150", Cell<AutoCompleteBox>(w, 4).Text);
        Assert.Equal("9", Cell<AutoCompleteBox>(w, 5).Text);
        Assert.Equal("007", Cell<AutoCompleteBox>(w, 6).Text);
        Assert.Equal("yes", Cell<AutoCompleteBox>(w, 7).Text);

        // Showing the file did not rewrite a single one of them.
        Assert.Equal("150", file.GetCell(4, 1));
        Assert.Equal("9", file.GetCell(5, 1));
        Assert.Equal("007", file.GetCell(6, 1));
        Assert.Equal("yes", file.GetCell(7, 1));
        Assert.False(file.Dirty);

        w.Close();
    }

    // Units, descriptions, notes past column J, unknown settings, blank
    // annotations and row order all belong to the person who wrote the file.
    [AvaloniaFact]
    public void Editing_one_value_changes_only_that_cell()
    {
        var w = ShowPrefs(
            "volume,40,,how loud the beeps are\n" +
            "some_future_setting,7,,not in any catalog yet\n" +
            ",,,\n" +
            "brightness,75,percent,,,,,,,,keep this note\n", out var file);
        var original = file.ToCsvText();

        Cell<NumericUpDown>(w, 4).Value = 55m;
        Dispatcher.UIThread.RunJobs();

        var after = file.ToCsvText();
        Assert.Equal(original.Replace("volume,40", "volume,55"), after);
        Assert.NotEqual(original, after);

        file.Dirty = false;
        w.Close();
    }

    // Undo has to reach a change made through a friendly control too.
    [AvaloniaFact]
    public void A_typed_control_edit_can_be_undone()
    {
        var w = ShowPrefs("volume,40\n", out var file);

        Cell<NumericUpDown>(w, 4).Value = 55m;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("55", file.GetCell(4, 1));

        Assert.True(file.Undo());
        Assert.Equal("40", file.GetCell(4, 1));

        file.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void Known_settings_get_a_category_heading_that_can_come_back()
    {
        var w = ShowPrefs(
            "volume,40\n" +
            "brightness,75\n" +
            "mouse_speed,100\n" +
            "some_future_setting,7\n" +
            "volume,40\n", out var file);

        var headings = w.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => (AutomationProperties.GetName(t) ?? "").EndsWith(" settings", StringComparison.Ordinal))
            .Select(t => t.Text).ToList();

        // One heading for the volume/brightness run, one for the mouse, and a
        // second "Sound and lights" because the file comes back to it.
        Assert.Equal(new[] { "Sound and lights", "Mouse", "Sound and lights" }, headings);

        file.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void A_known_setting_shows_its_label_its_token_and_what_it_does()
    {
        var w = ShowPrefs("volume,40\n", out var file);

        var texts = w.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? "").ToList();
        Assert.Contains("Speaker volume", texts);                                 // friendly label
        // The catalog's default for volume is QMP-4's 40, where the device's own
        // is 25. The line carries the number without claiming it was read off
        // the hardware, because for other settings the catalog follows the
        // device instead and one sentence cannot name both sources.
        Assert.Contains(texts, t => t.Contains("How loud the QuadStick's own beeps are.", StringComparison.Ordinal)
                                    && t.Contains("usually set to 40", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains("device ships with", StringComparison.Ordinal));
        Assert.Equal("volume", w.GetVisualDescendants().OfType<AutoCompleteBox>()
            .First(b => AutomationProperties.GetName(b) == "Setting name for row 4").Text); // raw token

        file.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void A_risky_setting_says_so_in_words_not_only_in_color()
    {
        var w = ShowPrefs("watchdog_disable,0\n", out var file);

        var risk = w.GetVisualDescendants().OfType<TextBlock>()
            .First(t => (t.Text ?? "").StartsWith("Careful: ", StringComparison.Ordinal));
        Assert.Contains("watchdog", risk.Text!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"Careful, Disable the watchdog: {risk.Text!["Careful: ".Length..]}",
            AutomationProperties.GetName(risk));

        file.Dirty = false;
        w.Close();
    }

    [AvaloniaFact]
    public void An_unknown_setting_keeps_the_plain_row()
    {
        var w = ShowPrefs("some_future_setting,7\n", out var file);

        Assert.True(Has<AutoCompleteBox>(w, 4));
        Assert.False(Has<NumericUpDown>(w, 4));
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<TextBlock>(),
            t => (AutomationProperties.GetName(t) ?? "").EndsWith(" settings", StringComparison.Ordinal));

        file.Dirty = false;
        w.Close();
    }

    // Every control the catalog puts on a row must be reachable and named. No
    // action here hides behind a right-click.
    [AvaloniaFact]
    public void Every_preference_control_is_named_and_keyboard_reachable()
    {
        var w = ShowPrefs(
            "volume,40\n" +
            "enable_swap_inputs,0\n" +
            "bluetooth_device_mode,none\n", out var file);

        foreach (Control c in new Control[]
                 { Cell<NumericUpDown>(w, 4), Cell<CheckBox>(w, 5), Cell<ComboBox>(w, 6) })
        {
            Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(c)));
            Assert.True(c.Focusable);
            Assert.True(c.IsEnabled);
        }

        var exact = w.GetVisualDescendants().OfType<Button>()
            .First(b => AutomationProperties.GetName(b) == "Type an exact value for Speaker volume");
        Assert.True(exact.Focusable);

        file.Dirty = false;
        w.Close();
    }

    // The friendly controls only offer what the official manager offers, and
    // the device takes more than that, so the plain box is always one button
    // away and it writes what you type.
    [AvaloniaFact]
    public void Typing_an_exact_value_swaps_the_control_for_a_plain_box()
    {
        var w = ShowPrefs("volume,40\n", out var file);

        Ui.Click(w.GetVisualDescendants().OfType<Button>()
            .First(b => AutomationProperties.GetName(b) == "Type an exact value for Speaker volume"));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        Assert.False(Has<NumericUpDown>(w, 4));
        var box = Cell<AutoCompleteBox>(w, 4);
        Assert.Equal("40", box.Text);

        box.Text = "150";
        box.RaiseEvent(new RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("150", file.GetCell(4, 1));

        file.Dirty = false;
        w.Close();
    }

    // The friendly label sits above the raw token, so the Setting column got
    // taller and wider. The Value column still has to start under its header.
    [AvaloniaFact]
    public void A_labelled_row_keeps_the_value_column_under_its_header()
    {
        var w = ShowPrefs("volume,40\n", out var file);

        var swatch = w.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Text == "Value").FindAncestorOfType<Border>()!;
        var cell = Cell<NumericUpDown>(w, 4).FindAncestorOfType<Border>()!;
        double headerX = swatch.TranslatePoint(new Avalonia.Point(0, 0), w)!.Value.X;
        double cellX = cell.TranslatePoint(new Avalonia.Point(0, 0), w)!.Value.X;
        Assert.Equal(cellX, headerX);

        file.Dirty = false;
        w.Close();
    }

    // Typing a different setting name has to bring that setting's control with
    // it, not leave the row wearing the old one.
    [AvaloniaFact]
    public void Renaming_a_setting_brings_its_own_control()
    {
        var w = ShowPrefs("volume,40\n", out var file);
        Assert.True(Has<NumericUpDown>(w, 4));

        var nameBox = w.GetVisualDescendants().OfType<AutoCompleteBox>()
            .First(b => AutomationProperties.GetName(b) == "Setting name for row 4");
        nameBox.Text = "enable_swap_inputs";
        nameBox.RaiseEvent(new RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        // 40 is not an on/off value, so the row falls back to plain text
        // rather than deciding for the user whether that means on or off.
        Assert.Equal("40", file.GetCell(4, 1));
        Assert.False(Has<NumericUpDown>(w, 4));
        Assert.False(Has<CheckBox>(w, 4));
        Assert.True(Has<AutoCompleteBox>(w, 4));

        file.Dirty = false;
        w.Close();
    }

    // A mode row's value goes through a bare atoi, so a dropdown of words must
    // never appear on one: a click would write "keyboard" into a cell the
    // device reads as 0. A dropdown of numbers is safe there, and picking the
    // console for one mode is exactly what people asked to be able to do.
    [AvaloniaFact]
    public void A_numbered_choice_is_pickable_on_a_mode_row_and_a_worded_one_is_not()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.Load(
            "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\n" +
            "enable_DS3_emulation,,0\n" +
            "bluetooth_device_mode,,none\n");
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();

        Assert.True(Has<ComboBox>(w, 4));           // numbers: safe
        Assert.False(Has<ComboBox>(w, 5));          // words: stays raw
        Assert.True(Has<AutoCompleteBox>(w, 5));

        var combo = Cell<ComboBox>(w, 4);
        combo.SelectedIndex = 5;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("5", file.GetCell(4, 2));      // column C, the one the device reads

        file.Dirty = false;
        w.Close();
    }

    // An infrared sheet is the same shape as a preferences sheet, so it shares
    // this editor, and it was borrowing the preferences words along with the
    // layout: a hex code sat under "Value" with a "Units" column beside it.
    // Reading a sheet correctly and then calling it something else is the same
    // failure as not reading it, one step later.
    [AvaloniaFact]
    public void An_infrared_sheet_is_labelled_as_commands_not_as_settings()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.Load(
            "Infrared,Samsung Most Models - Set #: 595,Comments\n" +
            ",http://irdb.globalcache.com/\n" +
            "Command Name,Hex Code\n" +
            "ir_tv_on_off,0000 006D 0000 0022 00AA 00AA\n");
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout();

        var text = w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        Assert.Contains("Command", text);
        Assert.Contains("Hex code", text);
        Assert.DoesNotContain("Setting", text);
        Assert.DoesNotContain("Units", text);
        // The rows follow the header, so the same words are read aloud.
        var names = w.GetVisualDescendants().OfType<Control>()
            .Select(AutomationProperties.GetName).Where(n => !string.IsNullOrEmpty(n)).ToList();
        Assert.Contains(names, n => n!.StartsWith("Command name for row"));
        Assert.Contains(names, n => n!.StartsWith("Hex code for row"));
        Assert.DoesNotContain(names, n => n!.StartsWith("Setting name for row"));

        file.Dirty = false;
        w.Close();
    }
}
