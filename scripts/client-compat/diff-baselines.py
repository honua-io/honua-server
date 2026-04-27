#!/usr/bin/env python3
"""Compare current-run client-compat .cert.json envelopes against committed baselines.

Inputs:
  --baselines DIR   Directory of committed baseline .cert.json files
                    (default: tests/baselines/client-compat).
  --current DIR     Directory of current-run .cert.json files
                    (default: docker/client-compat/output, recursed).
  --gap-report PATH Where to write the Markdown gap report
                    (default: docs/gis/gap-report.md).
  --expected-pairs PATH   JSON manifest of expected (client_lane, protocol)
                          pairs that the nightly matrix must produce evidence
                          for, even when no baseline has been committed yet
                          (default: tests/baselines/client-compat/expected-pairs.json).
  --client-lanes LIST   Optional comma-separated client_lane filter. When set,
                        baseline envelopes, current-run envelopes, and
                        expected-pair entries whose client_lane is not in the
                        set are dropped before the diff runs. Used by
                        workflow_dispatch subset runs so a partial lane
                        selection does not fail strict mode against the
                        full-matrix baselines/expected-pairs manifest.
  --strict          Exit non-zero if the current run fails the regression gate.

The script identifies envelopes by ``(client_lane, protocol)`` rather than by
filename so that ``run_id`` differences do not cause spurious diffs. For each
shared (lane, protocol, test_case_id) tuple it classifies the change:

  * regression: baseline ``pass`` → current ``fail``/``skip``/``not-applicable``
    (any drop from pass to non-pass is a regression — baseline bumps must be
    explicit, not silent)
  * improvement: baseline ``fail`` → current ``pass``
  * recovery: baseline non-pass non-fail → current ``pass``
  * still-failing: baseline ``fail`` → current ``fail``
  * gap: present in baseline, absent from current run
  * new-fail: current ``fail`` against either no baseline or a non-fail
    baseline (skip / not-applicable / placeholder). Treats ``skip``→``fail``
    and ``not-applicable``→``fail`` the same as no-baseline → fail because
    the test was never certified passing — a fail must block strict mode
    rather than fall through to the noise-bucket ``transition``.
  * new: absent from baseline, current non-fail

The report sorts regressions to the top so reviewers see the actionable diffs
first. The ``--strict`` flag is what the CI workflow uses; non-strict mode is
useful for local refresh runs.

In ``--strict`` mode the script exits non-zero when **any** of these hold:

  1. A baseline envelope's ``(client_lane, protocol)`` is missing from the
     current run (lane crashed without producing evidence).
  2. A baseline test_case_id is missing from a current-run envelope (lane ran
     but truncated its output).
  3. A baseline ``pass`` regressed to current ``fail``/``skip``/``not-applicable``.
  4. The current-run directory contained no envelopes at all.
  5. An ``expected-pairs.json`` entry is absent from both baseline and current
     run (lane never produced the contractually-required evidence).
  6. A current envelope reports ``fail`` for a test case with no baseline,
     or with a non-fail baseline (``skip``/``not-applicable``/placeholder).
  7. An ``expected-pairs.json`` entry has no committed baseline, even if the
     current run produced evidence — a never-baselined lane cannot pass the
     gate by emitting unreviewed pass/skip evidence.

Without those checks a crashed lane (lane-job exit 0 ⊕ no envelope written),
an all-fail brand-new lane, or a never-baselined lane that quietly emits
passing-looking evidence would silently pass the nightly gate.
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
    # 'gap'/'new*' branches handle one-sided absence; otherwise both are present.
    if baseline is None and current is not None:
        return "new-fail" if current == "fail" else "new"
    if baseline is not None and current is None:
        return "gap"
    # Any baseline pass that does not stay pass in the current run is a
    # regression — including pass→skip and pass→not-applicable, which would
    # otherwise hide an endpoint that became unavailable (e.g. a Playwright
    # test calling test.skip() because the endpoint now 404s).
    if baseline == "pass" and current != "pass":
        return "regression"
    if baseline == "fail" and current == "pass":
        return "improvement"
    if baseline != "pass" and current == "pass":
        return "recovery"
    if baseline == "fail" and current == "fail":
        return "still-failing"
    # Any non-pass baseline (skip / not-applicable / placeholder) becoming
    # current `fail` is a new failure — the test wasn't certified passing,
    # so a fail in it must block strict mode rather than fall through to
    # the noise-bucket "transition" classification.
    if current == "fail":
        return "new-fail"
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
    ("regression", "Regressions (baseline pass → current non-pass)"),
    ("new-fail", "New failures (no baseline → current fail)"),
    ("still-failing", "Still failing (baseline fail → current fail)"),
    ("gap", "Missing from current run"),
    ("new", "New IDs not in baseline"),
    ("improvement", "Improvements (baseline fail → current pass)"),
    ("recovery", "Recoveries (baseline non-pass → current pass)"),
]


def load_expected_pairs(path: Path) -> list[tuple[str, str]]:
    """Read the expected-pairs manifest. Missing manifest -> empty list (no gate)."""
    if not path.exists():
        return []
    try:
        payload = json.loads(path.read_text())
    except (OSError, json.JSONDecodeError) as exc:
        print(f"::warning::Could not read expected-pairs manifest {path}: {exc}", file=sys.stderr)
        return []
    pairs: list[tuple[str, str]] = []
    for entry in payload.get("expected_pairs", []):
        lane = entry.get("client_lane")
        protocol = entry.get("protocol")
        if lane and protocol:
            pairs.append((lane, protocol))
    return pairs


def write_gap_report(
    changes: Iterable[CaseChange],
    baselines: dict[tuple[str, str], dict],
    current: dict[tuple[str, str], dict],
    path: Path,
    expected_pairs: list[tuple[str, str]] | None = None,
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

    unbaselined_missing: list[tuple[str, str]] = []
    unbaselined_no_baseline: list[tuple[str, str]] = []
    if expected_pairs:
        present_pairs = set(baselines.keys()) | set(current.keys())
        unbaselined_missing = [
            (lane, protocol) for lane, protocol in expected_pairs
            if (lane, protocol) not in present_pairs
        ]
        unbaselined_no_baseline = [
            (lane, protocol) for lane, protocol in expected_pairs
            if (lane, protocol) not in baselines
        ]
        if unbaselined_missing:
            lines.append(f"## Expected pairs absent from this run ({len(unbaselined_missing)})")
            lines.append("")
            lines.append("| Lane | Protocol |")
            lines.append("|------|----------|")
            for lane, protocol in unbaselined_missing:
                lines.append(f"| {lane} | {protocol} |")
            lines.append("")
        if unbaselined_no_baseline:
            lines.append(
                f"## Expected pairs without committed baselines ({len(unbaselined_no_baseline)})"
            )
            lines.append("")
            lines.append(
                "These pairs are listed in `expected-pairs.json` but have no "
                "committed `.cert.json` baseline. Strict mode fails until a "
                "reviewed baseline is committed. Run "
                "`scripts/client-compat/refresh-baselines.sh` and open a "
                "baseline-bump PR after reviewing the captured envelopes."
            )
            lines.append("")
            lines.append("| Lane | Protocol | Has current run? |")
            lines.append("|------|----------|------------------|")
            for lane, protocol in unbaselined_no_baseline:
                has_current = "yes" if (lane, protocol) in current else "no"
                lines.append(f"| {lane} | {protocol} | {has_current} |")
            lines.append("")

    # The "No deviations" success section must only appear when *no* gate
    # failure section was emitted — including the expected-pair gates above,
    # which are independent of the by_classification diff sections.
    has_classification_findings = any(by_classification.get(k) for k, _ in SECTION_ORDER)
    has_expected_pair_findings = bool(unbaselined_missing or unbaselined_no_baseline)
    if not (has_classification_findings or has_expected_pair_findings):
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
        "--expected-pairs",
        default="tests/baselines/client-compat/expected-pairs.json",
        help=(
            "Path to the expected (client_lane, protocol) manifest. Strict "
            "mode fails if any listed pair is missing from both baseline "
            "and current run, and also if any listed pair has no committed "
            "baseline at all (even when the current run produced evidence)."
        ),
    )
    parser.add_argument(
        "--client-lanes",
        default=None,
        help=(
            "Optional comma-separated client_lane filter (e.g. 'js,js-cesium'). "
            "When set, baseline, current, and expected-pairs entries whose "
            "client_lane is not in this set are dropped before the diff runs. "
            "workflow_dispatch subset runs use this so a partial lane "
            "selection does not fail strict mode against full-matrix "
            "baselines/expected-pairs."
        ),
    )
    parser.add_argument(
        "--strict",
        action="store_true",
        help="Exit non-zero if any regressions are detected.",
    )
    args = parser.parse_args()

    baselines_dir = Path(args.baselines)
    current_dir = Path(args.current)
    gap_report_path = Path(args.gap_report)
    expected_pairs_path = Path(args.expected_pairs)

    baselines = index_envelopes(baselines_dir)
    current = index_envelopes(current_dir)
    expected_pairs = load_expected_pairs(expected_pairs_path)

    if args.client_lanes:
        wanted = {lane.strip() for lane in args.client_lanes.split(",") if lane.strip()}
        if wanted:
            baselines = {k: v for k, v in baselines.items() if k[0] in wanted}
            current = {k: v for k, v in current.items() if k[0] in wanted}
            expected_pairs = [(lane, protocol) for lane, protocol in expected_pairs if lane in wanted]
            print(
                f"Filtering to client_lanes={sorted(wanted)}: "
                f"{len(baselines)} baseline envelope(s), "
                f"{len(current)} current envelope(s), "
                f"{len(expected_pairs)} expected pair(s).",
                file=sys.stderr,
            )

    if not baselines:
        print(
            f"::warning::No baseline envelopes found under {baselines_dir}. "
            "Bootstrap baselines via scripts/client-compat/refresh-baselines.sh.",
            file=sys.stderr,
        )

    missing_envelopes = sorted(set(baselines.keys()) - set(current.keys()))
    no_current_evidence = bool(baselines) and not current

    expected_set = set(expected_pairs)
    present_set = set(baselines.keys()) | set(current.keys())
    expected_missing = sorted(expected_set - present_set)
    # Stronger gate: expected pair MUST have a committed baseline. Until a
    # reviewed baseline lands, strict mode fails loudly so a never-baselined
    # lane cannot silently produce uncertified evidence.
    expected_without_baseline = sorted(expected_set - set(baselines.keys()))

    if no_current_evidence:
        print(
            f"::error::No current-run envelopes found under {current_dir}. "
            "Did the lane containers write to /output?",
            file=sys.stderr,
        )
    elif missing_envelopes:
        print(
            f"::error::{len(missing_envelopes)} baseline envelope(s) missing from current run:",
            file=sys.stderr,
        )
        for lane, protocol in missing_envelopes:
            print(f"  - {lane}/{protocol}", file=sys.stderr)

    if expected_missing:
        print(
            f"::error::{len(expected_missing)} expected (lane, protocol) pair(s) "
            "absent from both baseline and current run:",
            file=sys.stderr,
        )
        for lane, protocol in expected_missing:
            print(f"  - {lane}/{protocol}", file=sys.stderr)

    if expected_without_baseline:
        print(
            f"::error::{len(expected_without_baseline)} expected (lane, protocol) "
            "pair(s) have no committed baseline; bootstrap via "
            "scripts/client-compat/refresh-baselines.sh:",
            file=sys.stderr,
        )
        for lane, protocol in expected_without_baseline:
            print(f"  - {lane}/{protocol}", file=sys.stderr)

    changes = diff(baselines, current)
    write_gap_report(changes, baselines, current, gap_report_path, expected_pairs)
    print(f"Wrote {gap_report_path}")

    regressions = [c for c in changes if c.classification == "regression"]
    new_fails = [c for c in changes if c.classification == "new-fail"]
    gaps = [c for c in changes if c.classification == "gap"]
    if regressions:
        print(f"::error::{len(regressions)} regression(s) detected:", file=sys.stderr)
        for r in regressions:
            print(
                f"  - {r.lane}/{r.protocol}: {r.test_case_id} {r.baseline_status} → {r.current_status}",
                file=sys.stderr,
            )
    if new_fails:
        print(
            f"::error::{len(new_fails)} new failure(s) in unbaselined test case(s):",
            file=sys.stderr,
        )
        for n in new_fails:
            print(
                f"  - {n.lane}/{n.protocol}: {n.test_case_id} → fail (no baseline)",
                file=sys.stderr,
            )
    if gaps:
        print(
            f"::error::{len(gaps)} baseline test case(s) missing from current run:",
            file=sys.stderr,
        )
        for g in gaps:
            print(
                f"  - {g.lane}/{g.protocol}: {g.test_case_id} (baseline {g.baseline_status})",
                file=sys.stderr,
            )

    if args.strict and (
        regressions
        or new_fails
        or gaps
        or missing_envelopes
        or no_current_evidence
        or expected_missing
        or expected_without_baseline
    ):
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
