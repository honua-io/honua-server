#!/usr/bin/env python3
"""Turn repeated per-shard flake-hunt TRX runs into one flake-candidate report.

The flake hunt runs a bounded rotating window of `.github/ci-shards.json` shards
(or one ad-hoc project) `--expect-iterations` times each. Every iteration writes
`flake-<suffix>__iter<N>.trx`; this script groups those files by selection,
compares each test's outcomes across that selection's iterations, and reports the
tests that were not consistent.

A test is a flake candidate when, inside ONE selection, it both passed and failed
across the iterations that actually ran it. Two things this deliberately does NOT
do, because both manufacture false candidates:

* it does not merge distinct test cases that share a display name (xUnit theory
  rows), so results are keyed by the TRX `testId` when present; and
* it does not treat several rows of one test in a SINGLE iteration as several
  trials — they are collapsed to one outcome per iteration (any failure wins).

Missing or unparseable evidence is a hard error, not an empty green report:
`--expect-shards` / `--expect-iterations` assert that the run actually produced
what it planned to produce.
"""

from __future__ import annotations

import argparse
import json
import re
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path

CONTRACT = "honua.flaky-detection/v1"
TRX_NAME = re.compile(r"^flake-(?P<shard>.+)__iter(?P<iteration>\d+)\.trx$")


def parse_trx(path: Path) -> list[tuple[str, str, str, str]]:
    """Return (testId, testName, outcome, first error line) for each TRX result.

    Raises ET.ParseError for a truncated/empty file; callers decide what that
    means for the shard.
    """
    root = ET.parse(path).getroot()
    rows: list[tuple[str, str, str, str]] = []
    for node in root.findall(".//{*}UnitTestResult"):
        name = node.attrib.get("testName") or "unknown"
        # testId is VSTest's stable per-test-case identity; it is what separates
        # two theory rows that render the same display name. executionId is
        # per-run and must never be used as an identity key.
        test_id = node.attrib.get("testId") or ""
        outcome = (node.attrib.get("outcome") or "Other").lower()
        message = ""
        found = node.find(".//{*}ErrorInfo/{*}Message")
        if found is not None and found.text:
            message = found.text.strip().splitlines()[0][:200]
        rows.append((test_id, name, outcome, message))
    return rows


def discover(results: Path) -> dict[str, dict[int, Path]]:
    """Map selection suffix -> {iteration: trx path} for every recognised file."""
    found: dict[str, dict[int, Path]] = defaultdict(dict)
    for path in sorted(results.rglob("flake-*__iter*.trx")):
        match = TRX_NAME.match(path.name)
        if not match:
            continue
        found[match.group("shard")][int(match.group("iteration"))] = path
    return dict(found)


def _collapse_iteration(rows: list[tuple[str, str, str, str]]) -> dict[tuple[str, str], tuple[str, str]]:
    """One outcome per test per iteration. Any failure in the iteration wins."""
    collapsed: dict[tuple[str, str], tuple[str, str]] = {}
    for test_id, name, outcome, message in rows:
        key = (test_id, name)
        previous = collapsed.get(key)
        if previous is None:
            collapsed[key] = (outcome, message)
            continue
        if previous[0] != "failed" and outcome == "failed":
            collapsed[key] = (outcome, message)
    return collapsed


