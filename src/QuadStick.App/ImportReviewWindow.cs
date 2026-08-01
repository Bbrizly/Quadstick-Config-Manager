using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using QuadStick.Format;

namespace QuadStick.App;

// Shown once, right after an import, to answer the only question an imported
// spreadsheet raises: did we understand it?
//
// The sheet already worked on the user's QuadStick, so the app is the newcomer
// here and has to show its work. Before this window the app said what it had
// done in a status line that scrolled away, and a real user read a correct
// import as a broken one: a whole mode had been left behind and the only trace
// on screen was one yellow cell.
//
// Two views. The simple one names what was lost, what the device will do
// differently, and what came in, and offers the few decisions a person has to
// make. The advanced one shows the grid with the app's reading painted onto
// it, which is also the only place the file format is ever taught. A clean
// import asks nothing and still offers the advanced view.
//
// Decisions apply the moment they are pressed, against the profile already open
// behind this window, so the row can say what happened instead of leaving the
// user to guess whether a button took. The most recent one keeps an Undo.
public class ImportReviewWindow : Window
{
    readonly MainWindow _owner;
    readonly ProfileFile _file;
    readonly string _source;
    readonly string? _limitation;
    readonly List<SkippedTab> _skipped;
    // decision key -> what we did, and how to put the question back. Undoing
    // the file is only half of it: a tab that came out of the skipped list has
    // to go back on it, or its rows would vanish from the window with nothing
    // said about them.
    readonly Dictionary<string, (string Text, Action Restore)> _settled = new();
    readonly StackPanel _body;
    readonly TextBlock _heading;
    readonly TextBlock _subheading;
    readonly Button _advancedButton;
    readonly Button _done;
    string? _undoable; // the one decision Undo would reverse, newest first
    bool _advanced;
    Control? _firstDecision;

