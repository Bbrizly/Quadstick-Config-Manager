using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.Format;

namespace QuadStick.App;

// Tuning the QuadStick itself. The file behind this page is prefs.csv on the
// device, which is the device's own settings and applies to every profile on
// it, so nothing here is a profile edit and nothing here goes through Install.
//
// The QuadStick Manager Program does this over two tabs (Joystick and Misc)
// with the controls identified only by position and tooltip. This is one page,
// grouped by what the setting does, with the explanation in text a screen
// reader can read.
//
// Two things QMP does that this deliberately does not:
//
// It turns four direction sliders into six stored keys on every save, and the
// arithmetic is lossy in both directions: load the shipped defaults, touch
// nothing, press Save, and deflection_multiplier_up goes 140 -> 17 -> 147.
// That is somebody's cursor aim moving because they opened a window. Here the
// stored keys are the controls, so a value nobody touched is written back
// exactly as it was read, or not written at all.
//
// It also drags neighbouring sliders around to keep its own invariants, with
// no word to the user. Here a value out of range is refused, never nudged, and
// the row that holds it says so.
public partial class MainWindow
{
    string? _deviceRoot;
    ProfileFile? _devicePrefs;
    // The bytes as they were read. Undo restores from this, and it is what
    // proves a setting is back where it started rather than merely re-typed.
    string _devicePrefsAsRead = "";
    readonly HashSet<string> _deviceChanged = new(StringComparer.Ordinal);
    TextBlock? _deviceStatus;

    public async Task ShowDevicePageAsync()
    {
        _file = null; // no profile is open on a page; a stale dirty file re-asks "leave?" on the next action
        ShowPage(DevicePage, ShellDeviceButton);
        await LoadDeviceSettingsAsync();
    }

    /// <summary>Test and screenshot seam: draw the page for a prefs.csv held in
    /// memory. No machine running the tests has a QuadStick plugged in, and the
    /// page is worth looking at with real settings on it.</summary>
    /// <param name="root">Where the device is mounted, or null for the page as
    /// it looks with nothing plugged in.</param>
    internal void ShowDeviceSettingsForPreview(string? prefsCsv = null, string? root = "/Volumes/QUADSTICK")
    {
        _file = null;
        ShowPage(DevicePage, ShellDeviceButton);
        _deviceRoot = root;
        _deviceChanged.Clear();
        ShowPrefs(prefsCsv ?? EmptyPrefs, root is null
            ? Strings.DevicePage_NoQuadStickIsPluggedIn
            : string.Format(CultureInfo.CurrentCulture,
                Strings.DevicePage_FoundYourQuadStickAtRoot, root));
    }

    /// <summary>Test seam: which settings this page counts as changed.</summary>
    internal IReadOnlyCollection<string> ChangedDeviceSettings => _deviceChanged;

    /// <summary>Test seam: the prefs.csv this page is editing.</summary>
    internal ProfileFile? DevicePrefsForPreview => _devicePrefs;

    /// <summary>Test seam: Undo without the confirmation dialog, which a
    /// headless run has nobody to answer.</summary>
    internal void UndoDeviceChangesForPreview()
    {
        _devicePrefs = ProfileFile.Load(_devicePrefsAsRead);
        _deviceChanged.Clear();
        BuildDevicePage(_deviceStatus?.Text ?? "", _devicePrefs);
    }

    // An empty settings file: the sheet and its header, no rows. Every setting
    // then shows as one the file does not carry, which is exactly true of a
    // QuadStick that is not here to be read.
    const string EmptyPrefs = "Preferences\nprefs.csv\nPreference,Value,Units,Description\n";

