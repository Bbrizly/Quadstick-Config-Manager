# Rollback plan

Rollback is a release operation, not “git revert and hope.”

## P0: config serializer/data corruption discovered

1. Immediately withdraw/disable Tauri update manifest for affected version.
2. Publish advisory: do not install/write device configs with affected version.
3. Keep previous stable Avalonia/Tauri artifact available.
4. Never auto-rewrite files back. Use off-device backup/recovery receipt to restore only with explicit user action.
5. Add failing fixture/hardware reproduction before fix.
6. Release fixed version only after oracle + hardware verification.

## Device detection regression

User can close Tauri and use previous Avalonia stable. No settings conversion should block this. Do not “fix” by letting frontend choose arbitrary drive paths without native validation.

## HID/live-input regression

This is not permission to disable write-safety. Disable live feature/capability for affected platform/version if necessary while profile editing/storage remains functional; release patch after hardware mode tests.

## WebView regression

Provide previous installer and known-good version. If platform WebView update causes widespread issue, investigate supported Tauri/WebView mitigation; no remote hosted replacement UI gets privileged native access as emergency shortcut.

## Accessibility regression

Block updater rollout/publish patch. Keep previous accessible stable available. Do not waive keyboard/screen-reader regression because config bytes are correct.

## macOS signing/notarization failure

Do not publish unsigned substitute. Fix signing/notarization, verify Gatekeeper. Previous version remains download.

## Windows signing/installer failure

Do not suggest users disable SmartScreen/security. Rebuild properly signed artifact; keep previous release.

## Linux packaging failure

Remove affected package from recommended downloads, keep previous tar/package; document supported distro dependency issue and republish fixed artifact.

## Rollback data guarantee

Rollback must not require understanding Tauri internal settings. User's real asset is standard QuadStick profile files/backups, preserved independently. Beta app-data remains separate until cutover maturity.

## Repository rollback

Migration commits are ordinary fast-forward history. Revert feature commits; do not force-push shared branches. Legacy source remains until Phase 11.