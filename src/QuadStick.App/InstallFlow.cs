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
        DeviceDescriptor? selected = null;

        if (devices.Count > 1)
        {
            // The existing picker presents mounted locations. Selection is
            // mapped back to the descriptor that owns the opaque DeviceId;
            // presentation never reconstructs identity from that path.
            var choices = devices.Select(DeviceLocation).ToList();
            var chosen = await PickDeviceRootAsync(choices);
            if (chosen is null)
            {
                Telemetry.Track(TelemetryEvent.InstallFailed, InstallFailure.CancelledDevice);
                Status("Install cancelled."); return;
            }
            selected = devices.FirstOrDefault(d => string.Equals(DeviceLocation(d), chosen, StringComparison.Ordinal));
        }
        else if (devices.Count == 1)
        {
            selected = devices[0];
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

            selected = await _architectureServices.ManualDevices.ResolveMountedFolderAsync(folders[0].Path.LocalPath);
            if (selected is null)
            {
                Telemetry.Track(TelemetryEvent.InstallFailed, InstallFailure.NotAQuadstick);
                Status("That folder does not look like a QuadStick (no default.csv at its root).", StatusKind.Error);
                return;
            }
        }

        if (selected is null)
        {
            Telemetry.Track(TelemetryEvent.InstallFailed, InstallFailure.NotAQuadstick);
            Status("That QuadStick is no longer available. Refresh and try again.", StatusKind.Error);
            return;
        }

        var location = DeviceLocation(selected);

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
                $"profile on {location}, not just one game. The prefs.csv already on the drive is backed up first. Continue?");
            if (!confirmPrefs)
            {
                Telemetry.Track(TelemetryEvent.InstallFailed, InstallFailure.CancelledPreferences);
                Status("Install cancelled."); return;
            }
        }

        // Snapshot on the presentation thread before asynchronous work begins.
        // The device workflow never receives the live mutable editor object.
        var snapshot = ProfileSnapshot.From(_file);
        await RunInstallDialogAsync(snapshot, selected.Id, location, confirmDefault, confirmPrefs);
    }

    static string DeviceLocation(DeviceDescriptor device) =>
        string.IsNullOrWhiteSpace(device.Detail) ? device.DisplayName : device.Detail!;

    async Task RunInstallDialogAsync(
        ProfileSnapshot profile,
        DeviceId deviceId,
        string location,
        bool confirmDefault,
        bool confirmPrefs)
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
                new TextBlock { Text = location, FontSize = 15, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
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
                profile, deviceId, confirmDefault, confirmPrefs);

            if (operation.Status != InstallProfileStatus.Installed || operation.Receipt is null)
            {
                var message = operation.Status switch
                {
                    InstallProfileStatus.HasErrors => "Fix the errors in the Problems list before installing.",
                    InstallProfileStatus.ConfirmationRequiredDefault => "Overwriting default.csv requires explicit confirmation.",
                    InstallProfileStatus.ConfirmationRequiredPreferences => "Installing prefs.csv requires explicit confirmation.",
                    _ => "The profile was not installed.",
                };
                throw new InvalidOperationException(message);
            }

            var result = operation.Receipt;
            var backup = result.Recovery?.DisplayLocation ?? "no previous file to back up";
            SetContent(new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    StatusChip(StatusKind.Ready, "Installed"),
                    new TextBlock { Text = result.FileName, FontWeight = FontWeight.Bold,
                                     FontSize = 16, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"Target drive: {location}", FontSize = 15, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"Backup: {backup}",
                                     FontSize = 15, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
                    close,
                },
            });
            close.Focus();

            Telemetry.Track(TelemetryEvent.InstallSucceeded);
            Status($"Installed {result.FileName} to {location}.", StatusKind.Ready);
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