#!/usr/bin/env python3
"""Turn repeated per-shard flake-hunt TRX runs into one flake-candidate report.

The flake hunt runs a bounded rotating window of `.github/ci-shards.json` shards
`--iterations` times each. Every iteration writes
`flake-<artifact-suffix>__iter<N>.trx`; this script groups those files by shard,
compares each test's outcomes across that shard's iterations, and reports the
tests that were not consistent.

A test is a flake candidate when, inside ONE shard, it both passed and failed
across the iterations that actually ran it. Tests that did not run in every
iteration of their shard are excluded: a shard whose second iteration aborted
early would otherwise make every remaining test look "inconsistent".
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


def parse_trx(path: Path) -> list[tuple[str, str, str]]:
    """Return (testName, outcome, first error line) for each result in a TRX."""
    root = ET.parse(path).getroot()
    rows: list[tuple[str, str, str]] = []
    for node in root.findall(".//{*}UnitTestResult"):
        name = node.attrib.get("testName") or "unknown"
        outcome = (node.attrib.get("outcome") or "Other").lower()
        message = ""
        found = node.find(".//{*}ErrorInfo/{*}Message")
        if found is not None and found.text:
            message = found.text.strip().splitlines()[0][:200]
        rows.append((name, outcome, message))
    return rows


def discover(results: Path) -> dict[str, dict[int, Path]]:
    """Map shard suffix -> {iteration: trx path} for every recognised TRX file."""
    found: dict[str, dict[int, Path]] = defaultdict(dict)
    for path in sorted(results.rglob("flake-*__iter*.trx")):
        match = TRX_NAME.match(path.name)
        if not match:
            continue
        found[match.group("shard")][int(match.group("iteration"))] = path
    return dict(found)


def summarize(results: Path) -> dict:
    discovered = discover(results)
    shards: list[dict] = []
    candidates: list[dict] = []

    for shard in sorted(discovered):
        iterations = discovered[shard]
        counts: dict[str, dict] = defaultdict(
            lambda: {"pass": 0, "fail": 0, "other": 0, "messages": []}
        )
        for _, path in sorted(iterations.items()):
            for name, outcome, message in parse_trx(path):
                bucket = counts[name]
                if outcome == "passed":
                    bucket["pass"] += 1
                elif outcome == "failed":
                    bucket["fail"] += 1
                    if message:
                        bucket["messages"].append(message)
                else:
                    bucket["other"] += 1

        observed = len(iterations)
        shard_candidates = []
        for name, bucket in counts.items():
            total = bucket["pass"] + bucket["fail"] + bucket["other"]
            if total < observed:
                continue
            if bucket["pass"] > 0 and bucket["fail"] > 0:
                shard_candidates.append(
                    {
                        "shard": shard,
                        "name": name,
                        "pass": bucket["pass"],
                        "fail": bucket["fail"],
                        "iterations": total,
                        "sample_message": (bucket["messages"] or [""])[0],
                    }
                )
        shard_candidates.sort(key=lambda item: (-item["fail"], item["name"]))
        candidates.extend(shard_candidates)
        shards.append(
            {
                "shard": shard,
                "iterations_observed": observed,
                "tests_seen": len(counts),
                "flaky_count": len(shard_candidates),
            }
        )

    return {
        "contract": CONTRACT,
        "shards_observed": len(shards),
        "tests_seen": sum(item["tests_seen"] for item in shards),
        "flaky_count": len(candidates),
        "shards": shards,
        "flaky_candidates": candidates,
    }


def render_markdown(report: dict) -> str:
    lines = [
        "# Flaky Test Detection",
        "",
        f"- Shards observed: {report['shards_observed']}",
        f"- Tests observed: {report['tests_seen']}",
        f"- Flake candidates: {report['flaky_count']}",
        "",
    ]
    if report["shards"]:
        lines += ["| Shard | Iterations | Tests | Candidates |", "|---|---:|---:|---:|"]
        for shard in report["shards"]:
            lines.append(
                f"| `{shard['shard']}` | {shard['iterations_observed']} "
                f"| {shard['tests_seen']} | {shard['flaky_count']} |"
            )
        lines.append("")
    if report["flaky_candidates"]:
        lines += ["| Shard | Test | Pass | Fail | Sample failure |", "|---|---|---:|---:|---|"]
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
    args = parser.parse_args()

    report = summarize(args.results)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    if args.markdown:
        args.markdown.parent.mkdir(parents=True, exist_ok=True)
        args.markdown.write_text(render_markdown(report), encoding="utf-8")
    print(
        f"flaky-detection shards={report['shards_observed']} "
        f"tests={report['tests_seen']} candidates={report['flaky_count']}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
