# For Drew, September 2026

Your notes from 1 September, all of them, with a shot of each. Every screen
here is the real app driven by a real profile, not a mockup.

Regenerate them with:

    dotnet run --project tools/RenderPreview -c Release -- docs/for-drew-september --drew2

## The settings groups

`1-joystick.png`, `1b-joystick-open.png`, `2-sip-and-puff.png`,
`3-bluetooth.png`, `4-usb.png`

Each of the four groups you named now opens on the settings you named, and
folds the rest behind **More options**. Joystick went from thirteen rows to
two, sip and puff from twelve to four, Bluetooth from six to three, USB from
fifteen to three.

"Joystick Full Deflection" is **Joystick sensitivity**, and it is the first
row, with the dead zone under it. The old name is still in the sentence below
it so a forum post that uses it still makes sense.

"Hard sip/puff threshold" is **Normal sip/puff threshold**, and its sentence
says what you said: this is the threshold the ordinary sip and puff controls
use, whatever QMP calls it. The hard sip/puff delay now says it is how long you
sip the side tube to switch game files.

`1b` is the same joystick group on a device that already has one of the folded
settings set. The fold opens itself in that case. Hiding a value somebody has
already set would be the app saying nothing about it, which is the one thing
this app is not allowed to do.

## Bluetooth pairing numbers

`3-bluetooth.png`

Each number now says what it does: *Just works, nothing to type (2)*, and so
on. The meanings are from the command reference for the radio inside the
QuadStick, which is what the firmware sends this number to.

## Emulation modes

`4-usb.png`, `9-emulation-picker.png`

All eight modes are on the list, and the four with no flash drive access are
marked on the option itself: *DualShock 4 wireless (7), no drive*. On a file
the device boots into, the row adds what that costs, and the app still refuses
to write one of those to the device.

The picker you get when you add a preferences sheet is one dropdown, not a
column of buttons. **Add** stays off until you pick something, so no mode is
set by default. "Do not set one" is the real version of a blank row: an empty
value in that cell is not "unset", the device reads it with atoi and gets mode
0, so nothing is written unless you choose.

## The picture follows what you click

`5-hole-combos.png`, `6-switch-jacks.png`, `7-main-controls.png`

Hole combos labels the five pairings on the device, each one between the two
holes it uses, and each one clickable. Switch jacks and USB devices put the
back of the case on the main screen with each socket clickable. **Main
controls** is the row back to the front of the device.

## Output functions

`8-functions.png`

The descriptions are your wording from the sheet you sent.

**One correction.** Your sheet gives `less_than` a default of 100%. Firmware
2373 sets 50: `DataFlow.c:1873` is `if (!function_parameter) function_parameter
= 50;`. The app already said 50 and still does. Every other default in your
sheet matches the firmware exactly.

## The Bluetooth dropdown you could not find

It was already there, and that was the bug. It is one dropdown per mode, in
the modes window behind the pencil icon next to Modes, and it sat in a column
with no heading. The column is named **Connection** now, and a mode that is not
on the cable says so in the modes list on the profile page, so there is
something to see from the page you were looking at.
