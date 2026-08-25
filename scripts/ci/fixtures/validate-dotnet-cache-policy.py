#!/usr/bin/env python3
"""Validate the NuGet cache key, scope, and write-boundary contract."""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path


DEPENDENCY_PATTERNS = (
    "**/*.csproj",
    "**/*.fsproj",
    "**/*.vbproj",
    "**/*.props",
    "**/*.targets",
    "**/packages.lock.json",
    "**/packages.config",
    "**/global.json",
    "**/NuGet.Config",
    "**/NuGet.config",
    "**/nuget.config",
)


class ContractError(Exception):
    """A structural violation of the NuGet cache policy."""


def _step(source: str, name: str) -> str:
    pattern = re.compile(
        rf"(?ms)^(?P<indent> +)- name: {re.escape(name)}\n"
        rf"(?P<body>.*?)(?=^(?P=indent)- (?:name:|uses:|id:)|\Z)"
    )
    match = pattern.search(source)
    if match is None:
        raise ContractError(f"missing step {name!r}")
    return match.group(0)


def _require(value: str, source: str, message: str) -> None:
    if value not in source:
        raise ContractError(message)


def _validate_key(step: str, label: str) -> str:
    match = re.search(r"^\s+key: (?P<key>.+)$", step, re.MULTILINE)
    if match is None:
        raise ContractError(f"{label} must declare a cache key")
    key = match.group("key")
    _require("-nuget-v2-", key, f"{label} must use the versioned NuGet key")
    for dependency_pattern in DEPENDENCY_PATTERNS:
        _require(
            f"'{dependency_pattern}'",
            key,
            f"{label} key omits dependency input {dependency_pattern}",
        )
    for unstable_component in ("github.run_id", "github.run_attempt", "github.ref", "github.sha"):
        if unstable_component in key:
            raise ContractError(f"{label} key must not include one-off component {unstable_component}")
    _require(
        "${{ github.repository }}-${{ runner.os }}-nuget-v2-",
        key,
        f"{label} must namespace the key by repository and runner OS",
    )
    return key


def validate_action(source: str) -> None:
    _require("  nuget-cache-write:", source, "the action must expose its cache-write policy")
    _require("    default: 'auto'", source, "NuGet cache writes must default to auto")

    fixtures = _step(source, "Validate .NET cache policy fixtures")
    _require(
        "scripts/ci/fixtures/validate-dotnet-cache-policy.py",
        fixtures,
        "the setup action must run the cache policy validator",
    )
    _require(
        "scripts/ci/fixtures/validate-dotnet-cache-policy.test.py",
        fixtures,
        "the setup action must run the cache policy failure-injection tests",
    )

    plan = _step(source, "Plan NuGet cache policy")
    for guard in (
        '"$EVENT_NAME" == "pull_request"',
        '"$EVENT_NAME" == "pull_request_target"',
        '"$REF_NAME" == refs/pull/*',
        '"$REF_NAME" == "refs/heads/$DEFAULT_BRANCH"',
        "auto|false",
        "push|schedule|workflow_dispatch",
        'echo "write=$write" >> "$GITHUB_OUTPUT"',
    ):
        _require(guard, plan, f"NuGet cache planner is missing guard {guard}")

    restore = _step(source, "Restore NuGet packages")
    writer = _step(source, "Cache NuGet packages on trusted default branch")
    _require(
        "if: steps.nuget-cache-policy.outputs.write != 'true'",
        restore,
        "restore-only cache step must be selected when writes are disabled",
    )
    _require("uses: actions/cache/restore@", restore, "untrusted refs must use cache/restore")
    _require(
        "if: steps.nuget-cache-policy.outputs.write == 'true'",
        writer,
        "writable cache step must require the trusted planner output",
    )
    _require("uses: actions/cache@", writer, "trusted refs must retain post-job cache saves")
    if "uses: actions/cache/restore@" in writer:
        raise ContractError("trusted cache writer must use the restore-and-save action")

    restore_key = _validate_key(restore, "restore-only cache")
    writer_key = _validate_key(writer, "trusted cache writer")
    if restore_key != writer_key:
        raise ContractError("restore-only and trusted cache steps must use the same exact key")
    for step, label in ((restore, "restore-only cache"), (writer, "trusted cache writer")):
        _require("-nuget-v2-", step, f"{label} must prefer v2 restore prefixes")
        _require("-${{ runner.os }}-nuget-", step, f"{label} must retain a one-time legacy fallback")


def validate_reusable_sdk_gate(source: str) -> None:
    restore = _step(source, "Restore NuGet cache")
    _require("if: inputs.dotnet-version != ''", restore, "SDK NuGet restore must remain .NET-only")
    _require("uses: actions/cache/restore@", restore, "SDK PR NuGet cache must be restore-only")
    if "uses: actions/cache@" in restore.replace("actions/cache/restore@", ""):
        raise ContractError("SDK PR NuGet cache must not use a writable cache action")
    _validate_key(restore, "SDK PR NuGet cache")


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8").replace("\r\n", "\n")


def main() -> int:
    root = Path(__file__).resolve().parents[3]
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--action", type=Path, default=root / ".github/actions/setup-dotnet-ci/action.yml"
    )
    parser.add_argument(
        "--sdk-workflow",
        type=Path,
        default=root / ".github/workflows/reusable-sdk-pr-gate.yml",
    )
    args = parser.parse_args()
    try:
        validate_action(read(args.action))
        validate_reusable_sdk_gate(read(args.sdk_workflow))
    except ContractError as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1
    print("dotnet-cache-policy=ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
