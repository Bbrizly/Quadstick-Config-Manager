# QuadStick configurator for iOS, Shipaton 2026

Date: 2026-08-14. Supersedes the scope of `20260813-shipaton-ios-app.md`:
the Shipaton entry is now a profile configurator, not a telemetry viewer.
The telemetry work in that spec (firmware verification, bridge design,
serial whitelist) stays valid and moves to the post-Shipaton backlog.

Deadline unchanged: app live on the App Store and Devpost submission in by
Oct 1, 2026, 3:45am ADT. Registered on Devpost. Real build window: submit
to App Review by Sep 10, which is 27 days from today. The spec is scoped
to that number, not to the calendar-week count.

Working name: SipStudio. Trademark check with Fred is a W1 blocker, not a
background note: store metadata and screenshots wait on his answer, so the
email goes out Aug 14-15 and the listing copy is not written until it is
answered.

Reviewed 2026-08-14 by Codex (adversarial pass against the Format source).
Structural changes it forced: the `qsf_apply` command ABI replacing a Swift
reimplementation of the mutators, subscriptions cut, Google OAuth cut from
v1, install-to-device compiled out of the submitted binary, accessibility
moved from a late pass to a per-week rule.

## What it is

The QuadStick Config Manager's editor, on the phone in your pocket. A
native SwiftUI iOS app that edits, validates, and imports QuadStick
profiles using the exact same parser, validator, and mutation code the
desktop app ships, compiled from the same C# source. A clinician or family
member fixes a profile at the bedside with no laptop in the room.

One sentence for judges: a quadriplegic gamer's controller profile,
editable on a phone, with the device's own file rules enforced live.

## Why this can win

Same categories, same order as before: Peace Prize, Design Award,
#BuildInPublic, HAMM. The demo: open a shared community profile for a new
game from its Sheets link, watch the validator flag two real problems with
the firmware-accurate explanation, fix them by tapping, share the file
back out. All on an iPhone, all hardware-free.

Rules check: brand-new app first released Aug 1 to Sep 30, RevenueCat SDK
for at least one purchase (the lifetime unlock and tip jar qualify),
2-minute video, live store URL, promo code for judges.

## v1 scope, cut to what 27 days holds

In: document library, editor, validator, undo, mode manager, import from
a public Sheets link or an .xlsx/.csv file, import review, share the file
via the share sheet, one non-consumable Pro unlock plus tip jar,
accessibility as a first-class requirement.

Out of v1, in the backlog, listed so they are cut on purpose: Google OAuth
(backup, restore picker, anyone-with-link sharing), subscriptions, PDF
binding sheet, community catalog browser, install-to-device, telemetry,
iPad-optimized layout (it runs scaled), Device view, tutorial tour,
custom names. The desktop remains the full tool.

## Architecture: one brain, literally

`src/QuadStick.Format` is the brain: parser, validator (764 lines, every
rule cited to firmware C), the ~50 `ProfileFile` mutators with their
normalization and row-placement rules, preference catalog, vocabulary,
xlsx import, Sheets URL handling, safe filenames. Zero package
references, no UI types. .NET 9 NativeAOT publishes it as an iOS dylib
with C entry points, consumable from Swift (Microsoft-documented for
iOS-like platforms, experimental label).

Shapes considered:

- **A. Stateless facade, Swift reimplements mutators.** Rejected by
  review: the mutators are not mechanical. They encode separator
  insertion, filename transfer between reordered modes, sheet boundaries,
  and comment preservation, and the validator cannot catch a mutation
  that produces a different but valid profile.
- **B. Stateful handles wrapping `ProfileFile`.** Rejected: object
  lifetimes across a C ABI for no gain.
- **C. Stateless command ABI (chosen).** One entry point applies a named
  mutation to a CSV string by running the real `ProfileFile` code and
  returns the new CSV. Swift never parses or mutates CSV. Undo is a
  Swift-side stack of CSV strings, which is also the desktop's model.

