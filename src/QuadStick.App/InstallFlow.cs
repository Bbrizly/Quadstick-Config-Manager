using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using QuadStick.Application.Devices;
using QuadStick.Format;

namespace QuadStick.App;

// Confirm -> progress -> receipt: presentation owns the dialogs while discovery,
// install policy and device I/O go through Application/Infrastructure.
public partial class MainWindow
{
    async Task RunInstallFlowAsync()
    {
        Telemetry.Track(TelemetryEvent.InstallAttempted);

        if (_file is null)
        {
            Telemetry.Track(TelemetryEvent.InstallFailed, InstallFailure.NoProfile);
            Status("Open a profile first."); return;
        }
        _file.Reparse();
        if (_file.HasErrors)
        {
            Telemetry.Track(TelemetryEvent.InstallFailed, InstallFailure.HasErrors);
            Status("Fix the errors in the Problems list before installing.", StatusKind.Error); RefreshIssues(); return;
        }

        var devices = await _architectureServices.DiscoverDevices.ExecuteAsync();
        var candidates = devices
            .Select(d => d.Location ?? d.Id.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        string? root;
        if (candidates.Count > 1)
        {
            root = await PickDeviceRootAsync(candidates);
            if (root is null)
            {
                Telemetry.Track(TelemetryEvent.InstallFailed, InstallFailure.CancelledDevice);
                Status("Install cancelled."); return;
            }
        }
        else if (candidates.Count == 1)
        {
            root = candidates[0];
        }
        else
        {
            Status("No QuadStick drive found (a drive with default.csv on it). Pick the drive or a folder manually.");
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = "Choose the QuadStick drive" });
            if (folders.Count == 0)
            {
                Telemetry.Track(TelemetryEvent.InstallFailed, InstallFailure.CancelledFolder);
                return;
            }
            root = folders[0].Path.LocalPath;
        }

        var deviceId = new DeviceId(root);
        if (!_architectureServices.InstallProfile.IsInstallTarget(deviceId))
        {
            Telemetry.Track(TelemetryEvent.InstallFailed, InstallFailure.NotAQuadstick);
            Status("That folder does not look like a QuadStick (no default.csv at its root).", StatusKind.Error);
            return;
        }

        bool confirmDefault = false;
        if (_file.Document.IsDefaultConfig)
        {
            confirmDefault = await ConfirmAsync(
                "Overwrite default.csv?",
                "A wrong default.csv can disable flash-drive access, and recovery needs a physical force-erase. A backup will be made first. Continue?");
            if (!confirmDefault)
            {
                Telemetry.Track(TelemetryEvent.InstallFailed, InstallFailure.CancelledDefault);
                Status("Install cancelled."); return;
            }
        }

        bool confirmPrefs = false;
        if (_file.Document.IsDevicePreferences)
        {
            confirmPrefs = await ConfirmAsync(
                "Install prefs.csv to this QuadStick?",
                $"prefs.csv holds the device's own settings, so this changes how the QuadStick behaves in every " +
                $"profile on {root}, not just one game. The prefs.csv already on the drive is backed up first. Continue?");
            if (!confirmPrefs)
            {
                Telemetry.Track(TelemetryEvent.InstallFailed, InstallFailure.CancelledPreferences);
                Status("Install cancelled."); return;
            }
        }

        await RunInstallDialogAsync(_file, deviceId, root, confirmDefault, confirmPrefs);
    }

    async Task RunInstallDialogAsync(ProfileFile file, DeviceId deviceId, string root, bool confirmDefault, bool confirmPrefs)
    {
        var host = new StackPanel { Margin = new Thickness(24), Spacing = 16, MinWidth = 420, MaxWidth = 480 };
        var dialog = new Window
        {
            Classes = { "dialog" },
            Title = "Installing profile",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        dialog.Content = DialogShell(dialog, ZoomWrap(host, _uiScale));

        void SetContent(Control content) { host.Children.Clear(); host.Children.Add(content); }

        var progressLine = new TextBlock
        { Text = "Backing up and installing…", FontSize = 15, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(progressLine, AutomationLiveSetting.Polite);
        SetContent(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Installing to", FontWeight = FontWeight.Bold, FontSize = 16 },
                new TextBlock { Text = root, FontSize = 15, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
                progressLine,
            },
        });

        var dialogTask = dialog.ShowDialog(this);

        var close = new Button { Content = "Close", MinWidth = 140, IsDefault = true, IsCancel = true };
        AutomationProperties.SetName(close, "Close");
        close.Click += (_, _) => dialog.Close();

        try
        {
            var operation = await _architectureServices.InstallProfile.ExecuteAsync(
                file, deviceId, confirmDefault, confirmPrefs);

            if (operation.Status != InstallProfileStatus.Installed || operation.Receipt is null)
            {
                var message = operation.Status switch
                {
                    InstallProfileStatus.HasErrors => "Fix the errors in the Problems list before installing.",
                    InstallProfileStatus.InvalidTarget => "That folder does not look like a QuadStick (no default.csv at its root).",
                    InstallProfileStatus.ConfirmationRequiredDefault => "Overwriting default.csv requires explicit confirmation.",
                    InstallProfileStatus.ConfirmationRequiredPreferences => "Installing prefs.csv requires explicit confirmation.",
                    _ => "The profile was not installed.",
                };
                throw new InvalidOperationException(message);
            }

            var result = operation.Receipt;
            SetContent(new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    StatusChip(StatusKind.Ready, "Installed"),
                    new TextBlock { Text = Path.GetFileName(result.InstalledPath), FontWeight = FontWeight.Bold,
                                     FontSize = 16, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"Target drive: {root}", FontSize = 15, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"Backup: {result.BackupPath ?? "no previous file to back up"}",
                                     FontSize = 15, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
                    close,
                },
            });
            close.Focus();

            Telemetry.Track(TelemetryEvent.InstallSucceeded);
            Status($"Installed {Path.GetFileName(result.InstalledPath)} to {root}.", StatusKind.Ready);
        }
        catch (Exception ex)
        {
            Telemetry.Track(TelemetryEvent.InstallFailed, InstallFailure.IoError);
            SetContent(new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    StatusChip(StatusKind.Error, "Install failed"),
                    new TextBlock { Text = ex.Message, FontSize = 15, TextWrapping = TextWrapping.Wrap },
                    close,
                },
            });
            close.Focus();
            Status(ex.Message, StatusKind.Error);
        }

        await dialogTask;
        InstallButton.Focus();
    }
}
