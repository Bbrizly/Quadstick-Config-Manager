using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using QuadStick.Format;

namespace QuadStick.App;

// The files that are actually on a plugged-in QuadStick, grouped by the drive
// they sit on. Same idiom as CommunityProfilesWindow: the list loads after the
// window opens, so home stays fast and a yanked stick shows up as a sentence in
// this window instead of a crash.
//
// Two things this window is careful about. A QuadStick is someone's hands, and
// a wrong delete takes their controls away, so every destructive step names the
// exact file and the exact drive before it runs and the rules themselves live
// in Device.DeleteProfile, not here. And nothing in it may speak in colour
// alone: the light guide writes the colour names out, because the people who
// need that guide most are the ones who cannot read the lights.
//
// This window does not rename, load, run, or talk to the device in any way
// other than reading and writing files on a mounted drive.
public class DeviceFilesWindow : Window
{
    readonly MainWindow _owner;

    readonly StackPanel _groupsPanel;
    readonly TextBlock _summary;
    readonly TextBlock _status;
    readonly Button _refresh;

    readonly List<DeviceGroup> _groups = new();
    Task _busy = Task.CompletedTask;

    /// <summary>Whatever the last button started: the first load, a refresh, a
    /// copy, a delete. Tests await it; nothing in the app has to.</summary>
    internal Task Busy => _busy;

    /// <summary>Which drives to look at. Tests point it at a temp folder so a
    /// run never touches a real removable drive.</summary>
    internal Func<IReadOnlyList<string>> FindRoots { get; set; } = () => Device.FindCandidatesCached();

    /// <summary>Where a deleted file is copied first. Tests point it at a temp
    /// folder so a run never writes to the real backup folder.</summary>
    internal string BackupDir { get; set; } = Device.DefaultBackupDir();

    /// <summary>How Open linked Sheet reaches the browser. Tests swap it so a
    /// run never opens a real browser window.</summary>
    internal Func<Uri, Task> OpenUri { get; set; }

    /// <summary>Every yes/no question this window asks. Tests swap it to answer
    /// without a nested modal, and to read back the exact words.</summary>
    internal Func<string, string, Task<bool>> Confirm { get; set; }

    /// <summary>The drives on screen, in the order they are shown.</summary>
    internal IReadOnlyList<string> Roots => _groups.Select(g => g.Root).ToList();

    public DeviceFilesWindow(MainWindow owner)
    {
        Classes.Add("dialog");
        _owner = owner;
        OpenUri = uri => Launcher.LaunchUriAsync(uri); // this window's own launcher
        Confirm = ConfirmDialogAsync;
        Title = Strings.Device_FilesOnYourQuadStick;
        Width = Math.Min(760 * owner.UiScale, 1100);
        // The shared frame adds a persistent header. Preserve the previous
        // result viewport so a normal three-file drive does not virtualize its
        // last action row just below the fold.
        Height = Math.Min(700 * owner.UiScale, 880);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var explain = new TextBlock
        {
            Text = Strings.Device_EverythingHereReadsAndWrites,
            FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
        };

        _summary = new TextBlock
        {
            Text = Strings.Device_LookingForYourQuadStick,
            FontSize = Size("BodySize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
        };

        _groupsPanel = new StackPanel { Spacing = 22 };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _groupsPanel,
        };

        _status = new TextBlock
        {
            Text = "",
            FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        _refresh = new Button { Content = Strings.Device_Refresh, MinWidth = 130 };
        AutomationProperties.SetName(_refresh, Strings.Device_LookForQuadStickDrivesAgain);
        _refresh.Click += (_, _) => _busy = LoadAsync(refresh: true);

        var close = new Button { Content = Strings.Device_Close, MinWidth = 130, IsCancel = true };
        AutomationProperties.SetName(close, Strings.Device_CloseThisWindow);
        close.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children = { _refresh, close },
        };

        var panel = new DockPanel { LastChildFill = true, Margin = new Thickness(24) };
        DockPanel.SetDock(explain, Dock.Top);
        DockPanel.SetDock(_summary, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(buttons, Dock.Bottom);
        explain.Margin = new Thickness(0, 0, 0, 12);
        _summary.Margin = new Thickness(0, 0, 0, 10);
        _status.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(explain);
        panel.Children.Add(_summary);
        panel.Children.Add(_status);
        panel.Children.Add(buttons);
        panel.Children.Add(scroll);

        Content = MainWindow.DialogShell(this, MainWindow.ZoomWrap(panel, owner.UiScale));

        Opened += (_, _) => close.Focus();
        Opened += (_, _) => _busy = LoadAsync();
    }

    // A fresh dialog may have no focused element, so handle Esc on the window.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape) { e.Handled = true; Close(); }
    }

