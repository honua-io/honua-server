#!/usr/bin/env python3
"""Parse WPS 2.0 ETS TestNG output for an OGC certification path."""

from __future__ import annotations

import argparse
import json
import sys
import xml.etree.ElementTree as ET
from dataclasses import asdict, dataclass
from pathlib import Path


CLASS_ALIASES = {
    "basic": ("basictests", ".basictests."),
    "async": ("asynchronous", ".asynchronous."),
    "sync": ("synchronous", ".synchronous."),
}
PROFILE_CLASSES = {
    "basic-async": ("basic", "async"),
    "basic-sync": ("basic", "sync"),
    "all": ("basic", "sync", "async"),
}


@dataclass
class Counts:
    total: int = 0
    passed: int = 0
    failed: int = 0
    skipped: int = 0
    canttell: int = 0

    def add(self, status: str) -> None:
        self.total += 1
        normalized = status.upper()
        if normalized in {"PASS", "PASSED", "SUCCESS"}:
            self.passed += 1
        elif normalized in {"FAIL", "FAILED", "FAILURE"}:
            self.failed += 1
        elif normalized in {"SKIP", "SKIPPED", "IGNORED"}:
            self.skipped += 1
        else:
            self.canttell += 1

    def merge(self, other: "Counts") -> None:
        self.total += other.total
        self.passed += other.passed
        self.failed += other.failed
        self.skipped += other.skipped
        self.canttell += other.canttell


def _find_result(path: Path) -> Path:
    if path.is_file():
        return path
    matches = sorted(path.rglob("testng-results.xml"), key=lambda item: item.stat().st_mtime)
    if not matches:
        raise ValueError(f"No testng-results.xml found below {path}")
    return matches[-1]


def _class_key(test_name: str, class_name: str) -> str | None:
    combined = f"{test_name} {class_name}".lower()
    for key, aliases in CLASS_ALIASES.items():
        if any(alias in combined for alias in aliases):
            return key
    return None


def parse_result(path: Path) -> tuple[Path, dict[str, Counts], Counts]:
    result_file = _find_result(path)
    root = ET.parse(result_file).getroot()
    classes = {key: Counts() for key in CLASS_ALIASES}

    for test in root.findall(".//test"):
        test_name = test.get("name", "")
        for class_node in test.findall(".//class"):
            key = _class_key(test_name, class_node.get("name", ""))
            if key is None:
                continue
            for method in class_node.findall(".//test-method"):
                if method.get("is-config", "false").lower() == "true":
                    continue
                classes[key].add(method.get("status", "UNKNOWN"))

    raw = Counts()
    raw.total = int(root.get("total", "0"))
    raw.passed = int(root.get("passed", "0"))
    raw.failed = int(root.get("failed", "0"))
    raw.skipped = int(root.get("skipped", "0")) + int(root.get("ignored", "0"))
    accounted = raw.passed + raw.failed + raw.skipped
    raw.canttell = max(raw.total - accounted, 0)
    if raw.total == 0:
        for counts in classes.values():
            raw.merge(counts)

    return result_file, classes, raw


def evaluate(classes: dict[str, Counts], profile: str) -> Counts:
    selected = Counts()
    missing = []
    for key in PROFILE_CLASSES[profile]:
        counts = classes[key]
        if counts.total == 0:
            missing.append(key)
        selected.merge(counts)
    if missing:
        raise ValueError(f"Selected conformance classes produced no tests: {', '.join(missing)}")
    return selected


def write_outputs(
    result_file: Path,
    classes: dict[str, Counts],
    raw: Counts,
    selected: Counts,
    profile: str,
    ets_exit_code: int,
    summary_path: Path,
    json_path: Path,
) -> None:
    success_rate = (selected.passed * 100 // selected.total) if selected.total else 0
    status = (
        "passed"
        if selected.total > 0
        and selected.failed == 0
        and selected.skipped == 0
        and selected.canttell == 0
        else "failed"
    )
    payload = {
        "suite": "ets-wps20",
        "version": "1.1",
        "sourceCommit": "e2acc691440fad98d32e873a6b7237c9d759b8df",
        "profile": profile,
        "selectedClasses": list(PROFILE_CLASSES[profile]),
        "status": status,
        "etsExitCode": ets_exit_code,
        "resultFile": str(result_file),
        "selectedTotals": asdict(selected),
        "rawTotals": asdict(raw),
        "classes": {key: asdict(value) for key, value in classes.items()},
    }
    json_path.parent.mkdir(parents=True, exist_ok=True)
    json_path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")

    rows = []
    selected_keys = set(PROFILE_CLASSES[profile])
    for key in ("basic", "sync", "async"):
        counts = classes[key]
        rows.append(
            f"| {key} | {'yes' if key in selected_keys else 'no'} | {counts.total} | "
            f"{counts.passed} | {counts.failed} | {counts.skipped} | {counts.canttell} |"
        )

    summary_path.write_text(
        "# WPS 2.0 CITE Conformance Test Results\n\n"
        "## Summary\n\n"
        f"- **Total Tests**: {selected.total}\n"
        f"- **Passed**: {selected.passed}\n"
        f"- **Failed**: {selected.failed}\n"
        f"- **Skipped**: {selected.skipped}\n"
        f"- **CantTell**: {selected.canttell}\n"
        f"- **Success Rate**: {success_rate}%\n\n"
        "These headline counts cover only the conformance classes selected by the "
        "profile. Complete upstream ETS counts remain below and in the raw TestNG output.\n\n"
        "## Conformance classes\n\n"
        "| Class | Selected | Total | Passed | Failed | Skipped | CantTell |\n"
        "|---|---|---:|---:|---:|---:|---:|\n"
        + "\n".join(rows)
        + "\n\n## Raw ETS totals\n\n"
        f"- Total: {raw.total}\n"
        f"- Passed: {raw.passed}\n"
        f"- Failed: {raw.failed}\n"
        f"- Skipped: {raw.skipped}\n"
        f"- CantTell: {raw.canttell}\n"
        f"- ETS process exit code: {ets_exit_code}\n\n"
        "## Environment\n\n"
        f"- Profile: `{profile}`\n"
        "- CITE Suite: `ets-wps20` 1.1\n"
        "- Source commit: `e2acc691440fad98d32e873a6b7237c9d759b8df`\n",
        encoding="utf-8",
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--profile", required=True, choices=PROFILE_CLASSES)
    parser.add_argument("--summary", required=True, type=Path)
    parser.add_argument("--json", required=True, type=Path)
    parser.add_argument("--ets-exit-code", type=int, default=0)
    args = parser.parse_args(argv)

    try:
        result_file, classes, raw = parse_result(args.input)
        selected = evaluate(classes, args.profile)
        write_outputs(
            result_file,
            classes,
            raw,
            selected,
            args.profile,
            args.ets_exit_code,
            args.summary,
            args.json,
        )
    except (ET.ParseError, OSError, ValueError) as error:
        print(f"WPS CITE result parsing failed: {error}", file=sys.stderr)
        return 2

    return 0 if selected.failed == selected.skipped == selected.canttell == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
