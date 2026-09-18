# Linux implementation

## Scope

Current CI publishes Linux x64 tar.gz. Target should define a supported distro/runtime baseline rather than imply every Linux desktop works identically.

## Tauri/WebKitGTK dependencies

At implementation time, use current official Tauri Linux prerequisite/package documentation for exact WebKitGTK/GTK packages. CI builds on supported Ubuntu runner and packaged smoke tests on at least one clean supported distro image/VM.

## Storage

Characterize QuadStick mount locations under common desktop automounters and permissions. Do not hardcode `/media/$USER` or `/run/media/$USER`; enumerate mounted filesystems/volumes then marker-check.

## HID permissions

`hidapi` backend may depend on hidraw/libusb and udev permissions. Determine whether standard desktop session can read the exposed gamepad interface without custom rule. If a udev rule is required:
- ship/document narrowly scoped VID/PID rule;
- do not grant world-writable unnecessary device access;
- package install/uninstall cleanly.

## Serial

Only if `OQ-001` proves required. Document `dialout`/udev implications; do not make users change groups for an unused feature.

## Google secure store

Current app disables Google integration on Linux because fallback token store is in-memory. Target choices:
1. preserve “unavailable” parity initially;
2. add Secret Service/libsecret/keyring integration as an intentional B improvement after reliability testing across GNOME/KDE/headless sessions.

Do not store refresh token plaintext to achieve feature parity.

## Packaging

Evaluate AppImage vs `.deb`/RPM based on Tauri current support and HID/udev integration. A tarball alone may remain beta artifact, but production should have explicit dependency installation and desktop integration.

## Accessibility

Test keyboard and at least one AT-SPI/Orca-compatible environment where feasible. Document WebKitGTK-specific limitations and supported desktop assumptions.