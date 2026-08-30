using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using QuadStick.Format;

namespace QuadStick.App;

// The Custom output names table: your own word for an output, like Shoot for
// the left mouse click. It sits in the mode list beside the real modes, but it
// is NOT a sheet in the file and never reaches the device. A name in use lives
// in its own row's column L, which the parser, the device and both official
// converters all ignore, so a shared copy reads back the same.
//
// Names with no mapping yet have no row to live on, so those wait in settings
// under the profile's path (AppSettings.CustomNames).
public partial class MainWindow
{
    static string CustomNamesLabel => Strings.Names_CustomOutputNames;

    // "Shoot" and "shoot" are one name to whoever reads them, so the table
    // matches names the way ProfileFile does, ignoring case.
    static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    // Names defined in the table that no mapping uses yet. Everything else is
    // read off the profile's rows, so this holds only what the file cannot.
    Dictionary<string, string> _drafts = new(NameComparer);

    // The names table is the last entry in the mode picker, past the real
    // sheets, so landing on it is exactly "no sheet selected".
    bool OnCustomNames => _file is not null && _sheetIndex >= _file.Document.Sheets.Count;

    void LoadDrafts(string? path) =>
        _drafts = path is not null && _settings.CustomNames.TryGetValue(path, out var saved)
            ? new Dictionary<string, string>(saved, NameComparer)
            : new Dictionary<string, string>(NameComparer);

    // Called after every table edit, and again after a Save As, which is where
    // an untitled profile's drafts finally get a path to be filed under.
    void PersistDrafts()
    {
        if (_savePath is null) return; // no path yet: they keep until the first save
        if (_drafts.Count == 0) _settings.CustomNames.Remove(_savePath);
        else _settings.CustomNames[_savePath] = new Dictionary<string, string>(_drafts);
        Settings.TrySave(_settings);
    }

    /// <summary>The whole table: names already on rows first, in row order,
    /// then the ones defined but not used yet.</summary>
    public List<(string Name, string Token)> CustomNameRows()
    {
        var rows = new List<(string, string)>();
        var seen = new HashSet<string>(NameComparer);
        if (_file is not null)
            foreach (var kv in _file.ActionTokens())
                if (seen.Add(kv.Key)) rows.Add((kv.Key, kv.Value));
        foreach (var kv in _drafts)
            if (seen.Add(kv.Key)) rows.Add((kv.Key, kv.Value));
        return rows;
    }

    /// <summary>Test and preview hook: show the names table.</summary>
    public void SelectCustomNamesForPreview()
    {
        if (_file is not null) SelectSheet(_file.Document.Sheets.Count);
    }

    void BuildCustomNameRows()
    {
        var header = ListGrid(CustomNameColumns);
        header.Children.Add(At(RowNumberHeaderSpacer(), 0));
        header.Children.Add(At(Swatch(Strings.Names_OutputRealButton, OutputTint), 1));
        header.Children.Add(At(Swatch(Strings.Names_YourNameForIt, FunctionTint), 2));
        RowsPanel.Children.Add(header);

        var rows = CustomNameRows();
        int number = 1;
        foreach (var (name, token) in rows) RowsPanel.Children.Add(CustomNameRow(name, token, number++));

        if (rows.Count == 0)
            RowsPanel.Children.Add(new TextBlock
            {
                Text = Strings.Names_NoNamesYetClickAdd,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 640,
                FontSize = Size("BodySize"), Classes = { "muted" }, Margin = new Thickness(4, 12),
            });
    }

    // handle, output, your name, delete, where it is used.
    static string CustomNameColumns =>
        $"{RowNumberWidth + 4},2.2*,2.4*,{Fixed(IconButtonSize)},1.4*";

