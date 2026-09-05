# Frontend state model

## Five kinds of truth

### 1. Device truth — Rust

Which storage/HID resources exist, capability state, operation busy/error state, generation.

### 2. Persisted profile truth — Rust

Raw grid, document projection, issues, persisted source, dirty/revision/undo.

### 3. Editor snapshot — React mirror, revision-tagged

A read-only projection of Rust truth used for rendering. Never independently serialized to device CSV.

### 4. Input draft — local React

Text currently being typed before commit (e.g. action name field). It may temporarily be invalid and is discarded/reconciled if canonical revision changes.

### 5. Ephemeral UI — React

Selected panel, hover/focus, dialog visibility, scroll, tooltip, search filter, expanded category.

## Revision contract

Every profile-mutating command sends `expectedRevision`. If Rust returns `QCM_PROFILE_REVISION_CONFLICT`, controller refetches current snapshot and either:
- safely reapplies a still-valid local draft with explicit user knowledge; or
- shows conflict/retry.

Never last-write-wins silently.

## Dirty state

Dirty is native canonical state. Frontend may show `snapshot.dirty`; it must not infer dirty by comparing JavaScript objects.

## Undo

Undo command runs Rust's raw-grid undo semantics. Browser `Ctrl/Cmd+Z` is routed to focused text input's native undo first; global editor undo only when focus context allows. Specify keyboard behavior in UI tests.

## Live state

Keep high-rate `LiveFrame` outside app-wide React context. The live hook stores latest frame in a ref/external store and publishes at render-frame cadence to only the visualizer/status subtree.

## Device library state

Snapshots carry `deviceId + generation`. Any mutation with older generation is rejected. Device-changed event invalidates snapshot.

## Settings

Persisted settings have revision and native owner. Theme/language can be mirrored into top-level React context after successful update. Safety-preview values (interface scale) have an explicit temporary preview state and timeout/revert semantics.

## Cloud backup state

Do not treat “Google token exists” as a frontend boolean. Native returns typed status:

```ts
type BackupStatus =
 | { kind:'unavailable'; reason:string }
 | { kind:'disconnected' }
 | { kind:'connected'; accountHint?:string }
 | { kind:'needsReconnect' };
```

No secrets cross IPC.