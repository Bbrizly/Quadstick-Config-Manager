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
/// <param name="Outputs">The profile words for everything the device is
/// sending this instant, aliases included, so a row can be matched by the word
/// it actually holds. Empty when nothing is being sent AND when the report
/// could not be read, which is what <paramref name="OutputsUnderstood"/> tells
/// apart.</param>
/// <param name="OutputsUnderstood">Whether this emulation mode's report is one
/// the app has been taught to read. False means the stick is being read but its
/// outputs cannot be named, which the screen has to say rather than show as
/// nothing happening.</param>
public sealed record LiveState(double X, double Y, IReadOnlyList<int> Buttons, string Product,
    IReadOnlySet<string> Outputs, bool OutputsUnderstood);

// Reading the stick while somebody tunes it.
//
// The QuadStick is already a gamepad on the USB cable, sending its position
// several hundred times a second because that is its job. Nothing here asks it
// for anything, turns its console on, or writes to it: this opens the same
// report stream a game reads and looks at it.
//
// Which part of the mouthpiece a report came from is a question this cannot
// answer. That mapping lives in the profile the device has loaded, and the
// report only carries the OUTPUT that came out the other end. So the report is
// turned back into output words, which is a fact about the device, and never
// into an input, which would be a guess about a file the app cannot see.
//
// ponytail: emulation mode 0 only. Every other mode publishes a report in a
// different shape, and a table written from a descriptor nobody has read would
// be a confident wrong answer. Add a mode by reading its descriptor.
public sealed class LiveInput : IDisposable
{
    // Every USB identity the QuadStick answers to. The emulation mode setting
    // picks between them, so the one to look for is not knowable in advance.
    // Each line is a case of CALLBACK_USB_GetDescriptor in
    // Joystick/Descriptors.c, plus CALLBACK_USB_GetDescriptor_DS4 in
    // Joystick/DescriptorsDS4.c, on firmware 2373.
    //
    // Emulation mode 3, Xbox 360 native, is deliberately absent. That mode
    // publishes interface class 0xFF, which is XInput and not HID, so nothing
    // here could open it however it were listed.
    internal static readonly (int Vendor, int Product)[] Known =
    {
        (0x16D0, 0x092B), // mode 0, QuadStick: the Afterglow PS3 descriptor
        (0x054C, 0x0268), // mode 1, Dual Shock 3
        (0x16D0, 0x092C), // mode 2, X360CE
        (0x16D0, 0x092D), // modes 4 and 7 once the device has decided it is on a PC
        (0x054C, 0x05C5), // mode 4 with a PS4 answering: wired DualShock 4
        (0x0F0D, 0x0066), // mode 4 before the console answers: the HORI pad
        (0x057E, 0x2009), // mode 5, Nintendo Switch Pro Controller
        (0x16D0, 0x092E), // mode 6, PS4 without the flash drive
        (0x054C, 0x05C4), // mode 7, wireless DualShock 4 V1
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
                var found = Find();
                if (found is null) { Post(null); Wait(1500); continue; }
                Follow(found.Value);
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

    // What was found: the interface, the descriptor it published, the
    // collection inside it that is the stick, and whether the USB identity is
    // the one emulation mode 0 answers to. The identity is half the question:
    // the descriptor has to agree before any report is read as mode 0.
    readonly record struct Stick(HidDevice Device, ReportDescriptor Descriptor, DeviceItem Item,
        bool Ps3Identity);

    // Emulation mode 0, the only report shape this has been taught to read.
    static readonly (int Vendor, int Product) Ps3 = (0x16D0, 0x092B);

    // A QuadStick in most modes puts three HID interfaces behind one USB
    // identity, because a profile can drive a gamepad, a mouse and a keyboard
    // at once. They enumerate in no promised order, so asking for the first
    // one at a matching vendor and product would some of the time hand back
    // the mouse, and the mouse reports X and Y as well: the page would then
    // draw the pointer's motion as the stick. Every interface is looked at and
    // the one that says it is a stick is the one read.
    static Stick? Find()
    {
        foreach (var (vendor, product) in Known)
        {
            foreach (var device in DeviceList.Local.GetHidDevices(vendor, product))
            {
                ReportDescriptor descriptor;
                try { descriptor = device.GetReportDescriptor(); }
                catch (Exception ex) when (ex is not OperationCanceledException) { continue; }

                var item = StickItem(descriptor);
                if (item is not null)
                    return new Stick(device, descriptor, item, (vendor, product) == Ps3);
            }
        }
        return null;
    }

    /// <summary>The collection in this interface's descriptor that is the
    /// stick, or null if the interface is the mouse or the keyboard.</summary>
    internal static DeviceItem? StickItem(ReportDescriptor descriptor) =>
        descriptor.DeviceItems.FirstOrDefault(item => item.Usages.GetAllValues().Any(IsStick));

    /// <summary>Whether a top-level usage names a stick. Which of the three the
    /// device says depends on the emulation mode: mode 0 publishes Game Pad,
    /// modes 1 and 5 publish Joystick.</summary>
    internal static bool IsStick(uint usage) =>
        usage is (uint)Usage.GenericDesktopJoystick
              or (uint)Usage.GenericDesktopGamepad
              or (uint)Usage.GenericDesktopMultiaxisController;

    void Follow(Stick stick)
    {
        // Reading by usage rather than by byte offset: the report shape is a
        // different one in every emulation mode, and the descriptor the device
        // itself publishes is the only thing that knows which.
        using var stream = stick.Device.Open();
        var receiver = stick.Descriptor.CreateHidDeviceInputReceiver();
        var parser = stick.Item.CreateDeviceItemInputParser();
        var buffer = new byte[stick.Descriptor.MaxInputReportLength];
        string product = Name(stick.Device);
        LiveState? last = null;
        // Worked out from the first report rather than up front: the parser is
        // built from the descriptor, and reading it once it has parsed
        // something is the one point where it is certainly populated.
        bool? ps3Layout = null;
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

                // The USB identity says which mode the device was put in, but a
                // firmware that moved a button would still answer to it. What
                // the descriptor declares has to agree before a report is read
                // as mode 0.
                ps3Layout ??= stick.Ps3Identity && DeclaresPs3Report(parser);
                var now = Read(parser, product, ps3Layout.Value, last);
                if (Same(now, last)) continue;
                last = now;
                Post(now);
            }
        }
    }

    /// <summary>Turn one parsed report into a reading. Split out so a test can
    /// hand it a report the firmware would send. <paramref name="previous"/> is
    /// the reading before this one, which the axis thresholds need and nothing
    /// else looks at.</summary>
    internal static LiveState Read(DeviceItemInputParser parser, string product,
        bool ps3Layout, LiveState? previous)
    {
        double x = 0, y = 0, z = 0, rz = 0;
        int hat = HatIdle;
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
                case (uint)Usage.GenericDesktopX: x = value.GetFractionalValue() * 2 - 1; break;
                case (uint)Usage.GenericDesktopY: y = value.GetFractionalValue() * 2 - 1; break;
                // Z and Rz are the right stick: USAGE_X, USAGE_Y, USAGE_Z,
                // USAGE_Rz are declared in that order over left_X, left_Y,
                // right_X, right_Y in USB_PS3_Report_Data_t.
                case (uint)Usage.GenericDesktopZ: z = value.GetFractionalValue() * 2 - 1; break;
                case (uint)Usage.GenericDesktopRz: rz = value.GetFractionalValue() * 2 - 1; break;
                // Read raw, not fractionally: the hat's resting value is 15,
                // which the descriptor declares as a null state past its
                // logical maximum of 7, and a fraction of a range it is
                // outside of means nothing.
                case (uint)Usage.GenericDesktopHatSwitch: hat = value.GetLogicalValue(); break;
                default:
                    // Button page. A button reads 1 while it is held.
                    if ((usage & 0xFFFF0000) == 0x00090000 && value.GetLogicalValue() != 0)
                        down.Add((int)(usage & 0xFFFF));
                    break;
            }
        }

        x = Math.Clamp(x, -1, 1); y = Math.Clamp(y, -1, 1);
        z = Math.Clamp(z, -1, 1); rz = Math.Clamp(rz, -1, 1);
        return new LiveState(x, y, down, product,
            ps3Layout ? Ps3Outputs(down, hat, x, y, z, rz, previous) : NoOutputs, ps3Layout);
    }

    /// <summary>Whether this interface declares the mode 0 report: buttons 1 to
    /// 13, a hat switch, and X, Y, Z and Rz, which is HID_DESCRIPTOR_PS3_JOYSTICK
    /// and no other descriptor in the firmware. A firmware that moved or added
    /// a button fails this and is read as a mode nobody has taught the app,
    /// which is the safe answer. The vendor page tail is not checked, because
    /// nothing here reads it.</summary>
    internal static bool DeclaresPs3Report(DeviceItemInputParser parser)
    {
        var buttons = new HashSet<int>();
        var axes = new HashSet<uint>();
        bool hat = false;
        for (int i = 0; i < parser.ValueCount; i++)
        {
            uint usage = parser.GetValue(i).Usages.FirstOrDefault();
            if (usage == (uint)Usage.GenericDesktopHatSwitch) hat = true;
            else if (Axes.Contains(usage)) axes.Add(usage);
            else if ((usage & 0xFFFF0000) == 0x00090000) buttons.Add((int)(usage & 0xFFFF));
        }
        return hat && axes.Count == Axes.Length
            && buttons.Count == 13 && buttons.Max() == 13;
    }

    // The two sticks, in the order the descriptor declares them over left_X,
    // left_Y, right_X, right_Y.
    static readonly uint[] Axes =
    {
        (uint)Usage.GenericDesktopX, (uint)Usage.GenericDesktopY,
        (uint)Usage.GenericDesktopZ, (uint)Usage.GenericDesktopRz,
    };

    static readonly IReadOnlySet<string> NoOutputs =
        new HashSet<string>(StringComparer.Ordinal);

    // The hat's resting value. Declared as a null state, so it sits past the
    // logical maximum of 7 rather than inside the range. ps3.h calls it
    // USBJOYSTICK_HAT_POS_IDLE.
    const int HatIdle = 15;

    // A mouth-held stick wanders, so a single threshold would flicker a row on
    // and off while somebody holds a deflection near it. It takes more push to
    // light a row than to keep it lit.
    const double AxisOn = 0.30, AxisOff = 0.20;

    /// <summary>Every profile word for what the device is sending right now.
    /// Aliases included, so a row saying "left_bumper" lights beside one saying
    /// "left_1" and no caller has to normalise anything.</summary>
    static IReadOnlySet<string> Ps3Outputs(List<int> down, int hat,
        double x, double y, double z, double rz, LiveState? previous)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var button in down)
            if (Ps3Buttons.TryGetValue(button, out var words)) set.UnionWith(words);
        // Anything outside 0 to 7 is the hat saying it is centred, and must
        // never be read as a direction.
        if (hat is >= 0 and <= 7) set.Add(Ps3Hat[hat]);
        // Same order as Ps3Joysticks: left away from centre, then towards, on
        // X then Y, then the same for the right stick on Z and Rz.
        Axis(set, previous, -x, Ps3Joysticks[0]);
        Axis(set, previous, x, Ps3Joysticks[1]);
        Axis(set, previous, -y, Ps3Joysticks[2]);
        Axis(set, previous, y, Ps3Joysticks[3]);
        Axis(set, previous, -z, Ps3Joysticks[4]);
        Axis(set, previous, z, Ps3Joysticks[5]);
        Axis(set, previous, -rz, Ps3Joysticks[6]);
        Axis(set, previous, rz, Ps3Joysticks[7]);
        return set;
    }

    static void Axis(HashSet<string> set, LiveState? previous, double push, string token)
    {
        bool lit = previous?.Outputs.Contains(token) == true;
        if (push >= (lit ? AxisOff : AxisOn)) set.Add(token);
    }

    // Which button number in the mode 0 report each profile word comes out on.
    //
    // The Xbox spellings are on here on purpose. output_keywords.h is one table
    // that the firmware searches whatever emulation mode it is in, so a row
    // written "left_bumper" produces ps3.L1 on a device in mode 0 exactly as
    // "left_1" does. The outputs_ps3 and outputs_xbox lists in validation.json
    // are the validator's suggestions per mode, not what the device accepts.
    // The numbers are the usages HID_DESCRIPTOR_PS3_JOYSTICK declares, 1 to 13,
    // over the bits of USB_PS3_Report_Data_t in that order: square, X, O,
    // triangle, L1, R1, L2, R2, select, start, L3, R3, PS3. The words on each
    // line are every entry in output_keywords.h that maps to that slot,
    // aliases and XAC aliases included. Firmware 2373.
    static readonly Dictionary<int, string[]> Ps3Buttons = new()
    {
        [1]  = new[] { "square",   "X",              "xac_left_up",   "xac_right_view" },
        [2]  = new[] { "x",        "A",              "xac_left_down", "xac_right_menu" },
        [3]  = new[] { "circle",   "B",              "xac_left_LS",   "xac_right_RS" },
        [4]  = new[] { "triangle", "Y",              "xac_left_LB",   "xac_right_RB" },
        [5]  = new[] { "left_1",   "left_bumper",    "xac_left_A",    "xac_right_X" },
        [6]  = new[] { "right_1",  "right_bumper",   "xac_left_B",    "xac_right_Y" },
        [7]  = new[] { "left_2",   "left_trigger",   "xac_left_view", "xac_right_up" },
        [8]  = new[] { "right_2",  "right_trigger",  "xac_left_menu", "xac_right_down" },
        [9]  = new[] { "select",   "back" },
        [10] = new[] { "start" },
        [11] = new[] { "left_3",   "left_stick" },
        [12] = new[] { "right_3",  "right_stick" },
        [13] = new[] { "ps3",      "guide" },
    };

    // The hat's eight directions, in the order ps3.h numbers them: N is 0 and
    // it turns clockwise. Only one can be sent at a time, because the firmware
    // sets one direction per output rather than combining two.
    static readonly string[] Ps3Hat =
        { "dpad_N", "dpad_NE", "dpad_E", "dpad_SE", "dpad_S", "dpad_SW", "dpad_W", "dpad_NW" };

    // The eight words the stick axes can send. Kept beside the table they
    // belong to so a test can hold the whole vocabulary against Vocab.
    static readonly string[] Ps3Joysticks =
    {
        "left_joy_left", "left_joy_right", "left_joy_up", "left_joy_down",
        "right_joy_left", "right_joy_right", "right_joy_up", "right_joy_down",
    };

    /// <summary>Every profile word this can ever light, for the tests that hold
    /// the table against the real output vocabulary and against itself.</summary>
    internal static IEnumerable<string> Ps3OutputWords =>
        Ps3Buttons.Values.SelectMany(w => w).Concat(Ps3Hat).Concat(Ps3Joysticks);

    // A stick at rest still jitters a count or two, and redrawing on every one
    // of those is a page that never stops moving. One percent of travel is
    // under a pixel on the pad. The outputs are compared as well, because the
    // hat and the right stick move without touching X or Y.
    static bool Same(LiveState a, LiveState? b) =>
        b is not null
        && Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01
        && a.Buttons.Count == b.Buttons.Count
        && !a.Buttons.Except(b.Buttons).Any()
        && a.OutputsUnderstood == b.OutputsUnderstood
        && a.Outputs.SetEquals(b.Outputs);

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
