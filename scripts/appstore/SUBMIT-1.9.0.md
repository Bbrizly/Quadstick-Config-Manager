# Submit 1.9.0

Both stores, drag and drop. Written 2026-11-04 on branch `finalize-1.9`.

## Decision first: the clinician button

1.9.0 has "Share with my clinician" (editor Share menu and Home). It cannot
connect until the relay (`clinic-remote/relay`) is deployed to
`wss://relay.bassamkamal.dev`. A reviewer who presses it sees "Couldn't reach
the session service". Pick one before uploading:

- **Deploy the relay first, then submit (recommended).** The button works for
  the reviewer, and the release notes and listing can say so.
- **Hide it behind a setting** for this build and ship sessions in 1.9.1. Not
  done in code yet; it is a small change if chosen.

Also check before submitting with sessions on: the export compliance answer
(sessions add AES-GCM end to end encryption, not only HTTPS) and whether the
App Privacy table needs a line for profile text passing through the relay.

## Gates, before either upload

1. **Name (APPSTORE.md "HARD GATE")**: cleared. The app is live as
   "Quadstick: Config Manager" (Mac App Store id6791035050, Microsoft Store
   9PPQZQNL4WKP), and the vault Drawer marks the name aligned with what Fred
   announced publicly as done. Nothing to do for 1.9.0.
2. **Real QuadStick, sandboxed build.** Nobody has run 1.9.0's console code on
   a real stick (commit f3525fe2). Install the TestFlight build (a Mac App
   Store build will not launch from a folder, launchd error 163) and check:
   - Install through the folder picker: choose the QuadStick drive, install a
     profile, the backup is made, the stick reads the new file.
   - Serial console: Device settings > the console page, pick the Bluetooth
     or cable port, Send profile, then the strength test. The entitlement is
     `com.apple.security.device.serial`.
   - HID live input: sip and puff, the matching row lights up.
   If HID shows nothing in the sandbox, the likely missing entitlement is
   `com.apple.security.device.usb`. It is not in
   `entitlements-app.plist` and must not be added until this test shows it is
   needed: an entitlement the app does not use is a review question.
3. **Windows, same three checks** on the Store build (it runs full trust, so
   only the console is new).
4. Push `finalize-1.9` and merge to main. Only Bassam pushes.

## Mac App Store

1. `make store-mac VERSION=1.9.0`
   Signs with the Apple Distribution and 3rd Party Mac Developer Installer
   identities in the keychain and writes
   `dist/store/QuadStickConfigManager-1.9.0.pkg`. Needs
   `scripts/appstore/embedded.provisionprofile` and
   `src/QuadStick.App/GoogleClient.Local.cs` (both gitignored).
2. Open Transporter, drag the .pkg in, Deliver.
3. App Store Connect > the app > + Version > 1.9.0. When the build has
   processed, attach it under Build.
4. Paste:
   - **What's New**: "Follow a Google Sheet: a profile imported from a Sheets
     link can get the latest version, and the app tells you when the sheet
     changes. Nothing is replaced until you say yes. Send a profile and
     measure sip and puff over the Bluetooth or cable console. Fixes for the
     Xbox controller bridge and clearer warnings."
     Add only once the relay is deployed: "Share with my clinician: a
     clinician can see your profile live and suggest changes, which you
     accept or decline."
   - **Promotional text**: "Edit, check and install QuadStick profiles
     safely, with backups every time."
   - **Description**: keep the current one. If it is rewritten, it must not
     mention clinician remote sessions until the relay is deployed.
5. **App Privacy**: the table in `APPSTORE.md` under "App Privacy". Short
   version: Yes, data is collected. Device ID, Product Interaction, Crash
   Data, Health (strength test), Customer Support. All not linked to the
   user, none used for tracking. Not "Data Not Collected".
6. **Export compliance**: before sessions, encryption was only HTTPS through
   the OS: exempt. `scripts/store-mac.sh` answers
   `export_compliance_uses_encryption: false` and `make-macos-app.sh` writes
   `ITSAppUsesNonExemptEncryption` false. Sessions add AES-GCM; recheck both
   (see the decision above).
7. **Review notes** (paste):
   "This is an accessibility tool for the QuadStick, a sip and puff game
   controller for quadriplegic players. It edits profile files. You do not
   need the device: press New profile, change any cell, then Install to
   QuadStick and choose any empty folder. The app writes the profile there
   and makes a backup. Google Sheets import works with any shared sheet link.
   The console page needs a QuadStick and shows a plain message without one."
   If the relay is deployed, add: "Share with my clinician starts an end to
   end encrypted session with a clinician's web page. To try it, open the
   join link the app shows (https://clinic.bassamkamal.dev/#join=<code>) in
   a browser. Nothing is written unless the patient presses Use this."
8. Submit for review.

## Microsoft Store

The .msix has to be packed on Windows. There is no local Windows build on this
Mac, so the CI artifact is the drag and drop file.

1. After the push, GitHub > Actions > **Windows Store package** > Run workflow,
   branch main, version `1.9.0`. (Or `scripts/store-windows.sh 1.9.0`, which
   runs the same workflow and pulls the file into `dist/`.) The workflow builds
   whatever is on GitHub, so it cannot see local commits.
2. Download the `msix` artifact: `QuadstickConfigManager-1.9.0.msix`. Its
   Identity Version is set to 1.9.0.0 by the workflow and in
   `packaging/windows/Package.appxmanifest`.
3. Partner Center > the app > Start update > **Packages** > drag the .msix in.
4. **Store listings** > What's new: the same text as the Mac.
5. **Properties / Age ratings / declarations**: `packaging/windows/STORE.md`
   under "Privacy answers". Privacy policy URL:
   https://bbrizly.github.io/Quadstick-Config-Manager/privacy.html
6. **Notes for certification**: the Mac review notes, plus: "runFullTrust is
   needed to read and write profile files on a USB drive and to open the
   QuadStick's serial port."
7. Submit to the Store.
