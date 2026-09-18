#!/usr/bin/env python3
"""Run the QCM C# -> Rust format compatibility suite from one entrypoint.

Default: regenerate every current C# oracle artifact, validate it, then run the
strict Rust format checks with every oracle marked required.

CI can split the same command across jobs:
    python3 scripts/parity.py --generate-only
    python3 scripts/parity.py --rust-only
"""
from __future__ import annotations

import argparse
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
CONFIG = "Release"

ORACLE_PROJECTS = (
    "tools/QcmOracle/QcmOracle.csproj",
    "tools/QcmCsvOracle/QcmCsvOracle.csproj",
    "tools/QcmCatalogOracle/QcmCatalogOracle.csproj",
    "tools/QcmParserOracle/QcmParserOracle.csproj",
)

REQUIRED_ORACLE_ENV = {
    "QCM_REQUIRE_CSV_ORACLE": "1",
    "QCM_REQUIRE_CATALOG_ORACLE": "1",
    "QCM_REQUIRE_PARSER_ORACLE": "1",
    "QCM_REQUIRE_VALIDATION_ORACLE": "1",
    "QCM_REQUIRE_NORMALIZE_ORACLE": "1",
    "QCM_REQUIRE_EDITOR_ORACLE": "1",
    "QCM_REQUIRE_EDITOR_STATE_ORACLE": "1",
}


def command_text(args: list[str]) -> str:
    return " ".join(args)


def run(args: list[str], *, env: dict[str, str] | None = None) -> None:
    print(f"\n==> {command_text(args)}", flush=True)
    subprocess.run(args, cwd=ROOT, env=env, check=True)


def run_to_file(args: list[str], destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    print(f"\n==> {command_text(args)} > {destination.relative_to(ROOT)}", flush=True)
    with destination.open("w", encoding="utf-8", newline="\n") as output:
        subprocess.run(args, cwd=ROOT, stdout=output, text=True, check=True)


def dotnet_run(project: str, *tool_args: str) -> list[str]:
    return [
        "dotnet",
        "run",
        "--project",
        project,
        "-c",
        CONFIG,
        "--no-build",
        "--",
        *tool_args,
    ]


def generate_oracles() -> None:
    run([sys.executable, "tools/fixtures/manifest.py", "verify"])

    for project in ORACLE_PROJECTS:
        run(["dotnet", "build", project, "-c", CONFIG, "--nologo"])

    # Determinism/culture checks stay part of the oracle boundary even when the
    # caller is not running the full legacy solution test suite.
    for locale in ("C", "fr_FR.UTF-8"):
        env = os.environ.copy()
        env["LANG"] = locale
        run(dotnet_run("tools/QcmOracle/QcmOracle.csproj", "selfcheck"), env=env)

    run(dotnet_run("tools/QcmOracle/QcmOracle.csproj", "generate"))
    run(
        dotnet_run(
            "tools/QcmCsvOracle/QcmCsvOracle.csproj",
            "fixtures/manifest.json",
            "fixtures/oracle",
        )
    )
    run(
        dotnet_run(
            "tools/QcmCatalogOracle/QcmCatalogOracle.csproj",
            "fixtures/oracle/catalog-canonical.txt",
        )
    )
    run(
        dotnet_run(
            "tools/QcmParserOracle/QcmParserOracle.csproj",
            "fixtures/manifest.json",
            "fixtures/oracle",
        )
    )
    run_to_file(
        dotnet_run(
            "tools/QcmOracle/QcmOracle.csproj",
            "apply-canonical",
            "fixtures/profiles/profile-headerless.csv",
            "fixtures/ops/task15-core.json",
        ),
        ROOT / "fixtures/oracle/task15-core.apply.json",
    )
    run_to_file(
        dotnet_run(
            "tools/QcmOracle/QcmOracle.csproj",
            "apply-canonical",
            "fixtures/profiles/profile-headerless.csv",
            "fixtures/ops/task16-state.json",
        ),
        ROOT / "fixtures/oracle/task16-state.apply.json",
    )

    # jsonschema is deliberately a pinned developer/CI prerequisite instead of
    # silently pip-installing into the caller's environment.
    try:
        __import__("jsonschema")
    except ImportError as error:
        raise SystemExit(
            "jsonschema is required for parity oracle validation. "
            "Install jsonschema==4.26.0, then rerun this command."
        ) from error
    run([sys.executable, "tools/oracle/validate.py"])


def rust_parity() -> None:
    env = os.environ.copy()
    env.update(REQUIRED_ORACLE_ENV)
    run(["cargo", "fmt", "--all", "--", "--check"], env=env)
    run(
        ["cargo", "clippy", "--workspace", "--all-targets", "--locked", "--", "-D", "warnings"],
        env=env,
    )
    run(["cargo", "test", "--workspace", "--locked"], env=env)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument(
        "--generate-only",
        action="store_true",
        help="verify fixtures and regenerate/validate all C# oracle artifacts",
    )
    mode.add_argument(
        "--rust-only",
        action="store_true",
        help="run strict Rust checks using already-generated oracle artifacts",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        if not args.rust_only:
            generate_oracles()
        if not args.generate_only:
            rust_parity()
    except FileNotFoundError as error:
        missing = error.filename or "required executable"
        print(f"parity prerequisite not found: {missing}", file=sys.stderr)
        return 2
    except subprocess.CalledProcessError as error:
        print(
            f"\nPARITY FAILED: {command_text([str(x) for x in error.cmd])} "
            f"exited with {error.returncode}",
            file=sys.stderr,
        )
        return error.returncode or 1

    print("\nQCM format parity OK", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
