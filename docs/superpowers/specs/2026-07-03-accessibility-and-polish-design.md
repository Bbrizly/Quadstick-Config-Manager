# QuadStick Config Manager — Accessibility + Premium Polish

Date: 2026-07-03
Status: Design (awaiting review)
Consulted: OpenAI Codex (principal design + Avalonia engineering second opinion)

## Why this exists

QuadStick Config Manager edits and installs game-controller profiles for the
QuadStick, a mouth-operated controller used by people with quadriplegia. The
user base skews heavily toward severe motor impairment and often low vision.
**Accessibility is not a feature here, it is the product.** The app currently
works and is reasonably clean, but it reads as a functional utility, not a
finished product people trust with the one device they game on.

Two goals, in priority order:

1. Make the app genuinely accessible for keyboard-only and screen-reader users
   with low vision and limited motor control.
2. Make it look and feel like a finished, trustworthy premium product.

These are complementary: for this audience "premium" means calm, high-contrast,
stable, large-target, obvious. No motion theater, no gradients, no glass.

## Non-goals (YAGNI)

- **No MVVM/architecture rewrite.** The UI is built in code-behind
  (`MainWindow.axaml.cs`, ~984 lines). That is fine. We extract small helper
  factories, we do not restructure. (Codex: this is the thing most likely to be
  over-engineering — cut it.)
- **No new NuGet dependencies.** Icons ship as `StreamGeometry` resources, not a
  font/icon package.
- **No animation** beyond the theme's default control transitions.
- **No emoji anywhere in the UI** — they render inconsistently across OSes and
  screen readers announce them as garbage.

## Current-state facts that shape the design

- Colors are hardcoded hex in two places: `App.axaml` styles **and** inline in
  code-behind (`Brushes.Green`, `Brushes.Gray`, `Color.Parse("#555555")`, and
  `static readonly` tint brushes `OutputTint`/`FunctionTint`/`InputTint`).
  A naive `DynamicResource` pass will leave every code-behind-assigned brush
  frozen when the theme flips. This is the central refactor.
- `settings.json` (`%AppData%/QuadStickConfigManager/settings.json`) currently
  stores only `{"model": ...}`; `SaveModel()` overwrites the whole file. Theme
  must be **merged**, not clobbered.
- Font sizes are ad hoc: 30, 19, 18, 17, 16, 15, 14, 13, 12.
- Editor toolbar and controls are horizontal `StackPanel`s that clip on narrow
  widths. `MinWidth=1020` is hostile to screen magnification.
- The zone-detail delete button is 40px — below even the current 44px target.
- The device schematic (Device View) is the only unique UI in the app and is
  currently a secondary toggle.

---

## Phase 1 — Foundations

The token layer everything else depends on.

### 1.1 Semantic design tokens (theme-aware)

Add a `ResourceDictionary` in `App.axaml` with `ThemeVariant.Light` and
`ThemeVariant.Dark` scopes. Set `RequestedThemeVariant="Default"` on
`Application` so the OS drives the theme (this also picks up OS high-contrast).

Token groups (starting palette; final values tuned to pass the contrast gate in
§Verification):

| Token | Light | Dark | Use |
|-------|-------|------|-----|
| `AppBackground` | `#F6F5F2` | `#1B1B1A` | window |
| `Surface` | `#FFFFFF` | `#262625` | cards, table, panels |
| `SurfaceSubtle` | `#FBFAF8` | `#2E2E2C` | detail panel, raised zones |
| `SurfaceBorder` | `#D8D6D2` | `#3A3A38` | 1px separators |
| `TextPrimary` | `#1F1F1F` | `#F2F1EE` | body/headings |
| `TextSecondary` | `#4F4F4F` | `#B8B6B1` | subtext (must hit AA) |
| `Accent` | `#0F6CBD` | `#4CA0EA` | primary actions, selection |
| `AccentText` | `#0B5CA3` | `#7FBFF2` | accent-colored text/links |
| `OnAccent` | `#FFFFFF` | `#0B1E30` | text on accent fill |
| `Error` | `#B3261E` | `#F2B8B5` | blocking problems |
| `Success` | `#146C2E` | `#7DD693` | ready/installed |
| `Warning` | `#8A5000` | `#E6C36B` | non-blocking problems |
| `Focus` | `#1348A6` | `#8FC3F5` | focus ring |
| `OutputTint` | `#FBF3D6` | `#3A3320` | output cell |
| `FunctionTint` | `#F9E1E8` | `#3A2630` | function cell |
| `InputTint` | `#DCEBFB` | `#22303F` | input cell |

