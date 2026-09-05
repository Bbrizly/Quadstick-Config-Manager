# macOS implementation

## Architectures

Current releases ship Apple Silicon and Intel. Target keeps arm64 + x86_64 until product data supports dropping Intel. Universal bundle may be considered after measuring CI/signing simplicity versus artifact size.

## Storage

Current .NET candidate logic explicitly tolerates fixed volumes only when rooted under `/Volumes`, because macOS removable reporting differs. Phase 3 Rust discovery must characterize mounted QuadStick behavior on real macOS before changing this heuristic.

Use volume identity/resource metadata where available; path under `/Volumes` is location, not permanent identity.

## HID

Verify permissions and report descriptor access on supported macOS versions. Avoid entitlements that are not actually needed. Test sleep/wake and unplug/replug.

## Keychain

Preserve refresh token in Keychain. If switching from current generic-password service/account (`QuadStick Config Manager` / `google-drive`), provide migration read or explicit reconnect. Never silently create two stale tokens.

## OAuth

Keep system browser + loopback installed-app flow while Google allows/supports it. Do not embed Google auth inside WKWebView.

## App bundle/signing

Target Tauri bundle replaces `scripts/make-macos-app.sh` only after parity:
- stable bundle identifier;
- Developer ID Application signing;
- hardened runtime as required;
- minimal entitlements;
- notarization + stapling;
- verify with `codesign`, `spctl` and notarization result in CI/release checklist.

## Updater

Signed update artifact is separate from Apple code signing. Protect updater signing key and preserve downgrade/rollback path by retaining previous release artifact.

## Accessibility

VoiceOver/keyboard/increased contrast/reduced motion/zoom tests. Validate WKWebView focus handling for dialogs, SVG hotspots and title-bar controls.

## Current startup workaround

`Program.InstallNativeLibraryFallback()` exists for .NET/Avalonia App Store bundle naming/Skia native library resolution. Classify as **E legacy implementation detail** unless an equivalent Tauri packaging issue is demonstrated; do not port it reflexively.