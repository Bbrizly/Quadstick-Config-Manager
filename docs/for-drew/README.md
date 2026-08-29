# For Drew, August 2026

Seven screenshots, one per thing you asked for. Every screen here is the real
app driven by a real profile, not a mockup.

Regenerate them with:

    dotnet run --project tools/RenderPreview -c Release -- \
      docs/for-drew tests/QuadStick.Format.Tests/corpus "" agent/corpus/silas --drew

## 1. Device settings without going back to QMP

`1-device-settings.png`

All six settings you named are on the Preferences sheet with real controls: a
dropdown for the emulation mode, spinners for the numbers, checkboxes for the
flags. Each one carries the name QMP uses, so searching for the words you
already have finds it. "Boot in PS4 Mode", "Titan 2 PS4 flag", "USB-A Host
Mode" and "Low Threshold Delay" all appear in the text under their settings.

Still to decide: whether these get sliders and a screen of their own rather
than a spreadsheet tab.

## 2. Emulation modes in plain language

`2-emulation-blocked.png`

The dropdown reads "DualShock 4, for a PS4", not "4". The shot shows what
happens when a mode that costs flash access is set in `default.csv`: an error,
which blocks the install, rather than a warning you can click past.

**One correction.** The safe set is 0, 2, 3 and 4, not 0 to 4. Mode 1
(DualShock 3) has no mass-storage interface in firmware 2373 either. Read off
the configuration descriptors: PS3_t (0), X360CE_t (2), X360_t (3) and CM_t (4)
each carry an MS_Interface; DS3_t (1), NS_t (5), Mode6_t (6) and PS4_t (7)
carry none.

**One more.** "Boot in PS4 Mode" is not a separate setting. It is emulation
mode 4. Firmware 2373 `Joystick.c:625` calls that value "boot in PS4 mode" in
its own comment.

## 3. The back panel

`3-back-panel.png`, `3b-rear-joystick.png`

The photo of the back with every socket labelled, and under it the sentence
each socket needs: one switch in the top jack is `digital_in_8`, only a
splitter uses 7 as well, and the same for 1 and 2 at the bottom and 5 and 6 on
the lip. The unused list under it now leads with the top jack instead of
`digital_in_1`.

The USB-A card is on the device view before anything maps to it, and it leads
with the four joystick directions. That was the click-through you asked for.

## 4. Ranges, bounds and defaults for the function numbers

`4-function-numbers.png`, `4b-out-of-range.png`

Every function that takes numbers now says, under the box, what each number
means, what unit it is in, how far it can go, and what the device does when you
leave it out. All of it is read off firmware 2373's `set_output`, not the
manual.

`greater_than` is 1 to 100 percent, and blank means 100. A value of 150 is a
level no input reaches, so the row never fires. QCM says so and still saves
exactly what was typed.

**Something worth knowing.** Both numbers in a function cell live in 14 bits.
`Configuration.c:302` packs them as `(((parameter2 << 14) + parameter) << 4) +
function_code`. Anything over 16383 in the first number carries into the second
one, and the row then does something nobody asked for. QCM warns about it.

## 5. Bluetooth

`5-bluetooth-per-mode.png`

Each mode picks its own connection: USB cable, Bluetooth, both, or neither. It
writes the cell the device reads, so it travels with the file. "Both" needs
firmware from 2025; QCM says so when you pick it.

## One question back

Which control do you mean by "joystick sensitivity"? There are six candidates
(`joystick_deflection_minimum`, `joystick_deflection_maximum`, and four
`deflection_multiplier_*`) and none of the QMP-4 names on record says
"sensitivity". I would rather ask than label the wrong one.

## One number to check

QCM says Low Threshold Delay defaults to 1300, which is what QMP-4's
`qsflash.py` writes onto a new device. The firmware's own compiled-in fallback
is 1200. Neither is the 1000 in your note. Which one were you working from?
