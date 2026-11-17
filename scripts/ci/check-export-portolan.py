#!/usr/bin/env python3
"""Check a published Export snapshot against the pinned Portolan specification.

Advisory: specification findings are reported in the job summary and as warnings, and
never fail the job. The job fails only when the check itself cannot be trusted: the
snapshot is missing or empty, the validator crashed, or it applied a different
specification than scripts/ci/portolan-pin.v1.json pins.

Usage: check-export-portolan.py SNAPSHOT_DIR REPORT_JSON [--rashid COMMAND]
"""

from __future__ import annotations

import argparse
import json
import os
import shlex
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PIN = ROOT / "scripts/ci/portolan-pin.v1.json"
MAX_ANNOTATIONS = 20


class HarnessError(Exception):
    """The check could not produce a trustworthy result."""


def load_pin(path: Path = PIN) -> dict[str, str]:
    return json.loads(path.read_text(encoding="utf-8"))


def run_validator(command: list[str], snapshot: Path) -> dict:
    if not snapshot.is_dir() or not any(path.is_file() for path in snapshot.rglob("*")):
        raise HarnessError(f"no published Export snapshot under {snapshot}")
    result = subprocess.run([*command, "check", str(snapshot), "--json", "--all", "--data-scope", "local"],
                            capture_output=True, text=True, check=False)
    # rashid exits 0 for a conforming catalog and 1 when it breaks a MUST requirement.
    if result.returncode not in (0, 1):
        raise HarnessError(f"validator exited {result.returncode}: {result.stderr.strip()[-2000:]}")
    try:
        return json.loads(result.stdout)
    except json.JSONDecodeError as error:
        raise HarnessError(f"validator printed no JSON report: {error}") from error


def pin_mismatches(report: dict, pin: dict[str, str]) -> list[str]:
    validator = report.get("validator", {})
    observed = {"validator_version": validator.get("version"), "profile": validator.get("applied_profile"),
                "spec_commit": validator.get("spec_commit")}
    return [f"{key}: pinned {pin[key]}, applied {value}" for key, value in observed.items() if value != pin[key]]


def summary_markdown(report: dict, pin: dict[str, str]) -> str:
    verdict = "conforms" if report.get("passed") else "does not conform"
    lines = [
        "## Export Portolan check (advisory)",
        "",
        f"The published Export {verdict} to Portolan profile {pin['profile']} "
        f"(rashid {pin['validator_version']}, specification {pin['spec_commit'][:12]}): "
        f"{report.get('error_count', 0)} errors, {report.get('warning_count', 0)} warnings, "
        f"{report.get('info_count', 0)} notes in {report.get('files_checked', 0)} files.",
        "",
        "| Rule | Severity | Count | Requirement |",
        "|---|---|---|---|",
    ]
    for rule in report.get("summary", {}).get("by_rule", []):
        lines.append(f"| `{rule['rule_id']}` | {rule['severity']} | {rule['count']} | {rule['description']} |")
    return "\n".join(lines) + "\n"


def escape_annotation(text: str) -> str:
    return text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def annotations(report: dict) -> list[str]:
    errors = [finding for finding in report.get("findings", []) if finding.get("severity") == "error"]
    lines = [f"::warning title=Portolan {finding['rule_id']}::"
             + escape_annotation(f"{finding.get('path') or '.'}: {finding['message']}")
             for finding in errors[:MAX_ANNOTATIONS]]
    if len(errors) > MAX_ANNOTATIONS:
        lines.append(f"::warning title=Portolan::{len(errors) - MAX_ANNOTATIONS} more errors in the report artifact")
    return lines


def check(snapshot: Path, command: list[str], pin: dict[str, str]) -> dict:
    report = run_validator(command, snapshot)
    mismatches = pin_mismatches(report, pin)
    if mismatches:
        raise HarnessError("validator does not match scripts/ci/portolan-pin.v1.json: " + "; ".join(mismatches))
    return report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("snapshot", type=Path)
    parser.add_argument("report", type=Path)
    parser.add_argument("--rashid", default="rashid", help="validator command (default: rashid)")
    arguments = parser.parse_args(argv)
    pin = load_pin()
    try:
        report = check(arguments.snapshot, shlex.split(arguments.rashid), pin)
    except HarnessError as error:
        print(f"::error title=Export Portolan check::{error}")
        return 2
    arguments.report.parent.mkdir(parents=True, exist_ok=True)
    arguments.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    summary = summary_markdown(report, pin)
    print(summary)
    print("\n".join(annotations(report)))
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as handle:
            handle.write(summary)
    return 0


if __name__ == "__main__":
    sys.exit(main())
