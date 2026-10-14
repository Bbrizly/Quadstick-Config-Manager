using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using HidSharp;
using Device = QuadStick.Format.Device;
using QuadStick.Format;

namespace QuadStick.App;

// The QuadStick's own console, for the two things a drive cannot do: reach a
// QuadStick across the room over Bluetooth, and measure how hard somebody can
// sip and puff. DeviceConsole.cs is the protocol; this is only the window.
//
// HidSharp already ships with the app for the live reading, and it opens serial
// ports too, so a paired Bluetooth SPP port and a USB serial cable are both
// just a SerialDevice here.
public class DeviceLinkWindow : Window
{
    readonly MainWindow _owner;
    readonly ComboBox _ports = new() { MinWidth = 320 };
    readonly Button _connect, _send, _measure, _stop;
    readonly TextBlock _status;
    readonly TextBlock _prompt;

    SerialStream? _stream;
    DeviceConsole? _console;
    bool _busy, _stopAsked;

    /// <summary>Test seam: the console to use instead of opening a port.</summary>
    internal DeviceConsole? ConsoleForTest { set { _console = value; Refresh(); } }

    /// <summary>Test seam: what the window last said.</summary>
    internal string StatusText => _status.Text ?? "";

    /// <summary>Test seam: shorten the waits between breaths.</summary>
    internal TimeSpan BreathWindow { get; set; } = TimeSpan.FromSeconds(5);
    internal TimeSpan SettleTime { get; set; } = TimeSpan.FromMilliseconds(700);

    /// <summary>Test seam: where the live reading comes from.</summary>
    internal Func<LiveState?> Live { get; set; }

    /// <summary>Test seam: where the device's old files are kept.</summary>
    internal string BackupDir { get; set; } = Device.DefaultBackupDir();

    /// <summary>Test seam: the big line the person is reading right now.</summary>
    internal string PromptText => _prompt.IsVisible ? _prompt.Text ?? "" : "";

    /// <summary>Test seam: press Stop.</summary>
    internal void StopForTest() => _stopAsked = true;

    /// <summary>The last strength measured, puff then sip, for the test.</summary>
    internal (int Puff, int Sip)? LastStrength { get; private set; }

