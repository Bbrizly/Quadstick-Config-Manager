---
name: product-marketing
description: Use when preparing or refreshing QuadStick Config Manager store screenshots, Mac/Microsoft Store listings, release graphics, launch copy, SEO, localization, or product marketing assets.
---

# QuadStick Config Manager product marketing

Use the repository and the actual application as the source of truth. Never invent QuadStick behavior, supported hardware, affiliations, accessibility claims, store availability, or features.

## Read first

- `README.md` for current product positioning, platforms, features, store links, and release workflow.
- Recent commits for unreleased changes.
- Existing docs and localization resources before writing technical claims.
- `tools/RenderPreview` before creating a second screenshot-capture system.

QCM is a cross-platform desktop app for configuring QuadStick profiles. Its strongest marketing advantage is not "CSV editing"; it is making a fragile technical configuration workflow visual, understandable, safer, and accessible.

## Hard rules

1. **Capture the real app.** Do not use an image generator to recreate Avalonia UI.
2. **Reuse the existing renderer.** The repo already has `tools/RenderPreview`; extend or drive it rather than introducing an unrelated capture framework.
3. **Locale-aware assets.** The screenshot tool already supports language selection. Use the real localized UI for localized store assets.
4. **No false affiliation.** Preserve the repository's clear non-affiliation wording where relevant.
5. **Do not promise hardware paths that are not implemented.** Verify USB/Bluetooth/device behavior in code/docs before marketing it.
6. **Accessibility is product behavior, not decoration.** Only claim support that the application actually provides and that can be demonstrated.

## Capture strategy

Prefer repeatable rendered states over hand-positioned windows. Build the campaign from visually meaningful real states such as:

- Home/library state with real profiles;
- picture-based device editor;
- validation with a clear actionable error/warning;
- Device settings with the hardware image and grouped controls;
- Community profiles/import flow;
- mounted-device file management or safe-install flow when visually understandable;
- localized/RTL UI where localization itself is being marketed.

Use realistic but non-sensitive sample profiles. Keep the same seeded profile/state across locales so campaigns remain comparable.

## Store campaign

QCM has multiple distribution surfaces. Do not reuse copy blindly across all of them.

### Mac App Store

Lead with the user problem and the visual configuration experience. Use real macOS captures and current Apple asset requirements.

### Microsoft Store

Reuse the core narrative but validate current Microsoft image dimensions, metadata limits, and store policy separately.

### GitHub / website

Use wider explanatory images, animated demos, before/after workflow diagrams, and release-feature graphics where they communicate more than store screenshots.

A default five-frame store story:

1. **Hero:** Configure a QuadStick visually.
2. **Safety:** Catch bad settings before install.
3. **Hardware understanding:** See what each physical input/control maps to.
4. **Workflow:** Import/share/community or device management.
5. **Confidence/access:** strong secondary value without fabricated social proof.

## Copy rules

- Sell user outcomes before implementation details.
- Keep device/firmware terminology exact when it must appear.
- Avoid unexplained internal names in headline copy.
- Do not claim that every configuration can be performed without the device unless that is true for the exact screen shown.
- Distinguish app-level warnings from actual device/firmware errors accurately.

## Localization

For each store locale:

1. Render the actual UI in that locale using the existing screenshot path.
2. Check clipping, RTL layout, hardware-marker correctness, and untranslated strings.
3. Localize the marketing headline separately; do not bake English marketing copy over localized UI.
4. Preserve hardware input/output/function tokens that intentionally remain English.

## Release workflow

When asked to market a new release:

1. Find the previous release/tag.
2. Read every user-facing commit since it.
3. Group changes into benefits, not commit-by-commit noise.
4. Select only changes worth communicating to users.
5. Refresh screenshots only where the visual/UI or positioning changed.
6. Produce store release notes, GitHub release highlights, website copy, and social launch assets from the same fact set.

## Output contract

Produce:

- positioning and audience statement;
- store-by-store metadata/copy differences;
- ordered screenshot storyboard with exact renderer/app state;
- locale matrix;
- capture/export checklist;
- release feature list grounded in commits;
- asset list for Mac App Store, Microsoft Store, GitHub, and website as applicable;
- any factual uncertainty that needs verification before publication.
