# Clinic layer: open core, separate download

Date: 2026-09-09.

The app stays free and MIT. A paid clinic layer, a clinician's roster of
clients with a history per client, ships as a separate download whose code is
absent from the free build.

Reviewed against the repo by a second model on 2026-09-09. Findings it got
right are folded in below. Findings it got wrong, and the things it wanted that
we are deliberately not building, are listed at the end with reasons.

## The two things being sold

The free app edits one profile at a time. It has no idea who the profile is
for. A clinician with a caseload has the opposite problem: the file is easy,
the person and the history are hard. Every visit starts with archaeology.

The clinic layer answers two questions:

- Vertical. What did we do with this client, when, and did it stick.
- Horizontal. New C5 who wants shooters, what worked for the other three.

Both are a search over one clinician's own folder. No server, no account, no
pooled data, no model training.

## Decisions

**A roster is one clinician's caseload, not a shared team folder.** A site with
six clinicians has six folders. This is the decision that keeps the storage
model a folder instead of a distributed system: no tombstones, no conflict
reconciliation, no revision checks, no locking protocol. A shared read-only
team view can come later, once real conflicts are observed rather than
imagined.

**A licence names an organization and carries a seat count.** The seat count
says how many clinicians at that site are covered, one roster each. It is
printed in the app and on the invoice and is not enforced in code, because
offline enforcement is not possible and pretending otherwise writes code that
lies. The licence text has to say the count is what was bought, not what is
prevented, or the number becomes a support argument.

**The clinic build makes no network calls at all.** Not "telemetry off". No
community catalog, no Drive, no loopback auth listener. This is the sentence
that gets the product through hospital procurement, so it has to be true and
tested rather than asserted.

**Keep the seam, do not redesign the editor.** The review argued the editor
needs rebuilding because `LoadProfile` hardcodes `savePath: null`. That is true
of that one method and false of the class. `MainWindow.OpenPath` already passes
a save path, and `CurrentProfilePath` and `OpenPathAndInstallAsync` exist
beside it. Read their doc comments: they were built so the Agent's output would
be "checked, validated and installed by the same screens as any other
profile." The host contract already exists. The clinic is its second consumer.

## The public seam

Four changes, no behavior change for a free user.

`src/QuadStick.App/Program.cs`

- `class Program` becomes `public class Program`. It is internal today, so
  `BuildAvaloniaApp` is public but unreachable from another assembly.
  `InternalsVisibleTo("QuadStick.Clinic")` was the alternative and is rejected:
  it writes the private product's name into the public repo forever.
- `InstallNativeLibraryFallback()` moves out of `Main` and becomes the **first**
  statement of `BuildAvaloniaApp()`, before `AppBuilder.Configure`. Registering
  it later can be too late for native loading.
- It gets a static bool guard. `BuildAvaloniaApp` is public and may be called
  more than once; without the guard each call adds another resolver.

`src/QuadStick.App/App.axaml.cs`, after `WindowFor`:

```csharp
public static Func<IReadOnlyList<string>?, Window> StartWindow { get; set; } = WindowFor;
```

and the call site in `OnFrameworkInitializationCompleted` uses it. The app
already chose between two start windows, so this generalizes an existing fork
rather than inventing an extension point.

`src/QuadStick.App/DeviceSettingsPage.cs`: `StartLiveInput` becomes public.

## The host contract

`StartWindow` alone is a startup seam. It does not make the editor safe to host.
These are the gaps, each verified in the source, and the smallest fix for each.

**Saving is invisible to the host.** `OpenPath` wires the save path, but nothing
tells the roster a save happened, so a snapshot cannot be recorded. Add a
`public event Action<string>? ProfileSaved`, raised where
`Telemetry.Track(TelemetryEvent.ProfileSaved)` already fires.

**The editor steals the main window.** `MainWindow.SetLanguage` builds a
replacement window and assigns `desktop.MainWindow = next`. In a clinic host
that quietly promotes an editor over the roster. Guard the assignment so it
only runs when this window is already the main window.

