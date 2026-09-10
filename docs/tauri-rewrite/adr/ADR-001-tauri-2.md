# ADR-001 — Tauri 2 desktop shell

**Status:** ACCEPTED  
**Date:** 2026-08-29

## Context
QCM needs native filesystem/HID/secure-storage/update capabilities but the target product UI is web technology. Current Avalonia shell has substantial code-behind coupling.

## Options
A. Stay Avalonia and refactor.  
B. Electron.  
C. Tauri 2 + system WebView + Rust.  
D. Browser-only WebHID/File System APIs.

## Decision
Choose **Tauri 2**.

## Why
Tauri gives a Rust-native boundary, system WebViews, explicit capability/permission model, desktop packaging/updater and mobile plugin route without requiring Chromium bundling. It also supports command/event/Channel IPC so live HID can remain native.

## Rejected
Avalonia refactor would reduce rewrite risk but not achieve the requested TS/Rust target. Electron gives mature web UI but a much larger privileged JS/runtime surface. Browser-only cannot guarantee current mounted-storage/HID/native secure-store parity.

## Consequences
Native operations stay in Rust/adapters; WebView is treated as lower trust. Platform WebView quirks and Linux WebKitGTK become release responsibilities.

## Revisit if
Tauri cannot meet physical HID/storage, accessibility or signing requirements in Phase 4/8 spikes.