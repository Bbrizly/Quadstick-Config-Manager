using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using QuadStick.Format;

namespace QuadStick.App;

// The Preferences half of List View. A preference row is not a binding: it is
// one setting name and one value, and the catalog can vouch for what some of
// those values are allowed to be. Where it can, the row gets a real control
// (a number spinner, a checkbox, a dropdown). Where it cannot, or where the
// value already in the file is not one that control could show without
// changing it, the row keeps the plain text box it has always had.
//
// The rule the whole file is built around: never rewrite a value the user did
// not type. A setting quietly clamped or rounded on someone's behalf can leave
// a disabled user with hardware that no longer answers them.
public partial class MainWindow
{
    // Every setting name the catalog knows, offered on a Preferences sheet.
    // Typing still works: firmware newer than this app has names the catalog
    // has never heard of, and those must stay reachable.
    static readonly List<string> PreferenceNameSuggestions =
        PreferenceCatalog.All.Select(d => d.Name).OrderBy(x => x).ToList();

    // A mode sheet's output column also accepts the preference names the
    // firmware honors per mode, so the picker offers those after the real
    // outputs. Only ModeOverride names: a standalone-only setting in a mode
    // sheet does nothing. They are plain tokens, never action names, so
    // picking one writes column A and clears column L like any other token.
    static List<string> WithModeOverrides(IReadOnlySet<string> outputs) =>
        outputs.OrderBy(x => x)
            .Concat(PreferenceCatalog.All
                .Where(d => d.ModeOverride && !outputs.Contains(d.Name))
                .Select(d => d.Name).OrderBy(x => x))
            .ToList();

    static PreferenceDefinition? Definition(string name) =>
        PreferenceCatalog.TryGet(name, out var d) ? d : null;

    // The device reads the same setting names in three places and means
    // something different each time, so the editor has to say which one is on
    // screen. These three phrases are the whole contract; they are read out as
    // text, never signalled by color alone.
    internal const string DeviceWideScope = "Device-wide settings";
    internal const string ProfileScope = "Active while this profile is loaded";
    internal const string ModeScope = "Active only in this mode";

    // A standalone prefs.csv is the device's own settings file, so its
    // Preferences sheet is device-wide. The same sheet inside a game profile
    // only lasts as long as that profile is loaded.
    string PreferencesScope() =>
        _file is not null && _file.Document.IsDevicePreferences ? DeviceWideScope : ProfileScope;

