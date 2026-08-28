using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using QuadStick.Application.Devices;
using QuadStick.Format;

namespace QuadStick.App;

// Presentation for profile files already on mounted QuadStick drives. Device
// discovery/filesystem mutation are Application use cases backed by
// Infrastructure; this window only renders, asks explicit questions and opens
// the resulting profile/URL.
public class DeviceFilesWindow : Window
{
    readonly MainWindow _owner;
    readonly DeviceFileManagementUseCase _files;

    readonly StackPanel _groupsPanel;
    readonly TextBlock _summary;
    readonly TextBlock _status;
    readonly Button _refresh;

    readonly List<DeviceGroup> _groups = new();
    Task _busy = Task.CompletedTask;

    internal Task Busy => _busy;

    // Test seam: roots are supplied only to the mounted-volume adapter inside
    // composition. Application receives opaque device ids, not these paths.
    internal Func<IReadOnlyList<string>> FindRoots { get; set; } = CompositionRoot.FindDeviceRoots;

    internal string BackupDir { get; set; } = CompositionRoot.DefaultDeviceBackupDirectory;

    internal Func<Uri, Task> OpenUri { get; set; }
    internal Func<string, string, Task<bool>> Confirm { get; set; }
    internal IReadOnlyList<string> Roots => _groups.Select(g => g.Root).ToList();

    public DeviceFilesWindow(MainWindow owner)
    {
        Classes.Add("dialog");
        _owner = owner;
        _files = CompositionRoot.CreateDeviceFileManagement(() => FindRoots());
        OpenUri = uri => Launcher.LaunchUriAsync(uri);
        Confirm = ConfirmDialogAsync;
        Title = "Files on your QuadStick";
        Width = Math.Min(760 * owner.UiScale, 1100);
        Height = Math.Min(700 * owner.UiScale, 880);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var explain = new TextBlock
        {
            Text = "Everything here reads and writes files on the QuadStick's drive, nothing else. "
                 + "Files are grouped by the drive they are on, and every action names the drive it will touch. "
                 + "Deleting keeps a copy in your backup folder first. "
                 + "default.csv and prefs.csv cannot be deleted, because the device needs them.",
            FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
        };

        _summary = new TextBlock
        {
            Text = "Looking for your QuadStick...",
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

        _refresh = new Button { Content = "Refresh", MinWidth = 130 };
        AutomationProperties.SetName(_refresh, "Look for QuadStick drives again and reload the file list");
        _refresh.Click += (_, _) => _busy = LoadAsync(refresh: true);

        var close = new Button { Content = "Close", MinWidth = 130, IsCancel = true };
        AutomationProperties.SetName(close, "Close this window");
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape) { e.Handled = true; Close(); }
    }

    sealed record DeviceFileInfo(
        DeviceProfileId Id, string Root, string Name, string Path,
        string Subtitle, string? SheetUrl, bool Protected);

    sealed record DeviceGroup(
        DeviceId Id, string Root, string Label, IReadOnlyList<DeviceFileInfo> Files, string? Error);

    internal sealed record GuideEntry(int Number, string FileName, IReadOnlyList<string> Colors)
    {
        internal string Line => Colors.Count > 0
            ? $"{Number}. {FileName}: {string.Join(", ", Colors)}"
            : $"{Number}. {FileName}: no light pattern is documented for this position";
    }

    internal static string LabelFor(string root) => CompositionRoot.DeviceLabelFor(root);

    static string Where(DeviceGroup g) => $"{g.Label} ({g.Root})";

    async Task LoadAsync(bool refresh = false)
    {
        _refresh.IsEnabled = false;
        if (refresh)
        {
            _files.InvalidateDiscovery();
            _status.Text = "Looking for QuadStick drives again...";
        }

        IReadOnlyList<ManagedDeviceGroup> found;
        try
        {
            found = await _files.ListAsync();
        }
        catch (Exception ex)
        {
            _groups.Clear();
            _groupsPanel.Children.Clear();
            _summary.Text = "Could not look at the drives on this computer.";
            _status.Text = $"Could not list the drives: {ex.Message} Press Refresh to try again.";
            _refresh.IsEnabled = true;
            return;
        }

        _groups.Clear();
        _groups.AddRange(found.Select(ToViewGroup));
        Rebuild();
        _refresh.IsEnabled = true;
        if (refresh) _status.Text = "";
    }