    /// <param name="source">What was imported, named the way the user named it.</param>
    /// <param name="limitation">Set when the import could not see the whole
    /// workbook, so the window never calls a partial read a clean one.</param>
    public ImportReviewWindow(MainWindow owner, ProfileFile file, string source,
        IReadOnlyList<SkippedTab> skipped, string? limitation = null)
    {
        _owner = owner;
        _file = file;
        _source = source;
        _limitation = limitation;
        _skipped = skipped.ToList();

        Title = "Import review";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _heading = new TextBlock { FontSize = Size("SubheadSize"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        _subheading = new TextBlock { FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap, Classes = { "muted" }, Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetLiveSetting(_subheading, AutomationLiveSetting.Polite);

        _advancedButton = new Button { MinWidth = 130, VerticalAlignment = VerticalAlignment.Top };
        _advancedButton.Click += (_, _) => { _advanced = !_advanced; Resize(); Build(); _advancedButton.Focus(); };

        var titles = new StackPanel();
        titles.Children.Add(_heading);
        titles.Children.Add(_subheading);
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(titles, 0);
        Grid.SetColumn(_advancedButton, 1);
        top.Children.Add(titles);
        top.Children.Add(_advancedButton);

        _body = new StackPanel { Spacing = 18, Margin = new Thickness(0, 18, 0, 0) };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _body,
        };

        // Both, deliberately: every decision has already been applied and shown,
        // so Enter and Esc mean the same safe thing, which is "I am finished
        // looking". Neither one can silently commit something unseen.
        _done = new Button { Content = "Done", Classes = { "primary" }, MinWidth = 140, IsDefault = true, IsCancel = true };
        AutomationProperties.SetName(_done, "Close the import review");
        _done.Click += (_, _) => Close();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { _done },
        };

        var panel = new DockPanel { LastChildFill = true, Margin = new Thickness(24) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(top);
        panel.Children.Add(buttons);
        panel.Children.Add(scroll);

        Content = MainWindow.ZoomWrap(panel, owner.UiScale);
        Resize();
        Build();
        // Land on the first thing that needs a person, not on the way out.
        Opened += (_, _) => (_firstDecision ?? _done).Focus();
    }

    // A fresh dialog may have no focused element, so handle Esc on the window.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape) { e.Handled = true; Close(); }
    }

    void Resize()
    {
        Width = Math.Min((_advanced ? 900 : 600) * _owner.UiScale, 1400);
        Height = Math.Min((_advanced ? 700 : 540) * _owner.UiScale, 900);
    }

    // Everything here is derived from the live profile, so a decision that
    // changes the file changes what is on screen by rebuilding.
    void Build()
    {
        _firstDecision = null;
        var open = OpenIssues();
        var errors = open.Where(i => i.Severity == Severity.Error).ToList();
        var warnings = open.Where(i => i.Severity == Severity.Warning).ToList();
        // A partial read is never clean, however few issues the part that
        // arrived happens to have.
        bool clean = _limitation is null && _skipped.Count == 0 && open.Count == 0;

        _heading.Text = clean ? "Your sheet came in clean." : "We read your sheet.";
        _subheading.Text = clean
            ? $"{Summary()} No profile data was skipped."
            : $"{_source}: {Summary()}";
        _advancedButton.Content = _advanced ? "Simple view" : "Advanced";
        AutomationProperties.SetName(_advancedButton, _advanced
            ? "Go back to the simple view"
            : "Show the spreadsheet with the app's reading marked on it");

        _body.Children.Clear();

        if (_limitation is not null)
            _body.Children.Add(Section("Only part of the spreadsheet could be read",
                new[] { Line(_limitation, null) }));

        if (errors.Count > 0)
            _body.Children.Add(Section(
                Count(errors.Count, "line the QuadStick would not read", "lines the QuadStick would not read"),
                errors.Select(i => Line($"{i.Cell}   {i.Message}", i.Fix))));

        if (_skipped.Count > 0)
            _body.Children.Add(Section(
                Count(_skipped.Count, "tab did not come in", "tabs did not come in"),
                _skipped.Select(SkippedRow)));

        // One heading for every warning, because the messages already say which
        // way each one goes. Splitting "ignores it" from "reads it as something
        // else" under separate headings would need every issue in the format
        // layer tagged with its consequence, and a wrong heading over an
        // accurate sentence is worse than a broad one.
        if (warnings.Count > 0)
            _body.Children.Add(Section(
                Count(warnings.Count, "cell the QuadStick will treat differently than it reads",
                                      "cells the QuadStick will treat differently than they read"),
                warnings.Select(WarningRow)));

        foreach (var key in _settled.Keys.ToList())
            _body.Children.Add(SettledRow(key));

        _body.Children.Add(Section("What came in", new[] { ModeTable() }));

        if (_advanced) _body.Children.Add(AdvancedView());
    }

    // Issues the user has not already answered. A decision that ends in "leave
    // it" is an answer: the cell keeps its warning in the editor, where a
    // warning belongs, and stops being a question here.
    List<Issue> OpenIssues() => _file.Issues.Where(i => !_settled.ContainsKey(IssueKey(i))).ToList();

    static string IssueKey(Issue i) => $"{i.Cell}|{i.Kind}|{i.Message}";

    string Summary()
    {
        var modes = _file.Document.Sheets.Count(s => s.Type == SheetType.ProfileName);
        var bindings = _file.Document.Sheets.Sum(s => s.Bindings.Count);
        var prefs = _file.Document.Sheets.Any(s => s.Type == SheetType.Preferences) ? ", and your preferences" : "";
        return $"{Count(modes, "mode", "modes")} and {Count(bindings, "binding", "bindings")}{prefs}.";
    }

    static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    // ---- decisions ----

    Control SkippedRow(SkippedTab tab)
    {
        // Named for what it does, not for what the user wishes were true. The
        // QuadStick skips this tab today, exactly as the app did, so bringing
        // it in is a new mode the device has never run, not a rescue of one it
        // was running.
        var add = new Button { Content = "Add it as a working mode", MinWidth = 190 };
        AutomationProperties.SetName(add,
            $"Add the tab {tab.Name} to this profile as a working mode, by writing Profile Name over its cell A1");
        add.Click += (_, _) =>
        {
            var index = _file.AppendSheetRows(Xlsx.RepairedAsMode(tab));
            if (index < 0) return;
            _skipped.Remove(tab);
            Settle($"tab:{tab.Name}",
                $"\"{tab.Name}\" is now a mode in this profile. Its cell A1 says \"Profile Name\" where your text was.",
                () => _skipped.Add(tab), touchedTheFile: true);
            _owner.ModesChanged(index, $"Added the \"{tab.Name}\" tab as a mode.");
            Build();
        };

        var leave = new Button { Content = "Leave it out", MinWidth = 130 };
        AutomationProperties.SetName(leave, $"Leave the tab {tab.Name} out of this profile, as the QuadStick does");
        leave.Click += (_, _) =>
        {
            _skipped.Remove(tab);
            Settle($"tab:{tab.Name}", $"\"{tab.Name}\" was left out, the same as your QuadStick does today.",
                () => _skipped.Add(tab), touchedTheFile: false);
            Build();
        };

        return Line(
            $"\"{tab.Name}\"   cell A1 has to say \"Profile Name\". Yours has other text in it, so this "
            + "tab is not a mode to the app or to your QuadStick. Neither one is running it today.",
            "Adding it changes cell A1. Everything else in the tab comes in as you wrote it.",
            add, leave);
    }

    Control WarningRow(Issue issue)
    {
        var (row, col) = ParseCell(issue.Cell);
        // Only the unknown-input warning has good answers. Every other warning
        // is stated and left alone: inventing a one-click fix for a problem the
        // app does not really understand is how a config gets quietly wrecked.
        if (issue.Kind != IssueKind.UnknownInput || row == 0)
            return Line($"{issue.Cell}   {issue.Message}", issue.Fix);

        var word = _file.GetCell(row, col).Trim();
        var buttons = new List<Button>();

        if (_file.CanMoveInputToActionName(row, col))
        {
            var name = new Button { Content = "Use as this row's name", MinWidth = 180 };
            AutomationProperties.SetName(name,
                $"Keep \"{word}\" as this row's own name, in column L, where the QuadStick never looks");
            name.Click += (_, _) =>
            {
                if (!_file.MoveInputToActionName(row, col)) return;
                Settle(IssueKey(issue), $"{issue.Cell}   \"{word}\" is now this row's own name, in column L.", () => { }, touchedTheFile: true);
                _owner.ModesChanged(SheetIndexOf(row), $"\"{word}\" is now this row's name.");
                Build();
            };
            buttons.Add(name);
        }

        var note = new Button { Content = "Move to notes", MinWidth = 140 };
        AutomationProperties.SetName(note, $"Move \"{word}\" into the notes column, where the QuadStick never looks");
        note.Click += (_, _) =>
        {
            _file.MoveInputToNotes(row, col);
            Settle(IssueKey(issue), $"{issue.Cell}   \"{word}\" moved into the notes column.", () => { }, touchedTheFile: true);
            _owner.ModesChanged(SheetIndexOf(row), $"Moved \"{word}\" into the notes column.");
            Build();
        };
        buttons.Add(note);

        var leave = new Button { Content = "Leave it", MinWidth = 110 };
        AutomationProperties.SetName(leave, $"Leave \"{word}\" where it is, and keep its warning in the editor");
        leave.Click += (_, _) =>
        {
            Settle(IssueKey(issue), $"{issue.Cell}   \"{word}\" left as it is. The QuadStick ignores it.", () => { }, touchedTheFile: false);
            Build();
        };
        buttons.Add(leave);

        return Line($"{issue.Cell}   {issue.Message}", issue.Fix, buttons.ToArray());
    }

    // Undo is offered on the newest change only, because ProfileFile's undo is
    // a stack: reversing an older one would take the newer ones with it without
    // saying so. An answer that changed nothing has nothing to undo, so it is
    // recorded with a restore that only puts the question back.
    void Settle(string key, string text, Action restore, bool touchedTheFile)
    {
        _settled[key] = (text, restore);
        _undoable = touchedTheFile ? key : null;
    }

    Control SettledRow(string key)
    {
        var (text, restore) = _settled[key];
        var panel = new StackPanel { Spacing = 8 };
        var line = new TextBlock { Text = text, FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap };
        BindBrush(line, TextBlock.ForegroundProperty, "Success");
        panel.Children.Add(line);

        if (key == _undoable)
        {
            var undo = new Button { Content = "Undo", MinWidth = 110, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(undo, $"Undo this change and ask again: {text}");
            undo.Click += (_, _) =>
            {
                if (!_file.Undo()) return;
                _settled.Remove(key);
                _undoable = null;
                restore();
                _owner.ModesChanged(0, "Undid the last import change.");
                Build();
            };
            panel.Children.Add(undo);
        }
        return panel;
    }

    int SheetIndexOf(int row)
    {
        var sheets = _file.Document.Sheets;
        for (int i = sheets.Count - 1; i >= 0; i--)
            if (sheets[i].StartRow <= row) return i;
        return 0;
    }

    // ---- simple view pieces ----

    Control Section(string title, IEnumerable<Control> rows)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = title, FontSize = Size("BodySize"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
        });
        foreach (var r in rows) panel.Children.Add(r);
        return panel;
    }