### The ABI

Every call takes and returns UTF-8. Every return is a JSON envelope
`{ok, result, error}`; a managed exception never crosses the boundary
(catch-all in every export, error text in the envelope). Every returned
pointer is freed with `qsf_free`. JSON uses source-generated
`System.Text.Json` contexts (NativeAOT requires it).

    qsf_parse(csv)             -> document JSON: modes, bindings, prefs, issues,
                                  and the raw grid, so the UI renders everything
                                  including comment columns without a Swift parser
    qsf_apply(csv, cmdJson)    -> {csv, document} after one named mutation
                                  (setCell, setOutput, addBindingRow, moveRows,
                                  renameMode, duplicateMode, deleteMode, moveMode,
                                  swapInputs, moveInputToNotes, retargetAction, ...)
                                  running the real ProfileFile mutator
    qsf_import_xlsx(bytes,len) -> {csv, skippedTabs, tabRenames, limitation}
    qsf_repair_tab(bytes,len,i)-> rows for one skipped tab (Import Review keep-anyway)
    qsf_normalize_for_device(csv) -> csv exactly as the desktop writes to the stick
    qsf_new_from_template()    -> csv
    qsf_check_file_name(name)  -> {ok, reason} (31-char device limit, reserved names;
                                  checks, never rewrites)
    qsf_sheets_url(url)        -> {xlsxExportUrl, csvExportUrl}
    qsf_catalogs()             -> {preferences, vocabulary} for pickers and labels
    qsf_version()              -> Format version string, shown in Settings
    qsf_free(ptr)

The issues in `qsf_parse` are the desktop-visible set: parser issues
concatenated with `Validator.Validate`, same as `ProfileFile.Issues`, not
the validator alone.

Profiles are tens of kilobytes; re-running parse-and-validate per edit is
the desktop's own model and is not a performance concern. If a large
profile proves it wrong, the fix is measured then, not designed now.

### Build and packaging

New project `src/QuadStick.Format.Native` (net9.0, `PublishAot`,
`PublishAotUsingRuntimePack`), referencing Format, holding only the
`UnmanagedCallersOnly` exports and the JSON context. Format stays net8.0.
Publish `ios-arm64` and `iossimulator-arm64` separately and combine with
`xcodebuild -create-xcframework` (device and simulator are different
platforms that happen to share an architecture; they are never lipo'd
together). The `make ios-kit` target owns the whole dance: publish,
install_name_tool, Info.plist, xcframework, plus the generated C header
and module map so Swift imports it as a module. Embed and Sign in Xcode.
CI builds the xcframework; hands never do.

`Device.cs` is excluded from the Native project's compile (it is
`DriveInfo` and desktop paths). Nothing else in Format is platform-bound.

### Parity is proven, not assumed

Because the mutators, parser, and validator that run on iOS are the same
compiled C#, parity testing collapses to two questions: did AOT and
trimming change behavior, and does the envelope encode faithfully.

- **Host parity, every CI run:** the facade published for the host RID
  (osx-arm64) runs the corpus through `qsf_parse`, `qsf_apply` sequences,
  and `qsf_normalize_for_device`, comparing against direct in-process
  `ProfileFile` calls. Byte-equal CSV, deep-equal issues.
- **Simulator smoke, every CI run:** an XCTest job on a macOS runner
  loads the real xcframework in the simulator and runs a compressed
  corpus sample plus one of every `qsf_*` call, proving embedded
  resources survive trimming and the ABI survives AOT.
- **Corpus:** the repo test corpus plus a pinned snapshot of the
  community catalog profiles, committed with expected outputs. The live
  catalog is external and mutable; CI never depends on it.
- The normalized output of every corpus file is also run through the
  existing firmware oracle test, so "the firmware reads it identically"
  is asserted by the device's own reader, per the house rule.