    static DeviceGroup ToViewGroup(ManagedDeviceGroup group)
    {
        var root = string.IsNullOrWhiteSpace(group.Device.Detail)
            ? group.Device.DisplayName
            : group.Device.Detail!;
        return new DeviceGroup(
            group.Device.Id,
            root,
            group.Device.DisplayName,
            group.Files.Select(file => Describe(root, file)).ToList(),
            group.Error);
    }

    static DeviceFileInfo Describe(string root, ManagedDeviceFile file)
    {
        var displayPath = Path.Combine(root, file.Name);
        string subtitle;
        string? sheetUrl = null;
        if (file.ReadError is not null)
        {
            subtitle = "could not be read just now";
        }
        else if (file.ParseFailure is { } parseFailure)
        {
            CrashGuard.Note(parseFailure, $"reading {file.Name} for the device file list");
            subtitle = "could not be read as a profile";
        }
        else if (file.Profile is { } profile)
        {
            var doc = profile.Document;
            var modes = doc.Sheets.Where(s => s.Type == SheetType.ProfileName).ToList();
            subtitle = doc.IsDevicePreferences
                ? "the device's own settings file"
                : MainWindow.TitleNote(doc, displayPath)
                    + $"{Plural.Of(modes.Count, "mode sheet")}, {Plural.Of(modes.Sum(s => s.Bindings.Count), "binding")}";
            sheetUrl = CompositionRoot.LinkedGoogleSheetUrl(profile);
        }
        else
        {
            subtitle = "could not be read just now";
        }

        if (file.Name.Equals("default.csv", StringComparison.OrdinalIgnoreCase))
            subtitle += ", the device's fallback file";
        if (file.Protected) subtitle += ", protected";
        return new DeviceFileInfo(file.Id, root, file.Name, displayPath, subtitle, sheetUrl, file.Protected);
    }

    void Rebuild()
    {
        _groupsPanel.Children.Clear();
        if (_groups.Count == 0)
        {
            _summary.Text = "No QuadStick drive is plugged in right now. "
                          + "A QuadStick drive is one with default.csv on it. "
                          + "On USB emulation mode 6 the drive does not appear at all.";
            return;
        }

        var total = _groups.Sum(g => g.Files.Count);
        _summary.Text = _groups.Count == 1
            ? $"{Files(total)} on {Where(_groups[0])}."
            : $"{Files(total)} across {_groups.Count} QuadStick drives. Each drive is listed on its own below.";

        foreach (var group in _groups)
            _groupsPanel.Children.Add(BuildGroup(group));
    }

    static string Files(int count) => count == 1 ? "1 file" : $"{count} files";

    Control BuildGroup(DeviceGroup group)
    {
        var stack = new StackPanel { Spacing = 10, Tag = group.Root };
        AutomationProperties.SetName(stack, $"Files on the QuadStick at {group.Root}");

        var title = new TextBlock
        {
            Text = Where(group),
            FontSize = Size("SectionSize"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(title, $"QuadStick drive {group.Label}, at {group.Root}");
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
                Text = "This drive has no .csv profiles on it.",
                FontSize = Size("BodySize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
            });
            return stack;
        }

        var list = new ListBox { SelectionMode = SelectionMode.Single, Tag = group.Root };
        AutomationProperties.SetName(list, $"Profiles on {group.Label} at {group.Root}, use the arrow keys");
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
            Text = file.SheetUrl is null ? file.Subtitle : file.Subtitle + ", linked to a Google Sheet",
            FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
        };

