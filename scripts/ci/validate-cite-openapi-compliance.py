#!/usr/bin/env python3
"""CI gate for CITE evidence table parity across docs and OpenAPI metadata.

Ensures the canonical ``docs/cite-status.md`` suite totals align with all
committed OpenAPI vendor extensions that declare CITE suites.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[2]

CITE_STATUS_PATH = REPO_ROOT / "docs" / "cite-status.md"

# Only committed source OpenAPI specs are part of this gate.
OPENAPI_FILES = (
    REPO_ROOT / "src" / "Honua.Server" / "openapi.json",
    REPO_ROOT / "src" / "Honua.Server" / "ogc-tiles-openapi.json",
    REPO_ROOT / "src" / "Honua.Server" / "ogc-processes-openapi.json",
    REPO_ROOT / "src" / "Honua.Server" / "ogc-maps-openapi.json",
    REPO_ROOT / "src" / "Honua.Server" / "ogc-coverages-openapi.json",
)


def parse_cite_status(path: Path) -> dict[tuple[str, str], dict[str, Any]]:
    """Parse suite rows from ``docs/cite-status.md`` into a canonical map."""
    text = path.read_text(encoding="utf-8")
    row_pattern = re.compile(
        r"^\|\s*([^|]+?)\s*\|\s*`?([^|`]+?)`?\s*\|\s*(\d+)\s*/\s*(\d+)\s*\|\s*([\d.]+)%\s*\|",
        re.MULTILINE,
    )
    out: dict[tuple[str, str], dict[str, Any]] = {}
    for suite, profile, passed, total, pass_rate in row_pattern.findall(text):
        if suite.strip() in {"Suite", "---"}:
            continue
        out[(suite.strip(), profile.strip())] = {
            "passed": int(passed),
            "total": int(total),
            "passRate": float(pass_rate),
        }
    return out


def parse_openapi_suites(extension: dict[str, Any]) -> dict[tuple[str, str], dict[str, Any]]:
    """Normalize ``x-honua-cite-compliance.suites`` to the same canonical map shape."""
    raw = extension.get("suites")
    if not isinstance(raw, list):
        return {}

    suites: dict[tuple[str, str], dict[str, Any]] = {}
    for item in raw:
        if not isinstance(item, dict):
            continue
        suite = str(item.get("suite", "")).strip()
        profile = str(item.get("profile", "")).strip()
        if not suite:
            continue
        passed = int(item.get("passed", 0))
        total = int(item.get("total", 0))
        raw_rate = str(item.get("passRate", "0%"))
        pass_rate = float(raw_rate[:-1]) if raw_rate.endswith("%") else float(raw_rate)
        suites[(suite, profile)] = {
            "passed": passed,
            "total": total,
            "passRate": pass_rate,
        }
    return suites


def normalize_source(source: Any) -> str:
    return str(source or "").strip() if source is not None else ""


def main() -> int:
    if not CITE_STATUS_PATH.exists():
        print(f"::error::canonical source not found: {CITE_STATUS_PATH}", file=sys.stderr)
        return 1

    cite_status = parse_cite_status(CITE_STATUS_PATH)
    if not cite_status:
        print(f"::error::no suite rows parsed from {CITE_STATUS_PATH}", file=sys.stderr)
        return 1

    failed = False
    for openapi_path in OPENAPI_FILES:
        if not openapi_path.exists():
            print(f"::error::{openapi_path} is missing", file=sys.stderr)
            failed = True
            continue

        openapi = json.loads(openapi_path.read_text(encoding="utf-8"))
        compliance = openapi.get("info", {}).get("x-honua-cite-compliance")
        if not isinstance(compliance, dict):
            print(
                f"::error::{openapi_path} is missing or malformed "
                "x-honua-cite-compliance metadata; this is required for drift gating.",
                file=sys.stderr,
            )
            failed = True
            continue

        source = normalize_source(compliance.get("authoritativeSource"))
        if source != "docs/cite-status.md":
            print(
                f"::error::{openapi_path} has authoritativeSource={source!r}, expected 'docs/cite-status.md'",
                file=sys.stderr,
            )
            failed = True

        suites = parse_openapi_suites(compliance)
        for key, openapi_values in suites.items():
            if key not in cite_status:
                print(
                    f"::error::{openapi_path} declares suite {key[0]!r} profile {key[1]!r}, which is missing in {CITE_STATUS_PATH}",
                    file=sys.stderr,
                )
                failed = True
                continue

            expected = cite_status[key]
            mismatches = [
                (field, openapi_value, expected[field])
                for field, openapi_value in openapi_values.items()
                if expected.get(field) != openapi_value
            ]
            if mismatches:
                msg_parts = ", ".join(
                    f"{field}={actual} (expected {expected_value})"
                    for field, actual, expected_value in mismatches
                )
                print(
                    f"::error::{openapi_path} suite {key[0]!r}/{key[1]!r} mismatch: {msg_parts}",
                    file=sys.stderr,
                )
                failed = True

    if failed:
        print(
            "::error::CITE docs to OpenAPI compliance metadata drift detected. "
            "Update docs/cite-status.md first, then refresh and commit OpenAPI vendor extension suites.",
            file=sys.stderr,
        )
        return 1

    print("CITE compliance metadata matches docs/cite-status.md.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
