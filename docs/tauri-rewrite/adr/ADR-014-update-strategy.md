# ADR-014 — Signed Tauri updater with native policy wrapper

**Status:** ACCEPTED IN PRINCIPLE; endpoint/channel details OPEN

## Decision
Use Tauri's signed updater mechanism for non-store desktop distribution, wrapped by domain commands/state. Beta/stable channels remain separate; updater cannot restart during unsafe write/dirty-session state.

## Evidence
Tauri updater documentation requires signed update artifacts; signature bypass is not an acceptable recovery strategy.

## Open
Endpoint/manifest hosting, key rotation, staged rollout and Windows Store interaction are resolved in OQ-013/OQ-008.

## Rollback
Withdraw affected manifest/channel and keep previous signed installer.