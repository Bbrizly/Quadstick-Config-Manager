# Windows implementation

## Baseline

Current release builds Windows x64 self-contained .NET artifact; separate Windows Store workflow exists. Target needs both ordinary signed desktop distribution and a deliberate Store decision.

## Storage discovery

Phase 3 parity can use Rust/Windows filesystem volume enumeration equivalent to current `DriveInfo` filtering, then marker `default.csv`. Improve later with Windows volume/device APIs only if they add reliable identity/hotplug without regressions.

Record:
- volume root;
- display label;
- filesystem type if accessible;
- stable volume GUID/serial if available and safe;
- current generation/fingerprint.

Do not rely on drive letter as identity.

## Filesystem operations

Test on actual QuadStick filesystem:
- create temp;
- flush/sync behavior;
- overwrite rename/replace semantics;
- unplug during each stage;
- antivirus/file-lock interference;
- case-insensitive collisions;
- reserved names/trailing dot/space behavior.

## HID

`hidapi` Windows backend spike must verify every known current QuadStick/controller mode and report descriptor extraction. Native XInput-only mode remains explicit limitation unless a separate XInput adapter is intentionally added as B improvement.

## Secure token storage

Parity option A: retain DPAPI CurrentUser semantics in a small Windows adapter. Option B: use a mature cross-platform keyring crate backed by Windows Credential Manager after security/reliability evaluation. Do not migrate refresh tokens to plain Tauri settings.

Migration should read existing DPAPI token if practical or intentionally ask user to reconnect once; document UX.

## WebView2

Tauri Windows uses WebView2. Packaging spec must decide Evergreen bootstrapper/runtime policy using current Tauri recommendations. Packaged smoke tests cover first-run machine with current WebView2 and enterprise/restricted environment where practical.

## Signing

- Authenticode sign installer/binaries per Tauri-supported workflow.
- signing certificate/private key only in protected CI secret/HSM workflow;
- verify signature in release job before publishing.

## Store

Do not assume existing MSIX Store workflow maps automatically. Create a separate Store spike after ordinary installer parity. Preserve package identity/data migration only if Store release continues.

## Accessibility

Required manual: keyboard, NVDA, Narrator, contrast themes, 200/400%, Voice Access smoke. Test title-bar/custom window controls if used.