        var open = RowButton($"Open {file.Name} from {group.Root} in the editor", "Open");
        open.Click += (_, _) => _busy = OpenAsync(group, file);
        var copy = RowButton($"Copy {file.Name} from {group.Root} into your profile library", "Copy to library");
        copy.Click += (_, _) => _busy = CopyToLibraryAsync(group, file);
        var sheet = RowButton($"Open the Google Sheet linked from {file.Name} on {group.Root}", "Open linked Sheet");
        sheet.IsEnabled = file.SheetUrl is not null;
        sheet.Click += (_, _) => _busy = OpenSheetAsync(group, file);
        var delete = RowButton($"Delete {file.Name} from the QuadStick at {group.Root}", "Delete");
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
        var why = file.Protected ? ", protected, it cannot be deleted" : "";
        if (file.SheetUrl is null) why += ", no linked Google Sheet in its header";
        AutomationProperties.SetName(row, $"{file.Name} on {group.Label} at {group.Root}, {file.Subtitle}{why}");
        return row;
    }

    static Button RowButton(string automationName, string content)
    {
        var b = new Button { Content = content, MinWidth = 110, MinHeight = 34 };
        AutomationProperties.SetName(b, automationName);
        return b;
    }

    internal IReadOnlyList<GuideEntry> Guide(string root)
    {
        var group = _groups.FirstOrDefault(g => g.Root == root);
        if (group is null) return Array.Empty<GuideEntry>();
        return DeviceProfileRules.SelectionOrder(group.Files.Select(f => f.Name))
            .Select((name, i) => new GuideEntry(i + 1, name, DeviceProfileRules.LedPattern(i + 1)))
            .ToList();
    }

    internal string GuideText(string root)
    {
        var group = _groups.FirstOrDefault(g => g.Root == root);
        var head = group is null ? root : Where(group);
        var lines = Guide(root).Select(e => e.Line);
        return string.Join(Environment.NewLine,
            new[] { $"File selection order on {head}" }.Concat(lines));
    }

    Control BuildGuide(DeviceGroup group)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = "File selection order and lights",
            FontSize = Size("BodySize"), FontWeight = FontWeight.Bold,
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Pushing the profile switch steps through the files in this order, and the five lights "
                 + "show which one is loaded. prefs.csv is settings, not a profile, so it is never in the list. "
                 + "The colours are written out below as well as shown.",
            FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
        });

        foreach (var entry in Guide(group.Root))
            stack.Children.Add(BuildGuideRow(entry));

        var copy = new Button
        {
            Content = "Copy this guide", MinWidth = 150, MinHeight = 34,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(copy, $"Copy the file selection guide for {group.Label} at {group.Root} as text");
        copy.Click += (_, _) => _busy = CopyGuideAsync(group);
        stack.Children.Add(copy);
        return stack;
    }

    Control BuildGuideRow(GuideEntry entry)
    {
        var row = new WrapPanel();
        AutomationProperties.SetName(row, entry.Line);
        row.Children.Add(new TextBlock
        {
            Text = $"{entry.Number}. {entry.FileName}",
            FontSize = Size("SmallSize"), FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 10, 4), VerticalAlignment = VerticalAlignment.Center,
        });

        if (entry.Colors.Count == 0)
        {
            row.Children.Add(new TextBlock
            {
                Text = "no light pattern is documented for this position",
                FontSize = Size("SmallSize"), Classes = { "muted" },
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            });
            return row;
        }

        foreach (var color in entry.Colors) row.Children.Add(Swatch(color));
        return row;
    }

    static Control Swatch(string color)
    {
        var dot = new Border
        {
            Width = 14, Height = 14, CornerRadius = new CornerRadius(7),
            Background = Brush(color),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
        };
        var text = new TextBlock
        {
            Text = color, FontSize = Size("SmallSize"), VerticalAlignment = VerticalAlignment.Center,
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 4),
            Children = { dot, text },
        };
    }

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
            _status.Text = "This computer would not give the app its clipboard, so the guide was not copied.";
            return;
        }
        try
        {
            await clipboard.SetTextAsync(text);
            _status.Text = $"Copied the file selection guide for {Where(group)} to the clipboard.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not copy the guide: {ex.Message}";
        }
    }

    async Task OpenAsync(DeviceGroup group, DeviceFileInfo file)
    {
        if (file.Name.Equals("prefs.csv", StringComparison.OrdinalIgnoreCase)
            && !await Confirm("Edit device preferences?",
                $"prefs.csv on {Where(group)} holds the QuadStick's own settings, not a game profile. "
                + "A wrong value here changes how the whole device behaves. "
                + "Only continue if you know which setting you are changing."))
        {
            _status.Text = $"{file.Name} on {Where(group)} was not opened.";
            return;
        }

        ProfileFile profile;
        try
        {
            profile = await _files.ReadProfileAsync(file.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException
                                       or InvalidDataException or InvalidOperationException)
        {
            await ReportGoneAsync($"Could not open {file.Name} from {Where(group)}: {ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not open {file.Name} from {Where(group)}: {ex.Message}";
            return;
        }

        _owner.OpenDeviceProfile(profile);
        Close();
    }

    async Task CopyToLibraryAsync(DeviceGroup group, DeviceFileInfo file)
    {
        try
        {
            var result = await _files.CopyToLibraryAsync(
                file.Id, MainWindow.LibraryDir, replaceExisting: false);

            if (result.Kind == LibraryCopyKind.NeedsReplaceConfirmation)
            {
                if (!await Confirm(
                        $"Replace {file.Name} in your library?",
                        $"Your library already has {file.Name}. Copying {file.Name} from {Where(group)} "
                        + $"will overwrite {result.Destination}. The copy on the QuadStick is not changed either way."))
                {
                    _status.Text = $"{file.Name} was not copied. Your library file is unchanged.";
                    return;
                }
                result = await _files.CopyToLibraryAsync(
                    file.Id, MainWindow.LibraryDir, replaceExisting: true);
            }

            if (result.Kind == LibraryCopyKind.RaceDetected)
            {
                _status.Text = $"{file.Name} turned up in your library while the copy was running, "
                             + $"so {result.Destination} was left alone. Copy it again to replace it.";
                return;
            }

            _owner.RefreshHomeAfterRestore();
            _status.Text = $"Copied {file.Name} from {Where(group)} to {result.Destination}. "
                         + "The file on the QuadStick is unchanged.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException
                                       or InvalidDataException or InvalidOperationException)
        {
            await ReportGoneAsync($"Could not copy {file.Name} from {Where(group)}: {ex.Message}");
        }
    }

    async Task OpenSheetAsync(DeviceGroup group, DeviceFileInfo file)
    {
        if (file.SheetUrl is not { } url)
        {
            _status.Text = $"{file.Name} on {Where(group)} does not name a Google Sheet in its header, "
                         + "so there is nothing to open.";
            return;
        }
        try
        {
            await OpenUri(new Uri(url));
            _status.Text = $"Opened the Google Sheet linked from {file.Name} on {Where(group)} in your browser.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not open the sheet in your browser: {ex.Message}";
        }
    }

    async Task DeleteAsync(DeviceGroup group, DeviceFileInfo file)
    {
        if (file.Protected)
        {
            _status.Text = $"{file.Name} on {Where(group)} is protected and cannot be deleted. "
                         + "Removing it can leave the device unusable.";
            return;
        }

        if (!await Confirm(
                $"Delete {file.Name} from {group.Label}?",
                $"{file.Name} will be deleted from the QuadStick at {group.Root}. "
                + $"A copy is saved in {BackupDir} first, so you can put it back with Install. "
                + "Nothing else on this or any other drive is touched."))
        {
            _status.Text = $"{file.Name} on {Where(group)} was not deleted.";
            return;
        }

        DeviceDeleteReceipt result;
        try
        {
            result = await _files.DeleteAsync(file.Id, BackupDir);
        }
        catch (Exception ex)
        {
            await ReportGoneAsync($"Could not delete {file.Name} from {Where(group)}: {ex.Message}");
            return;
        }

        await LoadAsync();
        _owner.RefreshHomeAfterRestore();
        _status.Text = $"Deleted {file.Path}. A copy is saved at {result.Recovery.DisplayLocation}.";
    }

    async Task ReportGoneAsync(string message)
    {
        await LoadAsync();
        _status.Text = message;
    }

    async Task<bool> ConfirmDialogAsync(string title, string message)
    {
        var yes = new Button { Content = "Yes, continue", MinWidth = 140 };
        AutomationProperties.SetName(yes, title + " Yes, continue.");
        var no = new Button { Content = "Cancel", MinWidth = 140, IsDefault = true, IsCancel = true };
        AutomationProperties.SetName(no, "Cancel, change nothing");
        var dialog = new Window
        {
            Classes = { "dialog" }, Title = title, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        dialog.Content = MainWindow.DialogShell(dialog, MainWindow.ZoomWrap(new StackPanel
        {
            Margin = new Thickness(24), Spacing = 16, MaxWidth = 520,
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