    // The scope line: the exact phrase on its own so a screen reader and a
    // test both find it, plus one plain sentence saying what it means.
    static Control ScopeBanner(string phrase, string explain)
    {
        var title = new TextBlock
        {
            Text = phrase, FontWeight = FontWeight.Bold, FontSize = Size("SmallSize"),
            TextWrapping = TextWrapping.Wrap,
        };
        var body = new TextBlock
        {
            Text = explain, FontSize = Size("SmallSize"), Classes = { "secondary" },
            TextWrapping = TextWrapping.Wrap,
        };
        var box = new Border
        {
            CornerRadius = new Avalonia.CornerRadius(5),
            Padding = new Avalonia.Thickness(10, 6),
            Margin = new Avalonia.Thickness(0, 0, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 860,
            Child = new StackPanel { Spacing = 2, Children = { title, body } },
        };
        BindBrush(box, Border.BackgroundProperty, "SurfaceSubtle");
        AutomationProperties.SetName(box, $"{phrase}. {explain}");
        return box;
    }

    // The same rule Validator uses: a mode row whose output is a preference
    // name sets that preference for the mode, unless increment_value or
    // decrement_value makes it a live binding that adjusts the setting instead.
    // The same rule read straight off the grid, for the moments before the
    // file has been reparsed into bindings.
    bool IsSettingRow(int row) =>
        _file is not null && Vocab.IsPreferenceOverride(_file.GetCell(row, 0), _file.GetCell(row, 1));

    // Which preference a settings row is for, or null when the row is not one.
    // Column C's control is built for a particular definition, so a row that
    // stays a setting but becomes a different one needs its control made again.
    PreferenceDefinition? SettingDefinition(int row) =>
        IsSettingRow(row) ? Definition(_file!.GetCell(row, 0)) : null;

    static bool IsModePreferenceOverride(Binding b) =>
        Vocab.IsPreferenceOverride(b.Output, b.Function);

    Control PrefsHeaderRow()
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        p.Children.Add(RowNumberHeaderSpacer());
        p.Children.Add(Swatch("Setting", 300, OutputTint));
        p.Children.Add(Swatch("Value", PrefsValueWidth, FunctionTint));
        p.Children.Add(Swatch("Units", 100, InputTint));
        p.Children.Add(Swatch("Description", 240, InputTint));
        if (CurrentSheet?.Type != SheetType.Preferences) return p;

        // The banner rides inside the header control rather than as its own
        // child of RowsPanel, so the delete/move animations can keep counting
        // on "the header is child 0, row N is child N".
        var scope = PreferencesScope();
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                ScopeBanner(scope, scope == DeviceWideScope
                    ? "This file is the QuadStick's own settings. Every change here applies to the whole device, in every profile."
                    : "These settings apply while this profile is the one running, and go back to the device's own settings when another profile loads."),
                p,
            },
        };
    }

    const double PrefsValueWidth = 160;

    Control PrefsRow(Binding b, int number)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        p.Children.Add(DragHandle(b, number));
        WireRowDrop(p, b);
        _rowPanels[b.Row] = p;
        PaintRow(b.Row);

        // A setting the catalog has never heard of, or one whose value a
        // friendly control could not show as it stands, keeps the plain row.
        var def = Definition(b.Output);
        var value = _file!.GetCell(b.Row, 1);
        bool typed = def is not null && CanRepresent(def, value, 1);

        Control Mid(Control c) { c.VerticalAlignment = VerticalAlignment.Center; return c; }

        p.Children.Add(PrefsNameCell(b, def));
        var valueCell = PrefsValueCell(b, typed ? def : null, 1);
        p.Children.Add(Mid(valueCell));
        // The official sheet annotates each preference with Units (column C)
        // and a Description (column D). The device ignores both, but hiding
        // them here hid the tester's own notes about what each setting does.
        p.Children.Add(Mid(SuggestBox(b.Row, 2, _file!.GetCell(b.Row, 2), 100, NoSuggestions, $"Units for row {b.Row}", InputTint)));
        var desc = NoteBox(b.Row, 3, $"Description for row {b.Row}. Saved in the file, ignored by the QuadStick");
        desc.Width = 240;
        p.Children.Add(Mid(desc));
        var del = new Button { Classes = { "icon", "danger" }, Content = Glyph("IconDelete", "Error") };
        ToolTip.SetTip(del, "Delete this whole row");
        AutomationProperties.SetName(del, $"Delete row {b.Row}");
        del.Click += (_, _) => DeleteListRow(b);
        p.Children.Add(Mid(del));

        var heading = def is null ? null : CategoryHeadingFor(b, def);
        var info = def is null ? null : PreferenceInfoLine(b, def, typed ? valueCell : null, 1);
        if (heading is null && info is null) return p;

        // Heading and notes travel inside the row's own control, so RowsPanel
        // keeps exactly one child per binding. The delete and move animations
        // count rows that way.
        var stack = new StackPanel { Spacing = 4 };
        if (heading is not null) stack.Children.Add(heading);
        stack.Children.Add(p);
        if (info is not null) stack.Children.Add(info);
        return stack;
    }

    // A heading whenever the known category changes on the way down the file.
    // Repeats are deliberate: a file that comes back to a category gets a
    // second heading rather than having its rows quietly regrouped. Unknown
    // rows in between do not break a run.
    Control? CategoryHeadingFor(Binding b, PreferenceDefinition def)
    {
        var sheet = CurrentSheet;
        if (sheet is null) return null;
        for (int j = sheet.Bindings.IndexOf(b) - 1; j >= 0; j--)
        {
            if (Definition(sheet.Bindings[j].Output) is not { } prev) continue;
            if (prev.Category == def.Category) return null;
            break;
        }
        var text = new TextBlock
        {
            Text = def.Category, FontWeight = FontWeight.Bold, FontSize = Size("SubheadSize"),
            Margin = new Avalonia.Thickness(RowNumberWidth + 4, 10, 0, 0),
        };
        AutomationProperties.SetName(text, $"{def.Category} settings");
        return text;
    }

    // The raw token is what the device reads, so it stays the editable cell.
    // The friendly label sits above it for people who do not think in tokens.
    Control PrefsNameCell(Binding b, PreferenceDefinition? def)
    {
        var box = SuggestBox(b.Row, 0, b.Output, 300, PreferenceNameSuggestions,
            $"Setting name for row {b.Row}", OutputTint,
            (before, after) => Definition(before) != Definition(after));
        if (def is null) return box;

        var label = new TextBlock
        {
            Text = def.Label, FontWeight = FontWeight.Bold, FontSize = Size("SmallSize"),
            // Capped at the Setting column's width so a long label wraps
            // instead of pushing the Value column out from under its header.
            TextWrapping = TextWrapping.Wrap, MaxWidth = 300,
        };
        AutomationProperties.SetName(label, $"{def.Label}, written as {def.Name}");
        return new StackPanel { Spacing = 2, Children = { label, box } };
    }

    // What a control can show without changing it. Anything else keeps the raw
    // text box, so an out-of-range or oddly written value stays exactly as the
    // file has it until someone edits it on purpose.
    // col matters for one reason: a mode row's value is read with a bare atoi
    // (Configuration.c:495), while a settings sheet's goes through a switch
    // that has keyword tables for the bluetooth settings (:598-621). So a
    // dropdown of words belongs on a settings sheet and nowhere else. Offering
    // one on a mode row would let a click write "keyboard" into a cell the
    // device turns into 0.
    static bool CanRepresent(PreferenceDefinition def, string value, int col) => def.Editor switch
    {
        PreferenceEditor.Toggle => value is "0" or "1",
        PreferenceEditor.Choice => col == 1 && def.Options.Contains(value, StringComparer.Ordinal),
        PreferenceEditor.Integer =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            // "007", "+8" and " 8 " all parse, but showing them in a spinner
            // would write them back in a different spelling.
            && n.ToString(CultureInfo.InvariantCulture) == value
            && n >= (def.Minimum ?? int.MinValue) && n <= (def.Maximum ?? int.MaxValue),
        _ => false,
    };

    // The value column. def is null here whenever the row has to stay raw.
    // col is where the value lives: B on a settings sheet, C on a mode row
    // that overrides one setting for that mode. The device reads a different
    // column in each case, so the control has to be told which.
    Border PrefsValueCell(Binding b, PreferenceDefinition? def, int col)
    {
        var name = $"Setting value for row {b.Row}";
        var wrapper = new Border
        {
            // Match the thickness RefreshIssues sets on an errored cell, so
            // flagging a problem never reflows the row.
            BorderThickness = new Avalonia.Thickness(3),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new Avalonia.CornerRadius(5),
            Width = PrefsValueWidth,
        };
        _cellBorders[$"{(char)('A' + col)}{b.Row}"] = wrapper;
        wrapper.Child = def is null
            ? RawValueBox(b.Row, col, name)
            : TypedValueControl(b.Row, col, def, _file!.GetCell(b.Row, col), name);
        return wrapper;
    }

    Control TypedValueControl(int row, int col, PreferenceDefinition def, string value, string name) => def.Editor switch
    {
        PreferenceEditor.Toggle => ToggleValueControl(row, col, value, name),
        PreferenceEditor.Choice => ChoiceValueControl(row, col, def, value, name),
        _ => IntegerValueControl(row, col, def, value, name),
    };

    // On/off. The device stores 0 or 1 and nothing else, so that is exactly
    // what this writes. The state is spelled out in words next to the box, so
    // it never depends on seeing a tick.
    Control ToggleValueControl(int row, int col, string value, string name)
    {
        var box = new CheckBox { IsChecked = value == "1", VerticalAlignment = VerticalAlignment.Center };
        void Paint() => box.Content = new TextBlock
        { Text = box.IsChecked == true ? "On (1)" : "Off (0)", FontSize = Size("BodySize") };
        Paint();
        AutomationProperties.SetName(box, name);
        box.IsCheckedChanged += (_, _) => { Paint(); CommitPreferenceValue(row, col, box.IsChecked == true ? "1" : "0"); };
        return box;
    }

    // A fixed set of device keywords. The list holds the exact tokens, so
    // picking one writes the token the firmware reads, letter for letter.
    Control ChoiceValueControl(int row, int col, PreferenceDefinition def, string value, string name)
    {
        var combo = new ComboBox
        {
            ItemsSource = def.Options.ToList(),
            SelectedItem = value,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        combo[!TemplatedControl.BackgroundProperty] = new DynamicResourceExtension(FunctionTint + "Brush");
        AutomationProperties.SetName(combo, name);
        combo.SelectionChanged += (_, _) =>
        { if (combo.SelectedItem is string token) CommitPreferenceValue(row, col, token); };
        return combo;
    }

    // A whole number. Bounds come from the official manager's own sliders, so
    // the spinner stops where that program stops. Typing past them is refused
    // rather than clamped, and "Type an exact value" below the row is the way
    // out for anyone who really wants an untested number.
    Control IntegerValueControl(int row, int col, PreferenceDefinition def, string value, string name)
    {
        var box = new NumericUpDown
        {
            Value = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            Increment = 1,
            FormatString = "0",
            ParsingNumberStyle = NumberStyles.Integer, // "3.5" is refused, never rounded
            NumberFormat = CultureInfo.InvariantCulture.NumberFormat,
            Minimum = def.Minimum ?? int.MinValue,
            Maximum = def.Maximum ?? int.MaxValue,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(box, name);
        box.ValueChanged += (_, e) =>
        {
            // Half-typed or refused text leaves the cell alone.
            if (e.NewValue is not decimal d || d != decimal.Truncate(d)) return;
            CommitPreferenceValue(row, col, ((int)d).ToString(CultureInfo.InvariantCulture));
        };
        return box;
    }

    // The plain editor every preference cell used to be, and still is for
    // unknown settings, free-text settings, and values no control can show.
    Control RawValueBox(int row, int col, string name)
    {
        var box = new AutoCompleteBox
        {
            Text = _file!.GetCell(row, col),
            ItemsSource = NoSuggestions,
            FilterMode = AutoCompleteFilterMode.Contains,
            MinimumPrefixLength = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        box[!TemplatedControl.BackgroundProperty] = new DynamicResourceExtension(FunctionTint + "Brush");
        AutomationProperties.SetName(box, name);
        void Commit() => CommitPreferenceValue(row, col, (box.Text ?? "").Trim());
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        return box;
    }

    // One control, one cell. Units, descriptions, notes, unknown rows and row
    // order are never touched from here.
    void CommitPreferenceValue(int row, int col, string exact)
    {
        if (_file is null || exact == _file.GetCell(row, col)) return;
        _file.SetCell(row, col, exact);
        RefreshIssues();
        // In Device View the card's sentence reads this value back, so leaving
        // it at the old text made the card disagree with the control the user
        // had just used. Only the Device View needs telling: the List View
        // control it was typed into is already showing what was typed.
        if (!_deviceView) return;
        BuildZoneDetail();
    }

    // What the catalog knows about this setting, under its row: what it does,
    // what it is measured in, what the official manager uses for it, and
    // anything worth reading before changing it.
    //
    // The default is sourced from the QuadStick Manager Program, not from a
    // device, and the firmware snapshot disagrees with it in places. So it is
    // named as the manager's value, never as what the hardware ships with.
    Control? PreferenceInfoLine(Binding b, PreferenceDefinition def, Border? typedCell, int col)
    {
        var line = new StackPanel
        {
            Spacing = 2, Margin = new Avalonia.Thickness(RowNumberWidth + 4, 0, 0, 2),
        };

        var parts = new List<string>();
        if (def.Description.Length > 0) parts.Add(def.Description);
        if (def.Unit.Length > 0) parts.Add($"Measured in {def.Unit}.");
        if (def.Default is { Length: > 0 } suggested)
            parts.Add($"The QuadStick Manager Program uses {suggested}. Your device may hold something else.");
        if (parts.Count > 0)
        {
            var about = string.Join(" ", parts);
            var text = new TextBlock
            {
                Text = about, FontSize = Size("SmallSize"), Classes = { "secondary" },
                TextWrapping = TextWrapping.Wrap, MaxWidth = 700,
            };
            AutomationProperties.SetName(text, $"About {def.Label}: {about}");
            line.Children.Add(text);
        }

        if (def.Risk.Length > 0)
        {
            // "Careful:" carries the warning in words; the color only repeats it.
            var risk = new TextBlock
            {
                Text = $"Careful: {def.Risk}", FontSize = Size("SmallSize"), Classes = { "warn" },
                TextWrapping = TextWrapping.Wrap, MaxWidth = 700,
            };
            AutomationProperties.SetName(risk, $"Careful, {def.Label}: {def.Risk}");
            line.Children.Add(risk);
        }

        if (typedCell is not null) line.Children.Add(ExactValueButton(b.Row, col, def, typedCell));
        return line.Children.Count > 0 ? line : null;
    }

    // The way back to plain typing. The friendly controls only offer what the
    // official manager offers, and the device itself takes more than that, so
    // no value is ever locked away behind them. A button, not a right-click.
    Button ExactValueButton(int row, int col, PreferenceDefinition def, Border cell)
    {
        var button = new Button
        {
            Classes = { "quiet" }, Content = "Type an exact value",
            FontSize = Size("SmallSize"), HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(button, $"Type an exact value for {def.Label}");
        ToolTip.SetTip(button, "Swap the control for a plain box, for a value outside the tested range.");
        button.Click += (_, _) =>
        {
            var box = RawValueBox(row, col, $"Setting value for row {row}");
            cell.Child = box;
            button.IsVisible = false;
            Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Loaded);
        };
        return button;
    }
}
