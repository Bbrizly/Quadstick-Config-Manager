# QuadStick Format Reference

Notes on how the CSV files work. Pulled from Fred Davison's validation
endpoint, his Apps Script and QMP converter code, and quadstick.com, mostly
from an email thread in July 2026. If something's still fuzzy, it's marked.

Sources, in order of authority:

| # | Source | What it settles |
|---|--------|-----------------|
| S1 | validation.quadstick.com + its Apps Script source | The complete legal names for inputs, outputs, functions |
| S2 | The Sheets add-on script (`_putCSVIntoCache`) | How spreadsheets become device CSV files |
| S3 | github.com/fdavison/QMP-4 (`xlsx2csv.py`, `qsflash.py`, `microterm.py`) | QMP's converter, prefs.csv, serial protocol, device detection |
| S4 | Fred's email, 2026-07-02 | Links to all of the above, firmware repo offer, serial console capability |
| S5 | quadstick.com product pages and user manual | Hardware per model, function parameter meanings, sheet layout rules |
| S6 | Firmware source, current (quadstick-Nintendo_Pro_Controller, FW_VERSION 2373, March 2025) | The device's own CSV reader: Configuration.c + keyword tables, plus DataFlow.c for what the parsed bindings then MEAN at runtime. Final authority on parsing mechanics and on match semantics |
| S7 | Firmware source, 2017 (quadstick-master, FW_VERSION 1476) | What a device nobody has updated still does. Cited only where the two firmwares disagree |

Fred's script sources are kept locally in `docs/google_apps_script_projects/`
and deliberately not committed (his code, not ours to publish).

## The file on the device

A game configuration is one CSV file on the QuadStick's flash drive (the
device presents itself as a USB mass-storage drive).

Line 1 is a header (S2, S3):

    QuadStick Configuration,Version 1.4,<spreadsheet url>,<name>     (written by the add-on)
    QuadStick Configuration,Version 1.5,<spreadsheet id>,<name>      (written by QMP)

Version 1.4 files carry the full spreadsheet URL; 1.5 carry the bare id
(`qsflash.py` reads only this first line to identify a file). This app writes
1.5 with an empty id field; whether the empty id is fully safe is an open
question with Fred.

After the header: each sheet's grid, in tab order, separated by one blank
line (S2: `lines.append("")`, "blank line separates sheets in csv file").

The separator must be an EMPTY line, not a row of empty cells. The firmware
tests the first byte of the line (S6, below), so `,,,,,,,,,` runs straight
through and the next sheet's rows are read as part of the sheet above. The
real `config.csv` in the firmware tree writes true empty lines. This app
normalizes both cases on save and install
(`ProfileFile.NormalizeForDeviceCsv`).

## The firmware's own reader (S6, Configuration.c)

How the device actually reads default.csv (and any file chosen via
`load_file`). Everything below is from the 2373 source, and it also holds on
1476: `next_word`, `search_for_keyword`, `search_for_keyword_with_parameter`,
the segment loader and the IR scanner are the same code in both trees, and so
is the fatfs `f_gets` under them. The eight years between the two changed the
keyword tables and what the parsed values mean at runtime, not the reader.

- Line 1 MUST start with the 9 characters `QuadStick` or the whole file is
  rejected and the device falls back to its built-in default configuration.
  Nothing after those 9 characters is parsed, so an empty id field in the
  version header is safe on-device.
- After line 1 the reader dispatches on how each line STARTS, case
  sensitively: `Preferences`, `Profile`, or `Infrared`. Any other line
  between sheets (including blanks) is skipped with "Unrecognized segment".
  A sheet whose A1 contains "Profile" but does not START with it is
  silently skipped: QMP's contains-check is looser than the device.
- Profile segment: the next line (A2/filename) is skipped, the line after
  carries the connection in its third cell (`none`/`usb`/`bluetooth`, plus
  `both` on 2373; unknown words fall back to usb). Binding rows follow until
  the first BLANK LINE (or 128 rows). A row whose output cell matches nothing is
  skipped, not a terminator; the row simply does nothing.
- "Blank line" means the FIRST BYTE of the line is `\n` or `\r`. Both row
  loops are written `while (f_gets(...) && line_buffer[0] != '\n' &&
  line_buffer[0] != '\r' && i < MAX)`. Nothing else ends a segment, so a
  row of commas does not, and neither does the next sheet's keyword row:
  that row's output cell matches nothing and gets skipped like any other.
  Without an empty line, a sheet's bindings load into the sheet above and
  the sheet itself never loads. Reading a 4-tab workbook import with no
  separators gives 1 profile of 128 rows instead of 3 profiles plus prefs.
  The preferences loop is written the same way.
