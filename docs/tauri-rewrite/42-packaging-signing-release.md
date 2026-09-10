# Packaging, signing and release

## Current release contract

Version tags `vX.Y.Z` trigger platform builds and GitHub Release. `make release VERSION=...` requires clean tree, valid semver, passing tests, then tags/pushes. Preserve this simple operator model where possible.

## Bundle identity during beta

Use a distinct beta identifier/name, e.g. `QuadStick Config Manager Beta`, with separate app-data directory and updater channel. This allows Avalonia stable and Tauri beta to coexist without corrupting each other's settings/session files.

Do **not** change the stable bundle identifier until cutover plan explicitly maps old user data.

## Windows

Target artifacts:
- signed Tauri-supported installer (MSI/NSIS decision at TASK release spike);
- optional Store package handled separately;
- signed executable/installer verification in CI;
- WebView2 bootstrap/runtime policy documented.

## macOS

- `.app` / signed distribution bundle from Tauri;
- Developer ID signing;
- hardened runtime/minimal entitlements;
- notarize and staple;
- produce updater artifact if enabled;
- arm64 + x64 until drop decision.

## Linux

Start with Tauri-supported AppImage/deb as appropriate; add RPM only when product support requires. Package WebKitGTK/runtime and HID/udev expectations honestly. Test installation/uninstall in clean VM/container where GUI feasible.

## Release notes

Continue `docs/release-notes/vX.Y.Z.md` human summary plus generated commit notes/download table. Rewrite release notes must distinguish:
- parity changes;
- intentional behavior changes;
- known hardware/platform limitations;
- beta rollback instructions.

## Update signing

Tauri updater signatures are a separate integrity layer from OS code signing. Official Tauri updater requires signatures; keep private updater key out of repository and CI logs. Public key lives in app config.

## Release verification checklist

Before publish:
- version consistent across `package.json`, Cargo/Tauri config and updater manifest;
- tests green;
- analytics source build/release-token behavior verified;
- no secret strings in bundle;
- executable signatures valid;
- macOS notarization passes;
- updater signature verifies;
- install/uninstall/open smoke on clean OS;
- real QuadStick install + readback + live input smoke;
- accessibility RC checklist signed off;
- previous stable artifact available.