The W1 spike must prove the whole chain, not one function: xlsx import
(Compression plus Xml under AOT), embedded resources, repeated calls, a
release-signed archive that passes App Store validation, and the
simulator slice. That is what "the facade is proven" means below.

## The app

SwiftUI, iOS 17+, iPhone first (iPad runs scaled, unoptimized). No
backend, no accounts, no sign-in anywhere. Documents live in the app
container with `UIFileSharingEnabled` and
`LSSupportsOpeningDocumentsInPlace` set, so profiles are visible and
openable in the Files app. Autosave on every mutation (documents are
small and `qsf_apply` returns the full new state); crash recovery is
therefore the normal open path. Export and share via the share sheet;
import via the document picker, the share sheet, or a pasted Sheets link
(anonymous xlsx export download, no OAuth).

Screens:

1. **Home.** Your profiles, New from template, Import (file or Sheets
   link). Recents first.
2. **Editor.** List view per mode: binding rows showing output, function,
   inputs, action name. Tap a row to edit in a sheet: output picker,
   function picker with arity-aware slots, grouped input picker. Mode
   switcher always shows the mode number (modes are positional; duplicate
   names are legal and the number is the identity). Problems bar pinned
   at the bottom: count, first problem, tap to jump, Fix button when a
   fix exists.
3. **Mode manager.** Add, rename, duplicate, reorder, delete.
   Preferences and Infrared shown but never counted as modes.
4. **Import review.** Non-negotiable, not polish: skipped tabs, partial
   reads, auto-renames, with the same keep/fix decisions the desktop
   offers; the move-to-notes and repair paths exist in the ABI. An
   import that is called clean must actually be clean.
5. **Settings.** Theme, Pro unlock, restore purchases, tip jar, about,
   licenses, Format engine version.

Demo content for App Review and judges: bundled sample profiles including
one deliberately broken one ("Fix this profile" onboarding), so the whole
editor-validator loop is experienced with zero hardware and zero
sign-in. Reviewer notes explain the hardware context; nothing in the app
requires the hardware.

## Accessibility, per week, not per pass

The desktop rule transfers whole: accessibility is correctness. This
app's own users may drive the phone with Voice Control, a mouth stick, or
Switch Control. The standing rule for every week: a screen merges only
with VoiceOver labels, Dynamic Type up through accessibility sizes, 44pt
targets, and no color-only signals (severity always has an icon and a
word). W4 holds a full-app audit on top, not instead.

## Monetization (HAMM)

Unchanged rule: nothing a disabled user needs to operate their own
hardware is ever paywalled. Editing, validation, import, and share are
free forever.

- **Pro, one-time $19.99 non-consumable:** profile version history
  (local snapshots per save, last 50 kept, restore to any). One product,
  no subscription group, no ongoing-value review argument. Post-Shipaton
  features (PDF binding sheet, telemetry) join the same unlock.
- **Tip jar:** $2/$5/$10 consumables.
- RevenueCat purchases-ios via SPM, entitlement "pro", RevenueCat
  Paywalls for the paywall UI. Judges get Pro through App Store Connect
  IAP promo codes (non-consumables support them; up to 100 per version).
  Real-purchase push to the community (forum, Facebook group, Fred's
  list) happens at launch; purchases before judging depend on the app
  being live, which is what the Sep 19 target is for.
- Privacy label tells the truth: purchases plus RevenueCat's device
  metadata are declared, RevenueCat's privacy manifest ships, "no
  analytics" is claimed nowhere. No PostHog in v1.

## Install-to-device: designed, not shipped in v1

The QuadStick mounts as a USB drive and the desktop's whole install
protocol is file I/O, so a Files-app path on USB-C iPhones is plausible:
document-picker folder grant, security-scoped bookmark, `default.csv`
check, backup to the app container (never to the removable volume),
write temp, read back and byte-compare, swap, with explicit handling for
the volume vanishing at every step. Unknowns only Fred's loaner can
answer: whether the composite HID-plus-storage device enumerates in
Files at all, and whether iPhone USB power suffices.