    public DeviceLinkWindow(MainWindow owner)
    {
        Classes.Add("dialog");
        _owner = owner;
        Live = () => owner.LiveReading;
        Title = Strings.Link_Title;
        Width = Math.Min(600 * owner.UiScale, 960);
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var explain = MainWindow.Explain(Strings.Link_Explain, Strings.Link_Title, Strings.Link_ExplainMore);

        AutomationProperties.SetName(_ports, Strings.Link_Port);
        var refresh = new Button { Content = Strings.Link_Refresh };
        refresh.Click += (_, _) => FillPorts();
        _connect = new Button { Content = Strings.Link_Connect, Classes = { "primary" } };
        _connect.Click += async (_, _) => await ConnectAsync();

        _send = new Button { Content = Strings.Link_SendProfile };
        _send.Click += async (_, _) => await SendAsync();
        _measure = new Button { Content = Strings.Link_Measure };
        _measure.Click += async (_, _) => await MeasureAsync();
        _stop = new Button { Content = Strings.Link_Stop, IsVisible = false };
        _stop.Click += (_, _) => _stopAsked = true;

        _prompt = new TextBlock
        {
            FontSize = Size("BodySize") * 1.4, FontWeight = Avalonia.Media.FontWeight.Bold,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false,
        };
        AutomationProperties.SetLiveSetting(_prompt, AutomationLiveSetting.Assertive);

        _status = new TextBlock
        {
            FontSize = Size("BodySize"), TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        var close = new Button { Content = Strings.Link_Close, MinWidth = 140, IsCancel = true };
        close.Click += (_, _) => Close();

        var panel = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 14,
            Children =
            {
                explain,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _ports, refresh, _connect } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _send, _measure, _stop } },
                _prompt,
                _status,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { close } },
            },
        };
        Content = MainWindow.DialogShell(this, MainWindow.ZoomWrap(panel, owner.UiScale));

        FillPorts();
        Refresh();
        Opened += (_, _) => (_ports.ItemCount > 0 ? _connect : refresh).Focus();
        Closed += (_, _) => _stream?.Dispose();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape && !_busy) { e.Handled = true; Close(); }
    }

    sealed record PortItem(SerialDevice Device)
    {
        public override string ToString()
        {
            string name;
            try { name = Device.GetFriendlyName(); } catch { name = ""; }
            return string.IsNullOrWhiteSpace(name) || name == Device.DevicePath
                ? Device.DevicePath : $"{name} ({Device.DevicePath})";
        }
    }

    void FillPorts()
    {
        IEnumerable<SerialDevice> found;
        try { found = DeviceList.Local.GetSerialDevices(); }
        catch { found = Array.Empty<SerialDevice>(); }
        // A Bluetooth QuadStick names itself, so it goes first when it is there.
        var items = found.Select(d => new PortItem(d))
            .OrderByDescending(p => p.ToString().Contains("quad", StringComparison.OrdinalIgnoreCase))
            .ToList();
        _ports.ItemsSource = items;
        _ports.SelectedIndex = items.Count > 0 ? 0 : -1;
        if (items.Count == 0 && _console is null) _status.Text = Strings.Link_NoPorts;
        Refresh();
    }

    void Refresh()
    {
        _connect.IsEnabled = !_busy && _ports.SelectedItem is not null;
        _send.IsEnabled = _measure.IsEnabled = !_busy && _console is not null;
        _stop.IsVisible = _busy;
    }

    async Task ConnectAsync()
    {
        if (_ports.SelectedItem is not PortItem port) return;
        _busy = true; Refresh();
        _status.Text = Strings.Link_Connecting;
        try
        {
            _stream?.Dispose();
            _console = null;
            var build = await Task.Run(() =>
            {
                var s = port.Device.Open();
                s.BaudRate = 115200;   // FORMAT.md, serial console
                s.ReadTimeout = 200;
                _stream = s;
                return new DeviceConsole(s).Build();
            });
            if (build is { } fw)
            {
                _console = new DeviceConsole(_stream!);
                _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.Link_Connected, fw);
            }
            else _status.Text = Strings.Link_NotAQuadStick;
        }
        catch (Exception ex)
        {
            _stream?.Dispose(); _stream = null;
            _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.Link_CouldNotOpen, port, ex.Message);
        }
        finally { _busy = false; Refresh(); }
    }

    // ---- sending a profile ----

    async Task SendAsync()
    {
        if (_console is not { } console) return;
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Link_SendProfile, AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } },
        });
        if (picked.Count == 0) return;

        ProfileFile file;
        try { file = ProfileFile.Load(await File.ReadAllTextAsync(picked[0].Path.LocalPath)); }
        catch (Exception ex) { _status.Text = ex.Message; return; }
        await SendFileAsync(file);
    }

    /// <summary>Send an already loaded profile. Split out so a test can drive
    /// it without a file picker.</summary>
    internal async Task SendFileAsync(ProfileFile file)
    {
        if (_console is not { } console) return;
        file.Reparse();
        // The same two questions a drive install asks, asked the same way.
        bool yesDefault = false, yesPrefs = false;
        if (file.Document.IsDefaultConfig &&
            !(yesDefault = await _owner.ConfirmAsync(Strings.Install_OverwriteDefaultCsv, Strings.Install_AWrongDefaultCsvCan))) return;
        if (file.Document.IsDevicePreferences &&
            !(yesPrefs = await _owner.ConfirmAsync(Strings.Link_OverwritePrefs, Strings.Link_OverwritePrefsMore))) return;

        _busy = true; Refresh();
        _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.Link_Sending, file.Document.CsvFileName);
        try
        {
            var result = await Task.Run(() => console.SendProfile(file, BackupDir, yesDefault, yesPrefs));
            _status.Text = result.BackupPath is { } backup
                ? string.Format(CultureInfo.CurrentCulture, Strings.Link_Sent, result.Name, backup)
                : string.Format(CultureInfo.CurrentCulture, Strings.Link_SentNew, result.Name);
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _busy = false; Refresh(); }
    }

    // ---- measuring strength ----

    async Task MeasureAsync()
    {
        if (!await _owner.ConfirmAsync(Strings.Link_MeasureConfirmTitle, Strings.Link_MeasureConfirm)) return;
        await MeasureNowAsync();
    }

    /// <summary>The test itself, after the yes. Split out for the same reason.</summary>
    internal async Task MeasureNowAsync()
    {
        if (_console is not { } console) return;
        // The breaths are seen as button presses on the USB gamepad. Without a
        // reading there is nothing to see them with, and pretending otherwise
        // would score every try as a miss.
        if (Live() is null) { _status.Text = Strings.Link_NeedsUsb; return; }

        _busy = true; _stopAsked = false; Refresh();
        int puff = StrengthSweep.Lowest - 1, sip = StrengthSweep.Lowest - 1, threshold = 0;
        bool finished = false;
        try
        {
            var prefs = await Task.Run(() =>
            {
                var before = console.Preferences();
                console.SendProfile(ProfileFile.Load(DeviceConsole.StrengthTestProfile), BackupDir);
                console.SetPreference(DeviceConsole.MaximumPreference(puff: true), 100);
                console.SetPreference(DeviceConsole.MaximumPreference(puff: false), 100);
                return before;
            });
            threshold = prefs.GetValueOrDefault(DeviceConsole.ThresholdPreference(puff: true)) is > 0 and var own
                ? own : prefs.GetValueOrDefault("sip_puff_threshold");

            Say(Strings.Link_HandsOff);
            await Task.Delay(SettleTime * 2);
            await Task.Run(console.Calibrate);

            _try = 0;
            puff = await StrengthSweep.FindAsync(t => TryAsync(console, puff: true, t));
            _try = 0;
            sip = await StrengthSweep.FindAsync(t => TryAsync(console, puff: false, t));
            finished = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally
        {
            Say(null);
            // Restart, always: it is what puts the person's own profile and
            // settings back, and the test changed both. The test file goes
            // first, while the console is still there to take the command.
            try { await Task.Run(() => { console.Delete(DeviceConsole.StrengthTestName); console.Boot(); }); }
            catch { /* unplugging does the same, and the confirm said so */ }
            _console = null;   // the restart drops the port
            _busy = false; Refresh();
        }

        if (finished)
        {
            LastStrength = (puff, sip);
            Telemetry.StrengthMeasured(puff, sip);
            _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.Link_Result,
                Percent(puff), Percent(sip), Percent(threshold));
        }
        else if (_stopAsked) _status.Text = Strings.Link_Cancelled;
    }

    int _try;

    async Task<bool> TryAsync(DeviceConsole console, bool puff, int threshold)
    {
        if (_stopAsked) throw new OperationCanceledException();
        await Task.Run(() => console.SetPreference(DeviceConsole.ThresholdPreference(puff), threshold));

        // Nothing held going in, or a stuck button would score as a breath.
        await WaitAsync(s => s.Buttons.Count == 0, BreathWindow);

        _try++;
        Say(string.Format(CultureInfo.CurrentCulture, puff ? Strings.Link_PuffNow : Strings.Link_SipNow, _try));
        bool fired = await WaitAsync(s => s.Buttons.Count > 0, BreathWindow);
        Say(null);
        await WaitAsync(s => s.Buttons.Count == 0, BreathWindow);
        await Task.Delay(SettleTime);
        return fired;
    }

    async Task<bool> WaitAsync(Func<LiveState, bool> until, TimeSpan within)
    {
        var end = DateTime.UtcNow + within;
        while (DateTime.UtcNow < end)
        {
            if (_stopAsked) throw new OperationCanceledException();
            if (Live() is { } s && until(s)) return true;
            await Task.Delay(30);
        }
        return false;
    }

    // Below the bottom of the sweep is a real answer, not a zero.
    static string Percent(int v) => v < StrengthSweep.Lowest
        ? string.Format(CultureInfo.CurrentCulture, Strings.Link_UnderLowest, StrengthSweep.Lowest)
        : string.Format(CultureInfo.CurrentCulture, Strings.Link_PercentValue, v);

    void Say(string? text)
    {
        _prompt.Text = text ?? "";
        _prompt.IsVisible = text is not null;
    }

    static double Size(string tokenKey) => (double)Application.Current!.FindResource(tokenKey)!;
}
