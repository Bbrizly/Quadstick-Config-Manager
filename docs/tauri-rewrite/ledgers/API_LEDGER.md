# API ledger

Every registered Tauri command/event/Channel must appear here. `PLANNED` means it must not yet exist in production; `REGISTERED` means this build wires it.

`src-tauri/src/lib.rs::registered_commands` is the machine-readable copy of the
`REGISTERED` rows, and a test in `src-tauri/tests/profile_commands.rs` holds the
two lists to the same length. Every command takes one raw JSON request and reads
it itself, so a malformed payload comes back as `QCM_REQUEST_MALFORMED` rather
than as a framework string the UI cannot switch on.

| API | Kind | Privilege | Validation | Status |
|---|---|---|---|---|
| get_app_snapshot | command | low | none | REGISTERED |
| get_settings | command | low | none | REGISTERED |
| update_settings | command | persisted | expected revision + closed value sets | REGISTERED |
| new_profile | command | state | name length | REGISTERED |
| choose_and_open_profile | command | file-read via picker | native picker mints the only id | REGISTERED |
| get_profile_snapshot | command | read-only state | opaque session id | REGISTERED |
| open_device_profile | command | device-read | opaque file ID/generation | REGISTERED |
| open_community_profile | command | network/read | catalog ID/size/parse | PLANNED |
| apply_editor_ops | command | state | session/revision/batch and text bounds | REGISTERED |
| undo_editor | command | state | session/revision | REGISTERED |
| save_profile | command | local file write | scoped source/revision | REGISTERED |
| save_profile_as | command | local picker/write | picker/revision checked first | REGISTERED |
| close_profile | command | state | dirty disposition | REGISTERED |
| validate_profile | command | none | session | PLANNED |
| export_profile | command | file write | picker/format | PLANNED |
| list_devices | command | device metadata | none | REGISTERED |
| refresh_devices | command | enumerate | rate/operation | REGISTERED |
| choose_device_folder | command | picker/device probe | marker validation | REGISTERED |
| get_device_library | command | device-read | device/generation | REGISTERED |
| prepare_install | command | preparation | session/device/revision | REGISTERED |
| commit_install | command+Channel | critical device-write | plan/confirmation/generation | REGISTERED |
| prepare_delete_device_profile | command | preparation | file ID | REGISTERED |
| commit_delete_device_profile | command | critical device-delete | plan/confirmation/generation | REGISTERED |
| rename_device_profile | command | device-write | safe filename/generation | PLANNED |
| reorder_device_profiles | command | device-write | IDs/generation/full ordering | PLANNED |
| open_device_preferences | command | device-read | device | REGISTERED |
| start_live_input | command+Channel | HID read | candidate/one-stream | REGISTERED |
| stop_live_input | command | HID lifecycle | stream ID | REGISTERED |
| choose_import_file | command | file-read | picker/size | PLANNED |
| inspect_import | command | parser | import ref/limits | PLANNED |
| apply_import_repairs | command | session state | import/session/revision | PLANNED |
| list_community_profiles | command | allowlisted network | explicit UI action/cache | PLANNED |
| download_community_profile | command | allowlisted network | catalog ID/size | PLANNED |
| get_backup_status | command | secure/cloud metadata | none | PLANNED |
| connect_google_backup | command | browser/network/secret | PKCE/state | PLANNED |
| disconnect_google_backup | command | secret delete | explicit action | PLANNED |
| list_google_profiles | command | network | auth + bounds | PLANNED |
| restore_google_profiles | command | network/local state | selection/conflict | PLANNED |
| get_share_state | command | network | linked session | PLANNED |
| enable_profile_link_sharing | command | network/permission | explicit confirmation | PLANNED |
| get_diagnostics_summary | command | local diagnostic | redaction | PLANNED |
| export_diagnostics_bundle | command | local file write | picker/redaction | PLANNED |
| send_feedback | command | network | consent/length/allowlist | PLANNED |
| send_crash_report | command | network | pending report + explicit user action | PLANNED |
| check_for_update | command | network | signed manifest | PLANNED |
| install_update | command | process/restart | safe app state + signature | PLANNED |
| subscribe_devices_changed | command+Channel | low | native invalidation producer only | REGISTERED |
| unsubscribe_devices_changed | command | low | opaque subscription id | REGISTERED |
| qcm://devices-changed | event | low | native producer only | PLANNED |
| qcm://settings-changed | event | low | native producer only | PLANNED |
| qcm://profile-source-changed | event optional | low | watch source only | PLANNED |
| live frames | Channel | HID stream | stream handle/bounded | PLANNED |
| operation progress | Channel | low/medium | operation ID/stages | PLANNED |

Forbidden APIs: generic read/write/list path, shell exec, arbitrary HTTP, arbitrary serial/HID commands.

The file picker is not on this list and is not a plugin. `rfd` is a plain Rust
dialog called from inside `choose_and_open_profile` and `save_profile_as`, so
`capabilities/main.json` still grants the window nothing and the window has no
way to open a dialog of its own. The path it returns never leaves the adapter:
the library mints an opaque id for it and that is what travels.
