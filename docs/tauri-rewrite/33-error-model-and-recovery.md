# Error model and recovery

## IPC error

```rust
pub struct QcmErrorDto {
  pub code: String,
  pub message: String,
  pub recoverable: bool,
  pub action: Option<RecoveryActionDto>,
  pub operation_id: Option<String>,
}
```

Internal Rust error enums preserve source chains/path/debug context; conversion to DTO redacts sensitive details.

## Stable families

```text
QCM_CONFIG_PARSE_*
QCM_CONFIG_VALIDATION_*
QCM_PROFILE_REVISION_CONFLICT
QCM_IMPORT_*
QCM_DEVICE_NOT_FOUND
QCM_DEVICE_STALE
QCM_DEVICE_BUSY
QCM_DEVICE_NOT_QUADSTICK
QCM_DEVICE_REMOVED_*
QCM_STORAGE_PERMISSION_DENIED
QCM_STORAGE_READ_ONLY
QCM_STORAGE_FULL
QCM_STORAGE_BACKUP_FAILED
QCM_STORAGE_VERIFY_FAILED
QCM_STORAGE_RESTORE_FAILED
QCM_HID_*
QCM_SERIAL_*              # only if serial ships
QCM_GOOGLE_AUTH_*
QCM_GOOGLE_REVOKED
QCM_CLOUD_CONFLICT
QCM_NETWORK_*
QCM_UPDATE_*
QCM_CANCELLED
QCM_INTERNAL
```

Do not make every OS error its own public code; public codes represent user/recovery semantics.

## Recovery examples

### Device removed during temp write

- code `QCM_DEVICE_REMOVED_DURING_WRITE`;
- recoverable true;
- state transitions storage to Absent;
- original target considered unchanged only if swap stage not reached;
- UI: reconnect and retry; never say “unchanged” when stage makes that uncertain.

### Temp read-back mismatch

`QCM_STORAGE_VERIFY_FAILED`; original target remains untouched; preserve backup if one was created; advise retry/another USB path.

### Revision conflict

No data lost; UI refetches canonical snapshot and asks/reapplies draft only when safe.

### Google `invalid_grant`

`QCM_GOOGLE_REVOKED`; pause backup; profile save remains successful; UI shows reconnect.

## Panic policy

Recoverable I/O/data errors never panic. Top-level panic hook writes a sanitized local crash record and attempts to preserve session rescue data without invoking complex network/UI work. Panic is a bug; do not use catch-unwind as routine control flow.

## `unwrap` policy

Allowed in tests and compile-time/proven initialization invariants with explanatory comment. Forbidden on user/device/network/filesystem data paths.

## Cancellation

Cancellation is a distinct non-error UX result when user initiated. It must not interrupt an irreversible storage swap at an unsafe boundary; the transaction owns safe cancellation checkpoints.