**Crash recovery is a single global.** `CrashGuard.CurrentFile` is one static
delegate reassigned by every MainWindow, and rescue files land in
`%AppData%/QuadStickConfigManager/rescue`, outside any clinic folder. The clinic
opens one editor at a time, and redirects the rescue directory into the roster
folder so a rescued file belongs to the client it came from.

**Free-app paths are the default everywhere.** Settings live in
`%AppData%/QuadStickConfigManager/settings.json`, the profile library defaults
to Documents. A clinic session must not scatter client data across them. Add a
settable roster-scoped path override, applied before the first window is built.

**Live input has no owner.** `StartLiveInput` parks a thread on USB
enumeration and stops on its own window's close. The clinic opens at most one
editor window at a time. This is a constraint on the roster, enforced by a test,
not a mechanism.

Six members total. If the list grows past ten, revisit whether the clinic wants
its own editor window instead.

## Private repository

```
quadstick-clinic/                     private
  core/                               submodule, pinned to a commit
  QuadStickClinic.sln                 core projects + clinic projects
  global.json                         SDK pin
  src/QuadStick.Clinic/               exe, AssemblyName QuadStickClinic
  tests/QuadStick.Clinic.Tests/
  tools/issue-licence/                local only, never in CI
```

Private depends on public, never the reverse. The public repo builds, tests and
releases with no knowledge that the clinic exists.

Clinic `Main`:

```csharp
QuadStick.App.App.StartWindow = _ => new RosterWindow();
QuadStick.App.Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
```

Constraints that bite in Avalonia:

- The core assembly name stays `QuadStickConfigManager`. `App.axaml` and the
  device drawings reference `avares://QuadStickConfigManager/...` directly.
  Clinic-owned resources use the clinic assembly name; never copy a core
  `avares://` URI into clinic XAML.
- Exactly one `Application` and one Fluent theme. The clinic never subclasses
  `App`, never calls `BuildAvaloniaApp` twice, never adds a second FluentTheme.
- The clinic project needs its own Avalonia package references for XAML
  compilation. A `ProjectReference` to the core is not enough.
- The clinic has its own `Strings.resx` and its own copy of the localization
  scan test, because the rule that no English lives outside resx holds there
  too and the test enforcing it lives in the public repo.
- No trimming. Set `StartupObject` if the compiler reports two entry points.

## Walking skeleton

Nothing in the roster is written until all of this passes.

- `make test` green. `make run` opens the editor. `--gallery` opens the
  workbench. `RenderPreview` still renders all twelve screenshots.
- Unit tests: `App.StartWindow(null) is MainWindow` and
  `App.StartWindow(["--gallery"]) is GalleryWindow`.
- Fresh clone of the public repo into an empty directory, `dotnet test`, green.
  This is the proof the dependency points one way.
- The clinic binary opens `RosterWindow`.
- `RosterWindow` opens a `MainWindow`, calls `OpenPath` with a fixture from
  `tests/QuadStick.Format.Tests/corpus`, and receives `ProfileSaved`.
- Core artwork renders in that editor, proving `avares://` resolves from a
  foreign host. A clinic string and a core string both resolve. After
  `Localization.Apply` for a non-English tag, a clinic string changes, which is
  what catches missing satellite assemblies. Run this against the packaged
  artifact, not a debug build.
- Two checks in the **public** repo's CI so they cannot regress:

```bash
dotnet publish src/QuadStick.App -c Release -o /tmp/free
ls /tmp/free | grep -qi clinic && exit 1
grep -ril "quadstick\.clinic" src/ tests/ tools/ packaging/ scripts/ && exit 1 || true
```

- A clinic session constructs no `HttpClient`.

## Licence

One line, UTF-8, in a file:

```
qsl1.<base64url(payload)>.<base64url(signature)>
```

