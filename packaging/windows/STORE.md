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

## Listing images

`python3 tools/store-icons.py` renders every Windows icon from
`docs/QSLogo.png`. It rewrites `Images/` in place and writes the listing set to
`dist/store-images/`. Upload these under **Store logos** and **Store display
images**:

| Partner Center field | File |
|---|---|
| 9:16 Poster art 720x1080 | `poster-720x1080.png` |
| 9:16 Poster art 1440x2160 | `poster-1440x2160.png` |
| 1:1 Box art 1080x1080 | `boxart-1080x1080.png` |
| 1:1 Box art 2160x2160 | `boxart-2160x2160.png` |
| 1:1 App tile icon 300x300 | `tile-300x300.png` |
| 1:1 150x150 | `tile-150x150.png` |
| 1:1 71x71 | `tile-71x71.png` |

Poster art is the main logo on Windows 10/11, so it is not optional in
practice. The mark is white on `#0F1216`, the app's own dark background, so it
reads on a light or dark Store card. The old black-on-transparent logos
disappeared on both.

Inside the package the shell picks its own theme: `_altform-unplated` is the
light mark for a dark taskbar, `_altform-lightunplated` the dark mark for a
light one. The workflow copies `Images/` whole, so new sizes ship on their own.

## Review notes (same as Mac)

Reviewer has no QuadStick device. Say: testable without hardware via
File > Open the sample CSV, edit, validate, Install to any folder (a USB stick
or empty folder stands in for the device).
