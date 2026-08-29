using Avalonia.Threading;
using HidSharp;
using HidSharp.Reports;
using HidSharp.Reports.Input;

namespace QuadStick.App;

/// <summary>Where the stick is right now, as the QuadStick is reporting it.</summary>
/// <param name="X">Left to right, -1 to 1.</param>
/// <param name="Y">Up to down, -1 to 1. Down is positive, which is how the
/// device sends it and how the screen draws it.</param>
/// <param name="Buttons">Which buttons are down, numbered from 1 the way the
/// device's own report numbers them.</param>
/// <param name="Product">What the QuadStick is announcing itself as.</param>
public sealed record LiveState(double X, double Y, IReadOnlyList<int> Buttons, string Product);

// Reading the stick while somebody tunes it.
//
// The QuadStick is already a gamepad on the USB cable, sending its position
// several hundred times a second because that is its job. Nothing here asks it
// for anything, turns its console on, or writes to it: this opens the same
// report stream a game reads and looks at it.
//
// Which button is a hard sip is a question this cannot answer. That mapping
// lives in the profile the device has loaded, and the report only carries the
// button that came out the other end. So the stick, which is the stick in
// every profile, is read as the stick, and buttons are reported as numbers.
//
// ponytail: axes and buttons only. Naming the part behind a button means
// resolving the loaded profile's bindings for the mode the device is in, and
// the device does not say which mode that is.
public sealed class LiveInput : IDisposable
{
    // Every USB identity the QuadStick answers to. The emulation mode setting
    // picks between them, so the one to look for is not knowable in advance.
    static readonly (int Vendor, int Product)[] Known =
    {
        (0x16D0, 0x092B), // QuadStick native, and PS3
        (0x16D0, 0x092C), // DualShock 3
        (0x045E, 0x028E), // Xbox 360 and x360ce
        (0x054C, 0x05C5), // DualShock 4, wired
        (0x054C, 0x05C4), // DualShock 4, wireless
        (0x057E, 0x2009), // Nintendo Switch Pro Controller
    };

    readonly Action<LiveState?> _report;
    readonly CancellationTokenSource _stop = new();

    /// <summary>Starts reading in the background and calls back on the UI
    /// thread whenever the reading changes. A callback with null means nothing
    /// is being read: no stick found, or the one found would not open.</summary>
    public LiveInput(Action<LiveState?> report)
    {
        _report = report;
        // Long-running: this thread spends its life blocked on a USB read, so
        // it must not hold a thread-pool slot the rest of the app wants.
        Task.Factory.StartNew(Run, _stop.Token,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    void Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            // Anything at all going wrong here, from a permission the OS did
            // not grant to a device unplugged mid-read, means the same thing
            // to the person tuning: no live reading. It is never an error the
            // app stops for, because every setting on the page still works.
            try
            {
                var device = Find();
                if (device is null) { Post(null); Wait(1500); continue; }
                Follow(device);
                // Follow returns when the device stops reporting, which is
                // usually somebody unplugging it. Without this the retry is a
                // spin on a device that will not open.
                Post(null);
                Wait(1000);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Post(null);
                Wait(2000);
            }
        }
    }

    static HidDevice? Find()
    {
        foreach (var (vendor, product) in Known)
        {
            var found = DeviceList.Local.GetHidDeviceOrNull(vendor, product);
            if (found is not null) return found;
        }
        return null;
    }

    void Follow(HidDevice device)
    {
        var descriptor = device.GetReportDescriptor();
        var item = descriptor.DeviceItems.FirstOrDefault();
        if (item is null) { Post(null); Wait(2000); return; }

        // Reading by usage rather than by byte offset: the report shape is a
        // different one in every emulation mode, and the descriptor the device
        // itself publishes is the only thing that knows which.
        using var stream = device.Open();
        var receiver = descriptor.CreateHidDeviceInputReceiver();
        var parser = item.CreateDeviceItemInputParser();
        var buffer = new byte[descriptor.MaxInputReportLength];
        string product = Name(device);
        LiveState? last = null;
        receiver.Start(stream);

        while (!_stop.IsCancellationRequested && receiver.IsRunning)
        {
            // A second with no report is normal: the device sends when
            // something moves. Waiting rather than spinning is what keeps this
            // thread off the CPU while somebody reads the page.
            if (!receiver.WaitHandle.WaitOne(1000)) continue;

            while (receiver.TryRead(buffer, 0, out var report))
            {
                if (!parser.TryParseReport(buffer, 0, report)) continue;

                double x = 0, y = 0;
                var down = new List<int>();
                for (int i = 0; i < parser.ValueCount; i++)
                {
                    var value = parser.GetValue(i);
                    uint usage = value.Usages.FirstOrDefault();
                    switch (usage)
                    {
                        // GetFractionalValue is 0 to 1 across the axis's own
                        // range, so an eight bit axis and a sixteen bit one
                        // both land on the same scale with centre at zero.
                        case 0x00010030: x = value.GetFractionalValue() * 2 - 1; break; // X
                        case 0x00010031: y = value.GetFractionalValue() * 2 - 1; break; // Y
                        default:
                            // Button page. A button reads 1 while it is held.
                            if ((usage & 0xFFFF0000) == 0x00090000 && value.GetLogicalValue() != 0)
                                down.Add((int)(usage & 0xFFFF));
                            break;
                    }
                }

                var now = new LiveState(Math.Clamp(x, -1, 1), Math.Clamp(y, -1, 1), down, product);
                if (Same(now, last)) continue;
                last = now;
                Post(now);
            }
        }
    }

    // A stick at rest still jitters a count or two, and redrawing on every one
    // of those is a page that never stops moving. One percent of travel is
    // under a pixel on the pad.
    static bool Same(LiveState a, LiveState? b) =>
        b is not null
        && Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01
        && a.Buttons.Count == b.Buttons.Count
        && !a.Buttons.Except(b.Buttons).Any();

    static string Name(HidDevice device)
    {
        try { return device.GetProductName(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return device.DevicePath;
        }
    }

    void Post(LiveState? state)
    {
        if (_stop.IsCancellationRequested) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_stop.IsCancellationRequested) _report(state);
        });
    }

    void Wait(int milliseconds)
    {
        try { _stop.Token.WaitHandle.WaitOne(milliseconds); }
        catch (ObjectDisposedException) { }
    }
}
