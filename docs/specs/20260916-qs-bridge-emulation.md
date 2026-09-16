# qs-bridge: controller emulation without QMP

Date: 2026-09-16. Status: plan, not started.

## Why

In mode 2 (x360ce) a QuadStick is a plain HID gamepad plus keyboard, mouse
and drive. XInput games see no controller. Mode 3 is a real Xbox 360 pad but
drops the keyboard, mouse and drive. QMP fixes mode 2 on Windows: it reads
the stick's report and feeds a virtual Xbox 360 pad (ViGEmBus), hiding the
real device (HidHide). DrTardis confirmed that is what makes his setup work,
not the x360ce program.

QMP is Windows only. DerPasi wants the same on Linux (2026-09-15, and offered
to test). That is the gap.

## What it is

One small headless Rust binary, `qs-bridge`. No UI, no settings file, no
tray. It does one loop:

1. Find the QuadStick in mode 2 (`16D0:092C`) with hidapi.
2. Read a report, decode it to one `Pad` value (sticks, triggers, buttons).
3. Write that `Pad` to a virtual Xbox 360 pad.
4. On unplug or read error: send a neutral `Pad`, drop the virtual pad, wait,
   look again.

Step 4 is the safety rule. A trigger or button left held on a virtual pad is
a controller somebody cannot let go of.

Mode 2 only. QMP also bridges modes 0 and 4 (PS3/PS4 reports), but mode 2 is
the one that keeps keyboard, mouse and drive, so it is the only one worth
bridging. Add the others if a user asks.

Lives in the rewrite workspace as `crates/qs-bridge`, depending on nothing
else in it, so it can ship before the Tauri app does. `hidapi =2.6.6` is
already pinned in `src-tauri`.

## Decode: the firmware is the oracle, not QMP

`decode(&[u8]) -> Pad` is pure and is where every test goes. The layout is
`USB_X360CE_Report_Data_t` in FW 2373 `Joystick/Descriptors.h:573`, filled at
`DataFlow.c:2733`: six little-endian u16 axes, then a button byte (square, X,
O, triangle, L1, R1, select, start from bit 0), then L3, R3, guide and a
4-bit D-pad.

QMP's `update_X360_with_X360CE` (fdavison/QMP-4, `ViGEmBus.py:327`) maps bit
6 to Start and bit 7 to Back. The struct says bit 6 is select and bit 7 is
start. One of them is wrong. Settle it on a device (press Start in a mode 2
profile, read the byte with Live Inputs) before writing the table, and tell
the QMP author if it is QMP.

Tests: one per button bit, axis extremes (0, 0x80, 0xFF, and QMP's 0x7F00 to
0x7FFF clamp), every D-pad value including 15 (released), a short report
rejected, and neutral-on-error.

## Backends, in order

### 1. Linux (first, and the reason this exists)

`evdev =0.13` uinput `VirtualDevice` with id `045e:028e`, Xbox 360 button and
axis codes. SDL, Steam and Proton treat that as an Xbox pad (xboxify does the
same). `grab()` the QuadStick's gamepad evdev node so games do not see two
pads. Only the gamepad node: keyboard and mouse stay live.

Cost: one udev rule for `/dev/uinput` and the QuadStick hidraw node, same
pattern as Valve's `60-steam-input.rules`. No driver. Ships as a binary, the
rule file and a systemd user unit.

Known gap: `grab()` hides evdev, not hidraw. Steam or SDL's HIDAPI driver can
still open the real stick and show a second pad. Check with DerPasi; if it
bites, a Steam per-device ignore is the documented workaround.

### 2. Windows (second, parity with QMP)

`vigem-client =0.1.4` on ViGEmBus 1.22.0, plus HidHide to hide the real
stick. Both drivers are what QMP users already have installed. ViGEmBus was
archived 2023-11-02 and still installs; its Windows 11 24H2/25H2 status is
unverified. HidHide is maintained but thin. Never call the old vigem.org
updater domain. The only maintained replacements are paid (Nefarius
VirtualPad, libvirtualhid), so this backend is "works while it works".

### 3. macOS (gated on Apple, start the wait now)

Technically possible, blocked on paperwork:

- API: `IOHIDUserDeviceCreateWithProperties` (public C, callable from Rust)
  or CoreHID `HIDVirtualDevice` (macOS 15+, Swift only, would need a shim).
- Both need the restricted entitlement
  `com.apple.developer.hid.virtual.device`. Apple grants it on request to
  paid program members (Apple DTS, forum thread 820708). There is no
  development variant, so nothing runs on a Mac until it is granted. Works
  in a sandbox since 13.1; Mac App Store review of it is unverified, so plan
  on a Developer ID build outside the store.
- Games: GameController ignores unknown pads. A virtual device spoofing a
  first-party id with that pad's exact descriptor and report bytes is
  recognised. `045E:0B13` (Xbox Series) is hardware-verified by
  OpenJoystickDriver (MIT, active); `045E:028E` passes
  `supportsHIDDevice`. Copy OJD's descriptor bytes, not our guess.
- Hiding the real stick is not needed: GameController does not bind a
  QuadStick in mode 2 anyway. Unverified for SDL games.
- The workspace has `unsafe_code = "forbid"`. The IOKit FFI needs one
  `#![allow(unsafe_code)]` module in this crate only.

Action with a lead time: request the entitlement now, for the existing
Developer ID. It costs a form and weeks of waiting, and nothing else here
does.

## Not doing

- No UI. QCM can later show "bridge running" by checking the process; that is
  a separate change.
- No remapping. The profile on the stick does the mapping; the bridge copies.
- No DS4 output, no rumble, no PS3/PS4 input modes. Add on request.

## Done means

DerPasi plays one XInput-only game on Linux with a mode 2 profile, keyboard
and mouse bindings still working, and unplugging mid-game leaves nothing
held. Target: a Linux build in his hands by 2026-09-30.