The signature covers the exact payload bytes recovered from base64url, never a
re-serialized object, so canonical JSON is not a problem to solve. ECDSA P-256,
`DSASignatureFormat.Rfc3279DerSequence` passed explicitly on both the signing
and verifying side so the runtime default cannot drift.

Payload: `v`, `kid`, `product`, `licensee`, `seats`, `issued`, `supportedUntil`.

- `product` must equal a constant compiled into the build, checked first.
- Key lookup is `(product, kid)`, not `kid` alone.
- Parser grammar is strict: exactly three dot-separated segments, no
  whitespace, only `A-Za-z0-9-_`, one padding policy, bounded sizes. Reject
  `issued > supportedUntil`, an empty licensee, an unknown `v`.
- Tests cover malformed DER, high-S signatures, truncated and negative
  integers, and trailing bytes.

**Expiry gates builds, not runs.** Features enable when the product matches,
the signature verifies, and the binary's build stamp is at or before
`supportedUntil`. The stamp is a UTC timestamp injected by the release
workflow from the tag, never from the local clock. Nothing reads the wall
clock at runtime, so a wrong clock, an air-gapped machine and a hospital
network that blocks everything all behave identically.

**Key rotation.** The build embeds a `kid` to public key map. Add `k2` to the
next build, issue `k2` licences, keep `k1` until every `k1` licence is past its
`supportedUntil`, then drop it. Never reuse a kid.

**Key custody.** The private key lives offline on one machine with an encrypted
backup. It never enters CI, the repo, or a GitHub secret. Issuance is a local
script, run by hand, that appends to a plain-text issue log. A public release
build must never have access to a signing key.

**The hard rule.** Missing, malformed, unknown-kid or expired licence: the
roster still opens read-only with export enabled. It never withholds a
clinician's own client records over a billing state, and never blocks getting
the data out. Read-only means: view clients, view notes, view history, export
profiles, export notes. It does not mean: create, edit or delete a client,
write a note, take a snapshot, restore a snapshot, or import. Export output is
a readable archive, not a roster the app will import back, so export is not a
way around the write gate.

## Storage

A folder the clinician picks. Sync is whatever the site already uses. The app
never copies data out of that folder.

```
clinic/
  clients/
    <uuid>/
      client.json        display name, abilities, goals, rig
      sessions/<date>-<uuid>.md
      profiles/<date>-<uuid>.csv
```

- Directory and file names are opaque UUIDs. Display names live inside records.
  Names in paths leak into Finder, Explorer, Spotlight, Recents, backup
  listings and every screen share.
- Records carry a schema version from the first write. Client id, snapshot id,
  note id, timestamps, clinician name, profile format version, device model.
- Scans filter on the exact expected extension and ignore `.DS_Store`,
  `Thumbs.db`, sync conflict copies and partial downloads. Anything else found
  goes to a quarantine list the roster shows rather than silently skipping.
- Snapshots are immutable. A change writes a new dated file; nothing is edited
  in place. This is what makes a synced folder safe with one writer, and it is
  also the history.
- `ProfileFile.WriteAtomic` writes to a fixed sibling temp path. Inside a
  single-writer roster folder that is fine. It is not a shared-folder protocol
  and must not be presented as one.
- Deleting a client removes the whole subtree and reports the file count. The
  documented limit, stated plainly to the customer: this cannot remove OneDrive
  version history, recycle-bin copies or the site's backups. Real erasure needs
  their IT.
- Confidentiality is the folder's permissions plus FileVault or BitLocker,
  which the site already mandates. No homegrown encryption. This is written
  down as an accepted boundary, not left implied.

## Privacy and network

- No PostHog client is ever constructed: the private release workflow never
  writes `TelemetryToken.Local.cs`, so the empty token short-circuits
  `Telemetry`. A clinic test asserts the token is empty so a copied CI step
  cannot switch it on.
- No network at all. Community catalog, Drive and the loopback auth listener
  are unreachable from the roster, and a test asserts no `HttpClient` is built.
