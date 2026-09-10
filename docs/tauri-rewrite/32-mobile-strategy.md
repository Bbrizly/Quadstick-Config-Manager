# Mobile strategy — Phase 12, separate from desktop cutover

## Principle

Tauri mobile support proves UI/native plugin plumbing exists; it does **not** prove iOS/Android can talk to QuadStick using desktop storage/HID/Classic-Bluetooth assumptions.

## Shared pieces

Likely reusable:
- React feature/component layer (with responsive adaptations);
- TypeScript QcmClient contract;
- `qcm-config` pure Rust core;
- much of `qcm-core` policy;
- fixtures/tests/localization/design tokens.

Likely platform-specific:
- USB/HID access;
- mass-storage access;
- Bluetooth Classic/BLE;
- secure store;
- OAuth redirect handling;
- background lifetime;
- file picker/share sheets.

## Native plugin boundary

Official Tauri 2 mobile plugin architecture supports Swift on iOS and Kotlin/Java on Android. If direct hardware access is viable:

```text
React
→ QcmClient
→ Tauri command
→ qcm-core port
→ Tauri mobile plugin
→ Swift/Kotlin OS API
→ QuadStick
```

Keep the port interface same; swap native adapter.

## Required feasibility spikes

### iOS
- Does current QuadStick expose an Apple-supported External Accessory/BLE path usable by third-party app?
- Can iOS access its USB HID/mass-storage interfaces in a way QCM needs?
- Which file/document APIs can safely round-trip configs?
- Background connection limitations?

### Android
- USB Host permission/device filter for QuadStick interfaces;
- HID raw report access versus OS claiming gamepad;
- mass-storage/document-provider access;
- Bluetooth Classic SPP behavior/version restrictions;
- reconnect/background limitations.

Do not write production mobile transport until a physical-device proof exists.

## Desktop-bridge fallback

If direct mobile access is infeasible, a legitimate architecture is:

```text
mobile QCM UI
→ authenticated local LAN/BLE bridge protocol
→ desktop QCM native service
→ QuadStick storage/HID/serial
```

This is a separate product/security design: pairing, authentication, replay protection, discovery and permission model required. Do not add a localhost/network server to desktop QCM preemptively.

## Mobile gate

Desktop rewrite release is not blocked by mobile. Mobile begins only after desktop core/API stabilizes and hardware feasibility report classifies each required capability as proven/unsupported/bridge-required.