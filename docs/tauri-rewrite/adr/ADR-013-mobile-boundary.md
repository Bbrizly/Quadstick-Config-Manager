# ADR-013 — Mobile is a later adapter problem

**Status:** ACCEPTED / DEFERRED IMPLEMENTATION

## Decision
Share React + qcm-config/qcm-core where viable, but require physical iOS/Android hardware feasibility before promising direct QuadStick connectivity. Native Swift/Kotlin Tauri plugins implement proven OS capabilities; desktop bridge is separate fallback design.

## Why
Tauri mobile support is not evidence that mobile OSes expose desktop mass-storage/HID/Bluetooth behavior.

## Consequence
Mobile cannot block or distort desktop rewrite.