- Words are split by any character that is not alphanumeric or `_ . space -`
  (so commas, tabs and quotes all split; keywords may contain spaces).
  A word longer than 64 chars kills its row; a line longer than the
  1024-byte buffer is split mid-row and misframes everything after it.
- Output cell lookup order: output_keywords, then preference_keywords. A
  preference match means "set this preference for this mode": the function
  cell is SKIPPED and the third cell is the value, read with atoi.
  (`digital_out_1..4` exist in both tables; outputs win.)
- Function cell: matched by PREFIX against the function list, then up to two
  integer parameters read with atoi (decimals truncate, "repeat 2.5" acts as
  2). Param 1 packs into 14 bits (max 16383), param 2 above it. An empty or
  unrecognized function cell yields code 0 = `normal`.
- Input cells: exactly 8 read (columns C..J), stored reversed and compacted;
  `none` equals blank. Nothing past column J is ever read in a profile row,
  which is why columns K+ are safe for comments (they still count against
  the 1024-byte line).
- This app uses two of those free columns. Column K holds the row's note.
  Column L holds the row's action name: the profile's own name for the output
  in column A, so the editor can say "Shoot" where the file says `mouse_left`.
  The name is per row, so the same token reads differently in two modes. The
  label row of each mode carries "Action" in column L so a shared spreadsheet
  reads properly. Older versions of this app and both official converters
  ignore both columns.
- Limits: 16 profiles, 128 binding rows each, 64 characters a keyword, 1024
  bytes a line. All four are the same on 1476 and 2373. Extras are read and
  discarded without any indication. Only rows the device accepts spend a
  slot. The binding loop's own `i++` sits after the `continue` it takes when the
  output cell matches neither an output nor a preference keyword, so a
  blank or misspelled output costs nothing. Counting every row instead
  warns about a limit the file is nowhere near: 12 of the 309 public
  profiles used to get that warning wrongly.
- Preferences segment: two lines skipped (`prefs.csv,,` and
  `Preference,Value,`), then `name,value` rows until a blank line. Values
  are atoi except `bluetooth_device_mode` (none/keyboard/game_pad/mouse/
  combo/joystick/ssp), `bluetooth_connection_mode` (slave/master/trigger/
  auto/dtr/any/pair) and `bluetooth_remote_address` (string, 16 chars).
  One Preferences segment holds at most `MAX_NUMBER_OF_PREFS` rows: 50 on
  1476, 61 on 2373, one per preference the firmware knows.
- Infrared segment: rows are an `ir_*` output keyword followed by up to 256
  Pronto hex words.
- Housekeeping the device does on its own: at startup it DELETES every
  non-csv file except joystick.bin, and every file starting with a dot
  (never store backups on the QuadStick drive). A `Joystick.bin` appearing
  on the drive is compared with flash and, if different, auto-flashed with
  a reboot. default.csv and prefs.csv are polled ~every 2 s by timestamp
  and reloaded when they change.

## What the app straightens out on the way to the device

Two things a spreadsheet treats as harmless are not, because the device has
no CSV parser at all. It reads one line into a 1024-byte buffer and scans it
for separators. `ProfileFile.ToCsvText` fixes both on the way out, and the
validator still sees the grid as typed so it can say what changed.

- **Trailing spaces in columns A..J.** `search_for_keyword` skips LEADING
  whitespace only, then compares the whole word, so an output written `x `
  throws its row away and an input written `lip ` is dropped from the
  binding. Every spreadsheet shows a working row either way. Trimming those
  cells on write touched 3,129 cells across the 309 public profiles.
- **A newline inside a quoted cell.** A cell holding several lines is one
  row to every spreadsheet and several lines to `f_gets`. If one of them is
  blank, the binding loop reads it as the end of the mode and drops every
  row below. One published profile loses thirty bindings to a paragraph
  break in a comment. The app joins those lines back into one on write and
  warns, because it is changing the user's own text.

Neither is a judgement call any more. `tests/QuadStick.Format.Tests/FirmwareOracle.cs`
is a transcription of the reader described above, with its keyword tables
generated from the firmware headers by `scripts/dump-firmware-keywords.py`.
Moving to 2373 needed new tables and not one line of the transcription.
`DeviceAgreementTests.cs` holds the rule it exists for: wherever the app's
view of a profile differs from what the device ends up with, the app must
say so on that row. Disagreeing is allowed, disagreeing in silence is not.
Before writing "the device does X" anywhere, including in this file, add the
case there and let the oracle answer.

