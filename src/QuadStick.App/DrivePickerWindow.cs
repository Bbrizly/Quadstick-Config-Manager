using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace QuadStick.App;

// The Drive restore picker: one dialog serves bulk restore and cherry-picking
// from all three entry points (home, settings, onboarding). Follows the app's
// code-behind dialog idiom (see SettingsWindow): a plain Window, ShowDialog
// from the caller, Esc closes, AutomationProperties on every control.
//
// The sheet list is fetched AFTER the window opens, not in the constructor:
// Home stays a local view and no Drive call happens until the picker is up. A
// network failure shows in the status line, never a crash.
public class DrivePickerWindow : Window
{
    readonly MainWindow _owner;
    readonly bool _preCheck;
    readonly StackPanel _list;
    readonly TextBlock _status;
    readonly Button _import;
    readonly List<(CheckBox Check, DriveSheetInfo Info)> _rows = new();

    public DrivePickerWindow(MainWindow owner, bool preCheck)
    {
        _owner = owner;
        _preCheck = preCheck;
        Title = "Import from Google Drive";
        Width = Math.Min(560 * owner.UiScale, 1000);
        Height = Math.Min(560 * owner.UiScale, 800);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var heading = new TextBlock
        {
            Text = "Your Google Drive backups",
            FontSize = Size("SubheadSize"), FontWeight = FontWeight.Bold,
        };
        var explain = new TextBlock
        {
            Text = "Pick the profiles to copy onto this computer. Only the sheets this "
                 + "app backed up are listed. Ones already in your profiles are greyed out.",
            FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
        };

        _list = new StackPanel { Spacing = 8 };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _list,
        };

        _status = new TextBlock
        {
            Text = "Loading your backups...",
            FontSize = Size("BodySize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        _import = new Button { Content = "Import", Classes = { "primary" }, MinWidth = 140, IsEnabled = false };
        AutomationProperties.SetName(_import, "Import the selected profiles");
        _import.Click += async (_, _) => await ImportAsync();

        var cancel = new Button { Content = "Close", MinWidth = 140, IsCancel = true };
        AutomationProperties.SetName(cancel, "Close this window");
        cancel.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children = { _import, cancel },
        };

        var panel = new DockPanel { LastChildFill = true, Margin = new Thickness(24) };
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(explain, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(buttons, Dock.Bottom);
        heading.Margin = new Thickness(0, 0, 0, 8);
        explain.Margin = new Thickness(0, 0, 0, 12);
        _status.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(heading);
        panel.Children.Add(explain);
        panel.Children.Add(_status);
        panel.Children.Add(buttons);
        panel.Children.Add(scroll);

        Content = MainWindow.ZoomWrap(panel, owner.UiScale);

        // Pull the keyboard into this window so Esc works from the first press,
        // the same fix SettingsWindow uses.
        Opened += (_, _) => cancel.Focus();
        Opened += async (_, _) => await LoadAsync();
    }

    // A freshly opened dialog can have no focused element, so handle Escape on
    // the window itself, matching SettingsWindow.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape) { e.Handled = true; Close(); }
    }

    async Task LoadAsync()
    {
        try
        {
            var sheets = await _owner.ListDriveSheetsAsync();
            _list.Children.Clear();
            _rows.Clear();
            foreach (var s in sheets)
            {
                var label = $"{s.Name}, {ShortDate(s.ModifiedTime)}";
                if (s.AlreadyLinked) label += "  (already in your profiles)";
                var check = new CheckBox
                {
                    Content = label,
                    FontSize = Size("BodySize"),
                    IsEnabled = !s.AlreadyLinked,
                    IsChecked = _preCheck && !s.AlreadyLinked,
                };
                AutomationProperties.SetName(check, label);
                _rows.Add((check, s));
                _list.Children.Add(check);
            }

            if (sheets.Count == 0)
                _status.Text = "No backups found in your Google Drive yet.";
            else
                _status.Text = "";
            _import.IsEnabled = _rows.Any(r => r.Check.IsEnabled);
        }
        catch (Exception ex)
        {
            _status.Text = "Could not load your backups: " + ex.Message;
        }
    }

    async Task ImportAsync()
    {
        var picks = _rows
            .Where(r => r.Check.IsChecked == true && !r.Info.AlreadyLinked)
            .Select(r => (r.Info.Id, r.Info.Name))
            .ToList();
        if (picks.Count == 0) { _status.Text = "Nothing selected to import."; return; }

        _import.IsEnabled = false;
        _status.Text = "Importing...";
        try
        {
            var summary = await _owner.RestoreFromDriveAsync(picks);
            _status.Text = summary.Message;
            _owner.RefreshHomeAfterRestore();
        }
        catch (Exception ex)
        {
            _status.Text = "Import failed: " + ex.Message;
        }
        finally
        {
            _import.IsEnabled = _rows.Any(r => r.Check.IsEnabled);
        }
        // The window stays open so the user reads the result and can pick more.
    }

    // Drive returns an RFC3339 timestamp; show the user a plain local short
    // date. If it will not parse, show it as-is rather than nothing.
    static string ShortDate(string modifiedTime)
    {
        if (DateTimeOffset.TryParse(modifiedTime, out var dt))
            return dt.ToLocalTime().ToString("d");
        return modifiedTime;
    }

    static double Size(string tokenKey) => (double)Application.Current!.FindResource(tokenKey)!;
}
