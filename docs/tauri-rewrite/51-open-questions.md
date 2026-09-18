# Open questions

Unknowns are not excuses to guess. Each has a resolution path and phase deadline.

## OQ-001 — Is serial communication part of current production parity?

**Why:** `System.IO.Ports` is referenced and product history discusses serial/Bluetooth, but audited critical paths found storage + HID.

**Know:** dependency exists; current live reader is HidSharp; current install/library uses mounted filesystem.

**Resolve:** exhaustive source/git-history/release-note search for `SerialPort`, COM/tty, RN-42, console protocol and user-visible serial flow; hardware/product confirmation only if source remains ambiguous.

**Blocks:** only serial feature design, not config/storage/HID rewrite.

**Deadline:** Gate 0.

**Status:** RESOLVED 2026-08-30. Serial is not a current behavior. Nothing was implemented and no serial crate was added.

**Evidence (TASK-029).** The premise of the question is wrong, and that is what decided it.

1. `System.IO.Ports` has never been a dependency of this app. `git log --all -p -- src/QuadStick.App/QuadStick.App.csproj` shows every `PackageReference` ever added or removed, and `Ports` is not among them. The current set is Avalonia (four packages), HidSharp 2.6.4, `System.Security.Cryptography.ProtectedData` and PostHog. No other project file in the solution references it either. The claim in `02-current-system-inventory.md` that `System.IO.Ports 8.0.0` and `System.Management 8.0.0` are current dependencies was a stale note; it is corrected in place rather than worked around.
2. The only occurrence of `SerialPort` or `System.IO.Ports` anywhere outside `docs/` is a **negative** assertion. `tests/QuadStick.App.Tests/DeviceFilesWindowTests.cs::The_window_stays_a_file_manager` reads `DeviceFilesWindow.cs` and fails if the source contains any of `SerialPort`, `HidDevice`, `Firmware`, `System.IO.Ports`, `Bluetooth`, `Device.Install`. It is a guard against serial code appearing, not serial code.
3. History agrees. `git log --all -S"SerialPort"` outside `docs/` returns exactly the two commits that added that guard test (`ddbac6e`, `e2cb264`, 2026-08-01). There is no commit that added, used and removed a serial path.
4. Every `Bluetooth` hit in the app is profile **content**, not a transport. `ModesWindow.cs` edits a mode's connection field (`usb`, `bluetooth`, `both`) and `preferences.json` carries `bluetooth_device_mode`, `bluetooth_authentication_mode`, `bluetooth_connection_mode`, `bluetooth_remote_address`, `bluetooth_remote_adapter`, `bluetooth_throttle`. Those are bytes the app writes into a CSV for the firmware to read. The release note "a mode can be set to USB or Bluetooth on its own" (`docs/release-notes/v1.7.0.md`) is that editor field.
5. The device's own serial console exists as a **device** feature the app configures and never speaks. The `debug` preference is described as turning the QuadStick's console output on when it is above zero; the app writes the number. `LiveInput.cs` says the same thing from the other side: "Nothing here asks it for anything, turns its console on, or writes to it."
6. Nothing shipped can reach a serial path. The live read is HidSharp over HID; install, library and delete are the mounted FAT volume; there is no window, menu item, command or CI step mentioning serial, COM, tty or baud.
7. The one forward-looking mention is a plan, not a route. `docs/specs/20260813-shipaton-ios-app.md` proposes "Serial port reader for the telemetry feed (System.IO.Ports, already the plan for the desktop visualizer)" for an unbuilt SipSight companion. A proposed reader for a product that was never built is not parity.

**Consequence.** Serial/Bluetooth console stays classified D (deferred), matching `gate0-review.md`, `implementation-baseline.md` and `BEHAVIOR_LEDGER.md` B-045. `15-serial-transport.md` remains the spec for the day evidence changes. No `serialport` or `tokio-serial` dependency enters the workspace. Any future sip/puff pressure telemetry over serial or BLE is a new capability with its own protocol spec and hardware spike, never a parity claim. No owner interview was needed: the code answered.

## OQ-002 — Can storage and HID interfaces be reliably correlated to one physical QuadStick?

**Why:** a unified device card could otherwise associate live state with wrong unit when two devices exist.

**Know:** current code handles mounted candidate and live HID independently.

**Resolve:** inspect platform USB topology/serial descriptors on two real units across Windows/macOS/Linux.

**Blocks:** automatic physical-unit aggregation, not independent capabilities.

**Deadline:** before multi-device visualizer claims.

## OQ-003 — Exact BOM/invalid UTF-8 behavior

**Why:** Rust `String` requires UTF-8; .NET readers may handle BOM/replacement differently.

**Resolve:** construct UTF-8 BOM, UTF-16, invalid byte fixtures and run current C# open/import/save paths. Choose reject vs preserve with compatibility classification.

**Blocks:** final native file decoder.

**Deadline:** TASK-008/14 before Gate 2.

## OQ-004 — Exact local save atomic/recovery contract versus device install

Current `ProfileFile.WriteAtomic` is simpler than `Device.Install`. Characterize external-change/replace/backup behavior and decide whether target intentionally improves local save. Keep improvement B separate.

**Deadline:** TASK-020/032.

