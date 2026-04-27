#!/usr/bin/env python3
"""Compare current-run client-compat .cert.json envelopes against committed baselines.

Inputs:
  --baselines DIR   Directory of committed baseline .cert.json files
                    (default: tests/baselines/client-compat).
  --current DIR     Directory of current-run .cert.json files
                    (default: docker/client-compat/output, recursed).
  --gap-report PATH Where to write the Markdown gap report
                    (default: docs/gis/gap-report.md).
  --strict          Exit non-zero if any pass→fail regression is detected.

The script identifies envelopes by ``(client_lane, protocol)`` rather than by
filename so that ``run_id`` differences do not cause spurious diffs. For each
shared (lane, protocol, test_case_id) tuple it classifies the change:

  * regression: baseline ``pass`` → current ``fail``
  * improvement: baseline ``fail`` → current ``pass``
  * recovery: baseline non-pass → current ``pass``
  * still-failing: baseline ``fail`` → current ``fail``
  * gap: present in baseline, absent from current run
  * new: present in current run, absent from baseline

The report sorts regressions to the top so reviewers see the actionable diffs
first. The ``--strict`` flag is what the CI workflow uses; non-strict mode is
useful for local refresh runs.
"""
from __future__ import annotations

import argparse
import json
import sys
from collections import defaultdict
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable


@dataclass(frozen=True)
class CaseChange:
    lane: str
    protocol: str
    test_case_id: str
    baseline_status: str | None
    current_status: str | None
    classification: str
    notes: str


def load_envelope(path: Path) -> dict | None:
    try:
        return json.loads(path.read_text())
    except (json.JSONDecodeError, OSError):
        return None


def index_envelopes(root: Path) -> dict[tuple[str, str], dict]:
    """Walk ``root`` and index any .cert.json by (client_lane, protocol)."""
    out: dict[tuple[str, str], dict] = {}
    if not root.exists():
        return out
    for path in sorted(root.rglob("*.cert.json")):
        env = load_envelope(path)
        if not env:
            continue
        lane = env.get("client_lane") or "unknown"
        protocol = env.get("protocol") or "unknown"
        # If multiple files report the same (lane, protocol), keep the most
        # recent one (highest run_date).
        key = (lane, protocol)
        existing = out.get(key)
        if existing is None or env.get("run_date", "") > existing.get("run_date", ""):
            out[key] = env
    return out


def index_results(envelope: dict) -> dict[str, dict]:
    return {r["test_case_id"]: r for r in envelope.get("results", [])}


def classify(baseline: str | None, current: str | None) -> str:
    if baseline is None and current is not None:
        return "new"
    if baseline is not None and current is None:
        return "gap"
    if baseline == "pass" and current == "fail":
        return "regression"
    if baseline == "fail" and current == "pass":
        return "improvement"
    if baseline != "pass" and current == "pass":
        return "recovery"
    if baseline == "fail" and current == "fail":
        return "still-failing"
    if baseline == current:
        return "stable"
    return "transition"


def diff(
    baselines: dict[tuple[str, str], dict],
    current: dict[tuple[str, str], dict],
) -> list[CaseChange]:
    changes: list[CaseChange] = []
    keys = set(baselines.keys()) | set(current.keys())
    for lane, protocol in sorted(keys):
        baseline_env = baselines.get((lane, protocol))
        current_env = current.get((lane, protocol))
        baseline_results = index_results(baseline_env) if baseline_env else {}
        current_results = index_results(current_env) if current_env else {}
        ids = set(baseline_results.keys()) | set(current_results.keys())
        for cid in sorted(ids):
            b = baseline_results.get(cid, {}).get("status")
            c = current_results.get(cid, {}).get("status")
            classification = classify(b, c)
            if classification in ("stable", "transition"):
                # Stable runs are not actionable; "transition" is a non-pass↔
                # non-pass move (skip↔not-applicable) which is also noise.
                continue
            notes = current_results.get(cid, {}).get("notes") or baseline_results.get(cid, {}).get("notes") or ""
            changes.append(CaseChange(
                lane=lane,
                protocol=protocol,
                test_case_id=cid,
                baseline_status=b,
                current_status=c,
                classification=classification,
                notes=notes[:160],
            ))
    return changes


