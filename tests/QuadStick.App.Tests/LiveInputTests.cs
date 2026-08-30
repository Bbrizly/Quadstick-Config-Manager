using System.Linq;
using HidSharp.Reports;
using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

// Reading the stick off the USB cable.
//
// A QuadStick in its ordinary mode is four USB interfaces at one identity: the
// gamepad, a mouse, a keyboard and the flash drive, because one profile can
// drive all of them. Nothing promises which order the OS lists them in, so
// picking the interface has to be a question about what the interface says it
// is, and this is where that is proved.
//
// The descriptors below are transcribed item for item from the firmware the
// app models (FW 2373). The gamepad one is HID_DESCRIPTOR_PS3_JOYSTICK in
// Joystick/Descriptors.h; the mouse and keyboard are HID_DESCRIPTOR_MOUSE
// (-127, 127, -127, 127, 5, false) and HID_DESCRIPTOR_KEYBOARD(6) from
// nxpUSBlib/Drivers/USB/Class/Common/HIDClassCommon.h, which is what
// Joystick/Descriptors.c publishes on the other two interfaces.
public class LiveInputTests
{
    // Emulation mode 0. Usage Page Generic Desktop, Usage Game Pad.
    static readonly byte[] GamepadDescriptor =
    {
        0x05, 0x01, 0x09, 0x05, 0xA1, 0x01,
        0x15, 0x00, 0x25, 0x01, 0x35, 0x00, 0x45, 0x01, 0x75, 0x01, 0x95, 0x0D,
        0x05, 0x09, 0x19, 0x01, 0x29, 0x0D, 0x81, 0x02,
        0x95, 0x03, 0x81, 0x01,
        0x05, 0x01, 0x25, 0x07, 0x46, 0x3B, 0x01, 0x75, 0x04, 0x95, 0x01,
        0x65, 0x14, 0x09, 0x39, 0x81, 0x42,
        0x65, 0x00, 0x95, 0x01, 0x81, 0x01,
        0x26, 0xFF, 0x00, 0x46, 0xFF, 0x00,
        0x09, 0x30, 0x09, 0x31, 0x09, 0x32, 0x09, 0x35,
        0x75, 0x08, 0x95, 0x04, 0x81, 0x02,
        0x06, 0x00, 0xFF,
        0x09, 0x20, 0x09, 0x21, 0x09, 0x22, 0x09, 0x23, 0x09, 0x24, 0x09, 0x25,
        0x09, 0x26, 0x09, 0x27, 0x09, 0x28, 0x09, 0x29, 0x09, 0x2A, 0x09, 0x2B,
        0x95, 0x0C, 0x81, 0x02,
        0x0A, 0x21, 0x26, 0x95, 0x08, 0xB1, 0x02,
        0x0A, 0x21, 0x26, 0x91, 0x02,
        0x26, 0xFF, 0x03, 0x46, 0xFF, 0x03,
        0x09, 0x2C, 0x09, 0x2D, 0x09, 0x2E, 0x09, 0x2F,
        0x75, 0x10, 0x95, 0x04, 0x81, 0x02,
        0xC0,
    };

    // The second interface. Usage Mouse, and it carries X and Y of its own,
    // which is the whole reason picking the wrong one is not a harmless miss.
    static readonly byte[] MouseDescriptor =
    {
        0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x09, 0x01, 0xA1, 0x00,
        0x05, 0x09, 0x19, 0x01, 0x29, 0x05, 0x15, 0x00, 0x25, 0x01,
        0x95, 0x05, 0x75, 0x01, 0x81, 0x02,
        0x95, 0x01, 0x75, 0x03, 0x81, 0x01,
        0x05, 0x01, 0x09, 0x30, 0x09, 0x31,
        0x16, 0x81, 0xFF, 0x26, 0x7F, 0x00, 0x36, 0x81, 0xFF, 0x46, 0x7F, 0x00,
        0x95, 0x02, 0x75, 0x08, 0x81, 0x06,
        0x09, 0x38, 0x16, 0x81, 0xFF, 0x26, 0x7F, 0x00,
        0x95, 0x01, 0x75, 0x08, 0x81, 0x06,
        0x05, 0x0C, 0x0A, 0x38, 0x02, 0x95, 0x01, 0x81, 0x06,
        0xC0, 0xC0,
    };