**TASK-020 (2026-08-29): parity, improvement deferred.** Local save stays at `WriteAtomic` behavior, because `06-behavioral-compatibility-contract.md` governs and a stronger local contract is a class-B improvement. `ProfileSessions::prepare_save` returns a `SavePlan` carrying the exact bytes and the target, and `commit_save` is the only step that touches the store, so backup, read-back and restore land in one body without moving the command surface. Still open for TASK-032/040.

## OQ-005 — Device profile reordering mechanism/file-number semantics

Inspect `DeviceFilesWindow`, `Device.cs`, tests and any LED/current-file numbering rules end-to-end. Do not equate alphabetical display sort with firmware load order.

**Deadline:** TASK-025.

**Answered at TASK-025.** The shipped app has no reorder mechanism at all, so
the question of rename versus metadata write does not arise. `DeviceFilesWindow`
says so in its own header comment: it does not rename, load or run anything.
What exists is `Device.SelectionOrder`, the app's model of the order the device
steps through files when the user cycles profiles: `default.csv` first,
`prefs.csv` excluded because it is settings rather than a profile, AppleDouble
sidecars excluded, and the rest ordinal case-insensitive. The file number is the
1-based position in that order, and `LedPattern(n)` is the audited QuadStick
Manager Program table, copied as data with nothing past 32 extrapolated. That is
ported exactly and no reorder command is invented.

What is still open is narrower and belongs to hardware: whether the device's own
cycling order really is that ordering on a real stick. Nothing in this rewrite
depends on the answer, because nothing writes anything to change it.

## OQ-006 — Linux secure token store

Current Google backup is unavailable on Linux. Decide whether initial Tauri parity preserves that or adds Secret Service/keyring as B improvement after KDE/GNOME reliability test.

**Deadline:** before TASK-044 release scope.

## OQ-007 — HID Rust descriptor API sufficiency

Can `hidapi` expose enough descriptor data and reliably cancel reads across all supported OSes/modes?

**Resolution:** TASK-026 physical spike.

**Fallback:** targeted platform adapter or report-descriptor parser; never fixed offsets without evidence.

## OQ-008 — Windows Store continuation

Current repo has Store workflow. Determine actual user/distribution importance, package identity and data location before Tauri Store packaging work.

**Deadline:** Phase 8; does not block normal signed installer.

## OQ-009 — Stable app bundle identifier/app-data migration

Inventory current IDs/settings folders and update behavior. Beta gets separate identity; stable cutover needs exact mapping.

**Deadline:** TASK-055.

## OQ-010 — Analytics provider continuation

Current PostHog behavior/privacy is contract; vendor is implementation. Decide keep current provider versus direct minimal HTTP/provider change separately. No provider switch may broaden payloads/consent.

**Deadline:** TASK-046.

## OQ-011 — Existing agent runtime/model/network architecture

`agent/` is substantial and must be fully inventoried: model calls, corpus licenses, cache size, eval/finalize assumptions, subprocess/qsf integration. Decide what stays Python sidecar/tooling and what moves to Rust/TS only after behavior map.

**Deadline:** Gate 0 for inventory; Phase 6 for product integration.

## OQ-012 — Community catalog exact endpoint/cache semantics

Current privacy says fetched only when Community opens/refreshes, cache enables offline. Confirm endpoint/cache TTL/invalid data behavior from source/tests before port.

**Deadline:** TASK-043.

## OQ-013 — Tauri updater rollout endpoint/channel

Decide GitHub Release static JSON versus dedicated endpoint, beta/stable channel representation, key rotation/recovery and rollout control. Signature verification is mandatory.

**Deadline:** TASK-047.

## OQ-014 — i18n library choice

Run conversion spike on current RESX plural/format placeholders and RTL/pseudo-loc. `i18next` is default candidate; choose another only if it better preserves semantics with less machinery.

**Deadline:** TASK-037.

## OQ-015 — Desktop minimum OS versions

Current .NET packages exist for three OSes but supported minimum versions must be made explicit for Tauri/WebView/HID. Derive from Tauri/WebView/dependency support and actual user needs.

**Deadline:** Phase 8 packaging.

## OQ-016 — Direct profile external-change detection

Determine current behavior when the local file changes after open. If absent, decide whether watchers are a B improvement or simple save-time metadata/hash check.

**Deadline:** TASK-040.

## OQ-017 — Current app settings storage schema

Locate/characterize Settings persistence, consent/install ID, recents, theme, scale, language, reduced motion and backup path map. Define one-time import vs fresh defaults for beta/stable.

**Deadline:** Gate 0/Task 055.

## OQ-018 — Device settings/preferences file lifecycle

Fully characterize reading `prefs.csv`, default fallback, saving/installing/reload expectations and Mode 7/flash-related device behavior from code/tests/firmware docs.

**Deadline:** TASK-025/042.

## OQ-019 — Release telemetry/privacy binary metadata

Current PostHog library may add runtime/library/OS metadata noted in `PRIVACY.md`. If transport changes, update behavior/policy/test so privacy document remains accurate; do not silently add geolocation/IP enrichment.

**Deadline:** TASK-046.

## OQ-020 — Mobile hardware viability

Physical iOS/Android proof required for USB/HID/storage/Bluetooth. Tauri support alone is not evidence.

**Deadline:** TASK-060; does not block desktop.