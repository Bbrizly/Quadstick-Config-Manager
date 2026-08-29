#!/usr/bin/env python3
"""Validate every generated QCM oracle output against the checked-in schema."""
from __future__ import annotations

import json
import pathlib
import sys

from jsonschema import Draft202012Validator

ROOT = pathlib.Path(__file__).resolve().parents[2]
ORACLE = ROOT / "fixtures" / "oracle"
SCHEMA = ORACLE / "schema.json"


def main() -> int:
    schema = json.loads(SCHEMA.read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema)
    files = sorted(p for p in ORACLE.glob("*.json") if p.name != "schema.json")
    if not files:
        print("no generated oracle JSON found", file=sys.stderr)
        return 1

    failures = 0
    for path in files:
        data = json.loads(path.read_text(encoding="utf-8"))
        errors = sorted(validator.iter_errors(data), key=lambda e: list(e.absolute_path))
        if not errors:
            continue
        failures += 1
        print(f"{path.relative_to(ROOT)}:", file=sys.stderr)
        for error in errors:
            where = ".".join(str(x) for x in error.absolute_path) or "<root>"
            print(f"  {where}: {error.message}", file=sys.stderr)

    if failures:
        print(f"oracle schema validation failed for {failures} file(s)", file=sys.stderr)
        return 1
    print(f"oracle schema OK: {len(files)} generated files")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
