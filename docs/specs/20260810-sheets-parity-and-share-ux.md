# Epic: Google Sheets parity, share onboarding, and editor UX

## Stakeholder context

QuadStick Config Manager users share profiles as Google Sheets and import community sheets from the home screen. The app is an accessibility tool: users rely on sip-and-puff hardware bindings being correct, and the UI must explain what it knows.

Today the Google Sheets round-trip is built as a silent backup pipe (`DriveBackup` + flat `PushGridAsync`), but users treat Share as a visible workflow. That mismatch causes:

1. **P0 bug:** Copy share link, then import the same link on home → "no profile tab" error.
2. **UX gap:** Share with no Google connection opens Settings with no explanation of connect → save → share.
3. **Community convention gaps:** Mode names live on sheet tabs (`Menu`, `Gameplay`), not in C1 (`Left Joystick`). Import ignores tab names. Export pushes one flat tab with no colors.
4. **Editor friction:** Row numbers show 1, 2, 3 instead of sheet row 4+. Output/input pickers always use deep category drill-down.

**Who is affected:** Solo dev (B) shipping to QuadStick community; end users sharing/importing profiles via Google Sheets; screen reader and keyboard users.

**Why now:** Share→import failure breaks trust in the primary sharing path. Seven commits sit unpushed on `main`; this epic should land before the next tagged release.

---

## Verified current state (2026-08-10)

### Share flow

- `ShareNeedsBackup()` in `src/QuadStick.App/MainWindow.axaml.cs` (~394): if `Backup()` is null, shows status "Sharing needs Google Sheets backup" and opens `SettingsWindow` with no wizard.
- `CopyShareLinkAsync` (~406): saves, pushes via `DriveBackup.GetShareLinkAsync`, copies URL.
- `DriveClient.PushGridAsync` (`src/QuadStick.App/DriveClient.cs` ~42): writes **entire flat CSV** to **sheet 1, A1**, values only (RAW). No tabs per mode, no formatting.

### Import flow

- `ImportSheetsAsync` (`MainWindow.axaml.cs` ~3060): anonymous `docs.google.com/.../export?format=xlsx`, fallback CSV.
- Error at ~3096–3097 when `ProfileFile.Load(text).Document.Sheets.Count == 0`: "That spreadsheet has no profile tab..."
- Restore from Drive uses authenticated `DriveClient.DownloadCsvAsync`; home import does not.

### Mode names from tabs

- `Xlsx.cs` lines 16–18: tab names explicitly dropped; C1 is mode name.
- `XlsxTests.EveryModeTabBecomesAMode`: three tabs all get `"Mouse Mode"` from C1, not tab titles.
- `RepairedAsMode` uses tab name only when C1 empty (unreadable A1 repair path).

### Row numbers

- `RebuildRows` (`MainWindow.axaml.cs` ~3419): `int number = 1` for display.
- `Binding.Row` is already 1-based sheet row (`Parser.cs` ~297: `new Binding(r + 1, ...)`).
- Comment at ~3517–3519 says display should match sheet rows; code contradicts.
- Accessibility strings already use `b.Row` ("Output for row 4"); visible label shows 1.

### Picker categorization

- `OutputCatalog.cs`: categories + subcategories (Controller → Buttons/D-pad/...).
- `InputCatalog` in `MainWindow.axaml.cs` (~4729): groups by mouthpiece zone title.
- `PickerCell` (~4817): always drills categories when `TokenCatalog` present.
- No setting in `AppSettings` (`Theme.cs`).

### Releases / version

- App version `1.6.0` in `src/QuadStick.App/QuadStick.App.csproj`.
- CI tags `v*` → GitHub Release zips (`.github/workflows/build.yml`). No in-app update check.

---

## Goal behavior

After this epic:

1. **Share → import round-trip works** for a profile the user just shared (single-mode new profile and multi-mode saved profile).
2. **Share without Google connected** shows an obvious 3-step wizard: Connect → Save (if needed) → Copy link.
3. **Import from community xlsx** uses **sheet tab name** as mode display name when C1 is empty or a known generic template name; import review names any rename.
4. **Export to Google** creates **one tab per mode**, tab title = mode name, header row colors frozen like community sheets.
5. **List view row numbers** show `Binding.Row` (first binding = 4 on a standard mode sheet).
6. **Settings → Advanced:** `Picker grouping` = Detailed (default) | Wide | Flat for output and input pickers.
7. **Settings → General:** Check for updates (GitHub releases API), show current vs latest, link to download. No auto-replace in v1.

---

## Out of scope

- Code signing / notarization / Velopack auto-replace (follow-up after signing).
- Rewriting C1 on save to match tab names without explicit user action.
- Clamping or reformatting user-entered cell values.
- Microsoft Store / itch listing (separate ship task).
- General performance profiling beyond progress indicators on share/import.

---

## Implementation phases (strict order)