    // The third interface. Usage Keyboard.
    static readonly byte[] KeyboardDescriptor =
    {
        0x05, 0x01, 0x09, 0x06, 0xA1, 0x01,
        0x05, 0x07, 0x19, 0xE0, 0x29, 0xE7, 0x15, 0x00, 0x25, 0x01,
        0x75, 0x01, 0x95, 0x08, 0x81, 0x02,
        0x95, 0x01, 0x75, 0x08, 0x81, 0x01,
        0x05, 0x08, 0x19, 0x01, 0x29, 0x05, 0x95, 0x05, 0x75, 0x01, 0x91, 0x02,
        0x95, 0x01, 0x75, 0x03, 0x91, 0x01,
        0x15, 0x00, 0x25, 0x65, 0x05, 0x07, 0x19, 0x00, 0x29, 0x65,
        0x95, 0x06, 0x75, 0x08, 0x81, 0x00,
        0xC0,
    };

    // Emulation mode 1 and mode 5 say Joystick rather than Game Pad, so the
    // opening three items of the Dual Shock 3 descriptor stand for both.
    static readonly byte[] JoystickOpening =
    {
        0x05, 0x01, 0x09, 0x04, 0xA1, 0x01,
        0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x08,
        0x05, 0x09, 0x19, 0x01, 0x29, 0x08, 0x81, 0x02,
        0xC0,
    };

    // The bug this replaced: the first interface at a matching vendor and
    // product was opened, whichever it was. Half the time on a real stick that
    // is the mouse, whose X and Y are the pointer's, and the page would then
    // draw somebody's mouse as their joystick.
    [Fact]
    public void OnlyTheGamepadInterfaceIsReadAsTheStick()
    {
        Assert.NotNull(LiveInput.StickItem(new ReportDescriptor(GamepadDescriptor)));
        Assert.NotNull(LiveInput.StickItem(new ReportDescriptor(JoystickOpening)));
        Assert.Null(LiveInput.StickItem(new ReportDescriptor(MouseDescriptor)));
        Assert.Null(LiveInput.StickItem(new ReportDescriptor(KeyboardDescriptor)));
    }

    // A report the firmware would send, read the way the page reads it. The
    // left stick is bytes 3 and 4 of USB_PS3_Report_Data_t, and the first
    // button bit is square. HidSharp keeps byte 0 of the buffer for the report
    // id, which this descriptor does not use, so the data starts one along.
    [Fact]
    public void AGamepadReportSaysWhereTheStickIsAndWhatIsHeld()
    {
        var descriptor = new ReportDescriptor(GamepadDescriptor);
        var item = LiveInput.StickItem(descriptor);
        Assert.NotNull(item);

        var parser = item.CreateDeviceItemInputParser();
        var buffer = new byte[descriptor.MaxInputReportLength];
        buffer[1] = 0x01;  // square held
        buffer[4] = 0xC0;  // left X, three quarters right
        buffer[5] = 0x40;  // left Y, a quarter down, which reads as up

        Assert.True(parser.TryParseReport(buffer, 0, descriptor.InputReports.First()));
        var live = LiveInput.Read(parser, "QuadStick", ps3Layout: true, null);

        Assert.Equal(0.51, live.X, 2);
        Assert.Equal(-0.50, live.Y, 2);
        Assert.Equal(new[] { 1 }, live.Buttons);
    }

