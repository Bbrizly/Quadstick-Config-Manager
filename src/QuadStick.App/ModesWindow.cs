using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using QuadStick.Format;

namespace QuadStick.App;

// The one place modes are managed: add, rename, reorder, copy, delete.
//
// This replaces a "Mode..." menu that hid all of it behind a button and moved a
// mode by swapping it with the sheet next to it. A Preferences sheet between
// two modes froze both of them, which is how a tester ended up reporting that
// the first mode could not be organized. Here the list holds modes only, and a
// move steps over anything that is not a mode.
//
// Every row carries its own controls, so there is no selected row to keep in
// sync and nothing to learn: the row you can see is the mode you are changing.
public class ModesWindow : Window
{
    readonly MainWindow _owner;
    readonly StackPanel _rows = new() { Spacing = 8 };

    // The row whose delete button is armed, by sheet index. Deleting a mode
    // takes two clicks on the same button rather than a second window on top
    // of this one: a mis-aimed click costs nothing, and there is no modal to
    // get lost behind. -1 means nothing is armed.
    int _armedDelete = -1;

    // Not a mode, but it is a sheet you add and remove, and this is now the
    // only window that manages sheets at all.
    readonly Button _prefs = new()
    {
        Classes = { "quiet" },
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    public ModesWindow(MainWindow owner)
    {
        Classes.Add("dialog");
        _owner = owner;
        Title = Strings.Modes_Modes;
        // Wide enough for the whole row: the name box, the connection dropdown,
        // and the four round buttons after them. At 620 the last two were off
        // the edge with no scrollbar to reach them, and 740 still clipped the
        // delete button once the shell's own margins and the scrollbar were
        // counted. The row is a Grid now, so this is the width it wants rather
        // than the width it needs, but a floor still has to be set or the user
        // can drag the window narrower than the delete button.
        Width = Math.Min(880 * owner.UiScale, 1200);
        Height = Math.Min(560 * owner.UiScale, 900);
        MinWidth = Math.Min(720 * owner.UiScale, 1200);
        MinHeight = Math.Min(400 * owner.UiScale, 900);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var add = new Button
        {
            Content = Strings.Modes_AddMode,
            Classes = { "quiet" },
            FontSize = Size("BodySize"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(add, Strings.Modes_AddAMode);
        add.Click += (_, _) => AddMode();
        _prefs.Click += (_, _) => { _owner.AddPreferencesSheetToFile(); Build(); };

        var close = new Button
        {
            Content = Strings.Modes_Done, Classes = { "primary" }, IsCancel = true,
            FontSize = Size("SubheadSize"), Padding = new Thickness(28, 12), MinWidth = 150,
        };
        AutomationProperties.SetName(close, Strings.Modes_CloseModes);
        close.Click += (_, _) => Close();
        // A dialog can open with the keyboard still on the window behind it,
        // and then Escape never reaches this one. Focusing a real control on
        // open pulls the keyboard in from the first key press.
        Opened += (_, _) => close.Focus();

        var body = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = Strings.Modes_AModeIsAFull,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = Size("BodySize"),
                    Classes = { "muted" },
                },
                new ScrollViewer
                {
                    Content = _rows,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    MaxHeight = 300,
                },
                add,
                _prefs,
            },
        };

        Content = MainWindow.DialogShell(this, MainWindow.ZoomWrap(new DockPanel
        {
            Children =
            {
                new Border
                {
                    [DockPanel.DockProperty] = Dock.Bottom,
                    Padding = new Thickness(24, 12),
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { close },
                    },
                },
                new ScrollViewer { Content = body },
            },
        }, owner.UiScale));

