# Tauri events, Channels and frontend subscriptions

## Decision

Use three IPC shapes deliberately:

1. **Commands** — request/response and side effects.
2. **Events** — low-frequency invalidation/lifecycle only.
3. **Channels** — ordered streaming/progress.

Tauri official guidance notes its event system is not intended for low-latency/high-throughput data; Channels are the selected live-input path.

## Proposed low-rate events

| Event | Producer | Meaning | Frontend reaction |
|---|---|---|---|
| `qcm://devices-changed` | device discovery service | candidate membership/capability changed | invalidate/refetch `list_devices` |
| `qcm://settings-changed` | native/core | settings changed outside current component | refetch settings if needed |
| `qcm://profile-source-changed` | file/cloud watcher, only if implemented | persisted source changed externally | surface conflict, never auto-overwrite |
| `qcm://update-state-changed` | updater service | low-rate update state | refresh update badge |

Do not emit “heartbeat” events.

## Channels

### Live input

`Channel<LiveFrameDto>` from Rust to caller. Ordered; high-rate; discarded with stream handle on stop/unmount.

### Progress

Use `Channel<OperationProgressDto>` for install/restore/export only when an operation has meaningful multi-step progress. Current install may initially send named stages rather than fake percentages:

```text
Validating
BackingUp
WritingTemporary
VerifyingTemporary
Replacing
VerifyingResult
Complete
```

## Subscription ownership

`TauriQcmClient` returns unsubscribe/stream handles. React feature hooks register in `useEffect` and **always** dispose on unmount. Development StrictMode double-mount must not create duplicate permanent listeners.

## Payload versioning

Internal same-version app does not need independent semantic versions for every event. Add `schemaVersion` only to streams persisted or consumed across independently updated boundaries. Otherwise app version + contract tests are enough.

## Stale data

Every device/profile event related to mutable state includes generation/revision or triggers refetch. Frontend ignores late state older than current generation/revision.

## Security

Never put secrets, tokens, unrestricted filesystem paths, raw crash dumps or arbitrary remote HTML in event payloads.