# Test coverage matrix

| Contract/risk | Legacy evidence | Target automated | Manual/hardware |
|---|---|---|---|
| CSV quoting/CRLF | Format tests/Csv.cs | qcm-config golden/property | — |
| firmware section/limits | FirmwareOracle, Firmware2373RuleTests | differential + boundaries/fuzz | optional device sample |
| app/device agreement | DeviceAgreementTests | cross-language oracle | real install/readback |
| raw extra-column preservation | ProfileFile/Edit tests | mutation parity/property | imported real profile smoke |
| undo/action names | ActionNameTests/ProfileFile | state parity | editor keyboard |
| device install integrity | EditInstallTests/Device | stage fault injection | unplug/full/read-only real drive |
| device library/delete/order | DeviceFileManagementTests | fake storage parity | hardware library |
| HID descriptor/live | App live tests where present + LiveInput | descriptor/report fixtures | each practical VID/PID mode |
| DeviceBand semantics | App tests/DeviceBand | component/live fake | AT/zoom |
| import/XLSX | qsf/import tests | fixture/malicious workbook | UI review AT |
| community on-demand/privacy | CommunityCatalogTests/PRIVACY | HTTP spy/cache | offline smoke |
| Google client | DriveClientTests | mock HTTP exact requests | real account smoke |
| Drive backup/conflict | DriveBackupTests | fake cloud/core | restore/share smoke |
| token security | TokenStore/GoogleAuth tests | adapter mocks/OS integration | DPAPI/Keychain packaged |
| telemetry privacy | Telemetry tests/PRIVACY | network spy/allowlist/redaction | privacy review |
| crash rescue | CrashGuardTests | forced panic/session rescue | packaged restart |
| localization | CultureTests/pseudo script | key/pseudo/RTL CI | language smoke |
| accessibility | App semantic tests | RTL/axe/keyboard | NVDA/Narrator/VoiceOver |
| agent/qsf | agent eval + qsf tests | old/new corpus comparison | human review |
| capability security | none equivalent | forged IPC/forbidden permission tests | release audit |
| updater | current UpdateCheck/release | mock + bad signature | packaged update/rollback |
| memory/resource leaks | ad hoc | fake soak | 2h HID/100 reconnects |

Every Critical risk R-001..R-005, R-011..R-014, R-020..R-021 must have a failing automated simulation before Gate 6, plus hardware where applicable.