## Sheet structure

- Cell A1 is the sheet type. QMP's rule (S3): A1 must CONTAIN "Profile", or
  equal "Preferences" or "Infrared"; anything else fails with "Cell A1 does
  not contain valid value". The add-on (S2) does not re-check A1; it relies
  on the template's dropdown validation.
- Cell A2 of the FIRST sheet is the CSV filename (S2: `rangeValues[1][0]`,
  default `config.csv`). A2 of later sheets is ignored.
- Row 3 carries the output-name-group label in A3 (a dropdown that switches
  PlayStation/XBox naming, S1's `updateValidation`), "Function" in B3, and
  the communication channel in C3.
- Binding rows from row 4: column A = output, column B = function with
  optional numeric parameters, columns C..J = up to 8 inputs.
- Those 8 input cells are a SEQUENCE in time, not a set held down together
  (S6). The device only ever has one input active at a time: the scan in
  `DataFlow.c` breaks at "the first active input to prevent _CENTER from
  always winning". It keeps `previous_active_inputs[8]`, a shift register of
  the last 8 distinct inputs with the newest at index 0, and matches a row
  with "compare this input pattern list with the last sequence of inputs, if
  match, bingo!".
  The column order maps onto that backwards, twice, which cancels out.
  `Configuration.c` reads column C+l into `primary_inputs[7-l]`, so C lands
  at index 7 and J at index 0, then compacts the gaps out downward. The
  result is that `primary_inputs[0]` is the RIGHTMOST filled column, and it
  is compared against the most recent input. So the sheet reads left to
  right in the order the user performs them: C first, then D, and the
  rightmost filled cell last.
  Two consequences worth stating. `input_id = primary_inputs[0]` means the
  output follows the state of the LAST input in the row; the earlier cells
  only gate it. And a row with just C filled skips sequence matching
  entirely, because `if (!matched && (input_pattern[1] != _NONE))` leaves a
  one-input pattern's state alone. One input is a plain trigger; two or more
  is a sequence.
  Do not confuse this with the combo INPUT NAMES (`mp_left_center_sip`).
  Those are single keywords for two or three holes used at once, and they do
  sit in one cell.
- Only the first 10 columns exist to the converters (S2: `MAXCOLUMN = 10`,
  S3 same). Columns after J are comments and never export.
- The first row with a blank output after row 3 ends the sheet for both
  converters; they DROP everything after it (S2: `break`, S3: same). The
  firmware is softer: it skips a blank-output row and stops only at a blank
  LINE (S6). Either way the row is dead weight, and a stray row after a
  blank is silently ignored on the device (or, if it happens to start with
  a sheet keyword, read as a phantom sheet). This app reports such a row as
  a warning and still installs: the device reads the file correctly, the row
  just does nothing. Error is reserved for a row the device MISREADS
  (`tests/QuadStick.Format.Tests/InertRowTests.cs`).
- Sheets named `Inputs`, `Outputs`, `Voice`, or `Reference Card` are helper
  sheets and never export (S2, S3).
- The add-on writes each row only up to its last used cell, with a trailing
  comma after every cell (S2). QMP writes rows from xlsx with numerics cast
  to int (S3). The firmware accepts both shapes, so this app preserves each
  file's original grid verbatim when editing.

## Names (the vocabulary)

The complete legal names live in `src/QuadStick.Format/Data/validation.json`,
embedded verbatim from S1 and verified by script against the Apps Script
source arrays: 140 inputs, 388 PS3-convention outputs, 380 XBox-convention
outputs, 14 functions. Those are the app's lists, and they do not line up with
the firmware's tables: the PS3 and XBox lists are two naming conventions over
one set of device outputs and share 367 names, and several names can point at
one output id. The firmware's own tables at 2373 hold 358 outputs, 146 inputs,
61 preferences and 14 functions (1476 held 339, 139, 50 and 12). Notes:

- `reset_quadstick` is in the script source but was missing from one deployed
  response of the endpoint; the script wins.
- Historically odd names (`kb_lockingroll_lock`, `kb_manu`,
  `kb_crsel_andprops`, `kb_sisreq`) are canon and must match exactly.
- `lip_soft` is absent from S1's current lists, but the firmware defines it,
  along with `push`, `right_sip_long`, `right_puff_long` and
  `bluetooth_status` (S6, `Vocab.LegacyInputs`), plus the output aliases
  `gyroscope_cw`/`gyroscope_ccw`, which the firmware keeps unaxed while the
  endpoint only lists `gyroscope_x_cw` and friends (S6, `Vocab.LegacyOutputs`).
  All of them are still in the 2373 tables, so this is the endpoint pruning
  names, not the device dropping them. Old profiles use these and the device
  parses them; the app accepts them with a "legacy name" warning instead of
  telling the owner to pick another name.
- `none` is a real input keyword on the device, equivalent to blank (S6).
- Preference names (`mouse_speed`, `sip_puff_threshold`, ...) go in the output
  column, but they are never a live control. An input cannot nudge a device
  setting mid-game. See the paragraph below the function grammar: the device
  branches on the NAME, then skips the function cell without reading it, so
  `mouse_speed,increment_value 5,right_sip` sets mouse_speed to
  `atoi("right_sip")`, which is 0. That is true on 1476 and on 2373.
- The 16 `xac_*` Xbox Adaptive Controller names added in 2373 are pure
  aliases and collapse onto only 8 output ids, with the left and right sets
  landing on the same 8: `xac_left_A` and `xac_right_X` are both `left_1`.
  The device cannot tell an alias from its canonical name, so binding both is
  binding the same button twice. The app does not warn about this yet.
- `capture` (2373) is an alias of `touch`. Both reach the DS4 touchpad click
  and the Switch Capture button, so they do nothing at all in emulation modes
  0 to 3.
- `any_direction` (2373) ignores `joystick_deflection_minimum`: it is fed the
  raw deflection distance and scaled from zero. With
  `joystick_dead_zone_shape` set to 0 (square) the distance it gets at rest is
  the dead zone itself, never zero, so the input is stuck on.

Function parameter grammar (S5, user manual): normal, toggle take none;
repeat [rate] [delay ms]; pulse [ms] [count]; duty [ms]; greater_than [on%]
[off%]; less_than [%]; force_off [ms]; delayed_latch [ms]; delay_off [ms];
delay_on [ms] [ms, exactly 1 = toggle]; tap [ms] [ms, exactly 1 = toggle];
increment_value / decrement_value [step%] [auto-repeat ms]. Parameters are
whole numbers on the device (atoi, S6): decimals truncate, and the first
parameter caps at 16383. All 14 functions and the two-parameter grammar are
firmware-confirmed. `increment_value` and `decrement_value` arrived with 2373
and act on an output's analog value (0 to 1023), not on a preference: the
first parameter is the step as a percent of full scale, default 10, and the
second is an auto-repeat period in ms. The value latches, so
`left_joy_up,increment_value 5,mp_center_puff` raises the stick 5% a puff and
holds it there when the puff stops.

A preference name in the output column is a per-mode preference override: the
device skips the function column and reads the value from the third column
(S6). This is column C, and it is specific to a mode sheet. A value in the
function column (B) on a mode sheet is read as 0, so the app warns.

The function column is skipped, not evaluated, which is why increment and
decrement cannot reach a preference. `Configuration.c` takes the preference
branch on the output name alone:

    binding.output += 1024;
    next_word(line_buffer, &k);                  // skip "function" keyword
    binding.function = atoi(next_word(line_buffer, &k));

and the switch that runs the function codes is guarded by
`if (output_id <= KEYBOARD_RIGHT_GUI)`, which an output id biased up by 1024
can never reach.

The column is different on a dedicated Preferences sheet: there the value
is in column B, not C (Fred Davison, 2026-07-08). The two forms are not in
conflict: a mode sheet uses C, a Preferences sheet uses B, because
mode-specific preferences were bolted on later than the Preferences sheet.
The app branches on sheet type (see `ValidatePreferencesSheet`).

## prefs.csv (S3, `qsflash.py`)

Device preferences file, exact header QMP writes:

    QuadStick Configuration,Version 1.1
    Preferences,,,,
    prefs.csv,,,,
    Preference,Value,Units,Description,

then sorted `name,value,,` rows. Parsing skips the first 4 rows. Serial
reads end at the `**END OF FILE**` marker.

## What a firmware update from 1476 to 2373 changes (S6 against S7)

2373 took nothing away. Every keyword a 2017 device knows a 2025 device still
knows, and the four limits are the same, so a profile written for the old
firmware still parses on the new one. What moved is what some of it MEANS,
and none of it is announced anywhere the owner can see.

- `enable_DS3_emulation` 5 meant "PC only, no joystick" on 1476. On 2373 it
  means Nintendo Switch Pro Controller. A file carrying 5 behaves completely
  differently after an update. The full list on 2373 is 0 QuadStick native,
  1 DualShock 3, 2 x360ce, 3 Xbox 360, 4 DualShock 4, 5 Nintendo Switch,
  6 DualShock 4 with no USB drive, 7 straight DS4 wireless.
- The longest profile FILE NAME the device can list and load dropped from 254
  characters to 31, `.csv` included. `Configuration.c` went from
  `char files[NUM_FILES][FN_LENGTH]` (FN_LENGTH is fatfs `_MAX_LFN`, 255) to
  `char files[NUM_FILES][32]`, and it still copies with a `strncpy` that
  writes no terminator when the name fills the slot. A longer name runs into
  the next slot and the file will not open.
- The mouthpiece interlock widened from three sensors to four by pulling the
  side tube in. `right_sip` and `right_puff` can no longer fire while the
  mouthpiece right hole is active, because that combination now has its own
  name (`mp_right_mode_sip`, `mp_right_mode_puff`, and the two `_soft`
  forms). Five mouthpiece combos now need the side tube quiet before they can
  start: triple, right_center, left_right, center and right. And `right_sip`
  latches. 1476 wrote `state = (sipuff_right.state == -2)`, which went false
  the instant a sip escalated to `right_sip_long`; 2373 holds it true while
  the sensor timer runs.
- `enable_auto_zero` is dead. `Load_Configuration_File` forces it to 0 after
  every config load ("it doesn't work right") and every read of it in
  `DataFlow.c` is commented out.
- `usb_2_dead_zone` is parsed and stored and never read. Only
  `usb_1_dead_zone` reaches `JoystickHost.c`.
- `joystick_deflection_minimum` of 0 no longer means no dead zone. 2373
  substitutes 129 raw counts.
- `sip_puff_delay_hard` is a real setting now, default 2000 ms. 1476 had no
  such preference: it hardcoded the hard delay to `SipPuffDelay << 1`, so
  with the 1200 ms soft default the old effective value was 2400.
- Three defaults moved: `bluetooth_authentication_mode` 4 to 2,
  `bluetooth_throttle` 15 to 5, `enable_auto_zero` 1 to 0.
- A mode whose channel cell says `both` loses Bluetooth entirely on a 2017
  device, silently. 2373 added the `both` keyword and made the channel a
  bitmask (USB 1, BLUETOOTH 2, both 3), tested with `&`. 1476 has no `both`
  keyword, so `search_for_keyword` returns its usb fallback, and it tests the
  channel with `==` anyway.

## Dangerous settings

`default.csv` is loaded at every power-up and is designed to stay unchanged
so the device can always recover (S4-adjacent, QMP video notes + Mac fork
README). USB emulation modes (PS4 boot mode, virtual XBox/Dualshock
emulation, `enable_DS3_emulation`) change USB enumeration so the flash drive
does not appear on a computer. The firmware confirms the mechanism: a
change to `enable_DS3_emulation` (USB emulation mode) or
`enable_usb_a_device` forces a USB disconnect and re-enumeration in the new
mode (S6). On 2373 the values that cost you the drive are 5, 6 and 7. Mode 6
skips `MS_Device_ConfigureEndpoints` outright, and the configuration
descriptors for modes 5 and 7 declare a single interface, the joystick, with
no mass-storage interface in them. Modes 0 to 4 keep the drive.

`reset_quadstick` (2373) is worth its own warning. It restarts the device,
but `force_reset` waits 300 ms and then checks the mouthpiece push switch: if
it is still held, the device drops into the serial ISP bootloader and stops
being a controller until it is power-cycled.

Two more S6 facts that matter for safety: line 1 must start with `QuadStick`
or the device ignores the whole file and boots its built-in defaults, and the
device deletes any non-csv file (except joystick.bin) from its drive at
startup, so backups must live on the computer, never on the device (this app
already does both). This app never installs a file with errors, backs up
before every write, and requires explicit confirmation to touch default.csv.

## Serial console (S3, `microterm.py`; S4)

The QuadStick has a serial console over the optional Bluetooth module or a
3.3V TTL serial cable: 115200 baud, no flow control. Commands are framed
`\b<command>\r`; every response ends with a `>` prompt. Probe: send
`\rreset\r`, a QuadStick answers containing "all outputs reset". Commands:
`files`, `read_file,<name>`, `write_file,0,<name>` / `write_file,1,<512-byte
chunks>` / `write_file,2,<yy,mm,dd,hh,mm,ss>`, `delete_file,<name>`, `build`,
`reset`. File management over serial is gated by the `enable_serial_port`
setting. Fred suggests a caregiver phone app on this channel; it is on this
project's roadmap after desktop v1.

## Device detection

Windows QMP finds the drive by volume label "quad stick"; the Mac fork scans
/Volumes for a name containing "Quad" and "Stick", preferring a volume with
prefs.csv or default.csv (S3). This app detects any volume whose root
contains default.csv, with a manual folder picker as fallback.

## Hardware per model (S5, product pages)

- FPS: 3-hole mouthpiece plus separate side tube (or a 4-hole mouthpiece
  that incorporates it), lip sensor, three rear 3.5 mm jacks (bottom = two
  switch inputs; center = lip sensor; top = two switch inputs OR two relay
  outputs, chosen at ordering). Larger, more precise joystick than Original.
- Original: same input set as the FPS; lighter joystick (~25 g force).
- Singleton: a single sip/puff tube at the end of the joystick; uses sip and
  puff patterns plus joystick movement. The product page lists no lip switch
  or jacks.

## Still open (tracked, not assumed)

1. ~~Firmware's own CSV reader~~ CLOSED 2026-07-07: read from the
   quadstick-master source snapshot (FW_VERSION 1476). The 2014-code caveat
   is answered: the 2373 source arrived and the reader is the same code, so
   nothing transcribed from 1476 had to change. The keyword tables grew
   (outputs 339 to 358, inputs 139 to 146, preferences 50 to 61, functions 12
   to 14) and the four device limits did not move.
2. ~~Version header id field~~ CLOSED for the device (S6: nothing after the
   first 9 chars of line 1 is parsed). QMP still uses the id only to
   identify files; empty is display-cosmetic there.
3. The complete list of default.csv values that disable flash access. Mostly
   answered: `enable_DS3_emulation` runs 0 to 7 on 2373 and modes 5, 6 and 7
   do not present the mass-storage drive, so those three cost you the config
   drive. What is still unread is whether anything other than the emulation
   mode and `enable_usb_a_device` can do it, and what the same values do on
   whatever firmware ships next.
4. ~~Multi-tab Sheets import~~ CLOSED 2026-07-24: the app now imports every
   tab the same way QMP does, by reading the workbook's xlsx export
   (`src/QuadStick.Format/Xlsx.cs`), and opens .xlsx files directly. Tabs are
   concatenated in tab order, minus the helper names above. Tab names are
   dropped, as they are by QMP: the CSV has nowhere to put them. A published
   link (`/d/e/.../pub`) still gives one tab only, so that path falls back to
   the CSV export and says so in-app.
5. ~~Preference value column B vs C~~ CLOSED 2026-07-08 (Fred): it depends
   on the sheet. A Preferences sheet puts the value in column B; a mode-sheet
   preference override puts it in column C (B is ignored there). The app now
   branches on sheet type in `ValidatePreferencesSheet`.
6. Which firmware a given user is actually running. The vocabulary half of
   this is closed: every name the app offers is in the 2373 tables, so
   `The_only_names_the_two_disagree_on_are_the_ones_we_know_about` now pins an
   empty list. The endpoint was never ahead of the device, only ahead of the
   source we could read. What is still open is the question underneath.
   Nothing in the app reads the connected device's version, so it describes
   2373 and cannot tell an owner on a 2017 stick that `both` will silently
   drop their Bluetooth, or that their `enable_DS3_emulation` 5 means
   something else here. Closing this needs a way to read a version off a
   device.

7. What the workbook reader drops without naming it. The import review window
   can only report the tabs `Xlsx.ToCsv` hands back, and that list is the tabs
   holding at least three rows with a known function in column B. Everything
   else leaves no trace: a real mode with only one or two recognised bindings,
   any tab called Inputs, Outputs, Voice or Reference Card whatever is in it,
   a tab whose workbook relationship will not resolve, and a formula cell with
   no cached value, which reads as empty. None of that is new (the app has
   always worked this way) but the review window now says out loud what came
   in, so a silent drop reads as a lie where it used to read as nothing.
   Closing this means `Xlsx` returning every tab it saw with what it decided
   about each one, instead of only the ones it half recognised.