        Build();
    }

    static double Size(string tokenKey) => (double)Application.Current!.FindResource(tokenKey)!;

    // The Preferences sheet is listed with the modes, in file order. It is not a
    // mode, but it is a sheet you can move, and hiding it made its position
    // invisible: a mode that moved "past" it seemed to jump two slots.
    // The Infrared sheet stays out; it is not ours to reorder.
    List<(ModeSheet Sheet, int Index)> Modes() =>
        _owner.OpenFile is null ? new()
        : _owner.OpenFile.Document.Sheets
            .Select((s, i) => (Sheet: s, Index: i))
            .Where(t => t.Sheet.Type != SheetType.Infrared)
            .ToList();

    // Rebuilding detaches the name boxes, and a detached box raises LostFocus.
    // That would commit its old text against a row number the rebuild has just
    // changed, renaming whichever mode now wears that number. Nothing commits
    // while this is true.
    bool _rebuilding;

    // An armed delete is remembered by sheet number, so any rebuild that can
    // renumber the sheets has to disarm it, or the confirmation lands on a
    // different mode than the one it was aimed at. Only the click that arms it
    // asks to keep it.
    //
    // The list is rebuilt whole after every change: it is a handful of rows,
    // and rebuilding is the only way a row's position number, its arrows and
    // its spoken names can never drift from the file.
    void Build(bool keepArmed = false)
    {
        if (!keepArmed) _armedDelete = -1;
        _rebuilding = true;
        _rows.Children.Clear();
        var modes = Modes();
        // A row's position in the list and its number as a mode are two
        // different things once the preferences sheet sits among them: the
        // arrows work on the position, the spoken name says "mode 3".
        int ordinal = 0;
        for (int p = 0; p < modes.Count; p++)
        {
            bool isMode = modes[p].Sheet.Type == SheetType.ProfileName;
            if (isMode) ordinal++;
            _rows.Children.Add(Row(modes[p].Sheet, modes[p].Index, p, modes.Count, isMode ? ordinal : 0));
        }

        var sheets = _owner.OpenFile?.Document.Sheets;
        // Removing the sheet is now the ✕ on its own row, so this button only
        // ever adds one, and it hides once the sheet is in the list above.
        bool hasPrefs = sheets?.Any(s => s.Type == SheetType.Preferences) ?? false;
        _prefs.Content = Strings.Modes_AddAPreferencesSheet;
        _prefs.FontSize = Size("BodySize");
        _prefs.IsVisible = sheets != null && !hasPrefs;
        AutomationProperties.SetName(_prefs,
            Strings.Modes_AddAPreferencesSheetWhere);
        _rebuilding = false;
    }

    int ModeCount() => Modes().Count(t => t.Sheet.Type == SheetType.ProfileName);

    Control Row(ModeSheet sheet, int sheetIndex, int position, int total, int ordinal)
    {
        bool isPrefs = sheet.Type == SheetType.Preferences;
        // Two modes are allowed to share a name, and the device tells them apart
        // by their order alone. The number is not repeated into every button
        // label here because the row already carries it twice: the position sits
        // in the first column, and the name box announces itself as "Name of
        // mode N" and takes focus before any of these buttons.
        var name = isPrefs ? Strings.Modes_ThePreferencesSheet
            : sheet.ModeName.Length > 0 ? sheet.ModeName : $"Mode {ordinal}";

        // What a screen reader says, which is not what a copy of this mode is
        // named. Duplicate writes name into the file, and the device renders a
        // name as CP437 and falls back to the mangled 8.3 name when it cannot,
        // so the default above stays English while this one translates.
        var spoken = isPrefs ? Strings.Modes_ThePreferencesSheet
            : sheet.ModeName.Length > 0 ? sheet.ModeName
            : string.Format(CultureInfo.CurrentCulture, Strings.Modes_UnnamedModeNumber, ordinal);

        // The preferences sheet has no name to type: the device finds it by the
        // keyword "Preferences" alone. A label sits where the name box would.
        Control label = isPrefs
            ? new TextBlock
            {
                Text = Strings.Modes_PreferencesDeviceSettings,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = Size("BodySize"),
                Classes = { "muted" },
            }
            : NameBox(sheet, sheetIndex, ordinal);

        // The same move, copy and delete controls the editor's rows use. They
        // used to be typed characters here and drawn icons there, which made
        // one job look like two. See RowControls.
        var up = Wire(RowControls.Move(true, string.Format(CultureInfo.CurrentCulture, Strings.Modes_MoveNameUp, spoken)), position > 0, () => Move(sheetIndex, -1));
        var down = Wire(RowControls.Move(false, string.Format(CultureInfo.CurrentCulture, Strings.Modes_MoveNameDown, spoken)), position < total - 1, () => Move(sheetIndex, 1));
        var copy = Wire(RowControls.Icon("IconFiles", string.Format(CultureInfo.CurrentCulture, Strings.Modes_MakeACopyOfName, spoken)), !isPrefs, () => Duplicate(sheetIndex, name));
        // Only one preferences sheet is ever read, so a copy of it would be dead
        // weight in the file. The button stays in place, greyed: hidden, it took
        // its column with it and every button on that row slid left out of line
        // with the rows above and below.

        bool armed = _armedDelete == sheetIndex;
        // The last mode cannot go: a profile with no modes is not a profile.
        // The preferences sheet is never the last mode, so it always can.
        bool canDelete = isPrefs || ModeCount() > 1;
        var delete = armed
            ? TextButton(Strings.Modes_ReallyDelete, string.Format(CultureInfo.CurrentCulture, Strings.Modes_ReallyDeleteName, spoken), canDelete, () => Delete(sheetIndex))
            : Wire(RowControls.Delete(string.Format(CultureInfo.CurrentCulture, Strings.Modes_DeleteName, spoken)), canDelete, () => { _armedDelete = sheetIndex; Build(keepArmed: true); });
        delete.Classes.Add("danger");

        // A Grid, not a StackPanel. Every fixed width added up to more than the
        // window and the delete button was cut in half by the edge; the name
        // column gives way now and nothing at the end can be pushed off.
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("28,*,Auto,Auto,Auto,Auto,Auto"),
            // The preferences row has no name box to hold the keyboard, so the
            // row itself takes focus after a move and keeps Alt+arrow working.
            Focusable = true,
        };
        var position1 = new TextBlock
        {
            Text = string.Format(CultureInfo.CurrentCulture, Strings.Modes_Position1, position + 1),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = Size("BodySize"),
            Classes = { "muted" },
        };
        var channel = isPrefs ? new Panel { Width = ChannelBoxWidth } : ChannelBox(sheet, sheetIndex, ordinal);
        int column = 0;
        foreach (var cell in new[] { position1, label, channel, up, down, copy, delete })
        {
            cell.Margin = new Thickness(column == 0 ? 0 : 8, 0, 0, 0);
            Grid.SetColumn(cell, column++);
            row.Children.Add(cell);
        }
        // Alt with an arrow moves the row from anywhere on it, including the
        // preferences row, which has no name box to hold the keyboard.
        row.KeyDown += (_, e) =>
        {
            if (e.KeyModifiers != KeyModifiers.Alt || e.Key is not (Key.Up or Key.Down)) return;
            e.Handled = true;
            Move(sheetIndex, e.Key == Key.Up ? -1 : 1);
        };
        return row;
    }

    // Which connection a mode's outputs travel over, column C of its header
    // row. It was readable, warned about and never settable: a profile could
    // only get a Bluetooth mode by being imported from somebody who already had
    // one. The preferences sheet has no channel, so its row holds a gap of the
    // same width and the columns stay lined up.
    const double ChannelBoxWidth = 210;

    // Blank is a real value, not a missing one: Configuration.c:528 falls back
    // to USB for a blank or unrecognised word. Anything already in the cell
    // that is not one of these stays in the list exactly as typed, so opening
    // this window can never quietly change somebody's file.
    static readonly (string Token, string Label)[] ChannelChoices =
    {
        ("", Strings.Modes_NotSetUSBCable),
        ("usb", Strings.Modes_USBCable),
        ("bluetooth", "Bluetooth"),
        ("both", Strings.Modes_USBAndBluetooth),
        ("none", Strings.Modes_NeitherSendsNothing),
    };

    Control ChannelBox(ModeSheet sheet, int sheetIndex, int ordinal)
    {
        var items = ChannelChoices.ToList();
        if (!items.Any(c => c.Token == sheet.Channel))
            items.Insert(0, (sheet.Channel, string.Format(CultureInfo.CurrentCulture, Strings.Modes_SheetChannelNotAWord, sheet.Channel)));

        var combo = new ComboBox
        {
            ItemsSource = items,
            SelectedItem = items.First(c => c.Token == sheet.Channel),
            Width = ChannelBoxWidth,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = Size("BodySize"),
            ItemTemplate = new FuncDataTemplate<(string Token, string Label)>((c, _) =>
                new TextBlock { Text = c.Label, FontSize = Size("BodySize") }, true),
        };
        AutomationProperties.SetName(combo,
            string.Format(CultureInfo.CurrentCulture, Strings.Modes_ConnectionForModeOrdinalWhere, ordinal));
        combo.SelectionChanged += (_, _) =>
        {
            if (_rebuilding || _owner.OpenFile is null) return;
            if (combo.SelectedItem is not ValueTuple<string, string> picked) return;
            if (!_owner.OpenFile.SetModeChannel(sheetIndex, picked.Item1)) return;
            _owner.ModesChanged(sheetIndex, picked.Item1.Length == 0
                ? Strings.Modes_ConnectionClearedTheDeviceFalls
                : string.Format(CultureInfo.CurrentCulture, Strings.Modes_ConnectionSetToPickedItem2, picked.Item2.ToLowerInvariant()));
            Build();
        };
        return combo;
    }

    TextBox NameBox(ModeSheet sheet, int sheetIndex, int ordinal)
    {
        var box = new TextBox
        {
            Text = sheet.ModeName,
            MinWidth = 160,
            // A tester renamed a mode to a whole paragraph; nothing past this
            // fits the mode picker or the side tube's speech anyway.
            MaxLength = 40,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(box, string.Format(CultureInfo.CurrentCulture, Strings.Modes_NameOfModeOrdinal, ordinal));
        // Commit on lost focus, the same rule the editor's cells follow.
        box.LostFocus += (_, _) => Rename(sheetIndex, box.Text ?? "");
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Rename(sheetIndex, box.Text ?? ""); };
        return box;
    }

    static Button Wire(Button b, bool enabled, Action onClick)
    {
        b.IsEnabled = enabled;
        b.Click += (_, _) => onClick();
        return b;
    }

    Button TextButton(string label, string spokenName, bool enabled, Action onClick)
    {
        var b = new Button
        {
            Content = label,
            IsEnabled = enabled,
            FontSize = Size("BodySize"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(b, spokenName);
        b.Click += (_, _) => onClick();
        return b;
    }

    void Rename(int sheetIndex, string text)
    {
        if (_rebuilding || _owner.OpenFile is null) return;
        if (_owner.OpenFile.RenameMode(sheetIndex, text))
        {
            _owner.ModesChanged(sheetIndex, Strings.Modes_ModeRenamed);
            Build();
            return;
        }
        // A blank name is refused, and the box was left showing the blank while
        // the mode kept its old name underneath. Say so and put the name back,
        // or a screen reader reads the mode as having no name at all.
        if (text.Trim().Length == 0)
        {
            _owner.ModesChanged(sheetIndex, Strings.Modes_AModeNeedsAName);
            Build();
        }
    }

    void Move(int sheetIndex, int delta)
    {
        if (_owner.OpenFile is null) return;
        // Where the mode lands has to be worked out before the move, while the
        // old sheet numbers still mean something.
        int landed = FocusedSheetAfterMove(sheetIndex, delta);
        if (!_owner.OpenFile.MoveMode(sheetIndex, delta)) return;
        Build();
        _owner.ModesChanged(landed, Strings.Modes_ModeMoved);
        // Keep the keyboard on the mode that moved, so Alt with an arrow can be
        // pressed again straight away to move it further.
        FocusName(Modes().FindIndex(t => t.Index == landed));
    }

    // MoveMode swaps two sheet blocks, so the moved sheet now sits where its
    // neighbour was: the nearest listed sheet in that direction from where it
    // started. Same skip rule as MoveMode, or the focus lands on the wrong row.
    int FocusedSheetAfterMove(int fromSheetIndex, int delta)
    {
        var sheets = _owner.OpenFile!.Document.Sheets;
        int step = Math.Sign(delta);
        for (int i = fromSheetIndex + step; i >= 0 && i < sheets.Count; i += step)
            if (sheets[i].Type != SheetType.Infrared) return i;
        return fromSheetIndex;
    }

    void Duplicate(int sheetIndex, string name)
    {
        if (_owner.OpenFile is null) return;
        int idx = _owner.OpenFile.DuplicateMode(sheetIndex, name + " copy");
        if (idx < 0) return;
        Build();
        _owner.ModesChanged(idx, Strings.Modes_ModeCopied);
        FocusName(Modes().FindIndex(t => t.Index == idx));
    }

    void Delete(int sheetIndex)
    {
        if (_owner.OpenFile is null) return;
        bool prefs = _owner.OpenFile.Document.Sheets[sheetIndex].Type == SheetType.Preferences;
        if (!_owner.OpenFile.DeleteMode(sheetIndex)) { Build(); return; }
        Build();
        _owner.ModesChanged(Math.Max(0, sheetIndex - 1),
            prefs ? Strings.Modes_PreferencesSheetRemovedControlZ : Strings.Modes_ModeDeletedControlZUndoes);
    }

    void AddMode()
    {
        // The same add the plus at the top of the modes list does, naming and
        // selecting included: one job, one implementation.
        int idx = _owner.AddModeAndOpen();
        if (idx < 0) return;
        Build();
        // No naming dialog: the new row is already there, so put the keyboard
        // in its name box and let the name be typed over.
        FocusName(Modes().FindIndex(t => t.Index == idx));
    }

    void FocusName(int position)
    {
        if (position < 0 || position >= _rows.Children.Count) return;
        // Focus after layout, or the box does not exist to take it yet.
        Dispatcher.UIThread.Post(() =>
        {
            var row = (Control)_rows.Children[position];
            var box = row.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault();
            if (box is null) { row.Focus(); return; }
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Loaded);
    }
}