Because Apple rejects hidden or dormant functionality (guideline 2.3.1),
this feature is not compiled into the submitted binary at all. It ships
in a post-Shipaton update once hardware-verified, as a visible,
reviewable feature. If the loaner arrives early and verification is
quick, it may make the Devpost video as a demo of the update in flight,
clearly labeled.

## Rules carried over from the desktop, verbatim

- Never rewrite a value the user did not type. The facade validates and
  applies named mutations; it never edits on its own.
- Never say nothing. Partial imports, skipped tabs, repeated mode names:
  described to the user, every time.
- Modes are positional; the number is shown wherever a mode is listed.
- Filenames obey the device: 31 chars, reserved names refused with the
  reason shown, checked never rewritten.

## Schedule (27 days to submission, external deadline, held)

- **Aug 14-17, the spike.** New repo `sipstudio`, remote day one, public
  day one. `QuadStick.Format.Native` with `qsf_parse`, `qsf_apply`
  (setCell first), `qsf_import_xlsx`; xcframework built by script;
  called from a SwiftUI skeleton on a real iPhone; host-parity harness
  running the corpus; release archive passes validation. Email Fred:
  trademark plus the loaner. **Gate decided Aug 18, before any dependent
  work starts: if the chain is not proven, the fallback discussion
  happens out loud with the honest option on the table that the scope
  shrinks to what a Swift port of the validator alone can carry. No
  silent absorption.**
- **Aug 18-24.** Remaining `qsf_*` exports, full parity suite plus
  simulator smoke in CI, Swift shell: Home, document library,
  open/save/autosave/rename, read-only editor rendering from
  `qsf_parse`. Accessibility rule in force from the first screen.
- **Aug 25-31.** Editing end to end through `qsf_apply`: row sheet,
  pickers, problems bar with jump and fix, undo, mode manager.
- **Sep 1-7.** Import (file, share sheet, Sheets link) and Import
  Review. RevenueCat: Pro unlock, tip jar, paywall, sandbox pass, App
  Store Connect products, agreements checked. Icon, screenshots (waiting
  on the trademark answer), TestFlight build out to a couple of
  community testers. Full-app accessibility audit.
- **Sep 8-10.** Freeze, reviewer notes, privacy labels, **submit no
  later than Sep 10.** Two review cycles fit before the Sep 19 live
  target.
- **Sep 11-19.** Review fixes only. Live on store target Sep 19. Demo
  video recorded against the store build.
- **Sep 20-30.** Devpost submitted Sep 25, five days of buffer.
  Community launch push. Hardware bring-up if the loaner arrived, for
  the update, not the submission.

Cut order when time bites: version history first (the Pro unlock goes
with it and the tip jar alone satisfies the RevenueCat purchase rule),
then Import Review's
advanced view (the simple view stays), then the Fix button (problems
still listed and jumpable). The editor-validator loop, import with
honest reporting, and accessibility are never cut; they are the product.

## Risks

- **NativeAOT for iOS is the load-bearing bet.** The spike proves the
  whole chain in the first four days, on device, with a named gate date
  and an out-loud fallback. The chosen ABI keeps the surface at eleven
  functions so the bet stays small.
- **Trimming or AOT eats a resource or a reflection path.** The parity
  suite and simulator smoke run against the published artifact, so it
  fails CI in W1, not on a device in September.
- **App Review.** Full editor that works hardware-free, bundled demo
  profiles, reviewer notes, no login, no dormant features in the binary,
  honest privacy labels. Sep 10 leaves two cycles.
- **Trademark.** W1 blocker with the listing copy gated on the answer.
- **Solo-dev bandwidth.** The v1 list above is the entire commitment;
  everything else is in the out-list by name. Any new idea lands in the
  backlog, not the schedule.
