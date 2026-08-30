# Behavior ledger

Source definitions live in `05-behavior-inventory.md`. This ledger tracks contract and proof.

| ID | Class | Contract | Target proof | Status |
|---|---|---|---|---|
| B-001 | A | CSV parse quoting/CRLF-LF | golden parity | CONTRACTED |
| B-002 | A | CSV writer CRLF | exact bytes | CONTRACTED |
| B-003 | A | raw grid extra columns preserved | property/mutation | CONTRACTED |
| B-004 | A | A..J trim/newline flatten | golden | CONTRACTED |
| B-005 | A | Version 1.5 + true blanks | oracle | CONTRACTED |
| B-006 | A | 63-char/1023-byte firmware diagnostics | boundary/oracle | CONTRACTED |
| B-007 | A | modes/bindings/prefs/IR parse+issues | differential | CONTRACTED |
| B-008 | A | K note/L action name | differential | CONTRACTED |
| B-009 | A | undo/dirty/revision | mutation state | CONTRACTED |
| B-010 | A | qsf explicit safe operations | JSON parity | CONTRACTED |
| B-011 | A | mount marker discovery | fake+hardware | CONTRACTED |
| B-012 | A | cached display/fresh destructive scan intent | discovery tests | CONTRACTED |
| B-013 | A | safe filename/path rules | negative tests | CONTRACTED |
| B-014 | A | default overwrite confirm | confirmation tests | CONTRACTED |
| B-015 | A | prefs install confirm | confirmation tests | CONTRACTED |
| B-016 | A | backup before target change | fault test | CONTRACTED |
| B-017 | A | temp/readback/replace/restore | fault+hardware | CONTRACTED |
| B-018 | A | backups off device | path tests | CONTRACTED |
| B-019 | A | protect default/prefs delete | negative | CONTRACTED |
| B-020 | A | library filtering/default first | parity | CONTRACTED |
| B-021 | A | LED file patterns audited 1–32 | exact table | CONTRACTED |
| B-022 | A | HID known IDs + gamepad collection | descriptor/hardware | CONTRACTED |
| B-023 | A | normalized axes/buttons/jitter | captures | CONTRACTED |
| B-024 | A | HID errors retry/nonfatal | state tests | CONTRACTED |
| B-025 | A limitation | XInput not current HID | capability test | CONTRACTED |
| B-026 | A | OAuth PKCE/state/loopback | auth tests | CONTRACTED |
| B-027 | A | drive.file + Sheets/Drive semantics | HTTP snapshots | CONTRACTED |
| B-028 | A | DPAPI/Keychain; Linux unavailable | OS tests | CONTRACTED |
| B-029 | A | cloud push does not block local save | fake cloud | CONTRACTED |
| B-030 | A | keep-online rescues local then validates remote | conflict tests | CONTRACTED |
| B-031 | A | telemetry opt-in/allowlist/scrub/CI-off | network tests | CONTRACTED |
| B-032 | A | persistent theme/lang/scale/reduced motion | settings tests | CONTRACTED |
| B-033 | A | scale preview auto-revert | timer/UI | CONTRACTED |
| B-034 | A | all locales + RTL + pseudo | i18n CI | CONTRACTED |
| B-035 | A | import/XLSX review limitations | fixtures/E2E | CONTRACTED |
| B-036 | A | community profiles | HTTP/E2E | CONTRACTED |
| B-037 | A | mode operations | mutation/E2E | CONTRACTED |
| B-038 | A | preference catalog/editor | parity/E2E | CONTRACTED |
| B-039 | A | agent constrained typed edits | corpus/eval | CONTRACTED |
| B-040 | A | crash rescue/report | forced crash/privacy | CONTRACTED |
| B-041 | A | update behavior | signed updater | CONTRACTED |
| B-042 | E | Avalonia gallery | web tooling replacement | ASSESSED |
| B-043 | E forbidden | generic JS filesystem | capability test absent | CONTRACTED |
| B-044 | E forbidden | generic JS shell/process | capability test absent | CONTRACTED |
| B-045 | D | production serial | OQ-001 resolution 2026-08-30 | CLOSED-DEFERRED: `System.IO.Ports` was never a dependency in any commit, and the only source hit is a test asserting its absence; do not implement without new evidence |

Status moves to PARITY-TESTED/HARDWARE-VERIFIED only with linked TEST_LEDGER evidence.
