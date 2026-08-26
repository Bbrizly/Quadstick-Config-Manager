using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace QuadStick.App;

// Drive restore picker. One dialog for bulk restore and cherry-pick, from
// home, settings, and onboarding. Same idiom as SettingsWindow.
//
// Sheets load after the window opens, not in the ctor, so home stays local
// and a Drive failure shows in the status line instead of crashing.
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
        Classes.Add("dialog");
        _owner = owner;
        _preCheck = preCheck;
        Title = Strings.DrivePick_ImportFromGoogleDrive;
        Width = Math.Min(560 * owner.UiScale, 1000);
        Height = Math.Min(560 * owner.UiScale, 800);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var heading = new TextBlock
        {
            Text = Strings.DrivePick_YourGoogleDriveBackups,
            FontSize = Size("SubheadSize"), FontWeight = FontWeight.Bold,
        };
        var explain = new TextBlock
        {
            Text = Strings.DrivePick_PickTheProfilesToCopy,
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
            Text = Strings.DrivePick_LoadingYourBackups,
            FontSize = Size("BodySize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        _import = new Button { Content = Strings.DrivePick_Import, Classes = { "primary" }, MinWidth = 140, IsEnabled = false };
        AutomationProperties.SetName(_import, Strings.DrivePick_ImportTheSelectedProfiles);
        _import.Click += async (_, _) => await ImportAsync();

        var cancel = new Button { Content = Strings.DrivePick_Close, MinWidth = 140, IsCancel = true };
        AutomationProperties.SetName(cancel, Strings.DrivePick_CloseThisWindow);
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

        Content = MainWindow.DialogShell(this, MainWindow.ZoomWrap(panel, owner.UiScale));

        // Focus a control so Esc works from the first press.
        Opened += (_, _) => cancel.Focus();
        Opened += async (_, _) => await LoadAsync();
    }

    // A fresh dialog may have no focused element, so handle Esc on the window.
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
                if (s.AlreadyLinked) label += Strings.DrivePick_AlreadyInYourProfiles;
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
                _status.Text = Strings.DrivePick_NoBackupsFoundInYour;
            else
                _status.Text = "";
            _import.IsEnabled = _rows.Any(r => r.Check.IsEnabled);
        }
        catch (Exception ex)
        {
            _status.Text = Strings.DrivePick_CouldNotLoadYourBackups + ex.Message;
        }
    }

    async Task ImportAsync()
    {
        var picks = _rows
            .Where(r => r.Check.IsChecked == true && !r.Info.AlreadyLinked)
            .Select(r => (r.Info.Id, r.Info.Name))
            .ToList();
        if (picks.Count == 0) { _status.Text = Strings.DrivePick_NothingSelectedToImport; return; }

        _import.IsEnabled = false;
        _status.Text = Strings.DrivePick_Importing;
        try
        {
            var summary = await _owner.RestoreFromDriveAsync(picks);
            _owner.RefreshHomeAfterRestore();
            // Reload to grey out imported sheets, then show the summary
            // (LoadAsync overwrites the status line, so set it after).
            await LoadAsync();
            _status.Text = summary.Message;
        }
        catch (Exception ex)
        {
            _status.Text = Strings.DrivePick_ImportFailed + ex.Message;
        }
        finally
        {
            _import.IsEnabled = _rows.Any(r => r.Check.IsEnabled);
        }
        // Stay open so the user reads the result and can pick more.
    }

    // Drive gives an RFC3339 timestamp. Show a local short date, or the
    // raw string if it will not parse.
    static string ShortDate(string modifiedTime)
    {
        if (DateTimeOffset.TryParse(modifiedTime, out var dt))
            return dt.ToLocalTime().ToString("d");
        return modifiedTime;
    }

    static double Size(string tokenKey) => (double)Application.Current!.FindResource(tokenKey)!;
}