def summarize(results: Path) -> dict:
    discovered = discover(results)
    shards: list[dict] = []
    candidates: list[dict] = []
    unparseable: list[dict] = []

    for shard in sorted(discovered):
        iterations = discovered[shard]
        counts: dict[tuple[str, str], dict] = defaultdict(
            lambda: {"pass": 0, "fail": 0, "other": 0, "messages": []}
        )
        parsed = 0
        for iteration, path in sorted(iterations.items()):
            try:
                rows = parse_trx(path)
            except ET.ParseError as error:
                # A shard SIGKILLed mid-flush leaves a truncated TRX. Record it
                # and keep going: one damaged file must not destroy the report
                # for every other selection in the window.
                unparseable.append(
                    {"shard": shard, "iteration": iteration, "file": path.name, "error": str(error)}
                )
                continue
            parsed += 1
            for key, (outcome, message) in _collapse_iteration(rows).items():
                bucket = counts[key]
                if outcome == "passed":
                    bucket["pass"] += 1
                elif outcome == "failed":
                    bucket["fail"] += 1
                    if message:
                        bucket["messages"].append(message)
                else:
                    bucket["other"] += 1

        shard_candidates = []
        for (test_id, name), bucket in counts.items():
            total = bucket["pass"] + bucket["fail"] + bucket["other"]
            # Only tests observed in EVERY parsed iteration are comparable.
            if total < parsed:
                continue
            if bucket["pass"] > 0 and bucket["fail"] > 0:
                shard_candidates.append(
                    {
                        "shard": shard,
                        "name": name,
                        "test_id": test_id,
                        "pass": bucket["pass"],
                        "fail": bucket["fail"],
                        "iterations": total,
                        "sample_message": (bucket["messages"] or [""])[0],
                    }
                )
        shard_candidates.sort(key=lambda item: (-item["fail"], item["name"], item["test_id"]))
        candidates.extend(shard_candidates)
        shards.append(
            {
                "shard": shard,
                "iterations_found": len(iterations),
                "iterations_parsed": parsed,
                "tests_seen": len(counts),
                "flaky_count": len(shard_candidates),
            }
        )

    return {
        "contract": CONTRACT,
        "shards_observed": len(shards),
        "tests_seen": sum(item["tests_seen"] for item in shards),
        "flaky_count": len(candidates),
        "unparseable_count": len(unparseable),
        "shards": shards,
        "unparseable": unparseable,
        "flaky_candidates": candidates,
    }


def coverage_problems(report: dict, expect_shards: int | None, expect_iterations: int | None) -> list[str]:
    """Evidence-completeness assertions. Empty list means the run is reportable."""
    problems: list[str] = []
    if expect_shards is not None and report["shards_observed"] < expect_shards:
        problems.append(
            f"expected TRX evidence from {expect_shards} selection(s), found {report['shards_observed']}"
        )
    if expect_iterations is not None:
        for shard in report["shards"]:
            if shard["iterations_parsed"] < expect_iterations:
                problems.append(
                    f"{shard['shard']}: parsed {shard['iterations_parsed']} of "
                    f"{expect_iterations} expected iteration(s)"
                    + (
                        f" ({shard['iterations_found']} TRX file(s) present)"
                        if shard["iterations_found"] != shard["iterations_parsed"]
                        else ""
                    )
                )
    return problems


def render_markdown(report: dict, problems: list[str] | None = None) -> str:
    lines = [
        "# Flaky Test Detection",
        "",
        f"- Selections observed: {report['shards_observed']}",
        f"- Tests observed: {report['tests_seen']}",
        f"- Flake candidates: {report['flaky_count']}",
        f"- Unparseable TRX files: {report['unparseable_count']}",
        "",
    ]
    if problems:
        lines += ["## Incomplete evidence", ""]
        lines += [f"- {problem}" for problem in problems]
        lines.append("")
    if report["shards"]:
        lines += [
            "| Selection | Iterations parsed | Tests | Candidates |",
            "|---|---:|---:|---:|",
        ]
        for shard in report["shards"]:
            lines.append(
                f"| `{shard['shard']}` | {shard['iterations_parsed']}/{shard['iterations_found']} "
                f"| {shard['tests_seen']} | {shard['flaky_count']} |"
            )
        lines.append("")
    if report["flaky_candidates"]:
        lines += ["| Selection | Test | Pass | Fail | Sample failure |", "|---|---|---:|---:|---|"]
        for item in report["flaky_candidates"]:
            message = item["sample_message"].replace("|", r"\|")
            lines.append(
                f"| `{item['shard']}` | `{item['name']}` | {item['pass']} "
                f"| {item['fail']} | {message} |"
            )
    else:
        lines.append("No flake candidates detected in this window.")
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--results", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--markdown", type=Path)
    parser.add_argument("--expect-shards", type=int)
    parser.add_argument("--expect-iterations", type=int)
    args = parser.parse_args()

    report = summarize(args.results)
    problems = coverage_problems(report, args.expect_shards, args.expect_iterations)
    report["coverage_problems"] = problems
    report["complete"] = not problems

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    if args.markdown:
        args.markdown.parent.mkdir(parents=True, exist_ok=True)
        args.markdown.write_text(render_markdown(report, problems), encoding="utf-8")

    print(
        f"flaky-detection selections={report['shards_observed']} "
        f"tests={report['tests_seen']} candidates={report['flaky_count']} "
        f"unparseable={report['unparseable_count']} complete={report['complete']}"
    )
    for problem in problems:
        print(f"::error::flake-hunt evidence incomplete — {problem}")
    return 1 if problems else 0


if __name__ == "__main__":
    raise SystemExit(main())