### Phase 1 — P0: Share→import round-trip (block release)

**Hypothesis:** Backup pushes flat single-tab grid; anonymous xlsx export may return empty/unparseable content for API-created sheets, OR export runs before data is visible. Fix by aligning export shape with import expectations and hardening import.

**Tasks:**

1. **Failing test first:** `tests/QuadStick.Format.Tests/ShareImportRoundTripTests.cs` (or App test with fake HTTP):
   - Simulate: push template profile grid → xlsx bytes (fixture from real Google export OR synthetic workbook matching post-push shape) → `ImportSheetsAsync` logic → assert `Sheets.Count >= 1`.
   - Must fail on current code if bug reproduced.

2. **Multi-tab push** in `DriveClient` + `DriveBackup`:
   - Split `ProfileFile` grid into mode sections (reuse `Parser.FindSectionStarts` boundaries or export helper on `ProfileFile`).
   - For each mode: create/rename worksheet tab (title = mode name, sanitized for Sheets 100-char limit).
   - Write mode rows starting A1 on that tab (include mode header rows: Profile Name row, filename row, Outputs/Function/channel row, bindings).
   - Preferences and Infrared get their own tabs when present.
   - Version header row (if present) goes on first tab only or duplicated per QMP convention (verify against `tests/QuadStick.Format.Tests/corpus/multi-tab.xlsx`).

3. **Smarter import** in `ImportSheetsAsync`:
   - If pasted URL matches `settings.DriveLinks` spreadsheet id AND user has Drive token → import via `DriveClient.DownloadCsvAsync` or authenticated xlsx export, not anonymous URL.
   - Anonymous path: retry xlsx download 2–3 times with 1–2s delay (fresh share propagation).
   - Replace generic "no profile tab" with specific messages: empty body, A1 actual value, suggest wait/re-share.

4. **Regression tests:** existing `DriveBackupTests`, `XlsxTests`, `SkippedTabTests` stay green.

**Acceptance criteria:**

- Manual: New profile → Share → Copy link → Home paste link → Import succeeds with ≥1 mode, no "no profile tab".
- `make test` green.
- Import review never calls a partial import "clean" (existing rule).

---

### Phase 2 — Share setup wizard

**New:** `ShareSetupWindow` (or modal `UserControl`) in `src/QuadStick.App/`.

**Trigger:** Replace bare `SettingsWindow` open in `ShareNeedsBackup()` when backup unavailable; also optional first-time share when connected but never shared.

**UI (accessible):**

| Step | Label | Action |
|------|-------|--------|
| 1 | Connect Google Drive | Button → `ConnectGoogleAsync`; green checkmark when `Backup() != null` |
| 2 | Save this profile | Shown only if `_savePath == null`; Save button → `SaveAsync` |
| 3 | Copy share link | Enabled when 1 done and 2 done (or skipped); runs existing `CopyShareLinkAsync` core |

- Do not use color alone: text states "Step 1 of 3, completed" etc.
- Link: "Backup settings…" opens Settings Google tab.
- `[AvaloniaFact]` test: Share with no token opens wizard, not Settings alone.

**Acceptance:** New user can complete share without opening Settings unless they choose Advanced.

---

### Phase 3 — Tab names as mode names (import)

**File:** `src/QuadStick.Format/Xlsx.cs` (+ `ProfileFile`/`Parser` if mode name applied at parse time).

**Rule (xlsx import only):**

When tab A1 is valid sheet keyword and tab imports as mode:

- Let `tabName = trimmed tab title`, `c1 = cell C1 trimmed`.
- If `c1` is empty OR `c1` is in **GenericModeNames** set OR `c1` equals another imported mode's C1 in same workbook → set `ModeName = tabName` in parsed `ModeSheet`.
- Do **not** change grid C1 on import (display + import review only unless user later edits).
- Import review line: "Mode 2 named 'Gameplay' from sheet tab (cell C1 said 'Left Joystick')."

**GenericModeNames** (seed list, case-insensitive):

- From `default-template.csv` C1: `Left Joystick`, `Right Joystick`, etc.
- Corpus generics: `Mouse Mode`, `Left joy`, `Right joy`, `Solo`, empty string.

**Tests:**

- Extend `SkippedTabTests` workbook with tabs `Menu`/`Gameplay`, C1 `Profile Name,,Left Joystick` → names `Menu`, `Gameplay`.
- `Voice` tab with valid A1 still imports (existing test must pass).
- Tab named `Reference Card` with helper A1 still skipped.

**Export alignment (Phase 1):** tab title = `ModeSheet.ModeName` after this logic on re-import.

---

### Phase 4 — Row numbers match Google Sheets

**Files:** `MainWindow.axaml.cs` `RebuildRows`, `PreferenceEditor.cs` `PrefsRow`, `CustomNames.cs` if applicable.

**Change:** Pass `b.Row` to `RowNumberLabel` / `DragHandle` instead of incrementing `number` from 1.

