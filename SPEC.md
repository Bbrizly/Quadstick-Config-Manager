# QuadStick Config Manager — v1 Spec

Unofficial, free, MIT-licensed desktop tool for editing and installing QuadStick game profiles.
Windows + macOS. Not affiliated with QuadStick / Fred Davison.

## Why

Today a QuadStick profile is a Google Sheets spreadsheet with strict conventions,
converted to CSV by a Workspace add-on, and written to the device by QMP, which is
Windows-only. Mac owners need a Windows VM just to manage configs. A wrong value in
`default.csv` can cut off flash-drive access, and recovery requires a physical
force-erase some users cannot perform. This tool gives users a safe editor that
cannot write a broken config.

## Product rules (non-negotiable)

1. Never corrupt a config. Validate before every write. Back up before every write.
2. Refuse to write a `default.csv` whose USB emulation values would disable
   flash-drive access. No override in the UI.
3. Operable by a quadriplegic user unassisted: mouth-mouse/switch friendly.
   Large click targets, no drag-only or right-click-only actions, full keyboard
   navigation, screen-reader labels on everything.
4. Works offline except the Sheets import.

## v1 scope

- Open, edit, and save QuadStick profile CSVs.
- Validate against the real format; show plain-English errors.
- Detect a mounted QuadStick and install a profile in one action.
- Import a shared profile from a Google Sheets URL.

Out of scope for v1: profile library browser (v1.1), serial/live device control,
firmware anything, QMP interop, telemetry (never).

## Architecture

Single Avalonia (.NET) desktop app + one xunit test project. No MVVM framework,
no other runtime dependencies beyond CsvHelper unless the corpus proves a need.

```
App
 ├─ Format/     parser, writer, validator (pure, no I/O)
 ├─ Device/     locate mounted QuadStick, backup, install
 ├─ Import/     Sheets URL → CSV fetch
 └─ UI/         one main window
```

`Format/` is a pure library: bytes in, model or typed errors out. Everything
testable without hardware.

## Format model (ground truth, not guesses)

The profile grammar is derived from two sources, in order of authority:
1. The official user manual's configuration documentation.
2. A test corpus of real profiles downloaded from the public library
   (configs.quadstick.com and the QMP factory set).

Known constraints to encode (from the manual and user-forum failure reports):
- Magic keyword expected in cell A1 per sheet type; wrong or missing A1 breaks loading.
- Columns A–J carry reserved meaning; extra content in reserved columns breaks conversion.
- Hidden mode sheets still export; multi-sheet profiles assemble into one CSV.
- `default.csv` sets USB emulation mode; specific values disable mass-storage access.
- Exact enumerations (input names, output names, function names, USB mode values):
  extracted from the manual + corpus, confirmed with the maker where ambiguous.

Open questions for Fred Davison (tracked, not assumed):
- Complete list of dangerous `default.csv` values.
- Any format edge cases not covered by the public manual.
- Blessing for the "(unofficial)" name.

## Validator

Every rule produces: cell/row location, what is wrong, how to fix it, severity.
- ERROR blocks writing (would not load, or is dangerous).
- WARN allows writing (unusual but legal).
Acceptance: every profile in the corpus round-trips byte-stable and validates clean;
every documented footgun from the forum reproduces as an ERROR with a helpful message.

## Install flow

1. Find removable volumes containing `default.csv` (candidate QuadSticks);
   file-picker fallback.
2. Copy existing target file to a timestamped local backup folder.
3. Write to a temp name on the volume, verify readback, rename into place.
4. Show what changed and where the backup lives. One-click restore.

## Sheets import

Fetch `https://docs.google.com/spreadsheets/d/{id}/export?format=csv[&gid=n]`
per sheet. Assemble multi-sheet profiles only if the corpus shows profiles need it.
Clear error when the sheet is not shared publicly.

## Testing

- Corpus round-trip suite (parse → write → byte-compare) over all public profiles.
- Validator golden tests: one test per documented failure mode.
- Manual beta on real hardware via testers recruited on the QuadStick forum/Discord
  before anything is called stable.

## CI / release

GitHub Actions: build + test on windows-latest and macos-latest; publish
self-contained win-x64 and osx-arm64 binaries on `v*` tags. macOS signing and
notarization deferred until after beta.

## Milestones

- M0: corpus collected; parser + validator green on corpus. (No UI needed.)
- M1: editor UI on Avalonia; open/edit/save with validation.
- M2: install flow + Sheets import; accessibility pass.
- M3: beta post on the QuadStick forum/Discord; fix reports; tag v1.