    // Find the drive, read prefs.csv off it, and draw the page. Every failure
    // gets its own sentence: "nothing plugged in", "plugged in but no settings
    // file" and "the file would not parse" need different answers from the user.
    //
    // Only the last of those hides the settings. With no device, or with a
    // device that has no settings file yet, the rows are still drawn from the
    // catalog: what a QuadStick can be set to is worth reading before buying
    // one, and it is the only way to look at this screen on a machine that has
    // no stick attached. The line at the top says which case you are in, and
    // the save bar does not offer a write there is nowhere to send.
    async Task LoadDeviceSettingsAsync()
    {
        _devicePrefs = null;
        _devicePrefsAsRead = "";
        _deviceRoot = null;
        _deviceChanged.Clear();
        BuildDevicePage(Strings.DevicePage_LookingForYourQuadStick, null);

        var roots = await Task.Run(Device.FindCandidates);
        string? root = roots.Count switch
        {
            0 => null,
            1 => roots[0],
            _ => await PickDeviceRootAsync(roots),
        };
        _deviceRoot = root;
        if (root is null)
        {
            ShowPrefs(EmptyPrefs, Strings.DevicePage_NoQuadStickIsPluggedIn);
            return;
        }

        var path = Path.Combine(root, "prefs.csv");
        string? text;
        try
        {
            text = await Task.Run(() => File.Exists(path) ? File.ReadAllText(path) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BuildDevicePage(string.Format(CultureInfo.CurrentCulture,
                Strings.DevicePage_CouldNotReadTheSettings, ex.Message), null);
            return;
        }

        if (text is null)
        {
            ShowPrefs(EmptyPrefs, string.Format(CultureInfo.CurrentCulture,
                Strings.DevicePage_FoundYourQuadStickAtRoot, root)
                + " " + Strings.DevicePage_ItHasNoSettingsFileYet);
            return;
        }

        try
        {
            _devicePrefs = ProfileFile.Load(text);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            BuildDevicePage(string.Format(CultureInfo.CurrentCulture,
                Strings.DevicePage_CouldNotReadTheSettings, ex.Message), null);
            return;
        }

        _devicePrefsAsRead = text;
        BuildDevicePage(string.Format(CultureInfo.CurrentCulture,
            Strings.DevicePage_FoundYourQuadStickAtRoot, root), _devicePrefs);
    }

    void ShowPrefs(string csv, string status)
    {
        _devicePrefs = ProfileFile.Load(csv);
        _devicePrefsAsRead = csv;
        BuildDevicePage(status, _devicePrefs);
    }

    // The Preferences sheet inside prefs.csv, or null when the file has none.
    ModeSheet? DevicePrefsSheet =>
        _devicePrefs?.Document.Sheets.FirstOrDefault(s => s.Type == SheetType.Preferences);

    void BuildDevicePage(string status, ProfileFile? prefs)
    {
        DevicePageBody.Children.Clear();
        DevicePageBody.Children.Add(DevicePageHeader(status));

        var sheet = prefs is null ? null : DevicePrefsSheet;
        if (sheet is not null)
            foreach (var card in DeviceSettingCards(sheet))
                DevicePageBody.Children.Add(card);

        RefreshDeviceSaveBar();
    }

    Control DevicePageHeader(string status)
    {
        var heading = new TextBlock { Text = Strings.DevicePage_YourQuadStickSSettings, Classes = { "section" } };

        var explain = new TextBlock
        {
            Text = Strings.DevicePage_TheseSettingsLiveInPrefs,
            FontSize = Size("BodySize"), Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap,
        };

        // No stick attached is a state, not an error, but it is the first thing
        // to know on this page: everything below it is what a QuadStick can be
        // set to rather than what yours is set to. The words carry that, and
        // the warn styling only repeats what they already say.
        _deviceStatus = new TextBlock
        {
            Text = status, FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
            FontWeight = _deviceRoot is null ? FontWeight.Bold : FontWeight.Normal,
        };
        if (_deviceRoot is null) _deviceStatus.Classes.Add("warn");
        AutomationProperties.SetLiveSetting(_deviceStatus, AutomationLiveSetting.Polite);

        var reload = new Button();
        reload.Content = Strings.DevicePage_Reload;
        AutomationProperties.SetName(reload, Strings.DevicePage_ReadTheSettingsFromYour);
        reload.Click += async (_, _) =>
        {
            // Undoing by reading the device again throws away edits, so it asks
            // first for exactly the same reason Undo does.
            if (_deviceChanged.Count > 0 && !await ConfirmAsync(
                Strings.DevicePage_ThrowAwayYourChanges,
                Strings.DevicePage_TheSettingsYouChangedHere)) return;
            Device.InvalidateCandidateCache();
            await LoadDeviceSettingsAsync();
        };

        var files = new Button { Classes = { "quiet" } };
        files.Content = Strings.Shell_ManageFiles;
        AutomationProperties.SetName(files, Strings.Shell_ManageTheProfileFilesOn);
        files.Click += async (_, _) => await ShowDeviceFilesAsync();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children = { reload, files },
        };

        return new Border
        {
            Classes = { "homepanel" },
            Child = new StackPanel
            {
                Spacing = 10,
                Children = { heading, explain, _deviceStatus, buttons },
            },
        };
    }