    // A stick at rest is 0x80 on both axes, which is the middle of an eight bit
    // range and has to read as centred rather than as a hair off.
    [Fact]
    public void AStickAtRestReadsAsCentred()
    {
        var descriptor = new ReportDescriptor(GamepadDescriptor);
        var parser = LiveInput.StickItem(descriptor)!.CreateDeviceItemInputParser();
        var buffer = new byte[descriptor.MaxInputReportLength];
        buffer[4] = 0x80;
        buffer[5] = 0x80;

        Assert.True(parser.TryParseReport(buffer, 0, descriptor.InputReports.First()));
        var live = LiveInput.Read(parser, "QuadStick", ps3Layout: true, null);

        Assert.True(System.Math.Abs(live.X) < 0.01, $"X was {live.X}");
        Assert.True(System.Math.Abs(live.Y) < 0.01, $"Y was {live.Y}");
        Assert.Empty(live.Buttons);
    }

    // What the device is sending, in the words a profile is written in.
    //
    // The report only ever carries the OUTPUT. Which part of the mouthpiece
    // produced it lives in the file the device has loaded, which the app cannot
    // see, so nothing here turns a report back into an input.

    const int HatIdle = 15;    // USBJOYSTICK_HAT_POS_IDLE in ps3.h
    const byte Centre = 0x80;  // where DataFlow.c parks every axis before it adds

    // One report in the shape USB_PS3_Report_Data_t packs: thirteen button bits
    // low to high, three pad bits, the hat in the next nibble, a pad nibble,
    // then left_X, left_Y, right_X, right_Y. Byte 0 is the report id HidSharp
    // reserves and this descriptor does not use.
    static byte[] Report(int[] buttons, int hat = HatIdle, byte leftX = Centre,
        byte leftY = Centre, byte rightX = Centre, byte rightY = Centre)
    {
        var descriptor = new ReportDescriptor(GamepadDescriptor);
        var buffer = new byte[descriptor.MaxInputReportLength];
        ushort bits = 0;
        foreach (var b in buttons) bits |= (ushort)(1 << (b - 1));
        buffer[1] = (byte)(bits & 0xFF);
        buffer[2] = (byte)(bits >> 8);
        buffer[3] = (byte)(hat & 0x0F);
        buffer[4] = leftX; buffer[5] = leftY; buffer[6] = rightX; buffer[7] = rightY;
        return buffer;
    }

    static LiveState Sending(byte[] report, LiveState? previous = null)
    {
        var descriptor = new ReportDescriptor(GamepadDescriptor);
        var parser = LiveInput.StickItem(descriptor)!.CreateDeviceItemInputParser();
        Assert.True(parser.TryParseReport(report, 0, descriptor.InputReports.First()));
        Assert.True(LiveInput.DeclaresPs3Report(parser));
        return LiveInput.Read(parser, "QuadStick", ps3Layout: true, previous);
    }

    static readonly int[] Nothing = System.Array.Empty<int>();

    [Fact]
    public void NothingHeldIsNothingBeingSent()
    {
        Assert.Empty(Sending(Report(Nothing)).Outputs);
    }

    [Theory]
    [InlineData(1, "square")]
    [InlineData(2, "x")]
    [InlineData(3, "circle")]
    [InlineData(4, "triangle")]
    [InlineData(5, "left_1")]
    [InlineData(6, "right_1")]
    [InlineData(7, "left_2")]
    [InlineData(8, "right_2")]
    [InlineData(9, "select")]
    [InlineData(10, "start")]
    [InlineData(11, "left_3")]
    [InlineData(12, "right_3")]
    [InlineData(13, "ps3")]
    public void EachButtonComesBackAsTheWordTheProfileUses(int button, string output)
    {
        Assert.Contains(output, Sending(Report(new[] { button })).Outputs);
    }

