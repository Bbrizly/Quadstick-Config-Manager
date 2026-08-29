# Fixture corpus

Status: **TASK-002 in progress until legacy corpus SHA-256 values are filled and CI verifies them.**

`fixtures/manifest.json` is the canonical inventory. It references the existing repository-owned legacy corpus in place and adds deterministic rewrite edge fixtures rather than duplicating binary XLSX assets.

Rules:

- every fixture has a stable ID, source/provenance, license note and behavior IDs;
- every fixture is SHA-256 pinned;
- private Google/user files are forbidden;
- path traversal and duplicate ID/path entries fail validation;
- `python3 tools/fixtures/manifest.py verify` is read-only and intended for CI;
- `python3 tools/fixtures/manifest.py write` is the only supported way to refresh hashes after an intentional fixture change.

The first implementation commit deliberately leaves the seven pre-existing corpus hashes blank so CI can report hashes from an actual checkout, including the binary XLSX files. Those blanks are not accepted by `verify` and must be filled before TASK-002 is marked DONE. This avoids pretending a Git object SHA-1 is a content SHA-256.
