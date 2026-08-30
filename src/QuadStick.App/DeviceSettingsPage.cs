using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.Format;

namespace QuadStick.App;

// Tuning the QuadStick itself. The file behind this page is prefs.csv on the
// device, which is the device's own settings and applies to every profile on
// it, so nothing here is a profile edit and nothing here goes through Install.
//
// Sixty one settings. The first version of this page put all of them in one
// column and asked people to scroll, which is the worst shape it could have
// had: somebody tuning a sip threshold cannot hold a scroll position and a
// mouthpiece at the same time. So the settings are in groups down the left,
// one group is on screen at a time, and the group is short enough to read
// without moving anything.
//
// Above the settings, and not scrolling with them, is a picture of the device
// with the parts the open group is about ringed on it, and a pad showing where
// the stick is. See DeviceBand.cs.
//
// The QuadStick Manager Program does this over seven notebook tabs with the
// controls identified only by position and by tooltip. Two things it does that
// this deliberately does not:
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
    // The same bytes, read out once into name and value. Every keystroke asks
    // whether a setting is back where it started, and parsing the whole file
    // to answer that made a slider drag stutter.
    readonly Dictionary<string, string> _deviceAsRead = new(StringComparer.Ordinal);
    readonly HashSet<string> _deviceChanged = new(StringComparer.Ordinal);
    // A pointer-held slider can paint its effect on the device diagram without
    // changing prefs.csv or the save state until it is released.
    readonly Dictionary<string, string> _devicePreview = new(StringComparer.Ordinal);
    TextBlock? _deviceStatus;

    string _deviceCategory = PreferenceCatalog.Categories[0];
    ListBox? _deviceRail;
    StackPanel? _deviceList;

    LiveInput? _liveInput;
    LiveState? _live;

    public async Task ShowDevicePageAsync()
    {
        _file = null; // no profile is open on a page; a stale dirty file re-asks "leave?" on the next action
        ShowPage(DevicePage, ShellDeviceButton);
        WatchDevicePage();
        StartLiveInput();
        await LoadDeviceSettingsAsync();
    }

    /// <summary>Test and screenshot seam: draw the page for a prefs.csv held in
    /// memory. No machine running the tests has a QuadStick plugged in, and the
    /// page is worth looking at with real settings on it. Nothing here opens a
    /// USB device: the live reading is only started by the real navigation.</summary>
    /// <param name="root">Where the device is mounted, or null for the page as
    /// it looks with nothing plugged in.</param>
    internal void ShowDeviceSettingsForPreview(string? prefsCsv = null, string? root = "/Volumes/QUADSTICK",
                                               string? category = null)
    {
        _file = null;
        ShowPage(DevicePage, ShellDeviceButton);
        _deviceRoot = root;
        _deviceChanged.Clear();
        _deviceCategory = category ?? PreferenceCatalog.Categories[0];
        ShowPrefs(prefsCsv ?? EmptyPrefs, root is null
            ? Strings.DevicePage_NoQuadStickIsPluggedIn
            : string.Format(CultureInfo.CurrentCulture,
                Strings.DevicePage_FoundYourQuadStickAtRoot, root));
    }

    /// <summary>Test seam: open one group of settings, the way clicking its row
    /// in the list on the left does.</summary>
    internal void ShowDeviceCategoryForPreview(string category)
    {
        _deviceCategory = category;
        if (_deviceRail is not null) _deviceRail.SelectedItem =
            _deviceRail.Items.OfType<DeviceGroupRow>().FirstOrDefault(r => r.Category == category);
        FillDeviceList();
    }

    /// <summary>Test seam: which settings this page counts as changed.</summary>
    internal IReadOnlyCollection<string> ChangedDeviceSettings => _deviceChanged;

    /// <summary>Test seam: the prefs.csv this page is editing.</summary>
    internal ProfileFile? DevicePrefsForPreview => _devicePrefs;

    /// <summary>Test and screenshot seam: draw the page as it looks with a
    /// stick being used, without a stick.</summary>
    internal void ShowLiveInputForPreview(LiveState? state)
    {
        _live = state;
        UpdateDeviceBand();
    }

    /// <summary>Test seam: Undo without the confirmation dialog, which a
    /// headless run has nobody to answer.</summary>
    internal void UndoDeviceChangesForPreview()
    {
        _devicePrefs = ProfileFile.Load(_devicePrefsAsRead);
        _deviceChanged.Clear();
        _devicePreview.Clear();
        BuildDevicePage(_deviceStatus?.Text ?? "", _devicePrefs);
    }

    // An empty settings file: the sheet and its header, no rows. Every setting
    // then shows as one the file does not carry, which is exactly true of a
    // QuadStick that is not here to be read.
    const string EmptyPrefs = "Preferences\nprefs.csv\nPreference,Value,Units,Description\n";

    // ---- live reading ----

    bool _deviceWatched;

    // Reading the stick costs a thread parked on a USB read, so it runs while
    // this page is the page and stops the moment it is not.
    void WatchDevicePage()
    {
        if (_deviceWatched) return;
        _deviceWatched = true;
        DevicePage.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty && !DevicePage.IsVisible) StopLiveInput();
        };
    }

    void StartLiveInput()
    {
        _liveInput ??= new LiveInput(state =>
        {
            _live = state;
            UpdateDeviceBand();
        });
    }

    void StopLiveInput()
    {
        _liveInput?.Dispose();
        _liveInput = null;
        _live = null;
    }

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
        SetDeviceAsRead("");
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

        SetDeviceAsRead(text);
        BuildDevicePage(string.Format(CultureInfo.CurrentCulture,
            Strings.DevicePage_FoundYourQuadStickAtRoot, root), _devicePrefs);
    }

    void ShowPrefs(string csv, string status)
    {
        _devicePrefs = ProfileFile.Load(csv);
        SetDeviceAsRead(csv);
        BuildDevicePage(status, _devicePrefs);
    }

    // Keep the bytes and the lookup over them in step. Undo restores from the
    // bytes; every "is this back where it started?" reads the lookup.
    void SetDeviceAsRead(string csv)
    {
        _devicePrefsAsRead = csv;
        _deviceAsRead.Clear();
        _devicePreview.Clear();
        if (csv.Length == 0) return;
        var file = ProfileFile.Load(csv);
        var sheet = file.Document.Sheets.FirstOrDefault(s => s.Type == SheetType.Preferences);
        if (sheet is null) return;
        foreach (var b in sheet.Bindings) _deviceAsRead[b.Output] = file.GetCell(b.Row, 1);
    }

    // The Preferences sheet inside prefs.csv, or null when the file has none.
    ModeSheet? DevicePrefsSheet =>
        _devicePrefs?.Document.Sheets.FirstOrDefault(s => s.Type == SheetType.Preferences);

    // ---- the frame ----

    void BuildDevicePage(string status, ProfileFile? prefs)
    {
        DevicePageBody.Children.Clear();
        _deviceRail = null;
        _deviceList = null;

        // No width cap. The page is the window: capping it at 1180 left a wide
        // grey gutter down the right of every large screen while the settings
        // themselves were squeezed. What has to stay readable is the prose,
        // and each caption carries its own measure.
        var frame = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var header = DevicePageHeader(status);
        Grid.SetRow(header, 0);
        frame.Children.Add(header);

        if (prefs is not null && DevicePrefsSheet is not null)
        {
            var split = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Margin = new Thickness(0, 14, 0, 0),
            };

            var rail = DeviceGroupRail();
            Grid.SetColumn(rail, 0);
            split.Children.Add(rail);

            _deviceList = new StackPanel();
            var right = new DockPanel { Margin = new Thickness(16, 0, 0, 0) };
            var band = BuildDeviceBand();
            DockPanel.SetDock(band, Dock.Top);
            right.Children.Add(band);
            // The settings are a card, the way every other list of things in
            // this app is. They used to sit straight on the page background
            // with the scrollbar floating in the gap beside them, which read
            // as an unfinished half of the screen rather than as one panel.
            right.Children.Add(DeviceCard(new ScrollViewer
            {
                Content = _deviceList,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            }));
            Grid.SetColumn(right, 1);
            split.Children.Add(right);

            Grid.SetRow(split, 1);
            frame.Children.Add(split);
            FillDeviceList();
        }

        DevicePageBody.Children.Add(frame);
        RefreshDeviceSaveBar();
    }

    // The one panel shape this page uses, so the group list, the picture and
    // the settings all read as the same kind of object as a profile card on
    // Home. Bound rather than read once: the theme and the gallery's own
    // sliders both change these while the page is up.
    static Border DeviceCard(Control child)
    {
        var card = new Border { Child = child, BorderThickness = new Thickness(1) };
        BindBrush(card, Border.BackgroundProperty, "Surface");
        BindBrush(card, Border.BorderBrushProperty, "SurfaceBorder");
        card[!Border.CornerRadiusProperty] = new DynamicResourceExtension("PanelRadiusCorner");
        return card;
    }

    Control DevicePageHeader(string status)
    {
        var heading = new TextBlock
        {
            Text = Strings.DevicePage_YourQuadStickSSettings, Classes = { "section" },
            VerticalAlignment = VerticalAlignment.Center,
        };

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

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        top.Children.Add(heading);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children = { reload, files },
        };
        Grid.SetColumn(buttons, 1);
        top.Children.Add(buttons);

        // No stick attached is a state, not an error, but it is the first thing
        // to know on this page: everything below it is what a QuadStick can be
        // set to rather than what yours is set to. The words carry that, and
        // the warn styling only repeats what they already say.
        _deviceStatus = new TextBlock
        {
            Text = status, FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
            FontWeight = _deviceRoot is null ? FontWeight.Bold : FontWeight.Normal,
            Margin = new Thickness(0, 6, 0, 0),
        };
        if (_deviceRoot is null) _deviceStatus.Classes.Add("warn");
        AutomationProperties.SetLiveSetting(_deviceStatus, AutomationLiveSetting.Polite);

        // One line, and it earns it: it is the difference between a setting
        // that changes one game and a setting that changes every game.
        var scope = new TextBlock
        {
            Text = Strings.DevicePage_TheseSettingsLiveInPrefs,
            FontSize = Size("SmallSize"), Classes = { "secondary" },
            TextWrapping = TextWrapping.Wrap, MaxWidth = 720,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        return new StackPanel { Children = { top, scope, _deviceStatus } };
    }

    // One row per group, in the catalog's own order. A list and not a row of
    // tabs: nine tabs across the top of a 1024 wide window either shrink to
    // initials or wrap, and a list is one arrow key per group either way.
    sealed record DeviceGroupRow(string Category, string Label, int Count)
    {
        public override string ToString() => Label;
    }

    Control DeviceGroupRail()
    {
        var rows = PreferenceCatalog.All
            .GroupBy(d => d.Category)
            .OrderBy(g => PreferenceCatalog.CategoryRank(g.Key))
            .Select(g => new DeviceGroupRow(g.Key, PreferenceCatalog.CategoryLabel(g.Key), g.Count()))
            .ToList();

        _deviceRail = new ListBox
        {
            ItemsSource = rows,
            SelectedItem = rows.FirstOrDefault(r => r.Category == _deviceCategory) ?? rows[0],
            Width = 210,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6),
            ItemTemplate = new FuncDataTemplate<DeviceGroupRow>((row, _) => new StackPanel
            {
                Spacing = 1,
                Margin = new Thickness(2, 3),
                Children =
                {
                    new TextBlock { Text = row.Label, FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap },
                    new TextBlock
                    {
                        Text = Plural.Of(row.Count, "DevicePage_Setting"),
                        FontSize = Size("SmallSize"), Classes = { "secondary" },
                    },
                },
            }),
        };
        AutomationProperties.SetName(_deviceRail, Strings.DevicePage_GroupsOfSettings);
        // A list box hides its sideways bar rather than turning it off, which
        // means it clips. Group names wrap in this list, and a language with a
        // longer word for "USB and compatibility" has to be able to.
        ScrollViewer.SetHorizontalScrollBarVisibility(_deviceRail, ScrollBarVisibility.Disabled);
        // The open group is bold as well as filled. A selected row that is only
        // a different shade of the background is a colour-only cue.
        // Avalonia.Styling.Style spelled out: this app has a Style class of
        // its own, and the using above is here for the selector helpers.
        _deviceRail.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Avalonia.Styling.Setter(ListBoxItem.CornerRadiusProperty,
                    new DynamicResourceExtension("CellRadiusCorner")),
                new Avalonia.Styling.Setter(ListBoxItem.PaddingProperty, new Thickness(10, 6)),
            },
        });
        _deviceRail.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>().Class(":selected"))
        {
            Setters = { new Avalonia.Styling.Setter(ListBoxItem.FontWeightProperty, FontWeight.Bold) },
        });
        _deviceRail.SelectionChanged += (_, _) =>
        {
            if (_deviceRail.SelectedItem is not DeviceGroupRow pick) return;
            _deviceCategory = pick.Category;
            FillDeviceList();
        };
        // Top, not stretched: a nine row list used to draw a white slab all
        // the way to the bottom of the window whatever was in it.
        var card = DeviceCard(_deviceRail);
        card.VerticalAlignment = VerticalAlignment.Top;
        return card;
    }

    // Only the open group is built. Nine groups of controls all alive at once
    // is what made the first version of this page a mile of scroll.
    void FillDeviceList()
    {
        if (_deviceList is null) return;
        var sheet = DevicePrefsSheet;
        _deviceList.Children.Clear();
        if (sheet is null) return;

        var group = PreferenceCatalog.All.Where(d => d.Category == _deviceCategory).ToList();
        for (int i = 0; i < group.Count; i++)
        {
            // A hairline between rows and none after the last, so the card's
            // own edge is the bottom of the list. Settings used to be told
            // apart by a gap, which at this row height read as one long block.
            var row = DeviceSettingRow(sheet, group[i]);
            row.Padding = new Thickness(16, 12);
            if (i < group.Count - 1)
            {
                row.BorderThickness = new Thickness(0, 0, 0, 1);
                BindBrush(row, Border.BorderBrushProperty, "SurfaceBorder");
            }
            _deviceList.Children.Add(row);
        }

        UpdateDeviceBand();
    }

    // Which grid row holds this setting, or -1 when the file does not carry it.
    int DeviceRowFor(ModeSheet sheet, string name)
    {
        foreach (var b in sheet.Bindings)
            if (string.Equals(b.Output, name, StringComparison.Ordinal)) return b.Row;
        return -1;
    }

    // Label and control on one line, and under them at most two short lines:
    // what the setting does, and the facts about its value. QMP puts all of
    // this in tooltips, which a keyboard or screen reader user never sees, and
    // the first version of this page put it in paragraphs, which nobody read.
    Border DeviceSettingRow(ModeSheet sheet, PreferenceDefinition def)
    {
        int row = DeviceRowFor(sheet, def.Name);
        bool present = row > 0;
        var value = present ? _devicePrefs!.GetCell(row, 1) : def.Default ?? "";

        var label = new TextBlock
        {
            Text = def.Label, FontWeight = FontWeight.Bold, FontSize = Size("BodySize"),
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
        };
        AutomationProperties.SetName(label, string.Format(CultureInfo.CurrentCulture,
            Strings.DevicePage_DefLabelWrittenAsDef, def.Label, def.Name));

        // The control column is a share of what is left rather than everything
        // left, so a checkbox and a slider start at the same place on every
        // row and a wide window does not stretch one slider across the screen.
        // MinHeight is the click-target floor: every row is at least one, and
        // that is also what centres a short label against a tall control.
        var line = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(DeviceLabelWidth, GridUnitType.Pixel),
                // Capped, not fixed: the control fills up to its own width on
                // a wide window and shrinks with a narrow one, so nothing on
                // this page ever needs a sideways scrollbar.
                new ColumnDefinition(1, GridUnitType.Star) { MaxWidth = DeviceControlWidth },
            },
            MinHeight = Size("ControlHeight"),
        };
        line.Children.Add(label);
        var control = DeviceValueControl(sheet, def, row, value);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        line.Children.Add(control);

        var stack = new StackPanel { Spacing = 3, Children = { line } };

        if (def.Description.Length > 0)
            stack.Children.Add(Caption(def.Description, "secondary"));

        // Range, unit, and what the device falls back to, in one line. These
        // are facts about the number rather than help, so they read smaller
        // and they are never a paragraph.
        var facts = new List<string>();
        // Whole sentences, because they are joined end to end: "10 to 50
        // Measured in percent." reads as one broken line.
        if (def.Minimum is { } lo && def.Maximum is { } hi)
            facts.Add(string.Format(CultureInfo.CurrentCulture,
                Strings.DevicePage_AnythingFromLoToHi, lo, hi));
        if (def.Unit.Length > 0)
            facts.Add(string.Format(CultureInfo.CurrentCulture, Strings.Prefs_MeasuredInDefUnit, def.Unit));
        if (!present && def.Default is { Length: > 0 } fallback)
            facts.Add(string.Format(CultureInfo.CurrentCulture,
                Strings.DevicePage_NotInYourSettingsFile, fallback));
        // Somebody who has been using QMP, or reading a forum post written by
        // somebody who has, arrives holding QMP's name for this control.
        if (def.AlsoCalled.Length > 0)
            facts.Add(string.Format(CultureInfo.CurrentCulture,
                Strings.Prefs_QMPCallsItDefAlsoCalled, def.AlsoCalled));
        if (facts.Count > 0)
            stack.Children.Add(Caption(string.Join(" ", facts), "muted"));

        if (def.Risk.Length > 0)
        {
            // "Careful:" carries the warning in words; the colour only repeats it.
            var risk = Caption(string.Format(CultureInfo.CurrentCulture,
                Strings.Prefs_CarefulDefRisk, def.Risk), "warn");
            AutomationProperties.SetName(risk, string.Format(CultureInfo.CurrentCulture,
                Strings.Prefs_CarefulDefLabelDefRisk, def.Label, def.Risk));
            stack.Children.Add(risk);
        }

        return new Border { Child = stack };
    }

    // Wide enough for a slider and its number side by side, and no wider. A
    // control that grows with the window puts a 900 pixel slider on a big
    // screen, where one pixel of travel is a whole step of the setting.
    const double DeviceControlWidth = 420;

    // Room for the longest label in the widest language before it wraps.
    const double DeviceLabelWidth = 240;

    // One width for every box a number goes in, whether it sits beside a
    // slider or on its own.
    const double DeviceNumberWidth = 124;

    // A MaxWidth on its own centres the words in whatever room is left, so the
    // caption drifts away from the control it explains.
    TextBlock Caption(string text, string style) => new()
    {
        Text = text, FontSize = Size("SmallSize"), Classes = { style },
        TextWrapping = TextWrapping.Wrap, MaxWidth = 700,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

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
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(slider, string.Format(CultureInfo.CurrentCulture,
            Strings.DevicePage_NameLoToHi, name, lo, hi));

        var box = new NumericUpDown
        {
            Value = start, Increment = 1, FormatString = "0",
            ParsingNumberStyle = NumberStyles.Integer, // "3.5" is refused, never rounded
            NumberFormat = CultureInfo.InvariantCulture.NumberFormat,
            Minimum = lo, Maximum = hi, Width = DeviceNumberWidth,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(box, name);

        // The thumb, its number, and the device preview must follow a pointer
        // drag immediately, but updating the saved model and save bar for every
        // pixel steals time from the drag itself. Hold that update until
        // release; keyboard, accessibility, and typed-number changes commit
        // immediately.
        bool pointerHeld = false;
        string? pending = null;
        void CommitPending()
        {
            if (!pointerHeld) return;
            pointerHeld = false;
            _devicePreview.Remove(def.Name);
            if (pending is { } final) CommitDeviceValue(sheet, def, ref row, final);
            pending = null;
        }

        // The Slider's thumb handles these events itself, so listen for the
        // bubbled handled event as well. That also covers release after the
        // pointer has left the control while its thumb holds capture.
        slider.AddHandler(InputElement.PointerPressedEvent, (_, _) => pointerHeld = true,
            RoutingStrategies.Bubble, handledEventsToo: true);
        slider.AddHandler(InputElement.PointerReleasedEvent, (_, _) => CommitPending(),
            RoutingStrategies.Bubble, handledEventsToo: true);
        slider.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => CommitPending(),
            RoutingStrategies.Bubble, handledEventsToo: true);

        // Each control writes the cell and mirrors the other. The guards stop
        // the pair echoing: without them a drag re-enters through the spinner.
        bool echo = false;
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || echo) return;
            int v = (int)Math.Round(slider.Value);
            echo = true; box.Value = v; echo = false;
            string exact = v.ToString(CultureInfo.InvariantCulture);
            if (pointerHeld)
            {
                pending = exact;
                PreviewDeviceValue(def.Name, exact);
            }
            else CommitDeviceValue(sheet, def, ref row, exact);
        };
        box.ValueChanged += (_, e) =>
        {
            // Half-typed or refused text leaves the cell alone.
            if (echo || e.NewValue is not decimal d || d != decimal.Truncate(d)) return;
            echo = true; slider.Value = (double)d; echo = false;
            CommitDeviceValue(sheet, def, ref row, ((int)d).ToString(CultureInfo.InvariantCulture));
        };

        var pair = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        pair.Children.Add(slider);
        Grid.SetColumn(box, 1);
        box.Margin = new Thickness(12, 0, 0, 0);
        pair.Children.Add(box);
        return pair;
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
            Width = DeviceNumberWidth, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
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
        var box = new CheckBox
        {
            IsChecked = value == "1",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
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
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
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
            Text = value,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
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
            UpdateDeviceBand();
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

    // The device diagram has only three setting-driven previews. Keeping this
    // narrow means a long slider drag does not redraw it when there is nothing
    // visual to show, while the dead-zone rings and LEDs stay live.
    void PreviewDeviceValue(string name, string exact)
    {
        _devicePreview[name] = exact;
        if (name is "brightness" or "joystick_deflection_minimum" or "joystick_deflection_maximum")
            UpdateDeviceBand();
    }

    // it was not in the file at all, so writing it is a change
    bool SameAsRead(string name, string exact) =>
        _deviceAsRead.TryGetValue(name, out var was)
        && string.Equals(was, exact, StringComparison.Ordinal);

    TextBlock? _deviceCount;
    Button? _deviceUndo;
    Button? _deviceSave;

    // Built once, on the first draw of the page, and never again. The old
    // version cleared and refilled this row on every change, which is every
    // pixel of a slider drag: the bar flickered, and appearing at all pushed
    // the settings up under the pointer that was dragging them.
    void BuildDeviceSaveBar()
    {
        if (_deviceCount is not null) return;

        _deviceCount = new TextBlock
        {
            FontSize = Size("BodySize"), VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetLiveSetting(_deviceCount, AutomationLiveSetting.Polite);

        _deviceUndo = new Button { Content = Strings.DevicePage_UndoChanges };
        AutomationProperties.SetName(_deviceUndo, Strings.DevicePage_PutEverySettingBackTo);
        _deviceUndo.Click += async (_, _) =>
        {
            if (!await ConfirmAsync(Strings.DevicePage_ThrowAwayYourChanges,
                Strings.DevicePage_TheSettingsYouChangedHere)) return;
            _devicePrefs = ProfileFile.Load(_devicePrefsAsRead);
            _deviceChanged.Clear();
            _devicePreview.Clear();
            BuildDevicePage(_deviceStatus?.Text ?? "", _devicePrefs);
        };

        _deviceSave = new Button { Classes = { "primary" } };
        _deviceSave.Content = Strings.DevicePage_SaveToYourQuadStick;
        AutomationProperties.SetName(_deviceSave, Strings.DevicePage_WriteTheChangedSettingsTo);
        _deviceSave.Click += async (_, _) => await SaveDeviceSettingsAsync();

        _deviceCount.Margin = new Thickness(0, 0, 16, 0);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _deviceUndo, _deviceSave },
        };
        Grid.SetColumn(buttons, 1);
        DeviceSaveBarRow.Children.Add(_deviceCount);
        DeviceSaveBarRow.Children.Add(buttons);
    }

    // Called on every edit, so it does no work beyond a word and two flags.
    //
    // A Save button with no device to save to is a button that lies, so with
    // nothing plugged in it stays off and the line says what to do instead.
    // The edits are kept either way: plugging the stick in and pressing Reload
    // is not the way to lose them.
    void RefreshDeviceSaveBar()
    {
        BuildDeviceSaveBar();
        int n = _deviceChanged.Count;
        bool attached = _deviceRoot is not null;

        _deviceCount!.Text = n == 0
            ? Strings.DevicePage_NoChangesYet
            : Plural.Of(n, attached ? "DevicePage_ChangedNotSavedYet" : "DevicePage_ChangedPlugIn");
        _deviceCount.Classes.Set("warn", n > 0 && !attached);

        _deviceUndo!.IsEnabled = n > 0;
        _deviceSave!.IsEnabled = n > 0 && attached;
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