    // left_1 and left_bumper are one slot, so a profile written either way
    // lights. The alias block at the end of output_keywords.h.
    [Fact]
    public void AButtonIsEveryWordTheFirmwareMapsToIt()
    {
        var outputs = Sending(Report(new[] { 5 })).Outputs;
        Assert.Contains("left_1", outputs);
        Assert.Contains("left_bumper", outputs);
        Assert.Contains("xac_left_A", outputs);
        Assert.DoesNotContain("right_1", outputs);
    }

    [Fact]
    public void TwoButtonsAtOnceAreBothSent()
    {
        var outputs = Sending(Report(new[] { 1, 10 })).Outputs;
        Assert.Contains("square", outputs);
        Assert.Contains("start", outputs);
    }

    [Fact]
    public void LettingGoStopsSendingIt()
    {
        Assert.Contains("square", Sending(Report(new[] { 1 })).Outputs);
        Assert.DoesNotContain("square", Sending(Report(Nothing)).Outputs);
    }

    [Theory]
    [InlineData(0, "dpad_N")]
    [InlineData(1, "dpad_NE")]
    [InlineData(2, "dpad_E")]
    [InlineData(3, "dpad_SE")]
    [InlineData(4, "dpad_S")]
    [InlineData(5, "dpad_SW")]
    [InlineData(6, "dpad_W")]
    [InlineData(7, "dpad_NW")]
    public void TheHatSwitchIsADirectionOnTheDPad(int hat, string output)
    {
        Assert.Contains(output, Sending(Report(Nothing, hat)).Outputs);
    }

    // 15 sits past the logical maximum of 7 because the descriptor declares the
    // hat with a null state. Reading it as a direction lights dpad_NW forever.
    [Fact]
    public void ACentredHatIsNotADirection()
    {
        Assert.Empty(Sending(Report(Nothing, HatIdle)).Outputs);
    }

    // left_Y counts DOWN from centre as the stick goes up: DataFlow.c does
    // ps3.left_Y -= joystickvalues[LEFT_JOYSTICK_UP].
    [Fact]
    public void TheLeftStickPushedUpSendsLeftJoyUp()
    {
        var outputs = Sending(Report(Nothing, HatIdle, leftY: 0x10)).Outputs;
        Assert.Contains("left_joy_up", outputs);
        Assert.DoesNotContain("left_joy_down", outputs);
    }

    [Fact]
    public void TheRightStickIsReadAsTheRightStick()
    {
        var outputs = Sending(Report(Nothing, HatIdle, rightX: 0xF0)).Outputs;
        Assert.Contains("right_joy_right", outputs);
        Assert.DoesNotContain("left_joy_right", outputs);
    }

    // A mouth-held stick wanders. Once a row is lit it stays lit until the
    // stick comes well back, so holding a deflection near the edge does not
    // strobe the row on and off.
    [Fact]
    public void AStickHeldNearTheThresholdDoesNotFlicker()
    {
        var lit = Sending(Report(Nothing, HatIdle, leftX: 0xD0));
        Assert.Contains("left_joy_right", lit.Outputs);
        Assert.Contains("left_joy_right", Sending(Report(Nothing, HatIdle, leftX: 0xA5), lit).Outputs);
        Assert.DoesNotContain("left_joy_right", Sending(Report(Nothing, HatIdle, leftX: 0xA5)).Outputs);
    }

    // A mode nobody has taught the app has to say so. Reporting no outputs and
    // calling that understood would draw a still screen while somebody sips.
    [Fact]
    public void AModeTheAppCannotReadSaysSoInsteadOfSayingNothing()
    {
        var descriptor = new ReportDescriptor(GamepadDescriptor);
        var parser = LiveInput.StickItem(descriptor)!.CreateDeviceItemInputParser();
        Assert.True(parser.TryParseReport(Report(new[] { 1 }), 0, descriptor.InputReports.First()));

        var state = LiveInput.Read(parser, "Some other pad", ps3Layout: false, null);
        Assert.False(state.OutputsUnderstood);
        Assert.Empty(state.Outputs);
    }