- `CrashGuard` writes crash logs with raw exception text. Audit it before a
  paying customer: no client name and no roster path may reach a log or a
  stack trace.
- The clinic gets its own privacy document. The public `PRIVACY.md` describes
  PostHog, Drive and US processing and is wrong for this product.
- Third-party notices ship with the clinic binary. The core pulls Avalonia,
  SkiaSharp, HidSharp and more.

## Signing and delivery

macOS, derived from `scripts/appstore/package-appstore.sh`:

- Identity becomes Developer ID Application. Drop the provisioning profile step
  and the `PlistBuddy` 12.0 bump.
- Entitlements are re-derived from actual hardened-runtime needs. The store
  file has `allow-jit` and no unsigned-executable-memory entitlement; do not
  copy it blind.
- Keep the inside-out signing loop over dylibs and managed dlls.
- Notarize the zip, staple the `.app`, re-zip the stapled bundle. Verify with
  `spctl -a -vvv -t install`.
- `scripts/make-macos-app.sh` hardcodes the executable name, bundle identifier
  and display name. All three are parameterized, defaults preserving today's
  values byte for byte. The clinic gets its own identifier or the two apps
  collide in LaunchServices.

Windows, Azure Artifact Signing (formerly Trusted Signing): Individual
Developer identity validation covers the PublicTrust profile, so a solo dev
qualifies. Confirm current eligibility and pricing at signup.

Signing does not grant SmartScreen reputation. Reputation accrues to the
publisher identity over downloads and time and cannot be bought. So sign the
free app first, with the same identity, and let the high-volume download bank
reputation the clinic build inherits.

Delivery is a merchant of record for tax, or a hand-written invoice for the
first customers. Artifacts never reach a public release.

## CI

Private repo, two workflows.

`release.yml`, tag-triggered, builds only from the pinned submodule commit.
Reproducibility first, because the public repo has no `global.json` and CI
floats on `8.0.x` today: pin the SDK with `rollForward: disable`, lock the
package graph, pass `ContinuousIntegrationBuild=true`, stamp the core commit
SHA and the UTC build time into the artifact.

The signing job is isolated. A release build must not execute code from an
unreviewed core commit while signing credentials are in scope. Moving the
submodule pin is a reviewed human commit; the release workflow only ever builds
a pin that already landed.

`drift.yml`, nightly, checks out core `main`, runs the full suite, commits
nothing and produces nothing, opens one tracking issue on failure. Lower
priority than the pinning work above.

## Order and stop gates

**Phase 0, public repo.** The four seam changes, the six host-contract members,
the two seam tests, the two absence checks.
Gate A: full suite green, run, gallery and RenderPreview all unchanged.

**Phase 1, private skeleton.** Repo, submodule, solution, a `RosterWindow` with
one button.
Gate B: every line of the walking skeleton passes. No roster feature before it.

**Phase 2, licence.** Keypair, verify, read-only fallback, issuing script.
Gate C: tests cover valid, tampered, unknown kid, build past `supportedUntil`,
and missing, with the last four landing in read-only plus export.

**Phase 3, the roster.** Data model, client timeline, snapshot on save,
horizontal search. The wrong-client safety work belongs here and is not
optional: persistent client identity on every screen, a target-client banner,
and a confirmation naming the client before any install to a device.
Gate D: Drew uses it on a real caseload for two weeks.

**Phase 4, signing and delivery.** Parameterize the mac script, Developer ID
and notarization, Azure signing on the public repo first, `CrashGuard` audit,
the privacy document, invoicing.
Gate E: a stranger on a managed Windows machine and a managed Mac installs and
launches with no override.

## Deliberately not building

Each of these was raised in review and rejected, with the reason.

- **Machine binding, revocation lists, minimum-supported-build floors.** These
  harden a DRM system. This is not one, and the same review says so: an
  attacker patches one branch and has won regardless. Complexity that buys
  minutes against someone who was never going to pay.
