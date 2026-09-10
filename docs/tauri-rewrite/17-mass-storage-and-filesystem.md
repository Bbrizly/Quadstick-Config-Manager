# Mass storage and filesystem safety

## Severity

A bug here can destroy the user's working QuadStick configuration. Treat install/delete/reorder as **critical data-integrity code**.

## Current safety sequence to preserve

`Device.Install` already does more than atomic local save:

```text
validate profile + filename
→ validate QuadStick root (`default.csv` marker)
→ require confirmation for default/prefs when applicable
→ backup existing target to home backup directory
→ clone + normalize device CSV
→ write same-directory `.qscm-tmp`
→ read temporary file back exactly and compare
→ move/replace target
→ on mid-swap failure, attempt safe restore using backup/restore temp
→ cleanup temp artifacts best-effort
```

Rust Phase 3 must reproduce this before any “better” algorithm is introduced.

## Native scoping

Frontend never receives a general writable path. Native registry maps `StorageDeviceId` to a current root and `DeviceFileId` to a direct-child filename. `LocalFileRef` from a file picker is similarly scoped.

## Install transaction target design

```rust
pub struct InstallPlan {
  operation_id: OperationId,
  device_id: StorageDeviceId,
  device_generation: u64,
  target_name: SafeDeviceFileName,
  expected_bytes: Vec<u8>,
  confirmation: Option<ConfirmationId>,
}
```

Execution:

1. Resolve device ID.
2. Re-enumerate/revalidate root; marker still present.
3. Revalidate filename and protected-file confirmation.
4. Reject target not direct child after canonicalization/path join checks.
5. Create backup directory off-device.
6. If target exists, copy to unique backup; flush/close; record path internally.
7. Create unique temp in device root, preferably same filesystem/directory.
8. Write all bytes; flush; call `sync_all` where supported/practical.
9. Close.
10. Reopen/read all bytes and exact-compare.
11. Replace target using platform/filesystem-safe same-volume operation.
12. Reopen/read target if practical; verify committed bytes for extra confidence.
13. Cleanup temp.
14. Return receipt with user-safe backup location display.

### Atomicity language

Do **not** promise POSIX-style atomic rename on every removable FAT-family filesystem/platform. Say “same-directory replace minimizes partial-state exposure” and rely on read-back + backup/restore. Hardware tests must include likely FAT storage.

## Failure matrix

| Failure | Required result |
|---|---|
| full disk before temp complete | original target untouched; temp cleanup best effort |
| unplug during temp write | original target untouched if swap not begun; typed removed-during-write error |
| temp read-back mismatch | original target untouched; fail |
| replace fails before target changed | report unchanged where provable |
| replace fails after target displaced | attempt restore; error says restored vs uncertain accurately |
| backup fails | abort before target modification |
| read-only device | fail before destructive stage where possible |
| marker removed/root reused | stale-device error, no write |
| target held open | fail; preserve backup/original according to stage |
| app shutdown | transaction reaches safe checkpoint/restore; never async-cancel in middle of irreversible swap without cleanup policy |

## Deletion

- only `.csv` direct children;
- never normal-delete `default.csv`/`prefs.csv`;
- backup target first;
- revalidate root immediately before delete;
- never follow a symlink/reparse escape to outside root;
- return receipt.

## Device library/order

Port current sorting/protection/rename/order semantics exactly from `Device.cs` + `DeviceFileManagementTests`. Do not infer file number from alphabetical order if current file-number logic says otherwise.

## Backups

Keep user backups **off the QuadStick drive**. Firmware may delete non-CSV/dot files on startup. Continue a recognizable home/app-data backup area and preserve rescue semantics during upgrade.