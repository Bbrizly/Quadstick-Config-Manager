# Test fixture plan

## Source fixture inventory

During TASK-002/TASK-003, enumerate and copy/link every non-secret profile/workbook fixture from:
- `tests/QuadStick.Format.Tests`;
- `tests/QuadStick.App.Tests`;
- embedded templates/default prefs;
- docs-safe sample profiles;
- qsf tests/fixtures if present;
- selected public/community profiles already legitimately fetched by current tests.

Do not copy private Google data or user home backups.

## Directory

```text
fixtures/
  profiles/
    valid/
    legacy/
    edge/
  malformed/
  xlsx/
  oracle/
    parse/
    normalize/
    firmware/
    operations/
  storage/
    quadstick-root/
  hid/
    descriptors/
    reports/
```

## Manifest

Every fixture is listed in `fixtures/manifest.json`:

```json
{
  "id":"missing-blank-separator",
  "path":"profiles/edge/missing-blank-separator.csv",
  "source":"existing-test",
  "behaviorIds":["B-005","B-006"],
  "expected":"normalization inserts true blank separator",
  "license":"repository-test-fixture"
}
```

## Canonical oracle generation

Oracle outputs are generated from audited C# source, not hand-edited. Each output stores:
- oracle source commit;
- fixture SHA-256;
- canonical representation schema version;
- result.

A script fails if fixture hash changes without regenerating expected oracle output.

## HID fixtures

Where legally/technically possible, store raw descriptor bytes + captured sanitized reports per supported VID/PID/mode, with product/mode metadata. Do not fabricate a descriptor and call it hardware evidence.

## Storage fake

`qcm-testkit` can materialize a temp QuadStick root containing `default.csv`, numbered profiles, prefs and marker data. Fault-inject wrapper simulates read-only/full/unplug by returning configured I/O errors/stages.

## Golden-update policy

Golden updates require review showing **why behavior changed**. A blanket `UPDATE_SNAPSHOTS=1` commit cannot silently redefine format compatibility.