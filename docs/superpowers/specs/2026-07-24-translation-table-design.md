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

"Custom output names" sits at the end of the mode picker, past the real sheets.
It is not a sheet in the file: selecting it is exactly "no sheet selected", so
`CurrentSheet` is null and every path that edits a sheet already bails. It shows
two columns, an output picker and a name box, and adds rows with the same "Add
row" button the modes use (`CustomNames.cs`).

Editing a name renames it on every row carrying it. Editing the output moves
every row carrying the name. Deleting drops the name from those rows and leaves
their output alone, so a mapping never breaks because a label was removed. Each
is one undo step (`RenameAction`, `RetargetAction`, `ClearAction`).

A name that is on a row is stored on that row, in column L, and travels with the
file. A name defined here and not used yet has no row to live on, so it waits in
settings under the profile's path (`AppSettings.CustomNames`). Those drafts do
not travel with a shared copy, which is the price of not putting anything in the
CSV that the device could see. Names actually in use do travel, and those are
the ones that matter to a reader.

## The picker

`OutputCatalog` gains "Custom" as the first category. The picker's item list
becomes the table's names plus the normal tokens, and `Classify` puts a name in
("Custom", ""). A name with no output yet is listed too, so the names can be
laid out first and the outputs filled in after. Picking one on a row that
already has an output makes that output what the name stands for, everywhere.
Naming a button you already picked is the easy way round, and it never empties
column A. On a row with no output either, the row stays blank and the problems
list says which name still needs a button.

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

An action name is rejected if it reads as a real output. Matched the way the
picker shows a token, not the way the file spells it: the list says "Triangle"
for `triangle` and "Mouse left" for `mouse_left`, so case and the
space-for-underscore swap both count as the same word. Otherwise the list would
hold two entries that read identically and mean different things.

Names are one name whatever their case, everywhere: the table lists "Shoot" and
"shoot" once, and a rename moves both rows. Re-spelling a name in another case
is still a real edit, so it is not refused as a clash with itself.

A refused name says which rule it hit. Putting the old text back with no word
for it reads as the app being broken.

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

Format tests: names in column L round trip through `ToCsvText` unchanged, the
names do not become bindings, a name long enough to pass 1023 bytes is flagged,
and retarget and clear each move every row carrying the name in one undo step.

App tests: picking a Custom entry writes both cells, the row displays the name,
the raw label style still shows the token, changing the output to a plain token
clears the name, and the whole table loop works, define a name against an output
with no mapping, then pick it from a mapping's output list.

## Later

Let people choose which column holds notes and which holds the action name.

Ship ready-made name lists for real games, which the table could load in one
click.