    // ---- what a drive and a file look like here ----

    sealed record DeviceFileInfo(
        string Root, string Name, string Path, string Subtitle, string? SheetUrl, bool Protected);

    sealed record DeviceGroup(
        string Root, string Label, IReadOnlyList<DeviceFileInfo> Files, string? Error);

    /// <summary>One line of the light guide: its position, its file, and the
    /// five light colours in left-to-right order.</summary>
    internal sealed record GuideEntry(int Number, string FileName, IReadOnlyList<string> Colors)
    {
        // The one wording. It is what the screen reads, what the guide row
        // announces, and what Copy puts on the clipboard, so the three can
        // never drift apart.
        internal string Line => Colors.Count > 0
            ? $"{Number}. {FileName}: {string.Join(", ", Colors)}"
            : string.Format(CultureInfo.CurrentCulture, Strings.Device_NumberFileNameNoLightPattern, Number, FileName);
    }

    // The drive's own name where the system gives us one, so the user reads
    // "QUADSTICK" and not a mount path. The exact path is always shown next to
    // it, because that is the thing an action actually touches.
    // Home shows the same name over its own per-drive groups, so this stays the
    // one place that decides what a drive is called.
    internal static string LabelFor(string root)
    {
        try
        {
            var match = DriveInfo.GetDrives()
                .FirstOrDefault(d => string.Equals(
                    Path.TrimEndingDirectorySeparator(d.RootDirectory.FullName),
                    Path.TrimEndingDirectorySeparator(root),
                    StringComparison.Ordinal));
            if (match is not null && !string.IsNullOrWhiteSpace(match.VolumeLabel))
                return match.VolumeLabel;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* fall through */ }

        var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        return string.IsNullOrWhiteSpace(folder) ? root : folder;
    }

    static string Where(DeviceGroup g) => $"{g.Label} ({g.Root})";

    // ---- loading ----

    async Task LoadAsync(bool refresh = false)
    {
        _refresh.IsEnabled = false;
        if (refresh)
        {
            // An explicit Refresh must not wait out the detection cache: a
            // stick plugged in a second ago has to show up now.
            Device.InvalidateCandidateCache();
            _status.Text = Strings.Device_LookingForQuadStickDrivesAgain;
        }

        List<DeviceGroup> found;
        try
        {
            // Reading a spun-down USB stick can take seconds. Do it off the UI
            // thread so the window never freezes while it happens.
            found = await Task.Run(Gather);
        }
        catch (Exception ex)
        {
            // Even the drive scan can throw on a machine mid-eject. Keep the
            // window alive and say so.
            _groups.Clear();
            _groupsPanel.Children.Clear();
            _summary.Text = Strings.Device_CouldNotLookAtThe;
            _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.Device_CouldNotListTheDrives, ex.Message);
            _refresh.IsEnabled = true;
            return;
        }

