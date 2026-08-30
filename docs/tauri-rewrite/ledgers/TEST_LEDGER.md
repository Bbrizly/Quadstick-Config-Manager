# Test ledger

Evidence paths are target placeholders until implemented.

| ID | Contract | Automated target | Manual/hardware | Status |
|---|---|---|---|---|
| T-001 | CSV exact parse/write | `qcm-config` golden | — | PLANNED |
| T-002 | firmware parser/limits | differential + FirmwareOracle | device sample | PLANNED |
| T-003 | raw-grid preservation | property/mutation | real imported profile | PLANNED |
| T-004 | editor ops/undo/revision | differential | keyboard editor | PLANNED |
| T-005 | safe filenames/traversal | unit/IPC negative | — | PLANNED |
| T-006 | discovery stale/multiple | fake/integration | real multiple/remount | PLANNED |
| T-007 | install transaction faults | qcm-testkit every stage | unplug/full/read-only | FAKE READY |
| T-008 | library/delete/order | fake parity | real device | PLANNED |
| T-009 | HID descriptor/normalize | captured fixtures | real modes 3 OS | PLANNED |
| T-010 | HID backoff/leak | fake soak | 2h/reconnect | PLANNED |
| T-011 | IPC revision/security | direct command negative | — | PLANNED |
| T-012 | capability least privilege | static/forbidden-call | release audit | PLANNED |
| T-013 | frontend editor | RTL/mock E2E | AT | PLANNED |
| T-014 | visualizer semantic equality | component/axe | NVDA/VoiceOver | PLANNED |
| T-015 | import/XLSX limits | malicious fixtures | review AT | PLANNED |
| T-016 | community privacy/offline | HTTP spy/cache | offline | PLANNED |
| T-017 | Google auth | mock state/PKCE | DPAPI/Keychain | PLANNED |
| T-018 | Drive backup/conflict | fake/HTTP mock | real account | PLANNED |
| T-019 | telemetry privacy | network spy/allowlist | policy review | PLANNED |
| T-020 | crash rescue | forced panic | packaged restart | PLANNED |
| T-021 | i18n | key/pseudo/RTL | locales | PLANNED |
| T-022 | updater signature/state | mock/bad signature | packaged rollback | PLANNED |
| T-023 | agent/qsf | old/new corpus eval | human sample | PLANNED |
| T-024 | performance | benchmarks | target vs baseline | PLANNED |
| T-025 | packaging | CI build/sign verify | clean machines | PLANNED |
| T-026 | privacy launch network | integration traffic spy | packet/support review | PLANNED |
| T-027 | error redaction | `qcm-core/tests/redaction.rs` every family | bug-report screenshot review | IMPLEMENTED |
| T-028 | session revision/atomic batch/close-dirty | `qcm-core/tests/profile_sessions.rs` on the fake library | leave prompt with a real save | IMPLEMENTED |

Critical behavior cannot move to PARITY-TESTED without at least one T row; storage/HID required parity also needs hardware evidence.