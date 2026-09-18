# Fixture corpus

Status: **TASK-002 complete pending CI confirmation.**

`fixtures/manifest.json` is the canonical inventory. It references the existing repository-owned legacy corpus in place and adds deterministic rewrite edge fixtures rather than duplicating binary XLSX assets.

Rules:

- every fixture has a stable ID, source/provenance, license note and behavior IDs;
- every fixture is SHA-256 pinned;
- private Google/user files are forbidden;
- path traversal and duplicate ID/path entries fail validation;
- `python3 tools/fixtures/manifest.py verify` is read-only and intended for CI;
- `python3 tools/fixtures/manifest.py write` is the only supported way to refresh hashes after an intentional fixture change.

The seven pre-existing corpus SHA-256 values were measured by GitHub Actions from a clean PR checkout rather than inferred from Git blob IDs. The manifest now rejects any content drift.
