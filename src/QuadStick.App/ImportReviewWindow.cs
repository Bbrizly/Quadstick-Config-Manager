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
    // What the last decision did, said out loud. Three of the decisions change
    // nothing in the heading or the subheading, so a screen reader had nothing
    // to read back after a button was pressed: the panel was rebuilt, the button
    // was gone, and the window was silent about whether anything had happened.
    readonly TextBlock _announce;
    readonly Button _advancedButton;
    readonly Button _done;
    string? _undoable; // the one decision Undo would reverse, newest first
    bool _advanced;
    Control? _firstDecision;

    // ---- advanced view state ----
    // ProfileFile's undo is one stack, so at most one of these two is ever
    // live: the newest change is the only one that can be reversed without
    // silently taking a newer one with it.
    string? _lastGridEdit;

    // One per drawn cell, kept so an edit repaints instead of rebuilding.
    sealed class CellView
    {
        public required Border Box;
        public required TextBlock Label;
        public bool Warn;
        public string? Tint;
    }

    readonly Dictionary<(int Row, int Col), CellView> _cells = new();
    readonly Dictionary<int, Control> _skippedGrids = new();
    readonly StackPanel _advancedHost;
    Control? _fileGrid;
    StackPanel? _undoLine;
    TextBlock _undoSaid = null!;
    Button _undoButton = null!;
    (int Row, int Col)? _selected;
    ScrollViewer _scroll = null!;
    TextBlock _inspectorHead = null!;
    TextBox _inspectorValue = null!;
    Panel _inspectorActions = null!;
    Border? _gridHost;
    int _gridRows, _gridCols;
    const string CellDragFormat = "quadstick/cell";

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

        _announce = new TextBlock
        {
            Text = "",
            FontSize = Size("SmallSize"), TextWrapping = TextWrapping.Wrap, Classes = { "muted" },
            Margin = new Thickness(0, 4, 0, 0),
            IsVisible = false,
        };
        AutomationProperties.SetLiveSetting(_announce, AutomationLiveSetting.Assertive);

        var titles = new StackPanel();
        titles.Children.Add(_heading);
        titles.Children.Add(_subheading);
        titles.Children.Add(_announce);
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(titles, 0);
        Grid.SetColumn(_advancedButton, 1);
        top.Children.Add(titles);
        top.Children.Add(_advancedButton);

        _body = new StackPanel { Spacing = 18, Margin = new Thickness(0, 18, 0, 0) };
        // The grid lives in its own container, next to the prose rather than
        // inside it. Clearing _body on every edit would detach and re-attach
        // thousands of cells, and it is that re-layout, not making the controls,
        // that cost over a second on an ordinary profile.
        _advancedHost = new StackPanel { Spacing = 14, Margin = new Thickness(0, 18, 0, 0), IsVisible = false };
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel { Children = { _body, _advancedHost } },
        };

        // Both, deliberately: every decision has already been applied and shown,
        // so Enter and Esc mean the same safe thing, which is "I am finished
        // looking". Neither one can silently commit something unseen.
        _done = new Button { Content = "Done", Classes = { "primary" }, MinWidth = 140, IsDefault = true, IsCancel = true };
        AutomationProperties.SetName(_done,
            "Close the import review. Every answer you gave has already been applied to the profile behind it");
        _done.Click += (_, _) => Close();
        // Esc closes as well as Enter, and neither one is a cancel: a decision
        // is applied the moment it is pressed. Someone reaching for Esc to back
        // out deserves to have been told that before they press it, and where
        // the way back actually is.
        var closingNote = new TextBlock
        {
            Text = "Your answers are already part of the profile. Nothing is saved to disk yet, "
                 + "and Control Z in the editor undoes any of it.",
            FontSize = Size("SmallSize"), TextWrapping = TextWrapping.Wrap, Classes = { "muted" },
            VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520,
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { closingNote, _done },
        };

        var panel = new DockPanel { LastChildFill = true, Margin = new Thickness(24) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(top);
        panel.Children.Add(buttons);
        panel.Children.Add(_scroll);

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

        // Two kinds, two headings. A lost mode is a decision the user has to
        // make; a reference card is a fact they only need told once. Filing
        // them together would put a button next to a tab that must never
        // become a mode.
        var lost = _skipped.Where(t => t.Kind == SkippedTabKind.UnreadableA1).ToList();
        var helpers = _skipped.Where(t => t.Kind == SkippedTabKind.Helper).ToList();

        // A partial read is never clean, however few issues the part that
        // arrived happens to have. A helper tab is not counted against it:
        // every workbook QMP writes has a Reference Card, so counting it would
        // mean no import is ever clean and the word would stop meaning
        // anything.
        bool clean = _limitation is null && lost.Count == 0 && open.Count == 0;

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

        if (lost.Count > 0)
            _body.Children.Add(Section(
                Count(lost.Count, "tab did not come in", "tabs did not come in"),
                lost.Select(SkippedRow)));

        if (helpers.Count > 0)
            _body.Children.Add(Section(
                Count(helpers.Count, "tab is not profile data", "tabs are not profile data"),
                new[] { Line(HelperTabText(helpers), null) }));

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

        _advancedHost.IsVisible = _advanced;
        if (_advanced && _advancedHost.Children.Count == 0) AdvancedView();
        if (_advanced) { RefreshInspector(); RefreshUndoLine(); }
    }

    // Issues the user has not already answered. A decision that ends in "leave
    // it" is an answer: the cell keeps its warning in the editor, where a
    // warning belongs, and stops being a question here.
    List<Issue> OpenIssues() => _file.Issues.Where(i => !_settled.ContainsKey(IssueKey(i))).ToList();

    static string IssueKey(Issue i) => $"{i.Cell}|{i.Kind}|{i.Message}";

    string Summary()
    {
        var modes = _file.Document.Sheets.Count(s => s.Type == SheetType.ProfileName);
        // Bindings on modes only. A preference is a setting and an infrared row
        // is a command, and folding both into one binding count made a profile
        // look like it had rows the device would never fire.
        var bindings = _file.Document.Sheets
            .Where(s => s.Type == SheetType.ProfileName).Sum(s => s.Bindings.Count);
        var prefs = _file.Document.Sheets.Any(s => s.Type == SheetType.Preferences) ? ", and your preferences" : "";
        return $"{Count(modes, "mode", "modes")} and {Count(bindings, "binding", "bindings")}{prefs}.";
    }

    static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    // ---- decisions ----

    // No buttons on this one. QMP writes these tabs as documentation and the
    // QuadStick has never read them, so there is no decision to make and the
    // only useful thing to do is stop the user looking for a mode that was
    // never there. The sentence says the device's name, not the app's, because
    // "the app skipped it" is the reading we are trying to correct.
    static string HelperTabText(IReadOnlyList<SkippedTab> helpers)
    {
        var names = string.Join(", ", helpers.Select(t => $"\"{t.Name}\""));
        var subject = helpers.Count == 1 ? "This tab is" : "These tabs are";
        var it = helpers.Count == 1 ? "it" : "them";
        return $"{names}   {subject} notes, not bindings. QMP and the Sheets add-on write "
            + $"{(helpers.Count == 1 ? "this tab" : "these tabs")} for you to read, and your QuadStick "
            + $"never loads {it}. Nothing was lost by leaving {it} out.";
    }

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
            AfterDecision($"\"{tab.Name}\" was added as a mode.");
        };

        var leave = new Button { Content = "Leave it out", MinWidth = 130 };
        AutomationProperties.SetName(leave, $"Leave the tab {tab.Name} out of this profile, as the QuadStick does");
        leave.Click += (_, _) =>
        {
            _skipped.Remove(tab);
            Settle($"tab:{tab.Name}", $"\"{tab.Name}\" was left out, the same as your QuadStick does today.",
                () => _skipped.Add(tab), touchedTheFile: false);
            AfterDecision($"\"{tab.Name}\" was left out.");
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
                AfterDecision($"\"{word}\" is now this row's own name, in column L.");
            };
            buttons.Add(name);
        }

        var note = new Button { Content = "Move to notes", MinWidth = 140 };
        AutomationProperties.SetName(note, $"Move \"{word}\" into the notes column, where the QuadStick never looks");
        note.Click += (_, _) =>
        {
            if (!_file.MoveInputToNotes(row, col)) return;
            Settle(IssueKey(issue), $"{issue.Cell}   \"{word}\" moved into the notes column.", () => { }, touchedTheFile: true);
            _owner.ModesChanged(SheetIndexOf(row), $"Moved \"{word}\" into the notes column.");
            AfterDecision($"\"{word}\" moved into the notes column.");
        };
        buttons.Add(note);

        var leave = new Button { Content = "Leave it", MinWidth = 110 };
        AutomationProperties.SetName(leave, $"Leave \"{word}\" where it is, and keep its warning in the editor");
        leave.Click += (_, _) =>
        {
            Settle(IssueKey(issue), $"{issue.Cell}   \"{word}\" left as it is. The QuadStick ignores it.", () => { }, touchedTheFile: false);
            AfterDecision($"\"{word}\" was left where it is.");
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
        // Only a decision that changed the file becomes the newest thing on the
        // stack. One that changed nothing used to retire the Undo offered for a
        // real change made a moment earlier, whose snapshot was still there and
        // still the newest: the affordance went and the user had to close the
        // window and reach for Ctrl+Z in the editor instead.
        if (!touchedTheFile) return;
        _undoable = key;
        _lastGridEdit = null;
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
                AfterDecision("That change was undone.");
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

    // Every mode is numbered, because that is the only thing that tells two of
    // them apart on the device. The firmware counts "Profile Name" segments as
    // it reads the file and never looks at the name in C1, so two modes may
    // share a name and still be two modes. A user whose workbook named both
    // tabs "Left Joystick" read the second one's absence from this list as a
    // failed import; it had been here all along, under the same words.
    //
    // Preferences and Infrared are not numbered. The firmware's loop increments
    // only on "Profile"; "Preferences" and "Infrared" each run their own
    // reader and leave the counter alone. Numbering an infrared sheet as a mode
    // would have shifted every mode under it by one, so the numbers on screen
    // would have named the wrong modes on the device.
    Control ModeTable()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            Margin = new Thickness(12, 0, 0, 0),
        };
        int r = 0, mode = 0;
        foreach (var s in _file.Document.Sheets)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            bool isMode = s.Type == SheetType.ProfileName;
            if (isMode) mode++;
            var name = new TextBlock
            {
                Text = s.Type switch
                {
                    SheetType.Preferences => "Preferences",
                    SheetType.Infrared => "Infrared commands",
                    _ => $"Mode {mode}: {DisplayName(s)}",
                },
                FontSize = Size("BodySize"), Margin = new Thickness(0, 0, 24, 4), TextWrapping = TextWrapping.Wrap,
            };
            var (one, many) = s.Type switch
            {
                SheetType.Preferences => ("setting", "settings"),
                SheetType.Infrared => ("command", "commands"),
                _ => ("binding", "bindings"),
            };
            var count = new TextBlock
            {
                Text = Count(s.Bindings.Count, one, many),
                FontSize = Size("BodySize"), Classes = { "muted" }, Margin = new Thickness(0, 0, 0, 4),
            };
            Grid.SetRow(name, r); Grid.SetColumn(name, 0);
            Grid.SetRow(count, r); Grid.SetColumn(count, 1);
            grid.Children.Add(name);
            grid.Children.Add(count);
            r++;
        }

        var repeated = RepeatedModeNames();
        if (repeated.Count == 0) return grid;

        var panel = new StackPanel();
        panel.Children.Add(grid);
        panel.Children.Add(new TextBlock
        {
            Text = RepeatedModeText(repeated),
            FontSize = Size("SmallSize"), TextWrapping = TextWrapping.Wrap, Classes = { "muted" },
            Margin = new Thickness(12, 10, 0, 0),
        });
        return panel;
    }

    /// <summary>Names carried by more than one mode, in the order they first
    /// appear. Trimmed and case-insensitive, because "Drive" and "drive " are
    /// the same word to a person reading a list and the device reads neither.
    /// </summary>
    List<string> RepeatedModeNames()
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var s in _file.Document.Sheets.Where(s => s.Type == SheetType.ProfileName))
        {
            var name = DisplayName(s);
            if (!seen.TryAdd(name, 1)) { if (seen[name]++ == 1) order.Add(name); }
        }
        return order;
    }

    static string RepeatedModeText(IReadOnlyList<string> repeated)
    {
        var names = string.Join(", ", repeated.Select(n => $"\"{n}\""));
        var subject = repeated.Count == 1 ? "More than one mode is named" : "More than one mode each carry the names";
        return $"{subject} {names}. That is allowed: your QuadStick tells modes apart by their "
            + "order in the file, not by their name, so these are separate modes and all of them came in. "
            + "Rename them in Modes if you want to tell them apart on screen.";
    }

    static string DisplayName(ModeSheet s) => s.ModeName.Trim().Length > 0 ? s.ModeName.Trim() : "(unnamed mode)";

    // ---- advanced view ----

    // The grid as the app read it, in the colour language the editor already
    // uses: a tint means the device reads that cell. No tint means it never
    // looks there, which is the whole point of the notes and name columns and
    // of everything past column L.
    //
    // It is also editable, because the alternative is sending someone back to
    // the spreadsheet, and not having to go back there is the point of the app.
    // The editor behind this window works in bindings, which cannot say "this
    // word is in the wrong column" at all. Only the raw grid can, so only the
    // raw grid can fix it.
    // The grid controls are made once and then kept. Rebuilding them on every
    // edit cost more than a second on a 150 row profile, which is an ordinary
    // size, and that is per keystroke committed. The prose above them is cheap
    // and is rebuilt every time; only the thousands of cells are reused, and
    // RefreshGridInPlace repaints them.
    void AdvancedView()
    {
        var panel = _advancedHost;
        panel.Children.Add(new TextBlock
        {
            Text = "Your spreadsheet, with what we read marked on it. A tinted cell is one the QuadStick "
                 + "reads. A plain cell is one it never looks at, so notes and your own names for rows are "
                 + "safe there. Click any cell to change it, or drag it to another column in the same row. "
                 + "Every change here is a change to the profile open behind this window.",
            FontSize = Size("SmallSize"), TextWrapping = TextWrapping.Wrap, Classes = { "muted" },
        });
        panel.Children.Add(Legend());
        panel.Children.Add(Inspector());
        panel.Children.Add(_fileGrid ??= RawGrid(_file.Grid, dimmed: false, editable: true));
        for (int i = 0; i < _skipped.Count; i++)
        {
            var tab = _skipped[i];
            // Helper tabs are named in the simple view and stop there. They
            // carry no cells, so there is nothing to draw, and the heading below
            // would tell the user their reference card was left out over a bad
            // A1 and then offer an empty grid as proof of it. The index is still
            // the key, so the grids stay with the tabs they were built from.
            if (tab.Kind != SkippedTabKind.UnreadableA1) continue;
            panel.Children.Add(new TextBlock
            {
                Text = $"\"{tab.Name}\", left out because cell A1 does not name a kind of sheet:",
                FontSize = Size("BodySize"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
            });
            // Read only: these rows are not in the profile, so there is nothing
            // here an edit could change. Bring the tab in first, above.
            //
            // Keyed by position, not by the tab's name. Nothing in a workbook
            // stops two worksheets sharing a name, and when they did, the second
            // one found the first one's control in here and added that same
            // instance to the panel again, which Avalonia refuses because it
            // already has a parent.
            if (!_skippedGrids.TryGetValue(i, out var g))
                _skippedGrids[i] = g = RawGrid(tab.Rows, dimmed: true, editable: false);
            panel.Children.Add(g);
        }
    }

    // Anything that changes the SHAPE of the grid, rather than the contents of
    // a cell, needs the controls made again: adding a mode, undoing one, a row
    // growing past the last column there was.
    void InvalidateAdvanced()
    {
        _advancedHost.Children.Clear();
        _fileGrid = null;
        _skippedGrids.Clear();
        _cells.Clear();
        _gridHost = null;
        _undoLine = null;
    }

    bool GridShapeChanged()
    {
        if (_fileGrid is null) return true;
        int rows = Math.Min(_file.Grid.Count, MaxAdvancedRows);
        // Against the tabs that are drawn, not every tab that was skipped. The
        // advanced view leaves helper tabs out, so counting them here made an
        // ordinary import reshape on every edit, and worse, one helper plus one
        // repaired tab could make the two numbers agree by accident and leave a
        // tab that is now a mode still on screen under "left out".
        return rows != _gridRows || ColumnsFor(_file.Grid, rows) != _gridCols
            || _skippedGrids.Count != _skipped.Count(t => t.Kind == SkippedTabKind.UnreadableA1);
    }

    // One rule for how wide the grid is, so the shape check and the grid itself
    // cannot drift apart and report a reshape on every single edit.
    static int ColumnsFor(IReadOnlyList<string[]> rows, int shown) =>
        Math.Clamp(shown == 0 ? 0 : rows.Take(shown).Max(r => r.Length),
                   Parser.ActionColumn + 1, MaxAdvancedColumns);

    // Repaint the kept cells from the reparsed file: text, tint, warning border
    // and the spoken description all follow the edit without a single control
    // being made.
    void RefreshGridInPlace()
    {
        var bindingRows = _file.Document.Sheets.SelectMany(s => s.Bindings).Select(b => b.Row).ToHashSet();
        var warned = _file.Issues.Select(i => ParseCell(i.Cell)).Where(x => x.Row > 0)
            .Select(x => (x.Row, x.Col)).ToHashSet();

        foreach (var (at, cell) in _cells)
        {
            var text = RawCell(at.Row, at.Col);
            bool isBinding = bindingRows.Contains(at.Row);
            bool warn = warned.Contains(at);
            var tint = isBinding ? TintFor(at.Col) : null;

            cell.Label.Text = text;
            cell.Warn = warn;
            cell.Tint = tint;
            if (tint is null) cell.Box.Background = null;
            else BindBrush(cell.Box, Border.BackgroundProperty, tint);
            AutomationProperties.SetName(cell.Box, Describe(at.Row, at.Col, text, isBinding, warn, dimmed: false));
            PaintCell(at);
        }
    }

    // The grid holds rows exactly as they were written, so read them untrimmed
    // here; GetCell trims, and the point of this view is what is actually there.
    string RawCell(int row, int col) =>
        row >= 1 && row <= _file.Grid.Count && col < _file.Grid[row - 1].Length
            ? _file.Grid[row - 1][col] ?? "" : "";

    // ---- editing the grid ----

    // The one place a cell is changed, so every route (typing, Clear, the Move
    // buttons, a drag) lands in the same state afterwards: the profile behind
    // this window knows, the newest change is the one Undo reverses, and the
    // window is rebuilt so the warnings and counts tell the truth again.
    void ApplyGridEdit(int row, string what, Func<bool> edit)
    {
        bool hadInput = BindingAt(row) is { } before && !Vocab.NothingFiresIt(before);
        if (!edit()) return;
        what += Consequence(row, hadInput);
        _lastGridEdit = what;
        _undoable = null; // one undo stack, so only the newest change offers one
        _owner.ModesChanged(SheetIndexOf(_selected?.Row ?? 1), what);
        Rebuild();
    }

    Binding? BindingAt(int row) =>
        _file.Document.Sheets.SelectMany(s => s.Bindings).FirstOrDefault(b => b.Row == row);

    // A row that keeps its output but loses its last input still reads as fine.
    // The factory template ships twelve rows shaped exactly like that on purpose
    // ("dpad_N,normal," and the rest), so nothing in the finished file marks
    // this one as broken and the Problems list stays quiet. Only the edit knows
    // an input used to be there, so the edit is the only thing that can say so.
    //
    // A settings row is left alone: its column C is a value, not an input, and
    // emptying it already has its own warning that says the device reads 0.
    string Consequence(int row, bool hadInput) =>
        hadInput && BindingAt(row) is { } b && Vocab.NothingFiresIt(b)
            ? $" Nothing presses \"{b.Output}\" now, so the QuadStick will not fire it."
            : "";

    // Rebuild without throwing the reader back to the top of a 400 row grid, or
    // losing the cell they were working on.
    void Rebuild() => Rebuild(focusGrid: true);

    void Rebuild(bool focusGrid)
    {
        var offset = _scroll.Offset;
        bool reshape = GridShapeChanged();
        if (reshape) InvalidateAdvanced();
        Build();
        if (_advanced && !reshape) RefreshGridInPlace();
        Dispatcher.UIThread.Post(() =>
        {
            var maxY = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
            _scroll.Offset = new Vector(offset.X, Math.Min(offset.Y, maxY));
            // A grid edit came from the grid, so focus belongs back there. A
            // decision came from a button that no longer exists, so focus goes
            // to the next thing that needs a person instead of nowhere at all.
            if (focusGrid) _gridHost?.Focus();
            else (_firstDecision ?? _done).Focus();
        }, DispatcherPriority.Loaded);
    }

    // Every decision ends here: the file changed, so the advanced grid has to
    // follow it, focus has to land somewhere, and what happened has to be said.
    //
    // Only the grid edit path used to rebuild. Pressing "Add it as a working
    // mode" with the grid open left that tab's rows sitting under the heading
    // "left out because cell A1 does not name a kind of sheet", which is a plain
    // untruth about a mode that is now in the profile, and "Move to notes" left
    // the moved word tinted in its old column with its spoken description still
    // calling it a word the QuadStick does not know. Toggling the view did not
    // clear it either, so the window went on saying it until the next grid edit.
    void AfterDecision(string announcement)
    {
        _announce.Text = announcement;
        _announce.IsVisible = announcement.Length > 0;
        Rebuild(focusGrid: false);
    }

    void Select(int row, int col)
    {
        var was = _selected;
        _selected = (row, col);
        if (was is { } w) PaintCell(w);
        PaintCell((row, col));
        RefreshInspector();
    }

    void PaintCell((int Row, int Col) at)
    {
        if (!_cells.TryGetValue(at, out var c)) return;
        bool selected = _selected == at;
        c.Box.BorderThickness = new Thickness(selected ? 3 : c.Warn ? 2 : 1);
        BindBrush(c.Box, Border.BorderBrushProperty,
            selected ? "Accent" : c.Warn ? "Warning" : "SurfaceBorder");
    }

    // The strip under the legend. Editing happens here rather than inside the
    // cell: a labelled text box is something a screen reader can announce and
    // a keyboard can reach, and an in-place editor in a grid of this size is
    // neither.
    Control Inspector()
    {
        var panel = new StackPanel { Spacing = 8 };

        _inspectorHead = new TextBlock { FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(_inspectorHead, AutomationLiveSetting.Polite);
        panel.Children.Add(_inspectorHead);

        _inspectorValue = new TextBox { MinWidth = 220, MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left };
        // Enter commits, Esc puts back what was there. Nothing commits on its
        // own: a half typed word must never reach the file because focus moved.
        _inspectorValue.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; CommitInspector(); }
            else if (e.Key == Key.Escape) { e.Handled = true; RefreshInspector(); _gridHost?.Focus(); }
        };

        var commit = new Button { Content = "Save cell", MinWidth = 110 };
        commit.Click += (_, _) => CommitInspector();

        _inspectorActions = new WrapPanel();
        var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        valueRow.Children.Add(_inspectorValue);
        valueRow.Children.Add(commit);
        panel.Children.Add(valueRow);
        panel.Children.Add(_inspectorActions);

        // Made once and shown or hidden, like everything else in here, so an
        // edit never detaches a control that the grid is laid out beside.
        _undoSaid = new TextBlock { FontSize = Size("SmallSize"), VerticalAlignment = VerticalAlignment.Center };
        BindBrush(_undoSaid, TextBlock.ForegroundProperty, "Success");
        _undoButton = new Button { Content = "Undo", MinWidth = 90 };
        _undoButton.Click += (_, _) =>
        {
            if (!_file.Undo()) return;
            _lastGridEdit = null;
            _owner.ModesChanged(0, "Undid the last cell change.");
            Rebuild();
        };
        _undoLine = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, IsVisible = false,
            Children = { _undoSaid, _undoButton },
        };
        panel.Children.Add(_undoLine);

        RefreshInspector();
        RefreshUndoLine();
        return panel;
    }

    void RefreshUndoLine()
    {
        if (_undoLine is null) return;
        _undoLine.IsVisible = _lastGridEdit is not null;
        if (_lastGridEdit is null) return;
        _undoSaid.Text = _lastGridEdit;
        AutomationProperties.SetName(_undoButton, $"Undo this change: {_lastGridEdit}");
    }

    void CommitInspector()
    {
        if (_selected is not { } at) return;
        var text = _inspectorValue.Text ?? "";
        if (text == _file.GetCell(at.Row, at.Col)) return;
        var where = $"{ColumnLetter(at.Col)}{at.Row}";
        ApplyGridEdit(at.Row,
            text.Trim().Length == 0 ? $"Emptied {where}." : $"Set {where} to \"{text.Trim()}\".",
            () => { _file.SetCell(at.Row, at.Col, text); return true; });
    }

    // What the selected cell is, what the device does with it, and the moves
    // that are legal from here. Rebuilt on every selection change and after
    // every edit, because a move that was legal a moment ago may not be now.
    void RefreshInspector()
    {
        _inspectorActions.Children.Clear();

        // A rebuild can leave the pick pointing past the end: clearing the only
        // cell in a far column takes that column off the grid with it.
        if (_selected is { } pick && _gridCols > 0 && pick.Col >= _gridCols)
            _selected = (pick.Row, _gridCols - 1);

        if (_selected is not { } at || at.Row < 1 || at.Row > _file.Grid.Count)
        {
            _selected = null;
            _inspectorHead.Text = "No cell picked. Click one, or press Tab to the sheet and use the arrow keys.";
            _inspectorValue.Text = "";
            _inspectorValue.IsEnabled = false;
            AutomationProperties.SetName(_inspectorValue, "No cell picked");
            return;
        }

        var where = $"{ColumnLetter(at.Col)}{at.Row}";
        var value = _file.GetCell(at.Row, at.Col);
        var issue = _file.Issues.FirstOrDefault(i => ParseCell(i.Cell) == at);
        bool isBinding = _file.Document.Sheets.SelectMany(s => s.Bindings).Any(b => b.Row == at.Row);
        var meaning = CellMeaning(at.Row, at.Col, isBinding);
        _inspectorHead.Text = issue is null
            ? $"{where}   {meaning}"
            : $"{where}   {meaning}   {issue.Message}";
        _inspectorValue.IsEnabled = true;
        _inspectorValue.Text = value;
        AutomationProperties.SetName(_inspectorValue, $"Contents of cell {where}, {meaning}");

        void Action(string label, string spoken, Func<bool> can, Action<Action<string, Func<bool>>> run)
        {
            if (!can()) return;
            var b = new Button { Content = label, MinWidth = 130, Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetName(b, spoken);
            // Every action here works on the picked cell, so the row is bound
            // once rather than threaded through each caller.
            b.Click += (_, _) => run((what, edit) => ApplyGridEdit(at.Row, what, edit));
            _inspectorActions.Children.Add(b);
        }

        Action("Clear it", $"Empty cell {where}",
            () => value.Length > 0,
            apply => apply($"Emptied {where}.", () => { _file.SetCell(at.Row, at.Col, ""); return true; }));

        // Named for the picked cell, because the simple view's own "Move to
        // notes" for the same warning is on screen right above this.
        Action("Move this to the note column",
            $"Move \"{value}\" from {where} into the note column, where the QuadStick never looks",
            () => _file.CanMoveCell(at.Row, at.Col, ProfileFile.NoteColumn),
            apply => apply($"Moved \"{value}\" from {where} into the note column.",
                () => _file.MoveCell(at.Row, at.Col, ProfileFile.NoteColumn)));

        Action("Make this the row's name",
            $"Keep \"{value}\" as this row's own name, in column L, where the QuadStick never looks",
            () => _file.CanMoveCell(at.Row, at.Col, ProfileFile.ActionColumn),
            apply => apply($"Moved \"{value}\" from {where} into this row's name.",
                () => _file.MoveCell(at.Row, at.Col, ProfileFile.ActionColumn)));

        // C to J are a sequence, done left to right, so which column an input
        // sits in is the order it happens in. The drag could move a word to any
        // free column in its row and these buttons could only reach the note and
        // name columns, so reordering a sequence was mouse only, in a window
        // whose own text teaches that the order is what makes a sequence. For
        // someone driving this with a mouth stick that is not a shortcut they
        // are missing, it is the whole operation.
        // A swap, not a move. MoveCell refuses an occupied destination, which is
        // right for a move and useless for a reorder: two inputs side by side is
        // the ordinary shape, so "move this earlier" appeared only when there
        // happened to be a gap to slide into, which is the one case where the
        // order does not change at all. Swapping with the neighbour is the
        // operation people actually want, and the only one the keyboard has.
        void Nudge(string label, string direction, int target)
        {
            var neighbour = _file.GetCell(at.Row, target).Trim();
            var spoken = neighbour.Length > 0
                ? $"Swap \"{value}\" with \"{neighbour}\", moving it one step {direction} in this row's sequence of inputs"
                : $"Move \"{value}\" to {ColumnLetter(target)}{at.Row}, one step {direction} in this row's sequence of inputs";
            Action(label, spoken,
                () => _file.CanSwapInputs(at.Row, at.Col, target),
                apply => apply(
                    neighbour.Length > 0
                        ? $"Swapped \"{value}\" and \"{neighbour}\" in row {at.Row}."
                        : $"Moved \"{value}\" from {where} to {ColumnLetter(target)}{at.Row}.",
                    () => _file.SwapInputs(at.Row, at.Col, target)));
        }

        const int FirstInput = 2;                             // C
        const int LastInput = Parser.KeywordColumns - 1;      // J

        // Three things have to be true before either button appears.
        //
        // The picked cell has to hold something. A swap works from either side,
        // so an empty cell beside a full one offered "Move it later" and then
        // moved the neighbour EARLIER: the label described the opposite of what
        // happened.
        //
        // The row has to be one the device reads as a binding, on a mode sheet.
        // C to J only mean "a sequence of inputs" there. A keyword row keeps the
        // mode name in C, a label row keeps the channel in C, a filename row and
        // anything below a blank line are read as nothing at all, and a
        // Preferences sheet keeps the value in B with C onward unread. Offering
        // to reorder any of those would move a structural value out of the one
        // column the device goes looking for it in.
        //
        // And it must not be a settings row, where column C is the value: a swap
        // takes the value out of C and the device reads whatever lands there
        // with atoi, quietly applying a different setting. A move refused that
        // only because the target happened to be occupied.
        var sheetOfRow = _file.Document.Sheets.ElementAtOrDefault(SheetIndexOf(at.Row));
        bool reorderable = value.Trim().Length > 0
            && isBinding
            && sheetOfRow?.Type == SheetType.ProfileName
            && !Vocab.IsPreferenceOverride(_file.GetCell(at.Row, 0), _file.GetCell(at.Row, 1));

        if (reorderable && at.Col is > FirstInput and <= LastInput)
            Nudge("Move it earlier", "earlier", at.Col - 1);
        if (reorderable && at.Col is >= FirstInput and < LastInput)
            Nudge("Move it later", "later", at.Col + 1);
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

    // The rows were capped and the columns were not, so the width came straight
    // off the file: a published sheet reaching Excel's last column would have
    // asked for sixteen thousand controls per row. Column L is the last one a
    // profile means anything by, and the widest real community workbook reaches
    // Z, so this is already far past what anyone reads across.
    const int MaxAdvancedColumns = 64;

    Control RawGrid(IReadOnlyList<string[]> rows, bool dimmed, bool editable)
    {
        int shown = Math.Min(rows.Count, MaxAdvancedRows);
        int cols = ColumnsFor(rows, shown);

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
                var cell = CellBox(text, tint, warn, Describe(rowNumber, c, text, isBinding, warn, dimmed),
                    muted: dimmed);
                Grid.SetRow(cell, r + 1); Grid.SetColumn(cell, c + 1);
                grid.Children.Add(cell);
                if (editable)
                {
                    _cells[(rowNumber, c)] = new CellView
                    {
                        Box = cell, Label = (TextBlock)cell.Child!, Warn = warn, Tint = tint,
                    };
                    WireCell(cell, rowNumber, c);
                }
            }
        }

        Control content = grid;
        if (rows.Count > shown)
        {
            var wrapper = new StackPanel { Spacing = 8 };
            wrapper.Children.Add(grid);
            wrapper.Children.Add(new TextBlock
            {
                Text = $"Showing the first {shown} rows of {rows.Count}. The rest imported the same way.",
                FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
            });
            content = wrapper;
        }
        if (!editable) return content;

        // One tab stop for the whole sheet, then arrow keys inside it. Making
        // every cell its own tab stop would put thousands of stops between the
        // legend and the Done button.
        _gridRows = shown;
        _gridCols = cols;
        var host = new Border { Focusable = true, Child = content, Padding = new Thickness(2) };
        AutomationProperties.SetName(host,
            "Your spreadsheet. Arrow keys pick a cell, Enter edits it, Delete empties it.");
        host.KeyDown += (_, e) => GridKey(e);
        host.GotFocus += (_, _) => { if (_selected is null && _gridRows > 0) Select(1, 0); };
        _gridHost = host;
        // A rebuild throws away every control, so put the highlight back on
        // whatever cell the user was working on.
        if (_selected is { } at) PaintCell(at);
        return host;
    }

    void GridKey(KeyEventArgs e)
    {
        if (_selected is not { } at) return;
        int r = at.Row, c = at.Col;
        switch (e.Key)
        {
            case Key.Left: c--; break;
            case Key.Right: c++; break;
            case Key.Up: r--; break;
            case Key.Down: r++; break;
            case Key.Home: c = 0; break;
            case Key.End: c = _gridCols - 1; break;
            case Key.Enter:
                _inspectorValue.Focus();
                _inspectorValue.SelectAll();
                e.Handled = true;
                return;
            case Key.Delete:
            case Key.Back:
                // An empty cell has nothing to clear, and clearing it anyway
                // would push an undo step that undoes nothing visible.
                if (_file.GetCell(r, c).Length > 0)
                    ApplyGridEdit(r, $"Emptied {ColumnLetter(c)}{r}.",
                        () => { _file.SetCell(r, c, ""); return true; });
                e.Handled = true;
                return;
            default: return;
        }
        Select(Math.Clamp(r, 1, Math.Max(1, _gridRows)), Math.Clamp(c, 0, Math.Max(0, _gridCols - 1)));
        e.Handled = true;
    }

    // Click to pick, drag to move. The drag is the accelerator; everything it
    // can do, the inspector's buttons can do from the keyboard.
    void WireCell(Border box, int row, int col)
    {
        bool pressed = false;
        var pressAt = default(Point);

        box.PointerPressed += (_, e) =>
        {
            pressed = true;
            pressAt = e.GetPosition(this);
            Select(row, col);
            _gridHost?.Focus();
        };
        box.PointerReleased += (_, _) => pressed = false;
        box.PointerMoved += (_, e) =>
        {
            // Only a real movement starts a drag, so a plain click stays a click.
            //
            // The button state is checked as well as the flag. The cell does not
            // capture the pointer, so pressing here and releasing anywhere else
            // never cleared the flag, and simply hovering back over this cell
            // later began a drag with nothing held down. For someone driving
            // this with a mouth stick or a head pointer, a drag that starts on
            // hover is exactly the kind of input noise the window is meant to
            // protect them from.
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) pressed = false;
            var d = e.GetPosition(this) - pressAt;
            if (!pressed || Math.Abs(d.X) + Math.Abs(d.Y) < 6) return;
            pressed = false;
            if (_file.GetCell(row, col).Length == 0) return; // nothing to carry
            var data = new DataObject();
            data.Set(CellDragFormat, new[] { row, col });
            _ = DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        };

        DragDrop.SetAllowDrop(box, true);
        box.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = CanDrop(e, row, col) ? DragDropEffects.Move : DragDropEffects.None);
        box.AddHandler(DragDrop.DragEnterEvent, (_, e) =>
        { if (CanDrop(e, row, col)) BindBrush(box, Border.BackgroundProperty, "SelectionTint"); });
        box.AddHandler(DragDrop.DragLeaveEvent, (_, _) => RestoreTint((row, col)));
        box.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            RestoreTint((row, col));
            if (!CanDrop(e, row, col)) return;
            var src = (int[])e.Data.Get(CellDragFormat)!;
            var word = _file.GetCell(src[0], src[1]);
            Select(row, col);
            ApplyGridEdit(row,
                $"Moved \"{word}\" from {ColumnLetter(src[1])}{src[0]} to {ColumnLetter(col)}{row}.",
                () => _file.MoveCell(row, src[1], col));
        });
    }

    // Same row only, and only where the move is legal anyway. A drop onto
    // another row would rewrite a different binding, which is never what
    // "this word is in the wrong column" means.
    bool CanDrop(DragEventArgs e, int row, int col) =>
        e.Data.Get(CellDragFormat) is int[] { Length: 2 } src
        && src[0] == row
        && _file.CanMoveCell(row, src[1], col);

    void RestoreTint((int Row, int Col) at)
    {
        if (!_cells.TryGetValue(at, out var c)) return;
        if (c.Tint is null) c.Box.Background = null;
        else BindBrush(c.Box, Border.BackgroundProperty, c.Tint);
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

    // What a cell actually is, which is not the same as what its column would
    // be on a binding row. The three rows that open a mode carry the keyword,
    // the file name and the labels, and calling the first of those "output"
    // would be a lie told right where it costs most: overwrite it and the whole
    // mode leaves the profile.
    string CellMeaning(int row, int col, bool isBinding)
    {
        var sheet = _file.Document.Sheets.LastOrDefault(s => s.StartRow <= row);
        bool prefsSheet = sheet?.Type == SheetType.Preferences;

        if (sheet is not null && sheet.StartRow == row)
            return prefsSheet
                ? "the word that makes this the settings sheet"
                : col switch
                {
                    0 => "the word that makes this a mode. Change it and the mode leaves the profile",
                    2 => "this mode's name",
                    _ => "part of the row that opens a mode",
                };

        if (!isBinding) return "not a binding row, so the QuadStick reads nothing here";

        // A settings sheet reuses the same columns for something else, and so
        // does a settings row parked inside a mode. Calling either one's value
        // an "input" would be wrong in the one place it costs most: this is the
        // cell somebody would drag away thinking the device never reads it.
        if (prefsSheet)
            return col switch
            {
                0 => "the setting's name",
                1 => "the setting's value",
                _ => "not read on a settings sheet",
            };

        if (Vocab.IsPreferenceOverride(_file.GetCell(row, 0), _file.GetCell(row, 1)))
            return col switch
            {
                0 => "a setting's name, used here to override it for this mode",
                1 => "skipped by the QuadStick on a settings row",
                2 => "the setting's value",
                _ => "not read on a settings row",
            };

        return ColumnMeaning(col);
    }

    static string? TintFor(int col) => col switch
    {
        0 => OutputTint,
        1 => FunctionTint,
        >= 2 and < Parser.KeywordColumns => InputTint,
        _ => null,
    };

    Border CellBox(string text, string? tintKey, bool warn, string accessibleName, bool bold = false, bool muted = false)
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

    // Colour alone cannot carry any of this, so the accessible name spells out
    // the cell, its text, and what the device does with it. Reading down a
    // column that way is how this view works without sight.
    string Describe(int row, int col, string text, bool isBinding, bool warn, bool dimmed) =>
        $"{ColumnLetter(col)}{row}, "
        + (text.Length > 0 ? $"\"{text}\", " : "empty, ")
        + (dimmed ? "not read, this tab was left out"
           : warn ? $"{CellMeaning(row, col, isBinding)}, the QuadStick does not know this word"
           : CellMeaning(row, col, isBinding));

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
