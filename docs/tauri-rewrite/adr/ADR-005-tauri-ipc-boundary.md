# ADR-005 — IPC: commands + events + Channels

**Status:** ACCEPTED

## Decision
- Commands: request/response + side effects.
- Events: low-frequency invalidation/lifecycle.
- `tauri::ipc::Channel`: ordered high-rate live input and meaningful progress streams.

## Evidence
Tauri 2 official IPC guidance distinguishes events from Channels and recommends Channels for ordered/high-throughput streaming.

## Rejected
- Global event for every HID packet: unnecessary JSON/event fan-out and weak lifecycle semantics.
- Polling live state from JS: latency/CPU waste.
- WebSocket localhost server: larger attack/discovery/lifecycle surface without need.

## Consequences
QcmClient owns unsubscribe/stream handles; high-rate data is local to feature subtree.