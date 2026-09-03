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

`4-usb.png`

The device settings screen offers only the four that keep the drive reachable.
The other four are not in the list, and the row says why and where they do
belong. If your device is already on one of the others, that one stays in the
list, because the app's job is to report what the device is running, not to
tidy it.

Adding a preferences sheet to a game profile now asks which controller the file
should make the QuadStick into, with the same block on the boot file. It writes
nothing unless you pick one: an empty value in that cell is not "unset", the
device reads it as mode 0.

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
