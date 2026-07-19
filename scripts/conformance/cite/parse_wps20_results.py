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

    def clean(self) -> bool:
        return self.failed == self.skipped == self.canttell == 0

    def same_as(self, other: "Counts") -> bool:
        return asdict(self) == asdict(other)


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


def parse_result(
    path: Path,
) -> tuple[Path, dict[str, Counts], Counts, Counts, Counts, list[str]]:
    result_file = _find_result(path)
    root = ET.parse(result_file).getroot()
    classes = {key: Counts() for key in CLASS_ALIASES}
    unmatched = Counts()
    configuration_issues = Counts()
    accounting_errors = []

    for test in root.findall(".//test"):
        test_name = test.get("name", "")
        for class_node in test.findall(".//class"):
            key = _class_key(test_name, class_node.get("name", ""))
            for method in class_node.findall(".//test-method"):
                status = method.get("status", "UNKNOWN")
                if method.get("is-config", "false").lower() == "true":
                    normalized = status.upper()
                    if normalized not in {"PASS", "PASSED", "SUCCESS"}:
                        configuration_issues.add(status)
                    continue
                if key is None:
                    unmatched.add(status)
                else:
                    classes[key].add(status)

    observed = Counts()
    for counts in classes.values():
        observed.merge(counts)
    observed.merge(unmatched)

    raw = Counts()
    raw_attributes = ("total", "passed", "failed", "skipped", "ignored")
    if any(root.get(attribute) is not None for attribute in raw_attributes):
        values = {}
        for attribute in raw_attributes:
            value = int(root.get(attribute, "0"))
            if value < 0:
                raise ValueError(f"TestNG root attribute {attribute} cannot be negative")
            values[attribute] = value
        raw.total = values["total"]
        raw.passed = values["passed"]
        raw.failed = values["failed"]
        raw.skipped = values["skipped"] + values["ignored"]
        accounted = raw.passed + raw.failed + raw.skipped
        if accounted > raw.total:
            accounting_errors.append(
                f"Raw TestNG statuses total {accounted}, exceeding root total {raw.total}"
            )
        raw.canttell = max(raw.total - accounted, 0)
    else:
        raw.merge(observed)

    if not raw.same_as(observed):
        accounting_errors.append(
            "Raw TestNG totals do not match classified and unmatched test methods: "
            f"raw={asdict(raw)}, observed={asdict(observed)}"
        )
    if unmatched.total:
        accounting_errors.append(
            f"Found {unmatched.total} test method(s) outside known WPS conformance classes"
        )
    if configuration_issues.total:
        accounting_errors.append(
            f"Found {configuration_issues.total} failed, skipped, or unknown configuration method(s)"
        )

    return (
        result_file,
        classes,
        raw,
        unmatched,
        configuration_issues,
        accounting_errors,
    )


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


def evaluate_exit_code(
    classes: dict[str, Counts],
    raw: Counts,
    profile: str,
    ets_exit_code: int,
    accounting_errors: list[str],
) -> None:
    if ets_exit_code == 0:
        return

    selected_keys = set(PROFILE_CLASSES[profile])
    unselected = Counts()
    for key, counts in classes.items():
        if key not in selected_keys:
            unselected.merge(counts)

    explained = (
        raw.failed + raw.skipped > 0
        and unselected.failed == raw.failed
        and unselected.skipped == raw.skipped
        and raw.canttell == 0
    )
    if not explained:
        accounting_errors.append(
            f"ETS exit code {ets_exit_code} is not explained solely by failures or skips "
            "in known unselected classes"
        )


def write_outputs(
    result_file: Path,
    classes: dict[str, Counts],
    raw: Counts,
    selected: Counts,
    unmatched: Counts,
    configuration_issues: Counts,
    accounting_errors: list[str],
    profile: str,
    ets_exit_code: int,
    summary_path: Path,
    json_path: Path,
) -> None:
    success_rate = (selected.passed * 100 // selected.total) if selected.total else 0
    status = "passed" if selected.total > 0 and selected.clean() and not accounting_errors else "failed"
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
        "unmatchedTotals": asdict(unmatched),
        "configurationIssueTotals": asdict(configuration_issues),
        "accountingErrors": accounting_errors,
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
        "## Result accounting\n\n"
        f"- Unmatched methods: {unmatched.total}\n"
        f"- Configuration issues: {configuration_issues.total}\n"
        f"- Accounting errors: {len(accounting_errors)}\n"
        + "".join(f"  - {error}\n" for error in accounting_errors)
        + "\n"
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
        (
            result_file,
            classes,
            raw,
            unmatched,
            configuration_issues,
            accounting_errors,
        ) = parse_result(args.input)
        selected = evaluate(classes, args.profile)
        evaluate_exit_code(classes, raw, args.profile, args.ets_exit_code, accounting_errors)
        write_outputs(
            result_file,
            classes,
            raw,
            selected,
            unmatched,
            configuration_issues,
            accounting_errors,
            args.profile,
            args.ets_exit_code,
            args.summary,
            args.json,
        )
    except (ET.ParseError, OSError, ValueError) as error:
        print(f"WPS CITE result parsing failed: {error}", file=sys.stderr)
        return 2

    return 0 if selected.clean() and not accounting_errors else 1


if __name__ == "__main__":
    raise SystemExit(main())
