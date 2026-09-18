# ADR-008 — Separate storage and live-input state machines

**Status:** ACCEPTED

## Decision
Storage: Unknown/Discovering/Absent/Available/Busy/Error.  
HID: Stopped/Scanning/Streaming/Backoff.

Core serializes destructive operations per storage device. UI disabled state is not synchronization.

## Why
Mounted storage has no long-lived socket “connection”; HID does. One universal Connected/Disconnected state would create invalid transitions.

## Consequences
Aggregate app status is derived. Every long operation carries generation/OperationId.