Rule: **cell tints are backgrounds; the text on them uses `TextPrimary`**, which
is dark on light tints and light on dark tints. Tints are validated against
`TextPrimary` for contrast in their own theme.

### 1.2 Spacing + type scale

Replace ad hoc sizes with named tokens (defined as resources so they are one
source of truth):

- Type: `TitleSize=28`, `SectionSize=19`, `SubheadSize=16`, `BodySize=15`,
  `SmallSize=14` (nothing below 14 in the UI). Line height ~1.4 on wrapping text.
- Spacing steps: `4, 8, 12, 16, 24, 32`. All margins/paddings/`Spacing` snap to
  these unless a value expresses genuine layout hierarchy.

### 1.3 Icon system

A `StreamGeometry` resource dictionary rendered via `PathIcon`. Needed icons:
`home, save, undo, install, help, add, delete, check-circle, warning-triangle,
error-octagon, device, joystick, mouthpiece, chevron-right, theme-system,
theme-light, theme-dark`. Icons always pair with a text label for anything
important; icon-only controls get an `AutomationProperties.Name`.

### 1.4 Theme control + persistence

- Home gets an "Appearance" control (System / Light / Dark), labeled for screen
  readers, using the theme icons + text.
- Persist to `settings.json` as a `theme` key. Rework the settings read/write to
  load-modify-save a small record (`model` + `theme`) so neither key clobbers the
  other. Default `System`.
- Apply on startup and on change via
  `Application.Current.RequestedThemeVariant = Default | Light | Dark`.

### 1.5 Code-behind helper factories

Extract the repeated builders so they pull from tokens/classes, not raw brushes:
`MakeStatusChip(...)`, `MakeCard(...)`, `MakeRow(...)`, `MakeZoneButton(...)`,
`Themed(control, classKey)`. **No `static readonly IBrush` fields for anything
that must react to the theme** — use `{DynamicResource}` in generated controls
(`control[!Control.ForegroundProperty] = new DynamicResourceExtension("...")`)
or, preferably, style classes so the theme-scoped styles set the color.

Acceptance: flipping the OS or the toggle repaints **every** surface, text run,
tint, and status color live, with nothing frozen.

---

## Phase 2 — Accessibility hardening

### 2.1 Contrast + non-color cues

- Every token meets WCAG AA in both themes: text ≥4.5:1, large text / UI
  affordances ≥3:1, against its actual background (contrast gate in §Verification).
- Meaning never rides on color alone. Status and problems carry an icon + text +
  color + border. Invalid input cells get a **warning icon + thicker border +
  accessible text**, not just a red outline.

### 2.2 Focus visibility

Per-control `:focus-visible` styles (Button, TextBox, ComboBox, AutoCompleteBox,
ToggleButton) drawing a 2px `Focus`-colored ring with offset. We do **not** rely
on a single `FocusAdorner`, because it does not reliably cover `AutoCompleteBox`,
templated parts, or our wrapper `Border`s (Codex). Focus must be visible on every
theme and every control by keyboard.

### 2.3 Live regions, calibrated

- `StatusText` announces, but **Assertive only for blocking states** (validation
  errors, install failure). Routine "Saved" / "Ready" is Polite or not live, so
  screen-reader users are not spammed.
- The Device View detail panel announces when the selected zone changes.

### 2.4 Keyboard operation + focus management

- After every screen/view/zone switch, focus moves intentionally to the primary
  target of the new context.
- Adding a row/mapping focuses the new editable field (List View already does
  this; Device View must match).
- Row/mapping delete preserves focus (moves to the next sibling) and is
  undo-friendly.
- Selecting a problem in the Problems list **focuses the offending cell** (today
  it only copies text). Add a "Fix first problem" action that jumps to the first
  error's cell.
- Invalid file name focuses the filename field and states the exact rule.
- Shortcuts: keep Ctrl/Cmd O/S/N/Z; add Install, Help, and Device/List toggle.
  All shortcuts surfaced in the Help guide.

