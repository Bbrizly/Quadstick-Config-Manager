# QuadStick Format Reference

Notes on how the CSV files work. Pulled from Fred Davison's validation
endpoint, his Apps Script and QMP converter code, and quadstick.com — mostly
from an email thread in July 2026. If something's still fuzzy, it's marked.

Sources, in order of authority:

| # | Source | What it settles |
|---|--------|-----------------|
| S1 | validation.quadstick.com + its Apps Script source | The complete legal names for inputs, outputs, functions |
| S2 | The Sheets add-on script (`_putCSVIntoCache`) | How spreadsheets become device CSV files |
| S3 | github.com/fdavison/QMP-4 (`xlsx2csv.py`, `qsflash.py`, `microterm.py`) | QMP's converter, prefs.csv, serial protocol, device detection |
| S4 | Fred's email, 2026-07-02 | Links to all of the above, firmware repo offer, serial console capability |
| S5 | quadstick.com product pages and user manual | Hardware per model, function parameter meanings, sheet layout rules |
| S6 | Firmware source snapshot (quadstick-master, FW_VERSION 1476, 2014) | The device's own CSV reader: Configuration.c + keyword tables. Final authority on parsing mechanics; its keyword lists are OLDER than S1 |

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

## The firmware's own reader (S6, Configuration.c)

How the device actually reads default.csv (and any file chosen via
`load_file`). Everything below is from the 1476 source; parsing mechanics
this basic rarely change between firmware versions.

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
  carries the connection in its third cell (`none`/`usb`/`bluetooth`,
  unknown words fall back to usb). Binding rows follow until the first
  BLANK LINE (or 128 rows). A row whose output cell matches nothing is
  skipped, not a terminator; the row simply does nothing.
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
- Limits: 16 profiles, 128 binding rows each; extras are read and discarded
  without any indication.
- Preferences segment: two lines skipped (`prefs.csv,,` and
  `Preference,Value,`), then `name,value` rows until a blank line. Values
  are atoi except `bluetooth_device_mode` (none/keyboard/game_pad/mouse/
  combo/joystick/ssp), `bluetooth_connection_mode` (slave/master/trigger/
  auto/dtr/any/pair) and `bluetooth_remote_address` (string, 16 chars).
- Infrared segment: rows are an `ir_*` output keyword followed by up to 256
  Pronto hex words.
- Housekeeping the device does on its own: at startup it DELETES every
  non-csv file except joystick.bin, and every file starting with a dot
  (never store backups on the QuadStick drive). A `Joystick.bin` appearing
  on the drive is compared with flash and, if different, auto-flashed with
  a reboot. default.csv and prefs.csv are polled ~every 2 s by timestamp
  and reloaded when they change.

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
  optional numeric parameters, columns C..J = up to 8 inputs that must all
  be active together.
- Only the first 10 columns exist to the converters (S2: `MAXCOLUMN = 10`,
  S3 same). Columns after J are comments and never export.
- The first row with a blank output after row 3 ends the sheet for both
  converters; they DROP everything after it (S2: `break`, S3: same). The
  firmware is softer: it skips a blank-output row and stops only at a blank
  LINE (S6). Either way the row is dead weight, and a stray row after a
  blank is silently ignored on the device (or, if it happens to start with
  a sheet keyword, read as a phantom sheet). This app treats such rows as
  errors that block installing.
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
outputs, 14 functions. Notes:

- `reset_quadstick` is in the script source but was missing from one deployed
  response of the endpoint; the script wins.
- Historically odd names (`kb_lockingroll_lock`, `kb_manu`,
  `kb_crsel_andprops`, `kb_sisreq`) are canon and must match exactly.
- `lip_soft` is absent from S1's current lists, but the 1476 firmware
  defines it, along with `push`, `right_sip_long`, `right_puff_long` and
  `bluetooth_status`, plus output aliases `gyroscope_cw`/`gyroscope_ccw`
  (S6). Old profiles use these and the device parses them; the app accepts
  them with a "legacy name" warning instead of an error.
- `none` is a real input keyword on the device, equivalent to blank (S6).
- Preference names (`mouse_speed`, `sip_puff_threshold`, ...) are valid
  OUTPUTS: with `increment_value`/`decrement_value` an input can adjust a
  device setting mid-game.

Function parameter grammar (S5, user manual): normal, toggle take none;
repeat [rate] [delay ms]; pulse [ms] [count]; duty [ms]; greater_than [on%]
[off%]; less_than [%]; force_off [ms]; delayed_latch [ms]; delay_off [ms];
delay_on [ms] [ms, exactly 1 = toggle]; tap [ms] [ms, exactly 1 = toggle];
increment_value / decrement_value [amount] [interval ms]. Parameters are
whole numbers on the device (atoi, S6): decimals truncate, and the first
parameter caps at 16383. `increment_value`/`decrement_value` postdate the
1476 firmware; the other 12 functions and the two-parameter grammar are
firmware-confirmed.

A preference name in the output column WITHOUT increment/decrement is a
per-mode preference override: the device skips the function column and
reads the value from the third column (S6). Files in the wild also carry
the value in the function column; 1476 would read those as 0, so the app
warns. Whether newer firmware accepts the column-B form is an open
question with Fred.

## prefs.csv (S3, `qsflash.py`)

Device preferences file, exact header QMP writes:

    QuadStick Configuration,Version 1.1
    Preferences,,,,
    prefs.csv,,,,
    Preference,Value,Units,Description,

then sorted `name,value,,` rows. Parsing skips the first 4 rows. Serial
reads end at the `**END OF FILE**` marker.

## Dangerous settings

`default.csv` is loaded at every power-up and is designed to stay unchanged
so the device can always recover (S4-adjacent, QMP video notes + Mac fork
README). USB emulation modes (PS4 boot mode, virtual XBox/Dualshock
emulation, `enable_DS3_emulation`) change USB enumeration so the flash drive
does not appear on a computer. The 1476 source confirms the mechanism: a
change to `enable_DS3_emulation` (USB emulation mode) or
`enable_usb_a_device` forces a USB disconnect and re-enumeration in the new
mode (S6). The complete list of dangerous VALUES on current firmware is
still open with Fred (PS4 boot mode postdates 1476). Two more S6 facts that
matter for safety: line 1 must start with `QuadStick` or the device ignores
the whole file and boots its built-in defaults, and the device deletes any
non-csv file (except joystick.bin) from its drive at startup, so backups
must live on the computer, never on the device (this app already does
both). This app never installs a file with errors, backs up before every
write, and requires explicit confirmation to touch default.csv.

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
   quadstick-master source snapshot (S6, FW_VERSION 1476). Caveat: 2014
   code; the repo invite from Fred will show what changed since.
2. ~~Version header id field~~ CLOSED for the device (S6: nothing after the
   first 9 chars of line 1 is parsed). QMP still uses the id only to
   identify files; empty is display-cosmetic there.
3. The complete list of default.csv values that disable flash access on
   CURRENT firmware (mechanism confirmed in S6; PS4-era values postdate it).
4. Multi-tab Sheets import: QMP converts every tab via xlsx export; this app
   currently imports the linked tab only and says so in-app.
5. Preference-override rows with the value in column B instead of C: 1476
   reads column C only; real files carry B. Ask Fred which is right today.
