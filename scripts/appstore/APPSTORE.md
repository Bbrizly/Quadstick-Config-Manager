# QuadStick Config Manager: Mac App Store runbook

Written 2026-07-11. The packaging is scripted; the Apple-side setup is yours.

## HARD GATE, do first

"QuadStick" is Fred Davison's product name. Do not create the App Store
listing until Fred OKs the name in writing (email, so it is on record). Ask
in the existing testers-group thread, one line:

> I'd like to put the config manager on the Mac App Store for free so
> QuadStick users can install it the easy way. It stays free and open
> source. Are you OK with "QuadStick Config Manager" as the store name,
> or would you prefer different wording?

If he prefers distance, fallback name: "Config Manager for QuadStick"
(compatibility phrasing, weaker trademark claim) with a description line
"not affiliated with QuadStick / Fred Davison" if he wants it.

## One-time Apple setup (~20 min)

1. developer.apple.com > Certificates: create **Apple Distribution** and
   **Mac Installer Distribution** certificates (Xcode > Settings > Accounts >
   Manage Certificates can do both).
2. Identifiers: register App ID `com.bbrizly.quadstickconfigmanager`
   (already the CFBundleIdentifier in make-macos-app.sh).
3. Profiles: create a **Mac App Store** provisioning profile for that App
   ID, download it, save as `scripts/appstore/embedded.provisionprofile`.
   Do not commit it.
4. Find your identity strings: `security find-identity -v -p codesigning`.

## Build and upload

```bash
scripts/appstore/package-appstore.sh 1.0.0 \
  "Apple Distribution: Bassam ... (TEAMID)" \
  "3rd Party Mac Developer Installer: Bassam ... (TEAMID)"
```

Install the app from the built bundle and test BEFORE uploading: the
sandbox changes behavior. Expected difference: automatic drive detection
may find nothing (sandbox hides /Volumes contents); the folder picker path
must carry the whole install flow. That is the acceptance test:
Install flow > pick the QuadStick volume by hand > write succeeds > backup
created.

If the app crashes at launch under sandbox, republish single-file
(`-p:PublishSingleFile=true` on the dotnet publish line): known Avalonia
sandbox fix.

Upload the .pkg with Transporter (App Store), then create the listing in
App Store Connect.

## Listing metadata

- Name: `QuadStick Config Manager` (pending Fred's OK)
- Subtitle: `Edit QuadStick profiles safely`
- Category: Utilities. Price: Free.
- Privacy: Data Not Collected.
- Description: lead with who it is for (quadriplegic gamers configuring a
  QuadStick), the safe edit/validate/install flow with automatic backups,
  free and open source, link the GitHub repo. Reuse the site copy.
- Review notes: reviewer will not have a QuadStick device. Include a sample
  CSV in the repo release and say: "Testable without hardware: File > Open
  the sample CSV, edit, validate, Install to any folder (a USB stick or an
  empty folder stands in for the device)." Verify that flow works before
  submitting, or the review dies here.

## Accessibility angle

This is an accessibility tool. In the review notes and description say so
plainly; App Store editorial occasionally features accessibility apps, and
it is the honest framing. VoiceOver-check the main window before submitting:
Avalonia's accessibility tree is imperfect, fix what is cheap, note the rest.
