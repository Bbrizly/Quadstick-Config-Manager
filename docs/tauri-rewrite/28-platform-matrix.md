# Platform capability matrix

Legend: **P** proven/current behavior; **T** technically expected but must be tested; **N** needs native adapter/spike; **X** unavailable/not target; **?** unresolved.

| Capability | Windows | macOS | Linux | iOS | Android | Browser |
|---|---:|---:|---:|---:|---:|---:|
| Tauri desktop shell | T | T | T | — | — | X |
| React shared UI | P/T | P/T | P/T | T | T | P |
| Rust pure config core | T | T | T | T | T | WASM possible, not required |
| mounted QuadStick mass storage parity | P | P | P/current app claims | X/N | N/? | File System Access optional, X for parity |
| HID gamepad live reader | P current .NET | P | P current target | X/N | N | WebHID optional, not parity |
| serial console | ? | ? | ? | likely restricted/N | N | Web Serial optional |
| secure Google refresh-token store | P DPAPI | P Keychain | X today / N target decision | N Keychain | N Keystore | X for local-first desktop token |
| Google OAuth loopback system-browser | P | P | X today by policy | mobile redirect model differs | mobile redirect differs | standard web OAuth differs |
| signed auto-update | T | T | T | store-managed | store-managed | deploy/web |
| code signing/notarization | signing | signing+notarization | package signing optional | App Store | Play Store | n/a |
| hotplug mounted storage | polling P | polling P | polling P | n/a | USB APIs N | n/a |
| WebView accessibility | test | test | test/WebKitGTK variance | test | test | browser-dependent |

## Desktop support priority

1. Windows x64 — current primary packaging + Store workflow.
2. macOS arm64 + x64 — current release artifacts and Keychain/Drive support.
3. Linux x64 — current release artifact, but Google backup intentionally unavailable today; WebKitGTK packaging/AT testing required.

## Mobile reality

Tauri supports native mobile plugin boundaries (Swift/Kotlin), but that does **not** grant desktop-style access to QuadStick interfaces. Mobile is Phase 12 and cannot weaken desktop design. See `32-mobile-strategy.md`.