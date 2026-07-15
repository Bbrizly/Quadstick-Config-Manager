# Microsoft Store runbook

The Mac side is scripted in `scripts/appstore/`. This is the Windows twin: it
produces the `.msix` you upload to the Microsoft Store. You never need a Windows
machine; the build runs on a cloud Windows runner in GitHub Actions.

## One-time setup

1. Sign up at partner.microsoft.com (Windows / Microsoft Store program). One
   time fee, not yearly. Individual account is fine.
2. Reserve the name: **Quadstick: Config Manager**.
3. Open the reserved app > **Product identity**. Copy these two values into
   `packaging/windows/Package.appxmanifest`:
   - **Package/Identity/Name** > the manifest `Identity Name`
   - **Publisher (CN=...)** > the manifest `Publisher`
   Commit that change. These are the only hand-filled values.

## Build the package

Actions tab > **Windows Store package** > Run workflow > enter the version
(e.g. `1.0.0`). When it finishes, download the `msix` artifact.

The Store signs the package during certification, so no code-signing
certificate is needed on our side.

## Submit

Partner Center > your app > **Packages** > upload the `.msix`. Fill the listing
(reuse the Mac copy from `scripts/appstore/APPSTORE.md`): description, the same
1440x900 screenshots in `appstore-assets/screenshots/`, category Utilities,
price Free, privacy policy URL
https://bbrizly.github.io/Quadstick-Config-Manager/privacy.html . Submit.

## Review notes (same as Mac)

Reviewer has no QuadStick device. Say: testable without hardware via
File > Open the sample CSV, edit, validate, Install to any folder (a USB stick
or empty folder stands in for the device).
