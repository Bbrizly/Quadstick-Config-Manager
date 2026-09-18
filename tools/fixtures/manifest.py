#!/usr/bin/env python3
"""Validate or refresh the rewrite fixture manifest using only stdlib.

Usage:
  python3 tools/fixtures/manifest.py verify
  python3 tools/fixtures/manifest.py write

`verify` is CI-safe and never mutates the repository. `write` fills every
SHA-256 from the files currently checked out and then re-validates the result.
"""
from __future__ import annotations

import hashlib
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "fixtures" / "manifest.json"
SHA256 = re.compile(r"^[0-9a-f]{64}$")


def digest(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def load() -> dict:
    return json.loads(MANIFEST.read_text(encoding="utf-8"))


def validate(data: dict, *, allow_missing_hashes: bool = False) -> list[str]:
    errors: list[str] = []
    if data.get("version") != 1:
        errors.append("manifest version must be 1")
    fixtures = data.get("fixtures")
    if not isinstance(fixtures, list) or not fixtures:
        return errors + ["fixtures must be a non-empty array"]

    ids: set[str] = set()
    paths: set[str] = set()
    for index, item in enumerate(fixtures):
        prefix = f"fixtures[{index}]"
        if not isinstance(item, dict):
            errors.append(f"{prefix} must be an object")
            continue
        fixture_id = item.get("id")
        rel = item.get("path")
        if not isinstance(fixture_id, str) or not fixture_id:
            errors.append(f"{prefix}.id must be non-empty")
        elif fixture_id in ids:
            errors.append(f"duplicate fixture id: {fixture_id}")
        else:
            ids.add(fixture_id)
        if not isinstance(rel, str) or not rel:
            errors.append(f"{prefix}.path must be non-empty")
            continue
        if rel in paths:
            errors.append(f"duplicate fixture path: {rel}")
        paths.add(rel)

        path = (ROOT / rel).resolve()
        try:
            path.relative_to(ROOT)
        except ValueError:
            errors.append(f"{fixture_id}: path escapes repository: {rel}")
            continue
        if not path.is_file():
            errors.append(f"{fixture_id}: missing file: {rel}")
            continue

        for field in ("kind", "source", "license"):
            if not isinstance(item.get(field), str) or not item[field].strip():
                errors.append(f"{fixture_id}: {field} must be non-empty")
        behavior_ids = item.get("behavior_ids")
        if not isinstance(behavior_ids, list) or not behavior_ids or not all(
            isinstance(x, str) and x for x in behavior_ids
        ):
            errors.append(f"{fixture_id}: behavior_ids must be non-empty strings")

        expected = item.get("sha256", "")
        actual = digest(path)
        if not SHA256.fullmatch(expected or ""):
            if allow_missing_hashes and not expected:
                continue
            errors.append(f"{fixture_id}: sha256 missing/invalid; actual={actual}")
        elif expected != actual:
            errors.append(f"{fixture_id}: sha256 mismatch expected={expected} actual={actual}")
    return errors


def write(data: dict) -> None:
    for item in data["fixtures"]:
        path = (ROOT / item["path"]).resolve()
        item["sha256"] = digest(path)
    MANIFEST.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def main() -> int:
    command = sys.argv[1] if len(sys.argv) > 1 else "verify"
    if command not in {"verify", "write"}:
        print("usage: manifest.py [verify|write]", file=sys.stderr)
        return 2
    data = load()
    pre = validate(data, allow_missing_hashes=command == "write")
    if pre:
        print("fixture manifest structural errors:", file=sys.stderr)
        for error in pre:
            print(f"- {error}", file=sys.stderr)
        return 1
    if command == "write":
        write(data)
        data = load()
    errors = validate(data)
    if errors:
        print("fixture manifest verification failed:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1
    print(f"fixture manifest OK: {len(data['fixtures'])} fixtures")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
