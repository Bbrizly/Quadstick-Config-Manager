# Risk matrix

Detailed descriptions: `../47-risk-register.md`.

| Risk | Owner | Phase detected | Release block? | Status |
|---|---|---|---:|---|
| R-001 config semantic drift | qcm-config | 1/2 | yes | OPEN |
| R-002 raw data loss | qcm-config | 1/2 | yes | OPEN |
| R-003 firmware framing limits | qcm-config | 1/2 | yes | OPEN |
| R-004 partial device write | core/storage | 3 | yes | OPEN |
| R-005 stale wrong drive | storage adapter | 3 | yes | OPEN |
| R-006 removable rename semantics | storage adapter | 3/8 | yes | OPEN |
| R-007 HID descriptor regression | HID | 3 | yes for live parity | OPEN |
| R-008 XInput limitation | product/HID | 3/6 | documented limitation allowed | OPEN |
| R-009 serial scope creep | architecture | 0 | no if unused | OPEN |
| R-010 giant React component | frontend | 5/6 | architecture gate | OPEN |
| R-011 split Rust/TS truth | core/frontend | 4/5 | yes | OPEN |
| R-012 excessive Tauri privileges | security | 4 | yes | OPEN |
| R-013 token exposure | cloud/security | 6 | yes | OPEN |
| R-014 cloud conflict data loss | backup core | 6 | yes | OPEN |
| R-015 privacy regression | diagnostics | 6/8 | yes | OPEN |
| R-016 localization regression | frontend | 5/6 | yes stable | OPEN |
| R-017 a11y regression | frontend | 5–9 | yes | OPEN |
| R-018 live rerender perf | frontend | 5/8 | maybe/P1 | OPEN |
| R-019 HID/listener leak | HID/frontend | 3/8 | yes if severe | OPEN |
| R-020 device op race | core | 3 | yes | OPEN |
| R-021 unsafe shutdown | core/storage | 3/8 | yes | OPEN |
| R-022 supply chain | release/security | continuous | critical findings yes | OPEN |
| R-023 hidapi backend failure | HID | 3 | blocks live architecture | OPEN |
| R-024 Linux variance | platform | 8 | scoped support | OPEN |
| R-025 updater/signing | release | 8/9 | yes | OPEN |
| R-026 migration/downgrade trap | release | 9 | yes | OPEN |
| R-027 agent privilege | agent/security | 6 | yes | OPEN |
| R-028 XLSX bomb | import/security | 6 | yes | OPEN |
| R-029 rewrite scope | project | all | reassess gate | OPEN |
| R-030 stale docs assumptions | architecture | 0/all | yes if critical | OPEN |
| R-031 agent eval lost | agent | 0/6 | feature dependent | OPEN |
| R-032 source-build privacy | diagnostics | 6/8 | yes | OPEN |