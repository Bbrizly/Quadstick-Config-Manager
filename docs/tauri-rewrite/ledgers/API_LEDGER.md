# API ledger

Every registered Tauri command/event/Channel must appear here. `PLANNED` means it must not yet exist in production.

| API | Kind | Privilege | Validation | Status |
|---|---|---|---|---|
| get_app_snapshot | command | low | none | PLANNED |
| get_settings | command | low | none | PLANNED |
| update_settings | command | persisted | expected revision + value ranges | PLANNED |
| new_profile | command | state | template enum/name bounds | PLANNED |
| choose_and_open_profile | command | file-read via picker | picker + size/decode | PLANNED |
| open_device_profile | command | device-read | opaque file ID/generation | PLANNED |
| open_community_profile | command | network/read | catalog ID/size/parse | PLANNED |
| apply_editor_ops | command | state | session/revision/op bounds | PLANNED |
| undo_editor | command | state | session/revision | PLANNED |
| save_profile | command | local file write | scoped source/revision | PLANNED |
| save_profile_as | command | local picker/write | picker/revision | PLANNED |
| close_profile | command | state | dirty disposition | PLANNED |
| validate_profile | command | none | session | PLANNED |
| export_profile | command | file write | picker/format | PLANNED |
| list_devices | command | device metadata | none | PLANNED |
| refresh_devices | command | enumerate | rate/operation | PLANNED |
| choose_device_folder | command | picker/device probe | marker validation | PLANNED |
| get_device_library | command | device-read | device/generation | PLANNED |
| prepare_install | command | preparation | session/device/revision | PLANNED |
| commit_install | command+Channel | critical device-write | plan/confirmation/generation | PLANNED |
| prepare_delete_device_profile | command | preparation | file ID | PLANNED |
| commit_delete_device_profile | command | critical device-delete | plan/confirmation/generation | PLANNED |
| rename_device_profile | command | device-write | safe filename/generation | PLANNED |
| reorder_device_profiles | command | device-write | IDs/generation/full ordering | PLANNED |
| open_device_preferences | command | device-read | device | PLANNED |
| start_live_input | command+Channel | HID read | candidate/one-stream | PLANNED |
| stop_live_input | command | HID lifecycle | stream ID | PLANNED |
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
| qcm://devices-changed | event | low | native producer only | PLANNED |
| qcm://settings-changed | event | low | native producer only | PLANNED |
| qcm://profile-source-changed | event optional | low | watch source only | PLANNED |
| live frames | Channel | HID stream | stream handle/bounded | PLANNED |
| operation progress | Channel | low/medium | operation ID/stages | PLANNED |

Forbidden APIs: generic read/write/list path, shell exec, arbitrary HTTP, arbitrary serial/HID commands.