**Tests:** Update `ListViewTests`, `PreferenceOverrideRowTests` that assert visible row "1" on first binding → expect "4" for standard template.

**Acceptance:** First binding row label shows 4; screen reader row number matches visible label.

---

### Phase 5 — Picker grouping setting

**Files:** `Theme.cs` (`AppSettings`), `SettingsWindow.cs`, `OutputCatalog.cs`, `MainWindow.axaml.cs` (`PickerCell`, `InputCatalog`).

**Setting:** `PickerGrouping` enum: `Detailed` | `Wide` | `Flat` (default `Detailed`).

| Mode | Output picker | Input picker |
|------|---------------|--------------|
| Detailed | Current: categories + subcategories | Current: zone groups |
| Wide | Categories only, tokens flat inside | One level: mouthpiece part, flat tokens (no left/middle/right sub-zones) |
| Flat | Single searchable list (Custom first) | Single searchable list |

**Tests:** `OutputCatalogTests` + headless test opening picker at each level counts navigation depth or visible category buttons.

---

### Phase 6 — Sheet formatting on export

**File:** `DriveClient.cs` new `ApplyTabFormattingAsync`.

After values push per tab, `batchUpdate`:

- Freeze rows 1–3 (or through header row for prefs).
- Header row background colors aligned with app palette (`Palette.cs` / `OutputTint`, `FunctionTint`, `InputTint` hex values).
- Optional: column width hints for A–J.

**Test:** Fake HTTP handler asserts `batchUpdate` request body contains expected color RGB for header row.

Depends on Phase 1 multi-tab structure.

---

### Phase 7 — Check for updates (v1)

**Files:** new `UpdateCheck.cs`, `SettingsWindow.cs` General tab, optional status on About/Help.

- Read current version from assembly / csproj at build time.
- `GET https://api.github.com/repos/Bbrizly/Quadstick-Config-Manager/releases/latest` (no auth).
- Compare semver to running version.
- UI: "You are on X. Latest is Y." + button opens release page URL from `html_url`.
- Handle: no network, rate limit, prerelease tag (show but label prerelease).
- Test: inject fake HTTP handler returning fixture JSON.

**Out of scope for this phase:** Download and replace binary.

---

### Phase 8 — Polish (if time in same PR, else follow-up)

- Progress status during share push and import ("Uploading…", "Reading 3 tabs…").
- Respect `ReduceMotion` for any new animations.

---

## Files touched (expected)

| Area | Files |
|------|-------|
| Share/import | `MainWindow.axaml.cs`, `DriveBackup.cs`, `DriveClient.cs`, `SheetsUrl.cs` |
| Xlsx/modes | `Xlsx.cs`, `Parser.cs`, `ImportReviewWindow.cs` |
| UI | `ShareSetupWindow.cs` (new), `SettingsWindow.cs`, `MainWindow.axaml` |
| Settings | `Theme.cs` |
| Pickers | `OutputCatalog.cs`, `MainWindow.axaml.cs` (`PickerCell`) |
| Updates | `UpdateCheck.cs` (new) |
| Tests | `DriveBackupTests.cs`, `XlsxTests.cs`, `SkippedTabTests.cs`, `ShareImportRoundTripTests.cs` (new), `SettingsWindowTests.cs`, `ListViewTests.cs` |

---

## Testing requirements

Every phase ships with tests that fail without the fix:

```bash
make test   # dotnet test both projects
```

Manual smoke before merge:

1. New profile → Share wizard → copy link → home import → editor opens.
2. Open `tests/QuadStick.Format.Tests/corpus/multi-tab.xlsx` → mode names match tab titles where C1 is generic.
3. Settings picker grouping toggles live.
4. Settings check for updates with network on/off.

---

## Rollback / risk

- **Drive push shape change:** Existing linked sheets may need re-push on next save (one-time). Do not delete old links; push overwrites tabs.
- **Tab rename on import:** Display-only; device CSV unchanged until user saves. Document in import review.
- **GenericModeNames false positive:** If user intentionally names a mode "Left Joystick", tab name won't override. Acceptable; rare.

---

## Definition of done

- [ ] All phases 1–7 implemented or explicitly deferred with issue comment.
- [ ] `make test` green.
- [ ] Manual share→import smoke passes.
- [ ] No user values rewritten without user edit.
- [ ] Import review reports skipped tabs and mode renames (never silent).
- [ ] Accessibility: wizard and picker changes have AutomationProperties names; no color-only state.

---

## Agent execution notes

- Read repo `CLAUDE.md` before coding. Device behavior: verify against firmware oracle tests, not comments.
- Commit style: plain sentences, no conventional prefixes, no em dashes, no tool credits.
- Do not push unless asked; repo is 7 commits ahead of origin at spec time.
- Start with Phase 1 failing test, then fix. Do not start Phase 6 before Phase 1 multi-tab push exists.
