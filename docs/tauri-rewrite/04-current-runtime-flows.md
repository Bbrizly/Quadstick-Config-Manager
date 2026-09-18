# Current runtime flows

## Startup

```text
Program.Main
→ InstallNativeLibraryFallback (macOS Skia workaround)
→ BuildAvaloniaApp
→ App.Initialize (XAML + Style registration)
→ App.OnFrameworkInitializationCompleted
→ CrashGuard.Install before windows
→ Settings.Load
→ Localization.Apply + Theme.Apply
→ MainWindow (or --gallery build-tool window)
```

Target parity note: crash handling must be established before user feature work starts; the `--gallery` path is tooling, not necessarily product parity.

## Open/import profile

```text
picker/import/community/Drive/device source
→ bytes/text or XLSX import
→ ProfileFile.Load / normalization as appropriate
→ Parser.Parse + Validator.Validate
→ ProfileDocument + Issues while raw Grid remains preserved
→ MainWindow/editor state
→ Device/List/Rail UI + issue UI
```

Target: Rust creates an opaque `ProfileSessionId` and returns `EditorSnapshot`; TS never becomes the canonical CSV model.

## Edit

```text
UI action
→ ProfileFile mutation (SetCell/SetOutput/AddBindingRow/mode operations/...)
→ raw Grid snapshot for undo
→ Dirty/Revision update
→ Reparse
→ document + issues refresh
→ redraw affected UI
```

Target: `apply_editor_ops(session, expected_revision, ops[])` executes equivalent Rust mutations atomically and returns a new snapshot/revision.

## Save local

```text
user save
→ serialize `ProfileFile.ToCsvText`
→ device-safe A..J trim/newline cleanup + header/sheet normalization
→ ProfileFile.WriteAtomic (.qscm-tmp → move overwrite)
→ update save/source state
→ optional async Google backup push
```

Characterize exact normal-save behavior before changing it; device install is stronger than ordinary local save.

## Install to QuadStick

```text
Install click
→ reparse + reject validation errors
→ Device.FindCandidates
→ choose candidate or explicit folder
→ Device.IsInstallTarget (must contain default.csv)
→ special confirmation for default.csv or prefs.csv
→ background Device.Install
→ filename/validation/root rechecks
→ backup existing target
→ clone/normalize profile
→ write .qscm-tmp
→ exact read-back verify
→ move temp over target
→ restore/diagnose nuanced mid-swap failures
→ accessible receipt + telemetry
```

## Live input

```text
LiveInput.Start
→ background blocking HidSharp read loop
→ enumerate known QuadStick VID/PID devices
→ identify gamepad top-level HID usage from report descriptor
→ parse axes/buttons from descriptor values
→ normalize X/Y and suppress jitter/duplicates
→ marshal changed LiveState to Avalonia UI
→ diagram/practice highlighting
```

Native Xbox 360/XInput mode is explicitly excluded by this HID path.

## Google backup

```text
setting/sign-in
→ system browser OAuth 2.0 installed-app + PKCE
→ random loopback callback + state validation
→ exchange code
→ refresh token to Keychain(macOS) / DPAPI file(Windows)
→ DriveBackup + DriveClient REST operations
→ push after save / retry dirty linked profile
→ conflict decision may preserve local to CrashGuard rescue before loading online
```

## Shutdown

Must be fully characterized during Phase 0 for: live-input cancellation/handle close, pending backup task, telemetry bounded flush, settings persistence, crash guard and update state. Do not infer shutdown correctness solely from process exit.