### 2.5 Device schematic as a real non-visual model

This is the hardest accessibility piece. The schematic must be fully usable
without sight or a mouse:

- Zones become `ToggleButton`s (or carry selection semantics), so selected/not
  is exposed to assistive tech, not just shown in blue.
- Each zone's `AutomationProperties.Name` announces: zone name, selected state,
  mapping count, warning state, and its primary mappings.
- Every zone is keyboard-reachable in a sensible tab order, and every zone has a
  List View equivalent (no schematic-only, precision-only interaction).

### 2.6 Target size, text size, window

- Interactive targets ≥48px where practical (raise the base from 44). Fix the
  40px zone-detail delete button.
- Nothing below 14px, including zone summaries (currently 12px).
- Lower `MinWidth`/`MinHeight` and ensure content reflows so the window is usable
  under screen magnification; no fixed-width region may clip its content.

---

## Phase 3 — The Install trust flow

The single most important thing to get right (Codex). Installing is the moment
the app touches the user's physical device; today it is a quiet status string.

Replace it with a **staged, confirmable, reversible flow**, presented in a
dedicated panel/dialog with clear, announced steps:

1. **Profile valid** — no blocking errors (or route the user to "Fix first
   problem").
2. **QuadStick found** — device detected, named, target drive shown. If not
   found, say exactly what to check.
3. **Backup will be made** — the existing on-device profile is backed up first;
   show where.
4. **Installing** — progress, announced.
5. **Verified** — re-read and confirm what landed.

Outcomes:
- **Success = a receipt**: installed filename, target drive, backup path.
- **Failure = safety**: an explicit "your QuadStick was not changed" message and
  the reason, focus returned to a recoverable place.

Every step is keyboard-operable, announced to screen readers, not time-limited,
and reversible where the hardware allows (backup path is always surfaced).

---

## Phase 4 — Premium polish

With foundations + accessibility in place, apply the finish:

- **Status-chip system** everywhere: `Ready`, `Unsaved`, `N errors`,
  `QuadStick connected` / `Not connected`, `Installed` — icon + text + color +
  border, driven by tokens.
- **Elevation as layers, not decoration**: `AppBackground` < `Surface` <
  selected/raised < dialog, with one restrained shadow on cards/panels. No
  gradients, glass, or animation.
- **Spacing/type tokens applied** across Home, Editor, both views, dialogs.
- **Empty states become action states**: "Your profiles" empty state offers New /
  Open / Import; disconnected QuadStick state says what to check, compactly.
- **Device View promoted to the primary editor surface**; List View reframed as
  the advanced/spreadsheet fallback. (Layout hierarchy change, not a data change.)

---

## Verification

- **Contrast gate:** a small check (script or unit test using relative-luminance
  math) asserting every text/background token pair meets its AA ratio in both
  themes. This is the one runnable check that fails if the palette regresses.
- **Visual:** regenerate the four screenshots via `tools/RenderPreview` in **both
  light and dark**. Necessary but **not sufficient** (Codex): screenshots do not
  prove tab order, SR names, live regions, or keyboard recovery.
- **Manual a11y pass (checklist, must be done, not just screenshots):**
  - Full keyboard-only run of: new → edit a row → introduce an error → "Fix first
    problem" → correct it → save → install (dry/mocked). No mouse.
  - Screen reader (Windows Narrator / macOS VoiceOver) reads every control's
    name, announces validation and selected-zone changes, and never spams.
  - Window resized small + magnified: nothing clips, no horizontal scroll of the
    whole page.
- **Existing tests:** `QuadStick.Format.Tests` must stay green (format library is
  untouched by this work).

## Risks / watch-items

- Theme repaint completeness: the biggest failure mode is a missed inline brush
  that freezes on theme switch. The helper-factory refactor plus a manual
  light/dark toggle test is the guard.
- `settings.json` merge: must not drop `model` when writing `theme`, and must
  tolerate an older file that has no `theme` key.
- Device schematic SR semantics are novel; budget real manual testing time.
- Scope is large (four phases). Land phase by phase behind one branch/PR with
  commits per phase so review is tractable.

## Phasing

All four phases in this spec, implemented in order (1 → 4), each as its own set
of commits on the feature branch so they can be reviewed and, if needed, landed
incrementally.
