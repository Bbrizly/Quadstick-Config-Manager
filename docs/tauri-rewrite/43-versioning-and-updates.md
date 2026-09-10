# Versioning and updates

## One product version

Do not independently version frontend/Rust crates/IPC for an application that ships atomically. Use one SemVer product version `X.Y.Z[-pre]` as current release flow does.

Crate `version` fields may mirror workspace product version or remain internal `0.x`; they are not public compatibility promises unless published separately.

## IPC/schema version

Tauri frontend and Rust backend ship together; no runtime negotiation required for ordinary commands. Add an internal `apiSchema` integer to `get_app_snapshot` only as a debugging/contract-test aid. Increase only for fixtures/tools that can mix versions.

## Config format

QCM does not own the QuadStick firmware format version. Preserve `Version 1.5` behavior as device contract; never bump it because application architecture changed.

## Beta channel

Tauri beta versions use SemVer prerelease tags (`v2.0.0-beta.N` or project-selected next major) and separate update channel/app identity where feasible.

## Updater

Native core controls `check_for_update` and `install_update`; frontend shows domain state. Do not grant generic updater plugin access if a narrow command wrapper suffices.

States:
```text
Idle → Checking → Available(version, notes) → Downloading → ReadyToRestart
                         ↘ UpToDate
              errors → Failed(retryable?)
```

Never auto-install in middle of an unsaved profile/device write. Before restart:
- no active device mutation;
- profile sessions rescued/persisted according to close policy;
- bounded telemetry/log flush;
- backup task has safe stop state.

## Downgrade

Beta and stable data formats must be either backward-compatible or segregated. If Tauri settings/session format cannot be read by Avalonia stable, beta must keep separate app-data and export ordinary QuadStick CSV so downgrade is always possible.

## Update endpoint failure

Update check is non-critical. App remains usable offline. Signature failure is hard fail for update artifact and should be diagnosable, never bypassed.