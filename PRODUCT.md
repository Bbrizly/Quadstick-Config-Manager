# Product

<!-- impeccable:product-schema 1 -->

## Platform

adaptive

## Users

The primary users are people who use QuadStick assistive controllers, and the people who help them configure those controllers. In the clinician view, the primary users are clinicians setting up, reviewing, and maintaining profiles for clients.

## Product Purpose

QuadStick Config Manager is an independent app for creating, editing, validating, backing up, sharing, and installing QuadStick game profiles and device settings. It helps users move from a difficult spreadsheet-and-file-copy workflow to a safer, understandable configuration workflow. Success means a user or clinician can make a correct change, understand whether it is safe, and get it onto the intended QuadStick with confidence.

## Positioning

QuadStick Config Manager is a large relief from the previous workflow of editing device CSVs in Google Sheets and transferring them with a separate tool. It combines device-aware editing, plain-language validation, safe installation, backup, sharing, and profile management in one community-made product.

## Operating Context

Users work with a physical QuadStick, its mounted USB storage, game profiles, device settings, and positional modes. They may edit through a device-oriented view or a spreadsheet/list view, test live input, review validation findings, save to a library, share or import profiles, and install a profile to a connected device. Clinicians additionally work in a clinician view with client-specific setup and review responsibilities.

The product runs across desktop environments and iOS, with the same product name and profile model across platforms. Some workflows can work offline; cloud import and backup features require network access.

## Capabilities and Constraints

- Device-aware profile editing with physical-input and list/spreadsheet views.
- Firmware-aware names, autocomplete, function explanations, and plain-language validation.
- Errors block installation; warnings explain risk without making a usable file appear invalid.
- Safe installation backs up existing files, writes and verifies a temporary copy, and requires confirmation before overwriting `default.csv`.
- Google Drive backup and profile sharing are available only in builds with a configured OAuth client. Public Sheets import, community profile browsing, and mounted-device file management are separate workflows.
- On iOS, profiles are exported through the Files picker. A USB drive can be written only when iOS exposes it through Files; export success alone does not mean the profile was installed on the controller.
- Device settings are edited separately from game profiles and are not written until the user explicitly saves.
- Live input feedback can highlight the bound control while the QuadStick is being used.
- Supports light and dark themes, keyboard shortcuts, screen readers, large controls, localization, and reduced-motion/accessibility needs.
- `default.csv` and `prefs.csv` are protected from deletion in the app.
- The product must preserve values exactly unless the user explicitly changes them, and must be transparent about skipped, repaired, inferred, or partially imported content.
- The clinician view and its client-management boundaries are still evolving and should remain explicit in future work rather than being inferred from the core editor.

## Brand Commitments

- Canonical product name on every platform and surface: **QuadStick Config Manager**.
- Do not describe the product as official or affiliated with QuadStick unless written authorization is recorded.
- Product language should emphasize relief, safety, clarity, and confidence compared with the previous configuration workflow.

## Evidence on Hand

- `README.md` documents the product purpose, workflows, supported platforms, accessibility commitments, and operational constraints.
- `src/QuadStick.App` contains the Avalonia desktop application and its editor, device, validation, import, sharing, and community workflows.
- `ios/` contains the iOS application and platform configuration.
- `docs/UI-UX-REVAMP-BRIEF.md` records the cross-platform information architecture and accessibility baseline.
- `docs/specs/` contains clinician and product workflow specifications.
- `tests/` and the application test projects contain automated coverage for product behavior.
- No testimonials, customer studies, or performance benchmarks are established here; future work must not invent them.

## Product Principles

1. Accessibility is correctness, not an enhancement.
2. Make the physical controller and the user's intended action understandable before exposing firmware vocabulary.
3. Make safety visible: validation, target device, backups, and recovery must be clear before consequential actions.
4. Preserve user data and be honest about every transformation, import limitation, and system state.
5. Keep the desktop, iOS, and clinician experiences recognizably one product.

## Accessibility & Inclusion

The product must support keyboard navigation, screen readers, Voice Control, Switch Control, mouth sticks, head mice, large controls, scalable text/UI, light and dark themes, reduced motion, high contrast, localization, long labels, disconnected hardware, offline states, and other first-class empty/error states where the platform supports them.
