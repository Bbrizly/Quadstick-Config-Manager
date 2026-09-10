# Concurrency and async model

## Runtime

Tauri supplies an async runtime integration; do not create nested global runtimes. `qcm-core` should remain runtime-light and use abstractions compatible with Tauri/Tokio where needed.

## Actors/gates

### ProfileSessionManager

A short-lived mutex/RwLock protects session map and each session mutation. Never hold it during file picker, device I/O or cloud HTTP. Pattern:
1. lock, validate revision, clone/prepare bytes/state;
2. unlock;
3. perform I/O;
4. relock, verify revision/source assumptions, commit persisted state.

### DeviceManager

Owns discovery snapshot and **per-storage-device mutation semaphore**. List/read can either wait or return busy during destructive mutation; choose deterministic policy and test it.

### LiveInputManager

One cancellation token + blocking worker/stream handle. No app-global lock around HID read.

### BackupManager

Per-profile/cloud-link gate prevents concurrent pushes/restores for same profile. It never blocks local save success unless feature explicitly requires synchronous cloud result.

## Blocking I/O

Mounted filesystem and HID reads can block. Run:
- HID on dedicated blocking thread/worker;
- large filesystem copy/readback via `spawn_blocking` or dedicated blocking task;
- HTTP asynchronously.

Do not call blocking `std::fs::read_to_string` for large/device files directly in an async command while holding shared state lock.

## Lock order

Avoid nested locks. If unavoidable document order:
`session registry → individual session`; `device registry → individual device gate`. Never hold session lock while acquiring device gate for long operation; snapshot data first.

## Races to test

- two saves same session;
- edit while save in progress;
- save then immediate Save As;
- install A + install B same device;
- install + delete same device;
- device invalidation during prepare→commit confirmation gap;
- disconnect live stream while worker emits;
- component unmount before command result;
- backup push while profile is renamed/moved;
- cloud restore while local unsaved edits exist;
- app shutdown during temp write, during replace, during background backup.

## Queue policy

No unbounded channels. Live state uses latest/bounded semantics. Diagnostics use bounded queue/drop policy with counters. User-requested operations are explicit futures/IDs, not hidden firehose jobs.