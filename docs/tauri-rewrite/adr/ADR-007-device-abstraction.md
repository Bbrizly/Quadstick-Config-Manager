# ADR-007 — Device as independent capabilities

**Status:** ACCEPTED

## Decision
Model mounted storage and HID live input as separate capabilities with opaque IDs. Do not invent a permanent “connected QuadStick” identity or correlate interfaces across two physical units without platform evidence.

## Why
Current code discovers mount and HID independently. One unit can expose multiple HID interfaces; path/drive letters are not stable identity.

## Consequences
UI can display storage/live status separately. A future LogicalQuadStickId is additive after OQ-002 proof.

## Rejected
Single global singleton `Device` with path + HID handle: creates false coupling and multi-device hazards.