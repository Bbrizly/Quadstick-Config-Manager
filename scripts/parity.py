#!/usr/bin/env python3
"""Run the complete frozen-C# <-> Rust format parity gate with one command."""
from __future__ import annotations

import importlib.util
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
ORACLE = ROOT / "fixtures" / "oracle"

CATALOG_FINGERPRINTS = {
    "src/QuadStick.Format/Data/validation.json": "dd793c1cd768f703dd3b6255c990f2e1c8ed2332",
    "src/QuadStick.Format/Data/preferences.json": "71914f9691d890d4ab8a6f32fc76d7e8005c8f68",
    "src/QuadStick.Format/Templates/default-template.csv": "21f23998d00de044a9bd1bc0899809c3f95a49b8",
    "tests/QuadStick.Format.Tests/corpus/firmware-2373.json": "7f90d32e6efa819c8eacbfa0eab9184bfccb509b",
}

ORACLE_PROJECTS = [
    "tools/QcmOracle/QcmOracle.csproj",
    "tools/QcmCsvOracle/QcmCsvOracle.csproj",
    "tools/QcmCatalogOracle/QcmCatalogOracle.csproj",
    "tools/QcmParserOracle/QcmParserOracle.csproj",
]

RUST_ORACLE_ENV = {
    "QCM_REQUIRE_CSV_ORACLE": "1",
    "QCM_REQUIRE_CATALOG_ORACLE": "1",
    "QCM_REQUIRE_PARSER_ORACLE": "1",
    "QCM_REQUIRE_VALIDATION_ORACLE": "1",
    "QCM_REQUIRE_NORMALIZE_ORACLE": "1",
    "QCM_REQUIRE_EDITOR_ORACLE": "1",
    "QCM_REQUIRE_EDITOR_STATE_ORACLE": "1",
}


def banner(label: str) -> None:
    print(f"\n== {label} ==", flush=True)


def run(args: list[str], *, env: dict[str, str] | None = None) -> None:
    print("+ " + " ".join(args), flush=True)
    completed = subprocess.run(args, cwd=ROOT, env=env, check=False)
    if completed.returncode != 0:
        raise SystemExit(completed.returncode)


def run_text(args: list[str]) -> str:
    completed = subprocess.run(
        args,
        cwd=ROOT,
        text=True,
        stdout=subprocess.PIPE,
        stderr=None,
        check=False,
    )
    if completed.returncode != 0:
        raise SystemExit(completed.returncode)
    return completed.stdout.strip()


def run_to_file(args: list[str], path: Path) -> None:
    print("+ " + " ".join(args) + f" > {path.relative_to(ROOT)}", flush=True)
    with path.open("w", encoding="utf-8", newline="") as stream:
        completed = subprocess.run(
            args,
            cwd=ROOT,
            text=True,
            stdout=stream,
            stderr=None,
            check=False,
        )
    if completed.returncode != 0:
        raise SystemExit(completed.returncode)


def dotnet_run(project: str, *args: str, env: dict[str, str] | None = None) -> None:
    run(
        ["dotnet", "run", "--no-build", "--project", project, "-c", "Release", "--", *args],
        env=env,
    )


def dotnet_run_to_file(project: str, output: Path, *args: str) -> None:
    run_to_file(
        ["dotnet", "run", "--no-build", "--project", project, "-c", "Release", "--", *args],
        output,
    )


def ensure_jsonschema() -> None:
    if importlib.util.find_spec("jsonschema") is not None:
        return
    banner("Install pinned schema validator")
    run(
        [
            sys.executable,
            "-m",
            "pip",
            "install",
            "--disable-pip-version-check",
            "jsonschema==4.26.0",
        ]
    )


def verify_fingerprints() -> None:
    banner("Frozen source fingerprints")
    for relative, expected in CATALOG_FINGERPRINTS.items():
        actual = run_text(["git", "hash-object", relative])
        if actual != expected:
            print(
                f"fingerprint mismatch: {relative}\n"
                f"  expected {expected}\n"
                f"  actual   {actual}",
                file=sys.stderr,
            )
            raise SystemExit(1)
        print(f"OK {relative}")


def generate_oracles() -> None:
    banner("Build legacy oracle tools once")
    for project in ORACLE_PROJECTS:
        run(["dotnet", "build", project, "--nologo", "-c", "Release"])

    banner("Oracle determinism / host culture")
    culture_c = os.environ.copy()
    culture_c["LANG"] = "C"
    dotnet_run("tools/QcmOracle/QcmOracle.csproj", "selfcheck", env=culture_c)
    culture_fr = os.environ.copy()
    culture_fr["LANG"] = "fr_FR.UTF-8"
    dotnet_run("tools/QcmOracle/QcmOracle.csproj", "selfcheck", env=culture_fr)

    banner("Generate canonical legacy artifacts")
    ORACLE.mkdir(parents=True, exist_ok=True)
    dotnet_run("tools/QcmOracle/QcmOracle.csproj", "generate")
    dotnet_run(
        "tools/QcmCsvOracle/QcmCsvOracle.csproj",
        "fixtures/manifest.json",
        "fixtures/oracle",
    )
    dotnet_run(
        "tools/QcmCatalogOracle/QcmCatalogOracle.csproj",
        "fixtures/oracle/catalog-canonical.txt",
    )
    dotnet_run(
        "tools/QcmParserOracle/QcmParserOracle.csproj",
        "fixtures/manifest.json",
        "fixtures/oracle",
    )
    dotnet_run_to_file(
        "tools/QcmOracle/QcmOracle.csproj",
        ORACLE / "task15-core.apply.json",
        "apply-canonical",
        "fixtures/profiles/profile-headerless.csv",
        "fixtures/ops/task15-core.json",
    )
    dotnet_run_to_file(
        "tools/QcmOracle/QcmOracle.csproj",
        ORACLE / "task16-state.apply.json",
        "apply-canonical",
        "fixtures/profiles/profile-headerless.csv",
        "fixtures/ops/task16-state.json",
    )

    ensure_jsonschema()
    run([sys.executable, "tools/oracle/validate.py"])


def main() -> int:
    banner("Fixture manifest")
    run([sys.executable, "tools/fixtures/manifest.py", "verify"])
    verify_fingerprints()

    banner("Frozen legacy tests")
    run(["dotnet", "test", "QuadStick.sln", "--nologo", "-c", "Release"])
    generate_oracles()

    banner("Rust quality gate")
    run(["cargo", "fmt", "--all", "--", "--check"])
    run(["cargo", "clippy", "--workspace", "--all-targets", "--locked", "--", "-D", "warnings"])

    banner("Differential parity matrix")
    rust_env = os.environ.copy()
    rust_env.update(RUST_ORACLE_ENV)
    run(["cargo", "test", "--workspace", "--locked"], env=rust_env)

    print("\nQCM differential parity: PASS", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
