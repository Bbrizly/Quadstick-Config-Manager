# Canonical parity schema

Status: **TASK-003/TASK-004 implementation present; CI/selfcheck pending.**

The migration compares C# and Rust using `fixtures/oracle/schema.json` (`qcm-parity-1`). Human UI layout is not part of this schema; format/device semantics are.

## Canonical rules

- raw grid row and cell order is always preserved;
- missing/empty are not collapsed where the current model distinguishes them (`csvFileName` is nullable; cells are strings);
- sheet order, binding order, input order and `inputCols` order are semantic and never sorted;
- issues remain in legacy emission order and include severity/cell/kind plus display message/fix;
- serialized text is included with exact UTF-8 byte count and SHA-256;
- source input is identified by basename only, never an absolute path;
- output contains the frozen legacy commit SHA;
- no timestamp, hostname, username, current locale or absolute path may appear.

## Oracle commands

`tools/QcmOracle` implements:

- `inspect-canonical` — raw grid + parsed projection + issues + current serialization;
- `normalize-canonical` — the same after `NormalizeForDeviceCsv`;
- `apply-canonical` — deterministic mutation replay for migration operations;
- `firmware-canonical` — what the firmware-oracle reader actually loads;
- `generate` — generate canonical outputs for every applicable fixture in `fixtures/manifest.json`;
- `selfcheck` — byte-determinism and normalization-idempotence smoke check.

The firmware reader in this tool is intentionally a tooling copy of the executable test oracle. Before cutover, drift between the two copies must itself be guarded by a test or both must be generated from one shared test-only source.
