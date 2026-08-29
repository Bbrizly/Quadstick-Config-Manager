# Tauri capabilities and permission plan

## Default-deny posture

Tauri 2 capabilities determine which WebViews/windows may invoke plugin/core commands. Official Tauri guidance supports narrowly scoped capabilities. QCM should grant **less** to the frontend than many example apps because native domain commands wrap privileged work.

## Main window capability

Expected frontend permissions:
- only core window/webview functions actually used (minimize, maximize, close, drag where required);
- application metadata if not already provided by `get_app_snapshot`.

Expected **not granted** directly to WebView:
- filesystem plugin write/read scopes;
- shell execution;
- generic opener URLs;
- arbitrary HTTP client;
- process execution;
- updater install/check if domain commands wrap it;
- keychain/stronghold access;
- HID/serial native plugins.

## Why Rust command wrapping still matters

Custom Tauri commands are native code and must validate requests themselves. A capability saying the main window can call `commit_install` does not mean every target is valid. `commit_install` resolves opaque IDs and revalidates marker/path/confirmation.

## Window labels

If future secondary windows are introduced (e.g. OAuth helper is **not** one; OAuth uses system browser), grant capabilities per window label. A component gallery/dev window should have no production native write permissions.

## CSP

Production CSP goals:
- no inline/eval scripts unless build system absolutely requires a hashed exception;
- no remote script/style origins;
- image/font origins restricted to packaged resources/data only where necessary;
- remote API communication ideally zero from frontend because Rust owns Google/community/update/telemetry network;
- frame/object embedding disabled;
- external links opened outside WebView.

Write the exact Tauri/WebView-compatible CSP after scaffold generation and test all platforms; do not copy a browser-only CSP that breaks Tauri IPC.

## Protocol/API allowlist

No arbitrary `fetch` to local file/custom protocols. Any custom scheme must have one documented resource purpose and traversal tests.

## Review checklist

- [ ] enumerate `src-tauri/capabilities/*.json`;
- [ ] enumerate plugins in Cargo/config;
- [ ] match each permission to API ledger/feature;
- [ ] verify frontend imports cannot invoke unwrapped privileged plugins;
- [ ] package release config separately from dev-only permissions;
- [ ] test malicious/invalid IPC requests directly, not only through UI.