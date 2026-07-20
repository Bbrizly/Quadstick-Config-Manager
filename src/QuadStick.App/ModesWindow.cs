using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
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
        _owner = owner;
        Title = "Modes";
        Width = Math.Min(620 * owner.UiScale, 1100);
        Height = Math.Min(520 * owner.UiScale, 900);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var add = new Button
        {
            Content = "+ Add mode",
            Classes = { "quiet" },
            FontSize = Size("BodySize"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(add, "Add a mode");
        add.Click += (_, _) => AddMode();
        _prefs.Click += (_, _) => { _prefsClick(); Build(); };

        var close = new Button
        {
            Content = "Done", Classes = { "primary" }, IsCancel = true,
            FontSize = Size("SubheadSize"), Padding = new Thickness(28, 12), MinWidth = 150,
        };
        AutomationProperties.SetName(close, "Close modes");
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
                    Text = "A mode is a full layout of your inputs. Switch between them while playing with the side tube, or with increment_mode and decrement_mode.",
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

        Content = MainWindow.ZoomWrap(new DockPanel
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
        }, owner.UiScale);

        Build();
    }

    static double Size(string tokenKey) => (double)Application.Current!.FindResource(tokenKey)!;

    List<(ModeSheet Sheet, int Index)> Modes() =>
        _owner.OpenFile is null ? new()
        : _owner.OpenFile.Document.Sheets
            .Select((s, i) => (Sheet: s, Index: i))
            .Where(t => t.Sheet.Type == SheetType.ProfileName)
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
        for (int p = 0; p < modes.Count; p++)
            _rows.Children.Add(Row(modes[p].Sheet, modes[p].Index, p, modes.Count));

        var sheets = _owner.OpenFile?.Document.Sheets;
        int prefsIndex = sheets?.ToList().FindIndex(s => s.Type == SheetType.Preferences) ?? -1;
        _prefs.Content = prefsIndex < 0 ? "+ Add a preferences sheet" : "Remove the preferences sheet";
        _prefs.FontSize = Size("BodySize");
        _prefs.IsEnabled = sheets != null;
        AutomationProperties.SetName(_prefs, prefsIndex < 0
            ? "Add a preferences sheet, where device settings like sip and puff pressure live"
            : "Remove the preferences sheet and its device settings");
        _prefsClick = prefsIndex < 0
            ? _owner.AddPreferencesSheetToFile
            : () => { _owner.OpenFile!.DeleteMode(prefsIndex); _owner.ModesChanged(0, "Preferences sheet removed. Control Z undoes it."); };
        _rebuilding = false;
    }

    Action _prefsClick = () => { };

    Control Row(ModeSheet sheet, int sheetIndex, int position, int total)
    {
        var name = sheet.ModeName.Length > 0 ? sheet.ModeName : $"Mode {position + 1}";

        var box = new TextBox
        {
            Text = sheet.ModeName,
            Width = 240,
            // A tester renamed a mode to a whole paragraph; nothing past this
            // fits the mode picker or the side tube's speech anyway.
            MaxLength = 40,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(box, $"Name of mode {position + 1}");
        // Commit on lost focus, the same rule the editor's cells follow.
        box.LostFocus += (_, _) => Rename(sheetIndex, box.Text ?? "");
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Rename(sheetIndex, box.Text ?? "");
            // Alt with an arrow moves the mode without touching the mouse.
            else if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.Up or Key.Down)
            {
                e.Handled = true;
                Move(sheetIndex, e.Key == Key.Up ? -1 : 1);
            }
        };

        var up = IconButton("▲", $"Move {name} up", position > 0, () => Move(sheetIndex, -1));
        var down = IconButton("▼", $"Move {name} down", position < total - 1, () => Move(sheetIndex, 1));
        var copy = IconButton("⧉", $"Make a copy of {name}", true, () => Duplicate(sheetIndex, name));

        bool armed = _armedDelete == sheetIndex;
        // The last mode cannot go: a profile with no modes is not a profile.
        bool canDelete = total > 1;
        var delete = armed
            ? TextButton("Really delete?", $"Really delete {name}", canDelete, () => Delete(sheetIndex))
            : IconButton("✕", $"Delete {name}", canDelete, () => { _armedDelete = sheetIndex; Build(keepArmed: true); });
        delete.Classes.Add("danger");

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = $"{position + 1}.",
                    Width = 28,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = Size("BodySize"),
                    Classes = { "muted" },
                },
                box, up, down, copy, delete,
            },
        };
    }

    Button IconButton(string glyph, string spokenName, bool enabled, Action onClick)
    {
        var b = new Button
        {
            Content = glyph,
            Classes = { "icon" },
            IsEnabled = enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(b, spokenName);
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
        if (!_owner.OpenFile.RenameMode(sheetIndex, text)) return;
        _owner.ModesChanged(sheetIndex, "Mode renamed.");
        Build();
    }

    void Move(int sheetIndex, int delta)
    {
        if (_owner.OpenFile is null) return;
        // Where the mode lands has to be worked out before the move, while the
        // old sheet numbers still mean something.
        int landed = FocusedSheetAfterMove(sheetIndex, delta);
        if (!_owner.OpenFile.MoveMode(sheetIndex, delta)) return;
        Build();
        _owner.ModesChanged(landed, "Mode moved.");
        // Keep the keyboard on the mode that moved, so Alt with an arrow can be
        // pressed again straight away to move it further.
        FocusName(Modes().FindIndex(t => t.Index == landed));
    }

    // MoveMode swaps two mode blocks, so the moved mode now sits where its
    // neighbour was: the nearest mode in that direction from where it started.
    int FocusedSheetAfterMove(int fromSheetIndex, int delta)
    {
        var sheets = _owner.OpenFile!.Document.Sheets;
        int step = Math.Sign(delta);
        for (int i = fromSheetIndex + step; i >= 0 && i < sheets.Count; i += step)
            if (sheets[i].Type == SheetType.ProfileName) return i;
        return fromSheetIndex;
    }

    void Duplicate(int sheetIndex, string name)
    {
        if (_owner.OpenFile is null) return;
        int idx = _owner.OpenFile.DuplicateMode(sheetIndex, name + " copy");
        if (idx < 0) return;
        Build();
        _owner.ModesChanged(idx, "Mode copied.");
        FocusName(Modes().FindIndex(t => t.Index == idx));
    }

    void Delete(int sheetIndex)
    {
        if (_owner.OpenFile is null) return;
        if (!_owner.OpenFile.DeleteMode(sheetIndex)) { Build(); return; }
        Build();
        _owner.ModesChanged(Math.Max(0, sheetIndex - 1), "Mode deleted. Control Z undoes it.");
    }

    void AddMode()
    {
        if (_owner.OpenFile is null) return;
        // Two modes with the same name are legal but unreadable in the picker,
        // so count past any name already taken.
        var taken = Modes().Select(t => t.Sheet.ModeName).ToHashSet();
        int n = Modes().Count + 1;
        while (taken.Contains($"Mode {n}")) n++;
        int idx = _owner.OpenFile.AddModeSheet($"Mode {n}");
        Build();
        _owner.ModesChanged(idx, "Mode added.");
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
            box?.Focus();
            box?.SelectAll();
        }, DispatcherPriority.Loaded);
    }
}