- **Tombstones, conflict reconciliation, append-only sync protocol.** These are
  shared-folder problems. A roster is one clinician's caseload, so there is one
  writer. Revisit only when a shared team roster is actually sold.
- **A clinic-specific editor window.** The host contract exists and is six
  members. Rebuilding the editor is months to avoid a day.
- **Enforced seats.** Impossible offline. The count describes what was bought.
- **A plugin system, page registry, DI container, network activation, auto
  updater.** Ruled out at the start and nothing here needs them.

## Open

- What the roster shows on screen, and how a clinician records what a client
  said. Phase 3, needs Drew.
- Whether a site ever wants a shared read-only view across its clinicians'
  folders. Ask after the first paying site.

## What changed while building it

Phases 0 to 4 are built. Two adversarial reviews found things this plan had
wrong, and the answers are here rather than left as a diff to read.

**The host contract is eleven members, not six.** The plan said revisit past
ten. Revisited, and every one of them earned its place, so the answer is not a
clinic-owned editor. The extra five:

- `NetworkFeature.Enabled`. "No network at all" was going to be a promise about
  what a clinician would not click. The hosted editor has a community page, a
  Google sign-in, a Sheets import box, a share menu, an update check, an
  analytics consent dialog and four links that open a browser. Off means all of
  it is out of the layout and no HTTP client is ever built.
- `MainWindow.EditorReplaced`. Changing the language throws the editor away and
  builds another. The roster held the old one, so it would have recorded
  snapshots until the day somebody picked French and then silently stopped.
- `MainWindow.OpenPathGuardedAsync`. `OpenPath` replaces the open profile with
  no question asked, so switching clients would have dropped unsaved edits.
- `MainWindow.HostBanner` and `MainWindow.BeforeInstall`. The wrong-client work
  the plan puts in phase 3 cannot live in the roster window alone: the danger
  is inside the editor, and the last moment worth catching is the write to a
  device.

**Read-only never opens the editor.** The plan wanted `ClinicAccess` on every
write path, but the core's save and install are private and a roster button is
not a gate. The editor is a way of writing a profile, so it is a write, and an
unlicensed roster does not open it. No core policy hook, no new surface.

**The snapshot record cannot say the device model.** The model is a setting in
the editor and is never written into a profile, so a snapshot claiming to know
it would be making it up. It records the profile's own title and mode count;
the client's rig field is where the hardware is written down.

**The editor opens a working file, never a snapshot.** A snapshot is what was
true on a day. Opening one for editing would rewrite history the first time
somebody pressed save. Each client has `working.csv`, every save of it becomes
a new snapshot, and restoring is a copy onto it.

**Expiry compares a build stamp that has to exist.** `-p:` alone generates
nothing, so an MSBuild target writes the stamp into a generated source file and
refuses anything that is not exactly `yyyy-MM-ddTHH:mm:ssZ`. An unstamped build
is read only. The release workflow takes the stamp from the tag's own commit
date, so building the same tag twice stamps the same instant.

**A licence has exactly one text.** Strict DER was in the plan; two more ways
to spell one licence were not. base64 keeps spare bits in the last character of
a group, which gave every licence sixteen other spellings, and
`DateTimeOffset.TryParse` accepts a bare date that means a different instant in
each timezone. Both are refused now.

**The submodule pin is committed but checked.** A gitlink is a commit id. If it
is not on the public remote nobody else can build the private repo, so
`scripts/check-pin.sh` asks the remote and the release workflow runs it first.

**Two bugs in the free app, found by hosting it.** `CrashGuard.Note` appended
into a folder only the crash path creates and swallowed its own failure, so on
most machines every handled error was logged nowhere. And the one `HttpClient`
was a static field, so opening any window built one whether the run ever asked
for it or not.

**Still open, and both need somebody who is not a machine.** Signing identities
(Developer ID and notarization, Azure Artifact Signing) and gate D, two weeks
on a real caseload.
