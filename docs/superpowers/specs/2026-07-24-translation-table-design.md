# Translation table

Date: 2026-07-24
Status: built 2026-07-24

## What this is

A profile can give its own name to an output. You set "Shoot" once and the
editor shows "Shoot" wherever that row appears, while the file still holds
`mouse_left` so the device works.

The name is per row, not per token. "Left click" can be Shoot in one mode and
Select in another, and both read correctly.

## How it works

The name lives on the binding row, in a column the device never reads.

```
A            B       C..J    K            L
mouse_left   normal  sip     my notes     Shoot
```

Column A keeps the real token. Column K is the notes column and stays as it is
(`ProfileFile.MoveInputToNotes` writes to index 10). The action name goes in
column L, index 11.

The parser only treats columns A..J as device data. `Parser.cs:112` says so in
as many words: "Only columns A..J matter; columns after J are comments." The
device, this app, and both official converters all stop at J.

The header row gets "Action" in column L so a shared Google Sheet reads
properly.

## Why not a separate sheet

The first design put the table in its own sheet, like Preferences. It was
wrong for three reasons, all found by reading the code.

The device would have received it. `Device.Install` clones the file, adds the
version header, and writes it out unchanged (`Device.cs:89`). There is no
export step to strip anything. So a sheet the device does not understand would
land on the hardware and we would be betting on its firmware ignoring an
unknown section. Not a bet to make with someone's controller.

Older versions would have damaged it. QCM v1.5.0 is already on both stores and
only knows the keywords Profile, Preferences and Infrared (`Vocab.cs:56`). It
would fold the table's rows into whichever mode sits above them, so deleting or
moving that mode takes the table with it.

It would have broken the editor. `RebuildRows` treats any sheet that is not a
mode as a preferences sheet (`MainWindow.axaml.cs:2295`), and the Modes window
lists every sheet that is not Infrared (`ModesWindow.cs:125`).

Putting the table in far-right columns instead was also rejected. A row with
content only past J still looks blank to the parser, because the blank check
reads columns A..J only (`Parser.cs:114`). That flips the "mode ends here"
switch and every binding below it errors out.

## The table

There is no separate table to store. The table is the set of names already
sitting in column L across the profile.

The Translation Table window reads them, lists them, and lets you rename one.
A rename updates every row using that name, in one undo step. It ships as
"Names..." beside "Modes...", because that is what it holds.

This means an action you have not used yet has nowhere to live, which is fine:
you name a row when you set it. Ready-made lists of real game controls belong
in the app later, not in every profile file.

## The picker

`OutputCatalog` gains "Game" as the first category. The picker's item list
becomes the profile's action names plus the normal tokens, and `Classify` puts
an action name in ("Game", "").

Because the names are per profile, the catalog has to be built per profile
instead of using the static one. Two call sites pass the static catalog today
and both need updating: Device View at `MainWindow.axaml.cs:3433` and List View
at `MainWindow.axaml.cs:2615`. The suggestion list at
`MainWindow.axaml.cs:422` is static too.

Picking "Shoot" writes `mouse_left` to column A and `Shoot` to column L as one
change.

## Display

A row's output shows its column L name when it has one. This is a per row
lookup, not a token lookup, so the call sites that render a binding's output
need the binding, not just the token string. `TokenLabel(token)` alone is not
enough.

In the raw token label style the real token still shows, so there is always a
way to see the truth.

## Rules

An action name is rejected if it matches a real output token. Otherwise "x" or
"mouse_left" would appear twice in the picker meaning different things.

Setting a row's output to a plain token clears its action name. Otherwise the
name would describe an output the row no longer has.

Names are capped at 40 characters, the same cap the mode name box uses.

One name means one output. A name already used for a different token is
refused, or the picker's "Shoot" would mean whichever row the reader hit
first. A file hand-edited past this rule still opens; the first row wins.

## Limits

The device reads lines into a 1024 byte buffer and the check already counts the
whole row including comment columns (`Parser.cs:75`), so a long name is caught
as an error today with no new code. The 64 character cell cap only applies to
columns A..J, so it does not apply here.

An unknown output is a warning, not an error (`Validator.cs:186`), and install
blocks on errors only.

Anyone who opens a shared Google Sheet can type whatever they like in column L.
It is a label, not validated data. That is acceptable for a label.

## Sharing

Drive backup pushes the whole grid (`DriveBackup.cs:175`), so the name travels
with the profile and shows up beside the row it belongs to. Older versions of
QCM ignore the column instead of choking on it.

## Tests

One format test: a profile with names in column L round trips through
`ToCsvText` unchanged, the names do not become bindings, and a name long enough
to pass 1023 bytes is flagged.

One app test: picking a Game entry writes both cells, the row displays the
name, the raw label style still shows the token, and changing the output to a
plain token clears the name.

## Later

Let people choose which column holds notes and which holds the action name.

Ship ready-made action lists for real games in the app, which is where the
"Game" section gets its content once presets exist.
