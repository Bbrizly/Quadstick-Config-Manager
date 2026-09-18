# Platform capabilities

| Area | Windows | macOS | Linux | Evidence/gate |
|---|---|---|---|---|
| Mass storage current app | yes | yes with `/Volumes` heuristic | shipped/needs target characterization | Device.cs + hardware Gate 3/8 |
| HID live current app | HidSharp | HidSharp | HidSharp shipped | LiveInput; Rust TASK-026 |
| XInput mode | current HID limitation | n/a mode behavior | n/a/varies | explicit capability |
| Secure Google token current | DPAPI | Keychain | unavailable/in-memory fallback | TokenStore |
| Tauri WebView | WebView2 | WKWebView | WebKitGTK | packaged smoke |
| Signing | Authenticode | codesign/notary | package-specific | Phase 8 |
| Updater | signed Tauri artifact | signed Tauri + OS-signed app | signed Tauri artifact | TASK-047 |
| AT | NVDA/Narrator | VoiceOver | AT-SPI/Orca target smoke | TASK-049–052 |
| Serial | OQ | OQ | OQ | TASK-029 |
| Google OAuth | system browser/loopback | same | current unavailable due token persistence | TASK-044 |
| Store | existing workflow OQ | not current | n/a | OQ-008 |