    Control Line(string text, string? fix, params Button[] actions)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(12, 0, 0, 0) };
        panel.Children.Add(new TextBlock { Text = text, FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap });
        if (fix is { Length: > 0 })
            panel.Children.Add(new TextBlock
            {
                Text = fix, FontSize = Size("SmallSize"), TextWrapping = TextWrapping.Wrap, Classes = { "muted" },
            });
        if (actions.Length > 0)
        {
            var row = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var a in actions) { a.Margin = new Thickness(0, 0, 8, 8); row.Children.Add(a); }
            panel.Children.Add(row);
            _firstDecision ??= actions[0];
        }
        return panel;
    }

    Control ModeTable()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            Margin = new Thickness(12, 0, 0, 0),
        };
        int r = 0;
        foreach (var s in _file.Document.Sheets)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            bool prefs = s.Type == SheetType.Preferences;
            var name = new TextBlock
            {
                Text = prefs ? "Preferences" : DisplayName(s),
                FontSize = Size("BodySize"), Margin = new Thickness(0, 0, 24, 4), TextWrapping = TextWrapping.Wrap,
            };
            var count = new TextBlock
            {
                Text = Count(s.Bindings.Count, prefs ? "setting" : "binding", prefs ? "settings" : "bindings"),
                FontSize = Size("BodySize"), Classes = { "muted" }, Margin = new Thickness(0, 0, 0, 4),
            };
            Grid.SetRow(name, r); Grid.SetColumn(name, 0);
            Grid.SetRow(count, r); Grid.SetColumn(count, 1);
            grid.Children.Add(name);
            grid.Children.Add(count);
            r++;
        }
        return grid;
    }

    static string DisplayName(ModeSheet s) => s.ModeName.Trim().Length > 0 ? s.ModeName.Trim() : "(unnamed mode)";

    // ---- advanced view ----

    // The grid as the app read it, in the colour language the editor already
    // uses: a tint means the device reads that cell. No tint means it never
    // looks there, which is the whole point of the notes and name columns and
    // of everything past column L.
    Control AdvancedView()
    {
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(new TextBlock
        {
            Text = "Your spreadsheet, with what we read marked on it. A tinted cell is one the QuadStick "
                 + "reads. A plain cell is one it never looks at, so notes and your own names for rows are "
                 + "safe there. Nothing here is editable; the editor behind this window is.",
            FontSize = Size("SmallSize"), TextWrapping = TextWrapping.Wrap, Classes = { "muted" },
        });
        panel.Children.Add(Legend());
        panel.Children.Add(RawGrid(_file.Grid, dimmed: false));
        foreach (var tab in _skipped)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"\"{tab.Name}\", left out because cell A1 does not name a kind of sheet:",
                FontSize = Size("BodySize"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(RawGrid(tab.Rows, dimmed: true));
        }
        return panel;
    }

    Control Legend()
    {
        var wrap = new WrapPanel();
        void Add(Control c) { c.Margin = new Thickness(0, 0, 10, 6); wrap.Children.Add(c); }
        Add(Swatch("A  output", OutputTint));
        Add(Swatch("B  function", FunctionTint));
        Add(Swatch("C to J  inputs, in order", InputTint));
        Add(Swatch("K  note   L  your name for the row   M on  never read", null));
        Add(Swatch("the QuadStick does not know this word", null, warn: true));

        // C to J is room, not a requirement, and the old chip read like a rule
        // the sheet had to satisfy. It is also where people learn that a second
        // input is a sequence and not a chord, so the correction belongs here
        // rather than only in the editor.
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(wrap);
        panel.Children.Add(new TextBlock
        {
            Text = "C to J is room for up to 8 inputs, not a requirement. Most rows use C on its own, "
                 + "and that is a plain trigger. When a row fills more than one, they are a sequence: "
                 + "you do them one after the other, left to right, and the last one fires the output. "
                 + "Blank cells in between are ignored.",
            FontSize = Size("SmallSize"), TextWrapping = TextWrapping.Wrap, Classes = { "muted" },
        });
        return panel;
    }

    Control Swatch(string text, string? tintKey, bool warn = false)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 4),
            BorderThickness = new Thickness(warn ? 2 : 1),
            Child = new TextBlock { Text = text, FontSize = Size("SmallSize") },
        };
        if (tintKey is not null) BindBrush(border, Border.BackgroundProperty, tintKey);
        BindBrush(border, Border.BorderBrushProperty, warn ? "Warning" : "SurfaceBorder");
        return border;
    }

    // The sheet's colour language, the same keys the editor uses.
    const string OutputTint = "OutputTint";
    const string FunctionTint = "FunctionTint";
    const string InputTint = "InputTint";

    // A whole profile is built at once rather than virtualized, which is fine
    // for the couple of hundred rows a real config runs to. Past that the tail
    // is dropped and said so, instead of freezing the window on a spreadsheet
    // nobody could read on screen anyway.
    const int MaxAdvancedRows = 400;

    Control RawGrid(IReadOnlyList<string[]> rows, bool dimmed)
    {
        int shown = Math.Min(rows.Count, MaxAdvancedRows);
        int cols = Math.Max(Parser.ActionColumn + 1,
            shown == 0 ? 0 : rows.Take(shown).Max(r => r.Length));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // row numbers
        for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int c = 0; c < cols; c++)
        {
            var head = CellBox(ColumnLetter(c), null, false, ColumnMeaning(c), bold: true);
            Grid.SetRow(head, 0); Grid.SetColumn(head, c + 1);
            grid.Children.Add(head);
        }

        // Only rows the device treats as bindings carry the column tints. A
        // keyword or label row is not data, and colouring it would teach the
        // wrong thing about which cells mean something.
        var bindingRows = dimmed
            ? new HashSet<int>()
            : _file.Document.Sheets.SelectMany(s => s.Bindings).Select(b => b.Row).ToHashSet();
        var warned = dimmed
            ? new HashSet<(int, int)>()
            : _file.Issues.Select(i => ParseCell(i.Cell)).Where(x => x.Row > 0)
                .Select(x => (x.Row, x.Col)).ToHashSet();

        for (int r = 0; r < shown; r++)
        {
            int rowNumber = r + 1;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var num = CellBox(rowNumber.ToString(), null, false, $"row {rowNumber}", muted: true);
            Grid.SetRow(num, r + 1); Grid.SetColumn(num, 0);
            grid.Children.Add(num);

            bool isBinding = bindingRows.Contains(rowNumber);
            for (int c = 0; c < cols; c++)
            {
                var text = c < rows[r].Length ? rows[r][c] ?? "" : "";
                var warn = warned.Contains((rowNumber, c));
                var tint = isBinding && !dimmed ? TintFor(c) : null;
                // Colour alone cannot carry any of this, so the accessible name
                // spells out the cell, its text, and what the device does with
                // it. Reading down a column that way is how this view works
                // without sight.
                var described = $"{ColumnLetter(c)}{rowNumber}, "
                    + (text.Length > 0 ? $"\"{text}\", " : "empty, ")
                    + (dimmed ? "not read, this tab was left out"
                       : warn ? $"{ColumnMeaning(c)}, the QuadStick does not know this word"
                       : isBinding ? ColumnMeaning(c)
                       : "not a binding row");
                var cell = CellBox(text, tint, warn, described, muted: dimmed);
                Grid.SetRow(cell, r + 1); Grid.SetColumn(cell, c + 1);
                grid.Children.Add(cell);
            }
        }

        if (rows.Count <= shown) return grid;
        var wrapper = new StackPanel { Spacing = 8 };
        wrapper.Children.Add(grid);
        wrapper.Children.Add(new TextBlock
        {
            Text = $"Showing the first {shown} rows of {rows.Count}. The rest imported the same way.",
            FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
        });
        return wrapper;
    }

    static string ColumnMeaning(int col) => col switch
    {
        0 => "output",
        1 => "function",
        >= 2 and < Parser.KeywordColumns => "input",
        Parser.KeywordColumns => "note, never read by the QuadStick",
        Parser.ActionColumn => "your name for the row, never read by the QuadStick",
        _ => "never read by the QuadStick",
    };

    static string? TintFor(int col) => col switch
    {
        0 => OutputTint,
        1 => FunctionTint,
        >= 2 and < Parser.KeywordColumns => InputTint,
        _ => null,
    };

    Control CellBox(string text, string? tintKey, bool warn, string accessibleName, bool bold = false, bool muted = false)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = Size("SmallSize"),
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
            MaxWidth = 130,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (muted) label.Classes.Add("muted");
        var border = new Border
        {
            MinWidth = 46,
            Padding = new Thickness(5, 3),
            BorderThickness = new Thickness(warn ? 2 : 1),
            Child = label,
        };
        if (tintKey is not null) BindBrush(border, Border.BackgroundProperty, tintKey);
        BindBrush(border, Border.BorderBrushProperty, warn ? "Warning" : "SurfaceBorder");
        AutomationProperties.SetName(border, accessibleName);
        return border;
    }

    // A, B, ... Z, AA, AB. Spreadsheets number columns this way and so does
    // every issue this app reports, so the header has to match.
    internal static string ColumnLetter(int col)
    {
        var s = "";
        for (int n = col; n >= 0; n = n / 26 - 1) s = (char)('A' + n % 26) + s;
        return s;
    }

    /// <summary>The inverse: "H24" back to a 1-based row and a 0-based column.
    /// Row 0 means the text does not name a cell, which is how an issue about a
    /// whole file is told apart from one about a cell.</summary>
    internal static (int Row, int Col) ParseCell(string reference)
    {
        int i = 0;
        while (i < reference.Length && char.IsAsciiLetterUpper(reference[i])) i++;
        if (i == 0 || i == reference.Length) return (0, 0);
        if (!int.TryParse(reference.AsSpan(i), out var row) || row < 1) return (0, 0);
        int col = 0;
        for (int k = 0; k < i; k++) col = col * 26 + (reference[k] - 'A' + 1);
        return (row, col - 1);
    }

    static void BindBrush(Control target, AvaloniaProperty property, string tokenKey) =>
        target[!property] = new DynamicResourceExtension(tokenKey + "Brush");

    static double Size(string tokenKey) => (double)Application.Current!.FindResource(tokenKey)!;
}
