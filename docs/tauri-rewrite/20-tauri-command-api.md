# Tauri command API

Command names are domain-level and versioned as one app-internal API. The frontend calls them only through `TauriQcmClient`.

## Common envelope

Rust commands return `Result<T, QcmErrorDto>`. Long operations return an `OperationId` and/or accept a progress `Channel<T>` when progress is meaningful.

## App/settings

| Command | Request | Response | Side effect |
|---|---|---|---|
| `get_app_snapshot` | none | version/platform/capabilities/settings | none |
| `get_settings` | none | `AppSettingsDto` | none |
| `update_settings` | typed patch + expected revision | updated settings | persists validated settings |
| `confirm_interface_scale` | preview token | settings | finalizes safety preview if retained natively |

## Profile session

| Command | Request | Response |
|---|---|---|
| `new_profile` | template/name | `EditorSnapshot` |
| `choose_and_open_profile` | none/options | `EditorSnapshot?` |
| `open_device_profile` | `DeviceFileRefDto` | snapshot |
| `open_community_profile` | catalog ID | snapshot |
| `apply_editor_ops` | session ID + expected revision + ops | snapshot/op result |
| `undo_editor` | session ID + expected revision | snapshot |
| `save_profile` | session ID + expected revision | `SaveReceiptDto` |
| `save_profile_as` | session ID + expected revision | receipt/cancel |
| `close_profile` | session ID + dirty disposition | none |
| `validate_profile` | session ID | issues/snapshot |
| `export_profile` | session ID + format | receipt/cancel |

`apply_editor_ops` must be atomic for a submitted batch.

## Device/storage

| Command | Request | Response |
|---|---|---|
| `list_devices` | none | device presence snapshot |
| `refresh_devices` | none | snapshot |
| `choose_device_folder` | none | validated temporary storage candidate/cancel |
| `get_device_library` | storage ID | library snapshot |
| `prepare_install` | session ID + storage ID | install plan/confirmation requirement |
| `commit_install` | plan ID + optional confirmation ID + progress channel | install receipt |
| `prepare_delete_device_profile` | file ref | confirmation/plan |
| `commit_delete_device_profile` | plan + confirmation | receipt |
| `rename_device_profile` | file ref + safe name + generation | library snapshot |
| `reorder_device_profiles` | device + expected generation + ordered IDs | library snapshot |
| `open_device_preferences` | device | profile/editor snapshot |

Preparation/commit split prevents a UI boolean from becoming security authorization and allows exact confirmation text.

## Live input

`start_live_input({candidateId?}, onFrame: Channel<LiveFrameDto>) -> LiveStreamHandleDto`  
`stop_live_input({streamId}) -> void`

Only one live stream initially unless hardware tests prove multi-stream need.

## Import/community

- `choose_import_file`
- `inspect_import`
- `apply_import_repairs`
- `list_community_profiles`
- `download_community_profile`

Network is native; frontend does not fetch arbitrary catalog URLs.

## Google backup/share

- `get_backup_status`
- `connect_google_backup` (native system-browser OAuth)
- `disconnect_google_backup`
- `list_google_profiles`
- `restore_google_profiles`
- `get_share_state`
- `enable_profile_link_sharing`
- `copy_share_link` may return URL string for clipboard via tightly scoped UI/native clipboard path.

## Diagnostics/update/agent

- `get_diagnostics_summary`
- `export_diagnostics_bundle`
- `send_feedback` / `send_crash_report` only after explicit consent/path
- `check_for_update`
- `install_update` behind signed updater policy
- agent commands operate on typed profile operations; no shell/file/network passthrough.

## Forbidden commands

Do not add:

```text
read_file(path)
write_file(path, bytes)
list_directory(path)
open_serial(port)
hid_read(path)
http_request(url, ...)
execute_shell(command)
```

If a feature needs one, define the domain operation and enforce its trust boundary in Rust.

## Contract tests

For every command, test JSON serialization, malformed request rejection, stable error code, state preconditions, authorization/confirmation requirements and cancellation.