    // The identity is only half of it. A firmware that moved a button would
    // still answer to the mode 0 vendor and product, so the descriptor has to
    // agree before a report is read as mode 0.
    [Fact]
    public void OnlyTheModeZeroReportShapeIsReadAsModeZero()
    {
        var gamepad = new ReportDescriptor(GamepadDescriptor);
        Assert.True(LiveInput.DeclaresPs3Report(
            LiveInput.StickItem(gamepad)!.CreateDeviceItemInputParser()));

        // Eight buttons and no hat: the Dual Shock 3 opening, which is a
        // different report and must not be read with the mode 0 table.
        var other = new ReportDescriptor(JoystickOpening);
        Assert.False(LiveInput.DeclaresPs3Report(
            LiveInput.StickItem(other)!.CreateDeviceItemInputParser()));
    }

    // A profile may spell a mode 0 button the Xbox way. output_keywords.h is one
    // table the firmware searches whatever mode it is in, so "A" produces
    // ps3.X on a device in mode 0 exactly as "x" does, and its row lights.
    [Fact]
    public void TheXboxSpellingOfAModeZeroButtonLightsToo()
    {
        var outputs = Sending(Report(new[] { 2 })).Outputs;
        Assert.Contains("x", outputs);
        Assert.Contains("A", outputs);
        Assert.DoesNotContain("square", outputs);
        Assert.DoesNotContain("X", outputs);   // X is the Xbox word for square
    }

    // A typo in the table is a row that can never light, silently.
    [Fact]
    public void EveryWordInTheTableIsARealOutput()
    {
        foreach (var word in LiveInput.Ps3OutputWords)
            Assert.True(QuadStick.Format.Vocab.AllOutputs.Contains(word), word);
    }

    // A keyboard, a mouse, an infrared code and a digital out leave the device
    // on a connection this never opens, so their rows can never light. Claiming
    // one here would be the app saying a row is being sent when it cannot know.
    [Fact]
    public void OutputsTheGamepadReportDoesNotCarryAreNotInTheTable()
    {
        string[] elsewhere = { "kb_", "mouse_", "ir_", "digital_out" };
        foreach (var word in LiveInput.Ps3OutputWords)
            Assert.DoesNotContain(elsewhere, prefix => word.StartsWith(prefix, System.StringComparison.Ordinal));
    }

    // A word copied onto the wrong line lights an unrelated row.
    [Fact]
    public void NoWordIsOnTwoSlots()
    {
        var all = LiveInput.Ps3OutputWords.ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    // Which USB identity to look for is the emulation mode's answer, and the
    // app cannot ask the device what mode it is in, so it looks for all of
    // them. This is CALLBACK_USB_GetDescriptor in Joystick/Descriptors.c and
    // CALLBACK_USB_GetDescriptor_DS4 in Joystick/DescriptorsDS4.c, mode by
    // mode. Mode 3, Xbox 360 native, is not here on purpose: that mode
    // publishes interface class 0xFF, which is XInput and not HID at all.
    [Fact]
    public void EveryModeTheFirmwareCanEnumerateAsIsLookedFor()
    {
        Assert.Equal(new[]
        {
            (0x16D0, 0x092B), // mode 0, QuadStick
            (0x054C, 0x0268), // mode 1, Dual Shock 3
            (0x16D0, 0x092C), // mode 2, X360CE
            (0x16D0, 0x092D), // modes 4 and 7 on a PC
            (0x054C, 0x05C5), // mode 4 with a PS4 answering
            (0x0F0D, 0x0066), // mode 4 before the console answers
            (0x057E, 0x2009), // mode 5, Nintendo Switch Pro Controller
            (0x16D0, 0x092E), // mode 6, PS4 without the flash drive
            (0x054C, 0x05C4), // mode 7, wireless DualShock 4 V1
        }, LiveInput.Known);

        Assert.DoesNotContain((0x045E, 0x028E), LiveInput.Known);
    }
}
