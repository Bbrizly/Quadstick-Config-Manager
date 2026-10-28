# iOS App Store release record

This file is the source for the iOS listing and review notes. The App Store
Connect listing and the review outcome are external state and must be checked
against this file for each submission. This record was updated after checking
the live App Store Connect record on 13 October 2026.

## Identity

- App Store Connect name (saved 13 October 2026) and current iOS display name:
  **QuadStick Config Manager**. App Store Connect says the change will release
  with the next app version.
- Current App Store Connect subtitle (saved 13 October 2026): **Edit game
  controller profiles** (29 characters). App Store Connect says the change will
  release with the next app version.
- The rejected listing's subtitle was “QuadStick profiles on iPhone”. Keep
  “iPhone” out of revised metadata.
- The owner’s iOS page source says the app was submitted as
  **Quadstick: Mobile Helper**. App Store Connect
  now confirms this is the existing iOS record, app ID `6804945166`, bundle ID
  `dev.bassamkamal.sipstudio`. Version 1.0 (build 1) was rejected on 15
  September 2026. Update this record; do not create a duplicate or change its
  bundle ID. See the [page source](https://github.com/Bbrizly/Bbrizly.github.io/blob/main/sipstudio/index.html)
  and [latest page change](https://github.com/Bbrizly/Bbrizly.github.io/commit/20315d4389d76631cbff43a80f07d9748ce33a8f).
- The current iOS target uses bundle identifier
  `dev.bassamkamal.sipstudio`, inherited from the previous iOS project and
  confirmed against the iOS record. It differs from the separate, Mac-only
  [Mac listing](https://apps.apple.com/ca/app/quadstick-config-manager/id6791035050?mt=12),
  titled **QuadStick: Config Manager**. The bundle ID is not the displayed
  product name.
- The name uses QuadStick's trademark. Obtain and retain written authorization
  from Fred Davison before submitting the listing, as required by the existing
  [App Store runbook](../scripts/appstore/APPSTORE.md). No written approval was
  found in this repository; do not represent it as obtained until it is recorded.

## Description

The checked-in OAuth example is empty, so builds made from repository defaults
do not include Google Drive. The description below is for that no-Drive build.
If Drive is enabled in the submitted archive, revise the listing and reviewer
notes to describe the optional Drive features, then test them using that exact
archive. Keep the public Google Sheets link import claim: that read-only import
does not require OAuth.

Edit a QuadStick game profile from a sample or your own file. Review controller
inputs, change assignments, organize modes, and check profiles for configuration
issues before export.

Open a CSV file or import a public Google Sheets link. Profiles remain on your
device. Export a CSV through Files when you are ready to move it to a QuadStick
drive.

Sample profiles let you explore without a controller. Profile editing,
validation, import, and CSV export are free. Optional tips unlock no features.

QuadStick Config Manager is an independent, accessibility-focused profile
editor for people who use a QuadStick and those who help configure one.

## App Review notes

The following review notes were saved in App Store Connect on 13 October 2026;
check them against the final signed build before resubmitting:

> QuadStick Config Manager does not require an account or controller for
> review. The app opens on the supplied Fortnite sample profile.
>
> Open Modes, QuadStick Settings, and Review Controls to inspect the editor.
> The home screen validation status opens the profile findings. Use Profiles to
> switch profiles or create, duplicate, import, or delete one.
>
> Install to QuadStick exports a CSV through the Files picker. Choose On My
> iPhone or a connected drive to review the export; no hardware is required.
>
> The optional tip purchases unlock no features. Google Drive is disabled in
> this build.

The current build has Google Drive disabled because no OAuth client is
configured. If Drive is enabled in the submitted build, provide a valid review
account and explain that Drive is optional for the core review; a share link
permits anyone who has it to view the spreadsheet. The tip jar is optional and
unlocks nothing. StoreKit sandbox purchases can be used to check the purchase
flow.

Contact: use the developer support link on the App Store listing.

## Other App Store fields — draft for the no-Drive build

- Primary language: English (Canada), as shown in App Store Connect. Localize
  the listing only after translated metadata and screenshots have been reviewed.
- Promotional text saved 13 October 2026 (151/170 characters): “Edit a
  QuadStick profile, check its mappings, and move it with CSV. Import a file or
  public Google Sheet; explore sample profiles without a controller.”
- Keywords (83/100 UTF-8 bytes): `gaming,controller,assistive,accessibility,CSV,spreadsheet,sip,puff,mapping,adaptive`.
  Review against the final name, subtitle, and Apple's search rules before saving.
- Category: Utilities. Price: Free. Answer the age-rating questionnaire from
  the submitted binary; do not set an age rating from the desktop listing.
- Proposed Support URL:
  `https://bassamkamal.dev/Quadstick-Config-Manager/ios-support.html`.
  The page links to GitHub Issues and the iOS privacy policy. It must be
  published and opened successfully at this URL before entering it in
  App Store Connect.
- Public URL check on 13 October 2026: the proposed support URL returns a
  GitHub Pages 404. The proposed `ios-privacy.html` route loads the old
  “SipStudio for iPhone” policy dated 11 September 2026. Do not enter either
  replacement URL until the updated pages are published and verified.
  The owner’s iOS-specific page also provides support at
  `https://bassamkamal.dev/sipstudio/support.html`. The existing iOS policy is
  `https://bassamkamal.dev/sipstudio/privacy.html`; it is branded SipStudio and
  describes the no-Drive app. Reuse those routes only after the page names and
  disclosures match the exact submission.
- Proposed replacement Privacy Policy URL from the canonical URL in
  `docs/ios-privacy.html`: `https://bassamkamal.dev/Quadstick-Config-Manager/ios-privacy.html`.
  It is not ready for App Store Connect until the corrected site is published
  and the public route shows the checked-in policy.
- Leave the Marketing URL unset until the iOS landing page matches the submitted
  name and features. The current source for the [SipStudio page](https://bassamkamal.dev/sipstudio/)
  in `Bbrizly.github.io` was last changed on 12 September 2026; it identifies
  the app as “Quadstick: Mobile Helper” and says it is awaiting App Review.
  This is not App Store Connect evidence. Check the live submission and update
  the page before reusing it for a renamed listing.
- The app uses three optional consumable tips through RevenueCat. Confirm each
  matching in-app purchase product, availability, localization, and review
  information in App Store Connect; product setup is external to this repository.
- Copyright: `© 2026 Bassam Kamal` (confirm the seller's preferred legal name).

Apple currently documents a 30-character limit for the name and subtitle, 170
characters for promotional text, 4,000 for the description, and 100 bytes for
keywords. See [App Store Connect platform version information](https://developer.apple.com/help/app-store-connect/reference/app-information/platform-version-information)
and [app information](https://developer.apple.com/help/app-store-connect/reference/app-information/app-information/).

## App Privacy answers to verify in App Store Connect

The current implementation links RevenueCat and optionally sends user-selected
profile contents to Google Drive. Do not select “Data Not Collected.” Apple
requires disclosure of data practices of third-party SDKs as well as the app
itself. Confirm every answer against the exact archive and current SDK
configuration and the selected App Store Connect app record before submission.
Apple's privacy responses apply across platforms in one app record. If iOS is
added to the existing Mac app, include the Mac build's Drive behavior; if iOS
uses a separate record and the archive has an empty `GIDClientID`, do not
describe Drive data collection for that iOS build. See [Apple's App Privacy Details guidance](https://developer.apple.com/app-store/app-privacy-details/),
[App Review](https://developer.apple.com/app-store/review/), and
[RevenueCat's Apple privacy questionnaire guide](https://www.revenuecat.com/docs/platform-resources/apple-platform-resources/apple-app-privacy).

- **Purchases / purchase history:** collected by RevenueCat for app
  functionality and its purchase reporting; not used for tracking. RevenueCat
  says this category is required when its SDK is used.
- **User Content / other user content:** profile data is sent to Google only
  when the user chooses Drive backup or sharing. Describe this as app
  functionality. Confirm whether the App Store Connect questionnaire treats
  the chosen Google workflow as collection under Apple's definitions.
- **User ID:** Google Drive uses the signed-in Google account to associate
  backups and spreadsheets. Declare the account identifier for app
  functionality if required by the questionnaire. The app does not ask Google
  for the account email profile scope.
- **Identifiers:** the RevenueCat SDK uses an anonymous app user identifier.
  Verify the archived SDK settings and answer the linked-to-identity question
  from that behavior. Do not claim “not linked” if an identifier is associated
  with a Google account or other identifying data in the submitted build.
- RevenueCat's pinned SDK privacy manifest in version 5.89.0 declares purchase
  history for app functionality, UserDefaults access for reason `CA92.1`, and
  no tracking. Its current privacy questionnaire guidance also describes
  purchase history reporting as analytics; reconcile the manifest, SDK
  behavior, and App Store Connect answers against the archived dependency.
- **Tracking:** the app does not implement advertising or cross-app tracking.
  Check the dependency privacy manifest and all RevenueCat integrations before
  asserting this in App Store Connect.

The public policy is [docs/ios-privacy.html](../docs/ios-privacy.html). Its
published URL, the App Store Connect privacy answers, the listing copy, and the
archived binary must describe the same behavior.

## Local preflight — 13 October 2026

- A fresh Release build for generic iOS Simulator succeeded from the current
  source with stable Xcode 27.0 and the iOS 27.0 SDK. It compiled `QuadStickKit`
  and RevenueCat 5.89.0. This was an unsigned simulator build, not an archive.
- A generic iOS device Release archive also succeeded with Xcode 27.0 and the
  iOS 27.0 SDK. A fresh build 2 archive is now at `/private/tmp/QCM-build2.xcarchive`.
  Its app has the expected display name, bundle ID, version/build, and empty
  `GIDClientID`; Xcode signed it with Apple Development and a development
  provisioning profile, so it cannot be uploaded to App Store Connect.
- The build 2 archive reports display name **QuadStick Config Manager**, bundle
  ID `dev.bassamkamal.sipstudio`, version `1.0` (build `2`), and supports
  iPhone and iPad. The RevenueCat privacy manifest is present in its package
  bundle.
- The project now targets version `1.0` (build `2`) in Debug and Release, the
  next build number after Apple's rejected build 1. The archive confirms its
  `CFBundleVersion`.
- `GIDClientID` is empty in this build, so Google Drive is disabled. Keep the
  conditional Drive copy out of the submitted listing unless the OAuth values
  are configured and verified in the signed archive.
- On the current worktree, both Google OAuth settings in `ios/Google.xcconfig`
  are empty (checked without printing their values). Google Drive remains
  disabled for a build from these settings.
- `QuadStickKit`'s 105 Swift tests passed in the current follow-up run. The Rust
  bridge's 13 tests and Clippy passed; its Linux target check passed, and `--replay`
  decoded synthetic reports through neutral, pressed, and disconnect-release
  states. After setting the missing `IsTestProject` property, all 1,039
  desktop app tests passed, including the 20 controller bridge cases.
- CoreSimulator and CoreDevice services were unavailable for launch/device
  testing. No trusted App Store distribution archive, physical iPhone/iPad
  accessibility check, App Store validation, or physical QuadStick/game test
  has been completed.
- The local keychain currently has no valid code-signing identities. The Apple
  Developer portal lists an active iOS Distribution certificate expiring
  15 June 2027, but its private key is not available as a valid identity on
  this Mac. Xcode's certificate manager lists only an Apple Development
  certificate. Two App Store Connect API key files exist locally, but no issuer
  ID is configured in the current environment. The development-signed archive
  is not a release artifact.

## App Review outcome — 15 September 2026

- App: **Quadstick: Mobile Helper**, iOS app ID `6804945166`, version `1.0`
  (build `1`), submission ID `fe2483bf-ce36-47ec-ad41-0f65e8e7dbf8`.
- Apple rejected the version under guidelines 2.3.8, 5.2.1, and 5.2.5.
- **2.3.8 Accurate Metadata:** the rejected marketplace name was “Quadstick:
  Mobile Helper”; the device name was “SipStudio”. On 13 October, the English
  (Canada) App Store Connect name was saved as “QuadStick Config Manager”,
  matching the checked-in `CFBundleDisplayName`. Verify the next archive and
  saved name before resubmission.
- **5.2.5 Apple Products:** the rejected subtitle mentioned “iPhone”. On 13
  October 2026 the English (Canada) App Information subtitle was saved as
  “Edit game controller profiles” (29/30 characters). It is queued for the
  next app version; verify it remains saved during resubmission.
- **5.2.1 Intellectual Property:** Apple requested documentary evidence in
  App Review Information showing authorization to include QuadStick content
  and market the app to control QuadStick hardware. The repository runbook
  also requires written authorization from Fred Davison. This remains a hard
  submission gate; no such authorization was found in the repository.
- The submission included Small, Medium, and Large Tip in-app purchases, each
  showing “Ready for Review”. Recheck their state when preparing the next
  submission.
- On 13 October 2026, App Store Connect still shows three **Drafts**, all
  “Ready for Review”: Small Tip (`dev.bassamkamal.sipstudio.tip.small`), Medium
  Tip (`dev.bassamkamal.sipstudio.tip.medium`), and Large Tip
  (`dev.bassamkamal.sipstudio.tip.large`). Verify the live RevenueCat offering
  returns these exact consumable products and exercise each purchase with a
  sandbox account before resubmission; the app loads the catalogue at runtime.
- These findings were read from the App Store Connect review submission page.
  Keep the full review message in the access-controlled App Store Connect
  record; this checklist summarizes it without personal account identifiers.

## App Store Connect metadata snapshot — 13 October 2026

The rejected version was submitted with SipStudio copy. This snapshot records
App Store Connect before the 13 October name, subtitle, promotional text,
description, and reviewer note updates. The Support URL remains stale and must
be replaced only after the corrected support page is publicly available.

- App Information localization is **English (Canada)**. Before the 13 October
  edits, the name was “Quadstick: Mobile Helper” and the subtitle was
  “QuadStick profiles on iPhone”. The subtitle is 28 characters and is the
  direct 5.2.5 rejection cause. The saved replacements are recorded above.
  Both use the QuadStick mark, so do not submit them until the requested
  written authorization is documented.
- The previous promotional text said “Set up your QuadStick from your phone”
  and mentioned “sip, puff and switch”. It was replaced with the saved text
  above, which avoids Apple device names.
- The previous 2,728-character description called the app SipStudio and said
  it installs over USB-C. It was replaced with the saved no-Drive description
  above. Review every claim against the exact signed build before resubmitting.
- Current keywords are
  `quadstick,sip and puff,mouth controller,adaptive gaming,accessibility,controller,profile,mapping`.
  Review against final name/subtitle, trademark authorization, and the 100-byte
  limit before resubmitting.
- Current Support URL is `https://sipstudio.bassamkamal-py.workers.dev/`.
  I opened it on 13 October 2026: it is live, but the page is titled
  “SipStudio”, says “QuadStick profiles on iPhone”, and links to
  `https://sipstudio.bassamkamal-py.workers.dev/privacy`. That privacy page is
  also titled “SipStudio for iPhone”, dated 11 September 2026, and describes
  local files, public Sheets import, and tip purchases. It does not use the
  current product name or the fuller, build-conditional disclosures in the
  checked-in `docs/ios-privacy.html`. Replace or update both destinations
  before resubmission; confirm the final published pages from the public URLs.
- The live App Privacy screen confirms its Privacy Policy URL is the same
  `/privacy` route on workers.dev. Its published label says **Purchases**, not
  linked to the user's identity, with the purposes **Analytics** and **App
  Functionality**. The current checked-in policy describes purchase history
  and transaction information as used for validation, reporting, and analytics,
  and says the SDK generates an anonymous app customer ID. That matches the
  current label at a high level. Verify the exact SDK behavior against the
  submitted archive before republishing these answers. RevenueCat's [Apple App
  Privacy guidance](https://www.revenuecat.com/docs/platform-resources/apple-platform-resources/apple-app-privacy)
  requires Purchase History for Analytics and App Functionality, and says
  anonymous RevenueCat IDs can be marked not linked when no other identity link
  exists. No User Privacy Choices URL is set.
- The rejected version's App Review notes still describe SipStudio. Their
  Fortnite sample reference matches the current first-launch sample in
  `QuadStickConfigManagerApp.swift` and `SampleData.swift`; update the old name,
  then recheck every reviewer step against the signed build.
- App Information currently answers **Yes** to having necessary rights to
  third-party content. Apple separately asked for documentary evidence that
  QuadStick content and hardware-control claims are authorized. Attach the
  requested evidence in App Review Information and ensure the answer is
  truthful and supported before resubmission.
- App Review Information showed contact phone/email and did not mark sign-in
  as required. Keep personal contact details in App Store Connect; do not copy
  them into this repository.

## Submission gate

- Record the next review case/build number and paste Apple's complete review
  message into the release issue or an access-controlled release record.
- Map each rejection item to a code or metadata change and verify that item in
  the resubmitted archive. The three rejection items above remain unresolved
  until revised metadata, a signed archive, and authorization evidence are
  submitted and accepted.
- Build and test the iOS target and `QuadStickKit` on a supported Xcode/macOS
  runner; verify the signed archive and App Store validation in Xcode/Transporter.
- The checked-in `Google.xcconfig.example` deliberately has no OAuth client.
  Before a listing or archive promises Google Drive, set `GOOGLE_CLIENT_ID` and
  its matching `GOOGLE_CLIENT_ID_REVERSED` in the release-only
  `ios/Google.xcconfig`. `Info.plist` expands these values into `GIDClientID`
  and the Google callback URL scheme. Verify both values in the built archive,
  then test sign-in, backup, share, restore, and revoke using that exact
  archive. Otherwise remove Drive backup/sharing availability and claims from
  that submission's listing and reviewer notes. Public Google Sheets link
  import remains available without OAuth.
- Check the live name, screenshots, privacy label, support URL, age rating,
  in-app purchase products, and reviewer notes in App Store Connect.
- The app targets both iPhone and iPad. Apple currently requires at least one
  iPhone Dynamic Island medium screenshot and one 13-inch iPad screenshot. The
  current iPhone 17 Pro capture (1206×2622) and iPad Pro 13-inch capture
  (2064×2752) meet those required sizes. Both are RGB PNGs without alpha. The
  iPad Pro 11-inch capture is a review reference only. Confirm the live size
  requirements in App Store Connect and retain the captures from the reviewed
  build. Additional listing screenshots can be added if useful. See [Apple's
  screenshot specifications](https://developer.apple.com/help/app-store-connect/reference/app-information/screenshot-specifications/).
  Do not submit the archived captures in
  `appstore-assets/legacy-sipstudio-iphone-6.9-do-not-submit/`; they show the
  old name and stale tip prices.
- Run VoiceOver and Dynamic Type checks on physical iPhone and iPad devices and
  retain the results with the submission record.

## Follow-up verification — 13 October 2026

- The Xcode project now uses build number `2` in both Debug and Release. The
  fresh archive verified version `1.0`, build `2`, bundle ID
  `dev.bassamkamal.sipstudio`, display name **QuadStick Config Manager**, and
  an empty `GIDClientID`.
- The desktop controller test project now declares `<IsTestProject>true</IsTestProject>`;
  without it, `dotnet test --no-restore` exited successfully without running
  tests. After restoring its NuGet dependencies, the full desktop suite passed
  1,039/1,039 tests with `--disable-build-servers -m:1`. VSTest's local socket
  is blocked by the default sandbox, so the test run used elevated execution.
- `swiftc -frontend -parse` passed for all iOS app, `QuadStickKit`, and
  `QuadStickKitTests` Swift files. The project file passed `plutil -lint`, the
  shared scheme passed XML parsing, and `git diff --check` passed.
- `swift test --package-path ios/QuadStickKit` passed all 105 tests after
  running outside the sandbox. The shared Xcode scheme has no app testables;
  Swift package coverage is not an iOS app/UI test run.
- Independent source review found that `ASWebAuthenticationSession.start()`'s
  Boolean result was ignored. If it returned `false`, Google sign-in could stay
  on “Opening Google...” indefinitely. `ios/App/DriveAccount.swift` now resumes
  the continuation with a retryable error when the session cannot start.
- An earlier SwiftPM attempt failed because sandbox setup was denied; the
  elevated retry above completed and passed all 105 package tests. A previous
  simulator build attempt also failed because CoreSimulator was unavailable
  and RevenueCat could not be fetched. The fresh generic-device archive below
  now provides a full app typecheck and compile.
- A fresh generic iOS device archive succeeded with stable Xcode 27.0 and the
  iOS 27.0 SDK at `/private/tmp/QCM-build2.xcarchive`. Xcode signed it using
  Apple Development and a development profile. `codesign --verify --deep
  --strict` returned `CSSMERR_TP_NOT_TRUSTED`, and `security find-identity`
  reports zero valid identities. Xcode Manage Certificates showed Apple
  Distribution creation disabled. A second archive forcing `Apple
  Distribution` failed because the project is configured for automatic
  development signing. The Apple Developer portal does list an active iOS
  Distribution certificate, but its private key is unavailable in this Mac's
  keychain; no uploadable App Store archive exists yet.
- Current Rust verification on macOS: 13 tests passed (9 report-decoder tests
  and 4 replay/send-state tests), and Clippy passed with warnings denied. A
  Linux x86_64 `cargo check` passed. This host run does not execute the
  Linux-only uinput tests.
- Ran the built `qs-bridge --replay` CLI with synthetic neutral and A-pressed
  reports; it printed neutral, pressed, and neutral-on-EOF states as expected.
- Replay-mode scope: it reads text lines of hex byte pairs, decodes the first
  14 bytes as a mode-2 x360ce report, and prints changed `Pad` states. It does
  not create or drive a virtual controller. Linux uinput event emission,
  physical QuadStick behavior, unplug handling, firmware accuracy, and
  game-level recognition still need Linux and hardware testing.
- Start/Back is not confirmed against a physical mode-2 controller: the Rust
  source comment says firmware bit 6 is Select, while the QMP reference calls
  that same bit Start. Do not treat the replay output as proof that this button
  mapping matches the device.
- `docs/ios-privacy.html` now explicitly describes RevenueCat purchase history
  for Analytics and App Functionality and the anonymous SDK-generated ID; the
  checked-in policy still needs to be published at the proposed URL and
  verified there.
- `git diff --check` passed after these changes.