SECTION_ORDER = [
    ("regression", "Regressions (baseline pass → current fail)"),
    ("still-failing", "Still failing (baseline fail → current fail)"),
    ("gap", "Missing from current run"),
    ("new", "New IDs not in baseline"),
    ("improvement", "Improvements (baseline fail → current pass)"),
    ("recovery", "Recoveries (baseline non-pass → current pass)"),
]


def write_gap_report(
    changes: Iterable[CaseChange],
    baselines: dict[tuple[str, str], dict],
    current: dict[tuple[str, str], dict],
    path: Path,
) -> None:
    by_classification: dict[str, list[CaseChange]] = defaultdict(list)
    for c in changes:
        by_classification[c.classification].append(c)

    now = datetime.now(timezone.utc).isoformat()
    lines: list[str] = []
    lines.append("# Cross-Client Certification Gap Report")
    lines.append("")
    lines.append(f"_Generated: {now}_")
    lines.append("")
    lines.append("This report is auto-refreshed by the `client-interop-nightly` workflow.")
    lines.append("It compares the latest `.cert.json` envelopes from each Docker client lane")
    lines.append("against the committed baselines under `tests/baselines/client-compat/`.")
    lines.append("")
    lines.append("## Lane coverage summary")
    lines.append("")
    lines.append("| Lane | Protocol | Total | Pass | Fail | Skip | N/A |")
    lines.append("|------|----------|-------|------|------|------|-----|")
    union = sorted(set(baselines.keys()) | set(current.keys()))
    for lane, protocol in union:
        env = current.get((lane, protocol)) or baselines.get((lane, protocol)) or {}
        s = env.get("summary") or {}
        total = s.get("total", 0)
        passed = s.get("passed", 0)
        failed = s.get("failed", 0)
        skipped = s.get("skipped", 0)
        na = s.get("not_applicable", 0)
        marker = "" if (lane, protocol) in current else " ⚠ no current run"
        lines.append(f"| {lane}{marker} | {protocol} | {total} | {passed} | {failed} | {skipped} | {na} |")
    lines.append("")

    for key, heading in SECTION_ORDER:
        items = by_classification.get(key, [])
        if not items:
            continue
        lines.append(f"## {heading} ({len(items)})")
        lines.append("")
        lines.append("| Lane | Protocol | Test case | Baseline | Current | Notes |")
        lines.append("|------|----------|-----------|----------|---------|-------|")
        for c in items:
            notes = c.notes.replace("|", "\\|") if c.notes else ""
            lines.append(
                f"| {c.lane} | {c.protocol} | {c.test_case_id} | {c.baseline_status or '—'} | {c.current_status or '—'} | {notes} |"
            )
        lines.append("")

    if not any(by_classification.get(k) for k, _ in SECTION_ORDER):
        lines.append("## No deviations from baseline")
        lines.append("")
        lines.append("All current `.cert.json` envelopes match the committed baseline. ✅")
        lines.append("")

    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(lines) + "\n")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--baselines", default="tests/baselines/client-compat")
    parser.add_argument("--current", default="docker/client-compat/output")
    parser.add_argument("--gap-report", default="docs/gis/gap-report.md")
    parser.add_argument(
        "--strict",
        action="store_true",
        help="Exit non-zero if any regressions are detected.",
    )
    args = parser.parse_args()

    baselines_dir = Path(args.baselines)
    current_dir = Path(args.current)
    gap_report_path = Path(args.gap_report)

    baselines = index_envelopes(baselines_dir)
    current = index_envelopes(current_dir)

    if not baselines:
        print(
            f"::warning::No baseline envelopes found under {baselines_dir}. "
            "Bootstrap baselines via scripts/client-compat/refresh-baselines.sh.",
            file=sys.stderr,
        )

    if not current:
        print(
            f"::error::No current-run envelopes found under {current_dir}. "
            "Did the lane containers write to /output?",
            file=sys.stderr,
        )

    changes = diff(baselines, current)
    write_gap_report(changes, baselines, current, gap_report_path)
    print(f"Wrote {gap_report_path}")

    regressions = [c for c in changes if c.classification == "regression"]
    if regressions:
        print(f"::error::{len(regressions)} regression(s) detected:", file=sys.stderr)
        for r in regressions:
            print(
                f"  - {r.lane}/{r.protocol}: {r.test_case_id} pass → fail",
                file=sys.stderr,
            )

    if args.strict and regressions:
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
