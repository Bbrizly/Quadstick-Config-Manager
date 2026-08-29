# Accessibility — release blocker

Baseline: **WCAG 2.2 AA where applicable**, plus actual desktop assistive-technology testing. QCM's accessibility bar is higher than passing an axe scan.

## Keyboard

Every action must work without pointer:
- shell navigation;
- open/new/import/save/install/share;
- mode selection/reordering;
- device region selection;
- binding editing;
- issues navigation/fix;
- device settings sliders/forms;
- library operations;
- dialogs;
- tutorial;
- agent flow.

Avoid custom roving-focus widgets unless native semantics are insufficient. Document keyboard model for any SVG/device spatial selector.

## Focus

- one visible focus indicator meeting contrast requirements;
- dialog focus trap only while modal;
- Escape consistently cancels non-destructive modal state;
- restore focus to invoking control;
- page navigation sends focus to meaningful heading/back control;
- errors can move/associate focus without stealing it during ordinary typing.

## Visualizer

The centered QuadStick visual **cannot be the only UI**. Provide a synchronized semantic list/tree of device inputs and assigned actions. Each visual hotspot has:
- accessible name;
- current assignment/value;
- selected/active state;
- keyboard activation;
- >= WCAG target size where practical;
- no color-only distinction.

A screen-reader user must be able to edit the same configuration without interpreting geometry.

## Live input

Do not announce 60 HID frames/second. Current code intentionally sets moving live text not to auto-announce. Target:
- visible updates;
- queryable text summary;
- optional deliberate “announce current state” action if useful;
- clear state on disconnect/stale timeout;
- reduced-motion handling.

## Device settings visuals

Current `DeviceBand.cs` explicitly repeats visual ring/photo meaning in text. Preserve that design principle. The dead-zone/full-deflection rings use line style as well as color; target equivalent must not rely on hue alone.

## Scaling/reflow

Test 200% and 400% zoom/equivalent interface scaling. Avoid clipped fixed-height panes. `min-width` layout breakpoints must collapse side rail gracefully while keeping controls reachable.

## RTL/localization

Arabic must render logical order correctly. Use CSS logical properties, `dir` propagation, mirrored directional icons only when semantically directional. Pseudo-loc remains in test pipeline.

## Motion

Honor `prefers-reduced-motion` plus QCM persisted reduce-motion preference. Functional state transitions cannot depend on animation completion.

## Automated gates

- semantic HTML linting where useful;
- axe component/page tests;
- keyboard interaction tests;
- color-contrast tokens checked manually/automatically;
- no unlabeled icon-only buttons;
- no duplicate IDs;
- snapshots under pseudo-loc/RTL.

## Required manual matrix before beta

### Windows
- keyboard only;
- NVDA;
- Narrator;
- Windows high contrast/contrast themes;
- 200/400% scale/zoom;
- Voice Access where practical.

### macOS
- keyboard navigation;
- VoiceOver;
- increased contrast;
- reduced motion;
- zoom/text scaling.

### Linux
At least keyboard + one supported screen reader/Desktop environment smoke test; document WebKitGTK/AT-SPI limitations rather than promise universality.

### Future mobile
VoiceOver + Switch Control; TalkBack + Switch Access before mobile release.

## Accessibility acceptance artifact

Each release candidate gets `tests/accessibility/<version>.md` with platform, AT version, tester, scenarios, pass/fail/issues. A critical blocker cannot be waived by an automated green check.