    // One card per category, in the catalog's own order, holding every setting
    // the catalog knows about in that category. Settings the file does not
    // carry are shown too: the device is using a value for them either way, and
    // hiding them is how QMP ends up with four keys nobody can see.
    IEnumerable<Control> DeviceSettingCards(ModeSheet sheet)
    {
        foreach (var group in PreferenceCatalog.All
            .GroupBy(d => d.Category)
            .OrderBy(g => PreferenceCatalog.CategoryRank(g.Key)))
        {
            var rows = new StackPanel { Spacing = 18 };
            foreach (var def in group)
                rows.Children.Add(DeviceSettingRow(sheet, def));

            var heading = new TextBlock
            {
                Text = PreferenceCatalog.CategoryLabel(group.Key), Classes = { "section" },
            };
            yield return new Border
            {
                Classes = { "homepanel" },
                Child = new StackPanel { Spacing = 14, Children = { heading, rows } },
            };
        }
    }

    // Which grid row holds this setting, or -1 when the file does not carry it.
    int DeviceRowFor(ModeSheet sheet, string name)
    {
        foreach (var b in sheet.Bindings)
            if (string.Equals(b.Output, name, StringComparison.Ordinal)) return b.Row;
        return -1;
    }

    Control DeviceSettingRow(ModeSheet sheet, PreferenceDefinition def)
    {
        int row = DeviceRowFor(sheet, def.Name);
        bool present = row > 0;
        var value = present ? _devicePrefs!.GetCell(row, 1) : def.Default ?? "";

        var label = new TextBlock
        {
            Text = def.Label, FontWeight = FontWeight.Bold, FontSize = Size("BodySize"),
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(label, string.Format(CultureInfo.CurrentCulture,
            Strings.DevicePage_DefLabelWrittenAsDef, def.Label, def.Name));

        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(label);
        stack.Children.Add(DeviceValueControl(sheet, def, row, value));

        // What it is measured in and what it does, in text. QMP puts all of
        // this in tooltips, which a keyboard or screen reader user never sees.
        var about = new List<string>();
        if (def.Description.Length > 0) about.Add(def.Description);
        if (def.Unit.Length > 0)
            about.Add(string.Format(CultureInfo.CurrentCulture, Strings.Prefs_MeasuredInDefUnit, def.Unit));
        if (about.Count > 0)
            stack.Children.Add(new TextBlock
            {
                Text = string.Join(" ", about), FontSize = Size("SmallSize"),
                Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 760,
                // A MaxWidth without this centres the text in whatever room is
                // left, so the words drifted away from the control they explain.
                HorizontalAlignment = HorizontalAlignment.Left,
            });

        if (def.Risk.Length > 0)
        {
            // "Careful:" carries the warning in words; the colour only repeats it.
            var risk = new TextBlock
            {
                Text = string.Format(CultureInfo.CurrentCulture, Strings.Prefs_CarefulDefRisk, def.Risk),
                FontSize = Size("SmallSize"), Classes = { "warn" },
                TextWrapping = TextWrapping.Wrap, MaxWidth = 760,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            AutomationProperties.SetName(risk, string.Format(CultureInfo.CurrentCulture,
                Strings.Prefs_CarefulDefLabelDefRisk, def.Label, def.Risk));
            stack.Children.Add(risk);
        }

        if (!present && def.Default is { Length: > 0 } fallback)
            stack.Children.Add(new TextBlock
            {
                Text = string.Format(CultureInfo.CurrentCulture,
                    Strings.DevicePage_NotInYourSettingsFile, fallback),
                FontSize = Size("SmallSize"), Classes = { "secondary" },
                TextWrapping = TextWrapping.Wrap, MaxWidth = 760,
                HorizontalAlignment = HorizontalAlignment.Left,
            });

        return stack;
    }

    // The control for one setting. A value the control could not show without
    // changing its spelling keeps a plain text box, exactly as the List View
    // does: an out-of-range or oddly written number stays as the file has it
    // until somebody edits it on purpose.
    Control DeviceValueControl(ModeSheet sheet, PreferenceDefinition def, int row, string value)
    {
        var name = string.Format(CultureInfo.CurrentCulture, Strings.DevicePage_DefLabel, def.Label);
        if (!CanRepresent(def, value, 1)) return DeviceRawBox(sheet, def, row, value, name);

        return def.Editor switch
        {
            PreferenceEditor.Toggle => DeviceToggle(sheet, def, row, value, name),
            PreferenceEditor.Choice => DeviceChoice(sheet, def, row, value, name),
            PreferenceEditor.Integer when def.Minimum is { } lo && def.Maximum is { } hi
                => DeviceSlider(sheet, def, row, value, name, lo, hi),
            PreferenceEditor.Integer => DeviceSpinner(sheet, def, row, value, name),
            _ => DeviceRawBox(sheet, def, row, value, name),
        };
    }

    // A slider and the number it is on, side by side. The number is always
    // shown and always typeable: a slider alone says a setting is "about here",
    // and somebody copying a value out of a forum post needs the digits.
    Control DeviceSlider(ModeSheet sheet, PreferenceDefinition def, int row, string value, string name, int lo, int hi)
    {
        int start = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

        var slider = new Slider
        {
            Minimum = lo, Maximum = hi, Value = start,
            SmallChange = 1, LargeChange = Math.Max(1, (hi - lo) / 10),
            TickFrequency = 1, IsSnapToTickEnabled = true,
            MinWidth = 220, VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(slider, string.Format(CultureInfo.CurrentCulture,
            Strings.DevicePage_NameLoToHi, name, lo, hi));

        var box = new NumericUpDown
        {
            Value = start, Increment = 1, FormatString = "0",
            ParsingNumberStyle = NumberStyles.Integer, // "3.5" is refused, never rounded
            NumberFormat = CultureInfo.InvariantCulture.NumberFormat,
            Minimum = lo, Maximum = hi, Width = 132,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(box, name);

        // Each control writes the cell and mirrors the other. The guards stop
        // the pair echoing: without them a drag re-enters through the spinner.
        bool echo = false;
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || echo) return;
            int v = (int)Math.Round(slider.Value);
            echo = true; box.Value = v; echo = false;
            CommitDeviceValue(sheet, def, ref row, v.ToString(CultureInfo.InvariantCulture));
        };
        box.ValueChanged += (_, e) =>
        {
            // Half-typed or refused text leaves the cell alone.
            if (echo || e.NewValue is not decimal d || d != decimal.Truncate(d)) return;
            echo = true; slider.Value = (double)d; echo = false;
            CommitDeviceValue(sheet, def, ref row, ((int)d).ToString(CultureInfo.InvariantCulture));
        };

        var range = new TextBlock
        {
            Text = string.Format(CultureInfo.CurrentCulture, Strings.DevicePage_LoToHi, lo, hi),
            FontSize = Size("SmallSize"), Classes = { "secondary" },
            VerticalAlignment = VerticalAlignment.Center,
        };

        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children = { slider, box, range },
        };
        return line;
    }

    Control DeviceSpinner(ModeSheet sheet, PreferenceDefinition def, int row, string value, string name)
    {
        var box = new NumericUpDown
        {
            Value = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            Increment = 1, FormatString = "0",
            ParsingNumberStyle = NumberStyles.Integer,
            NumberFormat = CultureInfo.InvariantCulture.NumberFormat,
            Minimum = def.Minimum ?? int.MinValue, Maximum = def.Maximum ?? int.MaxValue,
            Width = 180, HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(box, name);
        box.ValueChanged += (_, e) =>
        {
            if (e.NewValue is not decimal d || d != decimal.Truncate(d)) return;
            CommitDeviceValue(sheet, def, ref row, ((int)d).ToString(CultureInfo.InvariantCulture));
        };
        return box;
    }

    // The device stores 0 or 1 and nothing else, so that is exactly what this
    // writes. The state is spelled out beside the box, never a tick alone.
    Control DeviceToggle(ModeSheet sheet, PreferenceDefinition def, int row, string value, string name)
    {
        var box = new CheckBox { IsChecked = value == "1", HorizontalAlignment = HorizontalAlignment.Left };
        void Paint() => box.Content = new TextBlock
        {
            Text = box.IsChecked == true ? Strings.DevicePage_On1 : Strings.DevicePage_Off0,
            FontSize = Size("BodySize"),
        };
        Paint();
        AutomationProperties.SetName(box, name);
        box.IsCheckedChanged += (_, _) =>
        {
            Paint();
            CommitDeviceValue(sheet, def, ref row, box.IsChecked == true ? "1" : "0");
        };
        return box;
    }

    // A fixed set of device keywords. The item holds the exact token, so the
    // plain-language label never reaches the file.
    Control DeviceChoice(ModeSheet sheet, PreferenceDefinition def, int row, string value, string name)
    {
        var items = def.Options.Select(o => new DeviceChoiceOption(o, def.LabelForOption(o))).ToList();
        var combo = new ComboBox
        {
            ItemsSource = items,
            SelectedItem = items.FirstOrDefault(i => i.Token == value),
            MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(combo, name);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is DeviceChoiceOption pick)
                CommitDeviceValue(sheet, def, ref row, pick.Token);
        };
        return combo;
    }

    // ToString is what the ComboBox shows and what a screen reader reads, so
    // the token is spelled out beside the words: the number is what the file
    // will hold and what QuadStick support asks for.
    sealed record DeviceChoiceOption(string Token, string Label)
    {
        public override string ToString() => Label == Token ? Token : $"{Label} ({Token})";
    }

    Control DeviceRawBox(ModeSheet sheet, PreferenceDefinition def, int row, string value, string name)
    {
        var box = new TextBox
        {
            Text = value, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(box, name);
        int here = row;
        void Commit() => CommitDeviceValue(sheet, def, ref here, (box.Text ?? "").Trim());
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Commit(); };
        return box;
    }

    // One control, one setting. A setting the file did not carry gets a row
    // added the first time it is changed, and nothing else in the file moves.
    void CommitDeviceValue(ModeSheet sheet, PreferenceDefinition def, ref int row, string exact)
    {
        if (_devicePrefs is null) return;

        if (row > 0)
        {
            if (exact == _devicePrefs.GetCell(row, 1)) return;
            _devicePrefs.SetCell(row, 1, exact);
            MarkDeviceChanged(def.Name, exact);
            return;
        }

        // The setting was not in the file. Adding a row reparses, so the sheet
        // and every row number this page is holding go stale: rebuild the page
        // and put focus back where the edit came from.
        var live = DevicePrefsSheet;
        if (live is null) return;
        int added = _devicePrefs.AddBindingRow(live);
        _devicePrefs.SetCell(added, 0, def.Name);
        _devicePrefs.SetCell(added, 1, exact);
        row = added;
        MarkDeviceChanged(def.Name, exact);

        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        var wasFocused = focused is null ? null : AutomationProperties.GetName(focused);
        BuildDevicePage(_deviceStatus?.Text ?? "", _devicePrefs);
        if (string.IsNullOrEmpty(wasFocused)) return;
        Dispatcher.UIThread.Post(() =>
        {
            DevicePageBody.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(c => AutomationProperties.GetName(c) == wasFocused)?.Focus();
        }, DispatcherPriority.Loaded);
    }

    // A setting typed back to what the device already had is not a change.
    // Counting it as one would offer to write a file that is byte for byte the
    // one already on the stick.
    void MarkDeviceChanged(string name, string exact)
    {
        if (SameAsRead(name, exact)) _deviceChanged.Remove(name);
        else _deviceChanged.Add(name);
        RefreshDeviceSaveBar();
    }

    bool SameAsRead(string name, string exact)
    {
        if (_devicePrefsAsRead.Length == 0) return false;
        var asRead = ProfileFile.Load(_devicePrefsAsRead);
        var sheet = asRead.Document.Sheets.FirstOrDefault(s => s.Type == SheetType.Preferences);
        if (sheet is null) return false;
        foreach (var b in sheet.Bindings)
            if (string.Equals(b.Output, name, StringComparison.Ordinal))
                return string.Equals(asRead.GetCell(b.Row, 1), exact, StringComparison.Ordinal);
        return false; // it was not in the file at all, so writing it is a change
    }

    void RefreshDeviceSaveBar()
    {
        DeviceSaveBarRow.Children.Clear();
        DeviceSaveBar.IsVisible = _deviceChanged.Count > 0;
        if (_deviceChanged.Count == 0) return;

        var count = new TextBlock
        {
            Text = Plural.Of(_deviceChanged.Count, "DevicePage_ChangedSetting"),
            FontSize = Size("BodySize"), VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetLiveSetting(count, AutomationLiveSetting.Polite);

        var undo = new Button();
        undo.Content = Strings.DevicePage_UndoChanges;
        AutomationProperties.SetName(undo, Strings.DevicePage_PutEverySettingBackTo);
        undo.Click += async (_, _) =>
        {
            if (!await ConfirmAsync(Strings.DevicePage_ThrowAwayYourChanges,
                Strings.DevicePage_TheSettingsYouChangedHere)) return;
            _devicePrefs = ProfileFile.Load(_devicePrefsAsRead);
            _deviceChanged.Clear();
            BuildDevicePage(_deviceStatus?.Text ?? "", _devicePrefs);
        };

        DeviceSaveBarRow.Children.Add(count);
        DeviceSaveBarRow.Children.Add(undo);

        // A Save button with no device to save to is a button that lies. The
        // edits are kept, so plugging the stick in and pressing Reload is not
        // the way to lose them; the sentence says what to do instead.
        if (_deviceRoot is null)
        {
            DeviceSaveBarRow.Children.Add(new TextBlock
            {
                Text = Strings.DevicePage_PlugInYourQuadStickTo,
                FontSize = Size("BodySize"), Classes = { "warn" },
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            });
            return;
        }

        var save = new Button { Classes = { "primary" } };
        save.Content = Strings.DevicePage_SaveToYourQuadStick;
        AutomationProperties.SetName(save, Strings.DevicePage_WriteTheChangedSettingsTo);
        save.Click += async (_, _) => await SaveDeviceSettingsAsync();
        DeviceSaveBarRow.Children.Add(save);
    }

    // The same write every install uses, with the same backup, readback and
    // receipt. prefs.csv changes every profile on the device at once, so the
    // confirmation names the file and the drive before anything is written.
    async Task SaveDeviceSettingsAsync()
    {
        if (_devicePrefs is null || _deviceRoot is null) return;
        _devicePrefs.Reparse();
        if (_devicePrefs.HasErrors)
        {
            SetDeviceStatus(Strings.DevicePage_TheseSettingsHaveAProblem
                + string.Join("\n", _devicePrefs.Issues
                    .Where(i => i.Severity == Severity.Error).Select(i => i.Message)));
            return;
        }

        if (!await ConfirmAsync(Strings.Install_InstallPrefsCsvToThis,
            string.Format(CultureInfo.CurrentCulture, Strings.Install_PrefsCsvHoldsTheDevice, _deviceRoot)))
            return;

        await RunInstallDialogAsync(_devicePrefs, _deviceRoot, confirmDefault: false, confirmPrefs: true);
        // Whatever the write did, the device is the truth about what is on it
        // now, so the page reads it back rather than assuming it succeeded.
        await LoadDeviceSettingsAsync();
    }

    void SetDeviceStatus(string text)
    {
        if (_deviceStatus is not null) _deviceStatus.Text = text;
    }
}
