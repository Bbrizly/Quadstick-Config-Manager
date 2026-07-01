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
- The first row with a blank output after row 3 ends the sheet; both
  converters DROP everything after it (S2: `break`, S3: same). Because blank
  lines separate sheets, a stray row after a blank could be read as a new
  sheet. This app treats such rows as errors that block installing.
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
- `lip_soft` does not exist; `lip` is the only lip input.
- Preference names (`mouse_speed`, `sip_puff_threshold`, ...) are valid
  OUTPUTS: with `increment_value`/`decrement_value` an input can adjust a
  device setting mid-game.

Function parameter grammar (S5, user manual): normal, toggle take none;
repeat [rate] [delay ms]; pulse [ms] [count]; duty [ms]; greater_than [on%]
[off%]; less_than [%]; force_off [ms]; delayed_latch [ms]; delay_off [ms];
delay_on [ms] [ms, exactly 1 = toggle]; tap [ms] [ms, exactly 1 = toggle];
increment_value / decrement_value [amount] [interval ms].

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
does not appear on a computer. This app never installs a file with errors,
backs up before every write, and requires explicit confirmation to touch
default.csv. The complete list of dangerous values is still an open question
with Fred; the firmware repo invite will settle how the device itself reads
these files.

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

1. Firmware's own CSV reader (repo invite pending) — the writers are fully
   documented above; the reader is the final authority.
2. Whether the version header's id field may be empty (asked in email).
3. The complete list of default.csv values that disable flash access.
4. Multi-tab Sheets import: QMP converts every tab via xlsx export; this app
   currently imports the linked tab only and says so in-app.