    Control CustomNameRow(string name, string token, int number)
    {
        var p = ListGrid(CustomNameColumns);
        p.Children.Add(At(RowNumberLabel(number), 0));

        // The plain output picker, not the profile one: a name cannot stand for
        // another name.
        var wrapper = new Border
        {
            BorderThickness = new Thickness(3), BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(5),
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Humanize, not TokenLabel: the raw/Xbox word toggle belongs to Device
        // View, and this table must not change wording when someone flips it.
        p.Children.Add(At(PickerCell(wrapper, token, OutputSuggestions, Humanize,
            string.Format(CultureInfo.CurrentCulture, Strings.Names_OutputThatNameStandsFor, name), OutputTint, OutputCatalog.Catalog, Strings.Names_AnOutput,
            picked => RetargetCustomName(name, picked),
            picked => OutputVisuals.Render(OutputVisuals.For(picked, Humanize))), 1));

        var box = new TextBox
        {
            Text = name, MaxLength = ProfileFile.MaxActionName,
            FontSize = Size("BodySize"), VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(box, token.Length > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.Names_YourNameForHumanizeToken, Humanize(token)) : Strings.Names_YourNameForThisOutput);
        void Commit() { if (!_rebuildingRows) RenameCustomName(name, box.Text ?? ""); }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        p.Children.Add(At(box, 2));

        var del = new Button
        {
            Classes = { "icon", "danger" }, Content = Glyph("IconDelete", "Error"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(del, Strings.Names_RemoveThisName);
        AutomationProperties.SetName(del, string.Format(CultureInfo.CurrentCulture, Strings.Names_RemoveTheNameName, name));
        del.Click += (_, _) => DeleteCustomName(name);
        p.Children.Add(At(del, 3));

        int used = UsedBy(name);
        p.Children.Add(At(new TextBlock
        {
            Text = used == 0 ? Strings.Names_NotUsedYet : $"on {used} mapping{(used == 1 ? "" : "s")}",
            FontSize = Size("SmallSize"), Classes = { "muted" },
            VerticalAlignment = VerticalAlignment.Center,
        }, 4));
        return p;
    }

    int UsedBy(string name) => _file is null ? 0 : _file.Document.Sheets
        .Where(s => s.Type == SheetType.ProfileName)
        .SelectMany(s => s.Bindings).Count(b => NameComparer.Equals(b.ActionName, name));

    void AddCustomName()
    {
        if (_file is null) { Status(Strings.Names_OpenOrCreateAProfile); return; }
        var taken = CustomNameRows().Select(r => r.Name).ToHashSet(NameComparer);
        var name = Strings.Names_NewName;
        for (int i = 2; taken.Contains(name); i++) name = string.Format(CultureInfo.CurrentCulture, Strings.Names_NewNameI, i);
        _drafts[name] = "";
        PersistDrafts();
        RebuildRows();
        Status(Strings.Names_PickTheOutputThenType);
    }

    void RenameCustomName(string oldName, string typed)
    {
        var name = typed.Trim();
        if (name == oldName) return;
        // Refusing silently reads as the app being broken, so say which rule
        // it hit. Redrawing puts the old text back in the box. Re-spelling a
        // name in another case is a real edit, so the clash check skips the
        // row being renamed.
        if (name.Length == 0 || name.Length > ProfileFile.MaxActionName)
        { RebuildRows(); Status(string.Format(CultureInfo.CurrentCulture, Strings.Names_ANameHasToBe, ProfileFile.MaxActionName), StatusKind.Warning); return; }
        if (!ProfileFile.IsLegalActionName(name))
        { RebuildRows(); Status(string.Format(CultureInfo.CurrentCulture, Strings.Names_NameIsAlreadyWhatThe, name), StatusKind.Warning); return; }
        if (CustomNameRows().Any(r => NameComparer.Equals(r.Name, name) && !NameComparer.Equals(r.Name, oldName)))
        { RebuildRows(); Status(string.Format(CultureInfo.CurrentCulture, Strings.Names_ThisProfileAlreadyHasA, name), StatusKind.Warning); return; }

        _file?.RenameAction(oldName, name); // no-op when no mapping carries it
        if (_drafts.Remove(oldName, out var token)) _drafts[name] = token;
        PersistDrafts();
        CustomNamesChanged($"Renamed {oldName} to {name}.");
    }

    void RetargetCustomName(string name, string token)
    {
        if (token.Length == 0) return;
        _file?.RetargetAction(name, token); // moves every mapping carrying the name
        if (_drafts.ContainsKey(name)) _drafts[name] = token;
        PersistDrafts();
        CustomNamesChanged(string.Format(CultureInfo.CurrentCulture, Strings.Names_NameIsNowHumanizeToken, name, Humanize(token)));
    }

    void DeleteCustomName(string name)
    {
        int used = UsedBy(name);
        _file?.ClearAction(name); // those mappings keep their output, lose the word
        _drafts.Remove(name);
        PersistDrafts();
        CustomNamesChanged(used == 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.Names_Removed, name)
            : string.Format(CultureInfo.CurrentCulture, Strings.Names_Removed, name)
              + " " + Plural.Of(used, "Names_MappingNowReal"));
    }

    // A name can sit on rows in any mode, so the whole editor is redrawn, not
    // just the table.
    void CustomNamesChanged(string status)
    {
        RefreshEditor();
        if (status.Length > 0) Status(status, StatusKind.Ready);
    }
}