        _groups.Clear();
        _groups.AddRange(found);
        Rebuild();
        _refresh.IsEnabled = true;
        if (refresh) _status.Text = "";
    }

    // Everything that touches the filesystem, in one place, off the UI thread.
    // A drive that has been pulled or refuses permission becomes an error on
    // its own group; the other drives still load.
    List<DeviceGroup> Gather()
    {
        var groups = new List<DeviceGroup>();
        foreach (var root in FindRoots())
        {
            string[] paths;
            try
            {
                paths = Directory.GetFiles(root, "*.csv")
                    .Where(p => Device.IsProfileFileName(Path.GetFileName(p)))
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                groups.Add(new DeviceGroup(root, LabelFor(root), Array.Empty<DeviceFileInfo>(),
                    string.Format(CultureInfo.CurrentCulture, Strings.Device_CouldNotReadThisDrive, ex.Message)));
                continue;
            }

            var files = paths
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(p => Describe(root, p))
                .ToList();
            groups.Add(new DeviceGroup(root, LabelFor(root), files, null));
        }
        return groups;
    }

    static DeviceFileInfo Describe(string root, string path)
    {
        var name = Path.GetFileName(path);
        var isProtected =
            name.Equals("default.csv", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("prefs.csv", StringComparison.OrdinalIgnoreCase);

        string subtitle;
        string? sheetUrl = null;
        try
        {
            var doc = Parser.Parse(File.ReadAllText(path)).Doc;
            // Modes, not sheets: a preferences or infrared sheet is neither a
            // mode nor a set of bindings.
            var modes = doc.Sheets.Where(s => s.Type == SheetType.ProfileName).ToList();
            subtitle = doc.IsDevicePreferences
                ? Strings.Device_TheDeviceSOwnSettings
                : MainWindow.TitleNote(doc, path)
                    + $"{Plural.Of(modes.Count, "Count_ModeSheet")}, {Plural.Of(modes.Sum(s => s.Bindings.Count), "Count_Binding")}";
            if (SheetsUrl.TryGetEditUrlFromHeader(doc.HeaderVersion, doc.HeaderSource, out var url))
                sheetUrl = url;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            subtitle = Strings.Device_CouldNotBeReadJust;
        }
        // Anything else is the parser itself failing on a file it should have
        // handled, which is a bug here and not a bad file on the stick. The
        // subtitle stays gentle because this list is only a description and
        // never gates an action, but the crash log gets the real reason instead
        // of the app quietly blaming a file the user can open and edit.
        catch (Exception ex)
        {
            CrashGuard.Note(ex, string.Format(CultureInfo.CurrentCulture, Strings.Device_ReadingNameForTheDevice, name));
            subtitle = Strings.Device_CouldNotBeReadAs;
        }

        if (name.Equals("default.csv", StringComparison.OrdinalIgnoreCase))
            subtitle += Strings.Device_TheDeviceSFallbackFile;
        if (isProtected) subtitle += ", protected";
        return new DeviceFileInfo(root, name, path, subtitle, sheetUrl, isProtected);
    }

    // ---- drawing ----

    void Rebuild()
    {
        _groupsPanel.Children.Clear();

        if (_groups.Count == 0)
        {
            _summary.Text = Strings.Device_NoQuadStickDriveIsPlugged;
            return;
        }

        var total = _groups.Sum(g => g.Files.Count);
        _summary.Text = _groups.Count == 1
            ? $"{Files(total)} on {Where(_groups[0])}."
            : string.Format(CultureInfo.CurrentCulture, Strings.Device_FilesTotalAcrossGroupsCount, Files(total), _groups.Count);

        foreach (var group in _groups)
            _groupsPanel.Children.Add(BuildGroup(group));
    }

    static string Files(int count) => count == 1 ? "1 file" : $"{count} files";

    Control BuildGroup(DeviceGroup group)
    {
        var stack = new StackPanel { Spacing = 10, Tag = group.Root };
        AutomationProperties.SetName(stack, string.Format(CultureInfo.CurrentCulture, Strings.Device_FilesOnTheQuadStickAt, group.Root));

        var title = new TextBlock
        {
            Text = Where(group),
            FontSize = Size("SectionSize"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(title, string.Format(CultureInfo.CurrentCulture, Strings.Device_QuadStickDriveGroupLabelAt, group.Label, group.Root));
        stack.Children.Add(title);

        if (group.Error is not null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = group.Error,
                FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap, Classes = { "error" },
            });
            return stack;
        }

        if (group.Files.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = Strings.Device_ThisDriveHasNoCsv,
                FontSize = Size("BodySize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
            });
            return stack;
        }

        // One list per drive, so the arrow keys walk that drive's files and an
        // action can never be aimed at the drive next to it.
        var list = new ListBox { SelectionMode = SelectionMode.Single, Tag = group.Root };
        AutomationProperties.SetName(list, string.Format(CultureInfo.CurrentCulture, Strings.Device_ProfilesOnGroupLabelAt, group.Label, group.Root));
        list.ItemsSource = group.Files.Select(f => BuildRow(group, f)).ToList();
        stack.Children.Add(list);

        stack.Children.Add(BuildGuide(group));
        return stack;
    }

    ListBoxItem BuildRow(DeviceGroup group, DeviceFileInfo file)
    {
        var name = new TextBlock
        {
            Text = file.Name,
            FontSize = Size("BodySize"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
        };
        var sub = new TextBlock
        {
            Text = file.SheetUrl is null
                ? file.Subtitle
                : file.Subtitle + Strings.Device_LinkedToAGoogleSheet,
            FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
        };

        // Every action is a visible button on the row. Nothing here hides in a
        // right-click menu: a mouth stick cannot right-click.
        var open = RowButton(string.Format(CultureInfo.CurrentCulture, Strings.Device_OpenFileNameFromGroup, file.Name, group.Root), "Open");
        open.Click += (_, _) => _busy = OpenAsync(group, file);

        var copy = RowButton(string.Format(CultureInfo.CurrentCulture, Strings.Device_CopyFileNameFromGroup, file.Name, group.Root), Strings.Device_CopyToLibrary);
        copy.Click += (_, _) => _busy = CopyToLibraryAsync(group, file);

        var sheet = RowButton(string.Format(CultureInfo.CurrentCulture, Strings.Device_OpenTheGoogleSheetLinked, file.Name, group.Root), Strings.Device_OpenLinkedSheet);
        sheet.IsEnabled = file.SheetUrl is not null;
        sheet.Click += (_, _) => _busy = OpenSheetAsync(group, file);

        var delete = RowButton(string.Format(CultureInfo.CurrentCulture, Strings.Device_DeleteFileNameFromThe, file.Name, group.Root), "Delete");
        // The button is off for the two files the device cannot start without.
        // Device.DeleteProfile refuses them too; this only saves the user the
        // trip to a dialog that was always going to say no.
        delete.IsEnabled = !file.Protected;
        delete.Click += (_, _) => _busy = DeleteAsync(group, file);

        var actions = new WrapPanel();
        foreach (var b in new[] { open, copy, sheet, delete })
        {
            b.Margin = new Thickness(0, 0, 8, 0);
            actions.Children.Add(b);
        }

        var stack = new StackPanel { Spacing = 6, Children = { name, sub, actions } };
        var row = new ListBoxItem { Content = stack, Tag = file };
        // The row says the file, the drive, and why a button is off, so none of
        // that depends on seeing a greyed-out control.
        var why = file.Protected ? Strings.Device_ProtectedItCannotBeDeleted : "";
        if (file.SheetUrl is null) why += Strings.Device_NoLinkedGoogleSheetIn;
        AutomationProperties.SetName(row, string.Format(CultureInfo.CurrentCulture, Strings.Device_FileNameOnGroupLabel, file.Name, group.Label, group.Root, file.Subtitle, why));
        return row;
    }

    static Button RowButton(string automationName, string content)
    {
        var b = new Button { Content = content, MinWidth = 110, MinHeight = 34 };
        AutomationProperties.SetName(b, automationName);
        return b;
    }

    // ---- the light guide ----

    /// <summary>The order the device steps through this drive's files, with the
    /// five lights for each one.</summary>
    internal IReadOnlyList<GuideEntry> Guide(string root)
    {
        var group = _groups.FirstOrDefault(g => g.Root == root);
        if (group is null) return Array.Empty<GuideEntry>();
        return Device.SelectionOrder(group.Files.Select(f => f.Name))
            .Select((name, i) => new GuideEntry(i + 1, name, Device.LedPattern(i + 1)))
            .ToList();
    }

    /// <summary>The guide as plain text. This is what Copy puts on the
    /// clipboard, built from the same entries the screen draws.</summary>
    internal string GuideText(string root)
    {
        var group = _groups.FirstOrDefault(g => g.Root == root);
        var head = group is null ? root : Where(group);
        var lines = Guide(root).Select(e => e.Line);
        return string.Join(Environment.NewLine,
            new[] { string.Format(CultureInfo.CurrentCulture, Strings.Device_FileSelectionOrderOnHead, head) }.Concat(lines));
    }

    Control BuildGuide(DeviceGroup group)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = Strings.Device_FileSelectionOrderAndLights,
            FontSize = Size("BodySize"), FontWeight = FontWeight.Bold,
        });
        stack.Children.Add(new TextBlock
        {
            Text = Strings.Device_PushingTheProfileSwitchSteps,
            FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
        });

        foreach (var entry in Guide(group.Root))
            stack.Children.Add(BuildGuideRow(entry));

        var copy = new Button
        {
            Content = Strings.Device_CopyThisGuide,
            MinWidth = 150, MinHeight = 34,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(copy, string.Format(CultureInfo.CurrentCulture, Strings.Device_CopyTheFileSelectionGuide, group.Label, group.Root));
        copy.Click += (_, _) => _busy = CopyGuideAsync(group);
        stack.Children.Add(copy);
        return stack;
    }

    Control BuildGuideRow(GuideEntry entry)
    {
        var row = new WrapPanel();
        // The exact line Copy writes. Screen readers hear the same sentence the
        // clipboard gets, so the guide never depends on seeing the swatches.
        AutomationProperties.SetName(row, entry.Line);

        row.Children.Add(new TextBlock
        {
            Text = string.Format(CultureInfo.CurrentCulture, Strings.Device_EntryNumberEntryFileName, entry.Number, entry.FileName),
            FontSize = Size("SmallSize"), FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 10, 4), VerticalAlignment = VerticalAlignment.Center,
        });

        if (entry.Colors.Count == 0)
        {
            row.Children.Add(new TextBlock
            {
                Text = Strings.Device_NoLightPatternIsDocumented,
                FontSize = Size("SmallSize"), Classes = { "muted" },
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            });
            return row;
        }

        foreach (var color in entry.Colors)
            row.Children.Add(Swatch(color));
        return row;
    }

    // A colour chip that always carries its own name. Never colour alone.
    static Control Swatch(string color)
    {
        var dot = new Border
        {
            Width = 14, Height = 14, CornerRadius = new CornerRadius(7),
            Background = Brush(color),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
        };
        var text = new TextBlock
        {
            Text = color,
            FontSize = Size("SmallSize"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 12, 4),
            Children = { dot, text },
        };
    }

    // The four names the QuadStick table uses, and nothing else.
    static IBrush Brush(string color) => color switch
    {
        "purple" => new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6)),
        "blue" => new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
        "red" => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
        _ => new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
    };

    async Task CopyGuideAsync(DeviceGroup group)
    {
        var text = GuideText(group.Root);
        if (Clipboard is not { } clipboard)
        {
            _status.Text = Strings.Device_ThisComputerWouldNotGive;
            return;
        }
        try
        {
            await clipboard.SetTextAsync(text);
            _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.Device_CopiedTheFileSelectionGuide, Where(group));
        }
        catch (Exception ex)
        {
            _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.Device_CouldNotCopyTheGuide, ex.Message);
        }
    }

    // ---- the four row actions ----

    async Task OpenAsync(DeviceGroup group, DeviceFileInfo file)
    {
        if (file.Name.Equals("prefs.csv", StringComparison.OrdinalIgnoreCase)
            && !await Confirm(Strings.Device_EditDevicePreferences,
                string.Format(CultureInfo.CurrentCulture, Strings.Device_PrefsCsvOnWhereGroup, Where(group))))
        {
            _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.Device_FileNameOnWhereGroup, file.Name, Where(group));
            return;
        }

        string text;
        try
        {
            text = await Task.Run(() => File.ReadAllText(file.Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            await ReportGoneAsync(string.Format(CultureInfo.CurrentCulture, Strings.Device_CouldNotOpenFileName, file.Name, Where(group), ex.Message));
            return;
        }

        try
        {
            _owner.OpenDeviceProfile(ProfileFile.Load(text));
        }
        catch (Exception ex)
        {
            _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.Device_CouldNotOpenFileName2, file.Name, Where(group), ex.Message);
            return;
        }
        // The profile is open in the editor behind this window, so get out of
        // the way rather than covering the thing the user just asked for.
        Close();
    }

    async Task CopyToLibraryAsync(DeviceGroup group, DeviceFileInfo file)
    {
        var dest = Path.Combine(MainWindow.LibraryDir, file.Name);
        var existed = File.Exists(dest);
        if (existed && !await Confirm(
                string.Format(CultureInfo.CurrentCulture, Strings.Device_ReplaceFileNameInYour, file.Name),
                string.Format(CultureInfo.CurrentCulture, Strings.Device_YourLibraryAlreadyHasFile, file.Name, file.Name, Where(group), dest)))
        {
            _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.Device_FileNameWasNotCopied, file.Name);
            return;
        }

        try
        {
            // Read the device file and write the library file. The source is
            // only ever read, so a failure here cannot damage the QuadStick.
            var text = await Task.Run(() => File.ReadAllText(file.Path));

            // The library file can turn up between the check above and the
            // write below: a second drive holding the same name, another copy
            // from this window, another program. Nobody agreed to replace that
            // one, so stop instead of overwriting it in silence.
            if (!existed && File.Exists(dest))
            {
                _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.Device_FileNameTurnedUpIn, file.Name, dest);
                return;
            }

            await Task.Run(() =>
            {
                Directory.CreateDirectory(MainWindow.LibraryDir);
                ProfileFile.WriteAtomic(dest, text);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            await ReportGoneAsync(string.Format(CultureInfo.CurrentCulture, Strings.Device_CouldNotCopyFileName, file.Name, Where(group), ex.Message));
            return;
        }

        _owner.RefreshHomeAfterRestore();
        _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.Device_CopiedFileNameFromWhere, file.Name, Where(group), dest);
    }

    async Task OpenSheetAsync(DeviceGroup group, DeviceFileInfo file)
    {
        if (file.SheetUrl is not { } url)
        {
            _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.Device_FileNameOnWhereGroup2, file.Name, Where(group));
            return;
        }
        try
        {
            await OpenUri(new Uri(url));
            _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.Device_OpenedTheGoogleSheetLinked, file.Name, Where(group));
        }
        catch (Exception ex)
        {
            _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.Device_CouldNotOpenTheSheet, ex.Message);
        }
    }

    async Task DeleteAsync(DeviceGroup group, DeviceFileInfo file)
    {
        // The button for these is already off. This is the second lock, so a
        // stray key or an event raised by hand still cannot get to the delete.
        // Device.DeleteProfile is the third and the real one.
        if (file.Protected)
        {
            _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.Device_FileNameOnWhereGroup3, file.Name, Where(group));
            return;
        }

        if (!await Confirm(
                $"Delete {file.Name} from {group.Label}?",
                string.Format(CultureInfo.CurrentCulture, Strings.Device_FileNameWillBeDeleted, file.Name, group.Root, BackupDir)))
        {
            _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.Device_FileNameOnWhereGroup4, file.Name, Where(group));
            return;
        }

        Device.DeleteResult result;
        try
        {
            // Off the UI thread: a slow or half-pulled stick must not freeze the
            // window while it decides.
            result = await Task.Run(() => Device.DeleteProfile(file.Root, file.Name, BackupDir));
        }
        catch (Exception ex)
        {
            await ReportGoneAsync(string.Format(CultureInfo.CurrentCulture, Strings.Device_CouldNotDeleteFileName, file.Name, Where(group), ex.Message));
            return;
        }

        await LoadAsync();
        _owner.RefreshHomeAfterRestore();
        _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.Device_DeletedResultDeletedPathACopy, result.DeletedPath, result.BackupPath);
    }

    // A drive that vanished mid-action is normal for this hardware. Say what
    // failed, on which drive, then reload so the list matches reality again.
    async Task ReportGoneAsync(string message)
    {
        await LoadAsync();
        _status.Text = message;
    }

    // ---- the window's own yes/no dialog ----

    async Task<bool> ConfirmDialogAsync(string title, string message)
    {
        var yes = new Button { Content = Strings.Device_YesContinue, MinWidth = 140 };
        AutomationProperties.SetName(yes, title + " Yes, continue.");
        var no = new Button { Content = Strings.Device_Cancel, MinWidth = 140, IsDefault = true, IsCancel = true };
        AutomationProperties.SetName(no, Strings.Device_CancelChangeNothing);
        var dialog = new Window
        {
            Classes = { "dialog" },
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        dialog.Content = MainWindow.DialogShell(dialog, MainWindow.ZoomWrap(new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16,
            MaxWidth = 520,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.Bold, FontSize = Size("SubheadSize"), TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize") },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { yes, no } },
            },
        }, _owner.UiScale));
        var result = false;
        yes.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        return result;
    }

    static double Size(string tokenKey) => (double)Application.Current!.FindResource(tokenKey)!;
}
