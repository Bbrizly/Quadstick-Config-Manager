using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using QuadStick.Format;

namespace QuadStick.App;

// Confirm -> progress -> receipt: a single modal Window whose content panel
// is swapped in place as the install advances, rather than a multi-step
// stepper control. Device pick and the default.csv confirmation reuse the
// existing PickDeviceRootAsync/ConfirmAsync dialogs; only the write itself
// (Device.Install) gets its own progress/receipt window, because that's the
// step with no other feedback otherwise.
public partial class MainWindow
{
    async Task RunInstallFlowAsync()
    {
        // Every exit below is one reason, so the funnel says where installs
        // actually die rather than only that they did.
        Telemetry.Track(TelemetryEvent.InstallAttempted);

        if (_file is null)
        {
            Telemetry.Track(TelemetryEvent.InstallFailed, InstallFailure.NoProfile);
            Status(Strings.Install_OpenAProfileFirst); return;
        }
        _file.Reparse();
        if (_file.HasErrors)
        {
            Telemetry.Track(TelemetryEvent.InstallFailed, InstallFailure.HasErrors);
            Status(Strings.Install_FixTheErrorsInThe, StatusKind.Error); RefreshIssues(); return;
        }

        var candidates = Device.FindCandidates();
        string? root;
        if (candidates.Count > 1)
        {
            root = await PickDeviceRootAsync(candidates);
            // Cancelling the drive choice cancels the install; it must not
            // fall through to a folder picker the user did not ask for.
            if (root is null)
            {
                Telemetry.Track(TelemetryEvent.InstallFailed, InstallFailure.CancelledDevice);
                Status(Strings.Install_InstallCancelled); return;
            }
        }
        else if (candidates.Count == 1)
        {
            root = candidates[0];
        }
        else
        {
            Status(Strings.Install_NoQuadStickDriveFoundA);
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = Strings.Install_ChooseTheQuadStickDrive });
            if (folders.Count == 0)
            {
                Telemetry.Track(TelemetryEvent.InstallFailed, InstallFailure.CancelledFolder);
                return;
            }
            root = folders[0].Path.LocalPath;
        }

        if (!Device.IsInstallTarget(root))
        {
            Telemetry.Track(TelemetryEvent.InstallFailed, InstallFailure.NotAQuadstick);
            Status(Strings.Install_ThatFolderDoesNotLook, StatusKind.Error);
            return;
        }

        bool confirmDefault = false;
        if (_file.Document.IsDefaultConfig)
        {
            confirmDefault = await ConfirmAsync(
                Strings.Install_OverwriteDefaultCsv,
                Strings.Install_AWrongDefaultCsvCan);
            if (!confirmDefault)
            {
                Telemetry.Track(TelemetryEvent.InstallFailed, InstallFailure.CancelledDefault);
                Status(Strings.Install_InstallCancelled2); return;
            }
        }

        // prefs.csv is the device's settings file, not one game's profile, so it
        // gets its own confirmation naming the file, the drive, and the reach of
        // the change. Nothing is written until this comes back true.
        bool confirmPrefs = false;
        if (_file.Document.IsDevicePreferences)
        {
            confirmPrefs = await ConfirmAsync(
                Strings.Install_InstallPrefsCsvToThis,
                string.Format(CultureInfo.CurrentCulture, Strings.Install_PrefsCsvHoldsTheDevice, root));
            if (!confirmPrefs)
            {
                Telemetry.Track(TelemetryEvent.InstallFailed, InstallFailure.CancelledPreferences);
                Status(Strings.Install_InstallCancelled3); return;
            }
        }

        await RunInstallDialogAsync(_file, root, confirmDefault, confirmPrefs);
    }

    // Drives the modal itself: target drive -> progress -> receipt/failure.
    async Task RunInstallDialogAsync(ProfileFile file, string root, bool confirmDefault, bool confirmPrefs)
    {
        var host = new StackPanel { Margin = new Thickness(24), Spacing = 16, MinWidth = 420, MaxWidth = 480 };
        var dialog = new Window
        {
            Classes = { "dialog" },
            Title = Strings.Install_InstallingProfile,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        dialog.Content = DialogShell(dialog, ZoomWrap(host, _uiScale));
        // No inline/frozen Background: the app-wide "Window" style in
        // App.axaml already binds Background to {DynamicResource
        // AppBackgroundBrush}, so this dialog follows theme changes for free.

        void SetContent(Control content) { host.Children.Clear(); host.Children.Add(content); }

        var progressLine = new TextBlock
        { Text = Strings.Install_BackingUpAndInstalling, FontSize = 15, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(progressLine, AutomationLiveSetting.Polite);
        SetContent(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = Strings.Install_InstallingTo, FontWeight = FontWeight.Bold, FontSize = 16 },
                new TextBlock { Text = root, FontSize = 15, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
                progressLine,
            },
        });

        // Kick the modal off but don't await it yet: it only completes when
        // the user closes it, and we need the window on screen *while* the
        // background install runs so the progress line is actually seen.
        var dialogTask = dialog.ShowDialog(this);

        // Same dismiss button for both outcomes; only one branch ever shows it.
        var close = new Button { Content = Strings.Install_Close, MinWidth = 140, IsDefault = true, IsCancel = true };
        AutomationProperties.SetName(close, Strings.Install_Close2);
        close.Click += (_, _) => dialog.Close();

        try
        {
            // Device.Install does synchronous file I/O; keep it off the UI
            // thread. Avalonia's SynchronizationContext resumes the
            // continuation on the UI thread, so the content swap below is safe.
            var result = await Task.Run(() => Device.Install(file, root, Device.DefaultBackupDir(), confirmDefault, confirmPrefs));

            SetContent(new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    StatusChip(StatusKind.Ready, "Installed"),
                    new TextBlock { Text = Path.GetFileName(result.InstalledPath), FontWeight = FontWeight.Bold,
                                     FontSize = 16, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = string.Format(CultureInfo.CurrentCulture, Strings.Install_TargetDriveRoot, root), FontSize = 15, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = string.Format(CultureInfo.CurrentCulture, Strings.Install_BackupPath, result.BackupPath ?? Strings.Install_NoPreviousFile),
                                     FontSize = 15, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
                    close,
                },
            });
            close.Focus();

            Telemetry.Track(TelemetryEvent.InstallSucceeded);
            Status(string.Format(CultureInfo.CurrentCulture, Strings.Install_InstalledPathGetFileNameResultInstalledPath, Path.GetFileName(result.InstalledPath), root), StatusKind.Ready);
        }
        // Anything at all, not a named few. The panel on screen while the
        // install runs holds no button: `close` is only put in the tree by the
        // two branches here. So an exception this filter did not name escaped
        // an async void click handler, CrashGuard swallowed it to keep the app
        // alive, and the user was left looking at "Backing up and installing..."
        // for ever with no way out but the title bar, and the reason for it only
        // in a crash log. Whatever went wrong, saying so beats hanging.
        catch (Exception ex)
        {
            // Show the exception message verbatim: Device.Install already
            // distinguishes "the device was not modified" (readback failure)
            // from "restored from backup" (mid-swap failure), and inventing a
            // blanket "unchanged" here would misstate the mid-swap case.
            Telemetry.Track(TelemetryEvent.InstallFailed, InstallFailure.IoError);
            SetContent(new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    StatusChip(StatusKind.Error, Strings.Install_InstallFailed),
                    new TextBlock { Text = ex.Message, FontSize = 15, TextWrapping = TextWrapping.Wrap },
                    close,
                },
            });
            close.Focus();

            Status(ex.Message, StatusKind.Error);
        }

        await dialogTask;
        InstallButton.Focus(); // both outcomes return focus to Install once the modal is gone
    }
}
