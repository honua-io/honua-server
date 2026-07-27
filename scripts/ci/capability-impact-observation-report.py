#!/usr/bin/env python3
"""Aggregate per-PR capability-impact comparison reports over an observation window.

ADR-0037 remains authoritative. The Capability Impact Comparison workflow
uploads a report-only `capability-impact-report.json` artifact for every PR
(#2897). This tool rolls those artifacts up across the observation period and
renders the switch-decision evidence: subset-size distributions versus the
legacy path-heuristic selection and run_all, set-relation frequencies, the
legacy-only shard roll-up (potential escapes, informational), and
run_all-fallback reasons.

Live mode pulls artifacts from recent workflow runs via `gh api`; offline mode
(`--from-dir`) reads already-downloaded report JSONs and is what the unit
tests in tests/python/unit/test_capability_impact_observation_report.py use.
Python 3 standard library only, matching scripts/ci/capability-impact.py.
"""

from __future__ import annotations

import argparse
import datetime as dt
import io
import json
import statistics
import subprocess
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SHARDS = ROOT / ".github/ci-shards.json"
WORKFLOW = "capability-impact-comparison.yml"
REPORT_FILENAME = "capability-impact-report.json"
ARTIFACT_PREFIX = "capability-impact-"


def load_total_shard_count(path: Path = SHARDS) -> int:
    return len(json.loads(path.read_text(encoding="utf-8"))["shards"])


def is_comparison_report(document: object) -> bool:
    return (
        isinstance(document, dict)
        and isinstance(document.get("comparison"), dict)
        and isinstance(document.get("capabilitySelection"), dict)
    )


def is_observable_comparison(document: dict) -> bool:
    """True when the report describes a real PR comparison worth aggregating.

    workflow_dispatch runs of the comparison workflow diff trunk against
    itself and upload an empty report (zero changed files, empty selections on
    both sides); folding those into the aggregate would dilute every
    distribution, so they are excluded here in addition to the event filter in
    ``fetch_reports``.
    """
    if document.get("changedFileCount", 0) == 0:
        return False
    comparison = document["comparison"]
    selection = document["capabilitySelection"]
    legacy = document.get("legacy", {}) or {}
    has_any_selection = (
        comparison.get("capabilityShardCount", 0) > 0
        or comparison.get("legacyShardCount", 0) > 0
        or selection.get("runAll")
        or legacy.get("run_all")
    )
    return bool(has_any_selection)


def load_reports_from_dir(root: Path) -> list[dict]:
    """Read every parseable per-PR comparison report under ``root`` (recursive).

    Non-report JSON files (changed-files lists, legacy selections, summaries)
    are silently skipped so a directory of extracted artifacts can be pointed
    at directly.
    """
    reports: list[dict] = []
    for path in sorted(root.rglob("*.json")):
        try:
            document = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        if is_comparison_report(document) and is_observable_comparison(document):
            meta = document.get("_meta") if isinstance(document.get("_meta"), dict) else {}
            meta.setdefault("source", str(path.relative_to(root)))
            document["_meta"] = meta
            reports.append(document)
    return reports


def gh_api(arguments: list[str], *, binary: bool = False) -> bytes | str:
    completed = subprocess.run(["gh", "api", *arguments], capture_output=True, check=True)
    return completed.stdout if binary else completed.stdout.decode("utf-8")


def fetch_reports(repo: str, days: int) -> list[dict]:
    """Download `capability-impact-*` artifacts from recent pull_request runs.

    workflow_dispatch runs are excluded (`event=pull_request`): they diff
    trunk against itself and would pollute the aggregate with empty
    comparisons — including the dispatch run that hosts this very aggregation
    job.
    """
    cutoff = (dt.datetime.now(dt.timezone.utc) - dt.timedelta(days=days)).strftime("%Y-%m-%d")
    run_lines = gh_api(
        [
            "-X",
            "GET",
            f"repos/{repo}/actions/workflows/{WORKFLOW}/runs",
            "-f",
            "event=pull_request",
            "-f",
            f"created=>={cutoff}",
            "-f",
            "per_page=100",
            "--paginate",
            "--jq",
            (
                ".workflow_runs[] | {id: .id, url: .html_url, createdAt: .created_at, "
                + "prNumber: (.pull_requests[0].number // null)} | tojson"
            ),
        ]
    ).splitlines()
    reports: list[dict] = []
    for run_line in run_lines:
        if not run_line.strip():
            continue
        run = json.loads(run_line)
        artifact_lines = gh_api(
            [
                "-X",
                "GET",
                f"repos/{repo}/actions/runs/{run['id']}/artifacts",
                "--jq",
                (
                    f'.artifacts[] | select(.expired | not) | select(.name | startswith("{ARTIFACT_PREFIX}")) '
                    + "| {id: .id, name: .name} | tojson"
                ),
            ]
        ).splitlines()
        for artifact_line in artifact_lines:
            if not artifact_line.strip():
                continue
            artifact = json.loads(artifact_line)
            payload = gh_api([f"repos/{repo}/actions/artifacts/{artifact['id']}/zip"], binary=True)
            try:
                with zipfile.ZipFile(io.BytesIO(payload)) as archive:
                    document = json.loads(archive.read(REPORT_FILENAME))
            except (KeyError, zipfile.BadZipFile, json.JSONDecodeError):
                continue
            if is_comparison_report(document) and is_observable_comparison(document):
                # Provenance for the --escapes-reviewed correlation step:
                # both live and --from-dir reports carry one `_meta` shape.
                document["_meta"] = {
                    "runId": run["id"],
                    "runUrl": run.get("url"),
                    "runCreatedAt": run.get("createdAt"),
                    "prNumber": run.get("prNumber"),
                    "artifactName": artifact.get("name"),
                }
                reports.append(document)
    return reports


def distribution(values: list[int]) -> dict:
    if not values:
        return {"min": None, "median": None, "mean": None, "max": None}
    return {
        "min": min(values),
        "median": statistics.median(values),
        "mean": round(statistics.mean(values), 2),
        "max": max(values),
    }


def _frequency_table(counter: dict[str, int]) -> dict[str, int]:
    return dict(sorted(counter.items(), key=lambda item: (-item[1], item[0])))


def aggregate(
    reports: list[dict],
    total_shards: int,
    *,
    min_comparisons: int,
    min_strict_subset_pct: float,
    escapes_reviewed: bool = False,
) -> dict:
    capability_sizes: list[int] = []
    legacy_sizes: list[int] = []
    relations = {
        "equal": 0,
        "capabilitySubsetOfLegacy": 0,
        "capabilitySupersetOfLegacy": 0,
        "divergent": 0,
    }
    legacy_only_frequency: dict[str, int] = {}
    legacy_only_occurrences: list[dict] = []
    reports_with_legacy_only = 0
    reports_with_legacy_only_excluding_legacy_run_all = 0
    capability_run_all_reasons: dict[str, int] = {}
    legacy_run_all_reasons: dict[str, int] = {}
    strictly_smaller = 0

    for report in reports:
        comparison = report["comparison"]
        selection = report["capabilitySelection"]
        legacy = report.get("legacy", {}) or {}
        capability_size = comparison.get("capabilityShardCount", len(selection.get("shards", [])))
        legacy_size = comparison.get("legacyShardCount", len(legacy.get("shards", [])))
        capability_sizes.append(capability_size)
        legacy_sizes.append(legacy_size)
        legacy_only = comparison.get("legacyOnlyShards", [])
        capability_only = comparison.get("capabilityOnlyShards", [])
        if not legacy_only and not capability_only:
            relations["equal"] += 1
        elif not capability_only:
            relations["capabilitySubsetOfLegacy"] += 1
        elif not legacy_only:
            relations["capabilitySupersetOfLegacy"] += 1
        else:
            relations["divergent"] += 1
        # capability-impact.py emits `comparison.escapedDefectCandidates` as
        # exactly `legacyOnlyShards` (the raw set difference legacy - graph);
        # a per-PR report carries no shard outcome data, so a legacy-only
        # shard is only a *potential* escape and is aggregated here under its
        # honest name. Consume the authoritative per-report field rather than
        # recomputing, falling back to `legacyOnlyShards` for older reports.
        legacy_only_candidates = comparison.get("escapedDefectCandidates", legacy_only)
        for shard in legacy_only_candidates:
            legacy_only_frequency[shard] = legacy_only_frequency.get(shard, 0) + 1
        if legacy_only_candidates:
            reports_with_legacy_only += 1
            if not legacy.get("run_all"):
                reports_with_legacy_only_excluding_legacy_run_all += 1
            # Preserve run identity so the --escapes-reviewed correlation step
            # can click through to exactly the runs that need checking.
            meta = report.get("_meta") if isinstance(report.get("_meta"), dict) else {}
            legacy_only_occurrences.append(
                {
                    "runId": meta.get("runId"),
                    "runUrl": meta.get("runUrl"),
                    "runCreatedAt": meta.get("runCreatedAt"),
                    "prNumber": meta.get("prNumber"),
                    "artifactName": meta.get("artifactName"),
                    "source": meta.get("source"),
                    "reportIndex": len(capability_sizes) - 1,
                    "legacyOnlyShards": sorted(legacy_only_candidates),
                    "legacyRunAll": bool(legacy.get("run_all")),
                }
            )
        if selection.get("runAll"):
            reason = selection.get("reason", "unknown")
            capability_run_all_reasons[reason] = capability_run_all_reasons.get(reason, 0) + 1
        if legacy.get("run_all"):
            reason = legacy.get("reason", "unknown")
            legacy_run_all_reasons[reason] = legacy_run_all_reasons.get(reason, 0) + 1
        if capability_size < legacy_size:
            strictly_smaller += 1

    count = len(reports)
    strict_subset_pct = round(100.0 * strictly_smaller / count, 1) if count else 0.0
    criteria = [
        {
            "name": "comparisonCount",
            "threshold": f">= {min_comparisons}",
            "observed": count,
            "met": count >= min_comparisons,
        },
        {
            "name": "strictlySmallerSelectionPct",
            "threshold": f">= {min_strict_subset_pct}",
            "observed": strict_subset_pct,
            "met": strict_subset_pct >= min_strict_subset_pct,
        },
    ]
    return {
        "schemaVersion": 1,
        "mode": "observation-aggregate",
        "totalShardCount": total_shards,
        "comparisonCount": count,
        "selectionSizes": {
            "capability": distribution(capability_sizes),
            "legacy": distribution(legacy_sizes),
            "runAll": total_shards,
            "meanCapabilityFractionOfRunAll": (
                round(statistics.mean(capability_sizes) / total_shards, 3) if capability_sizes and total_shards else None
            ),
            "meanLegacyFractionOfRunAll": (
                round(statistics.mean(legacy_sizes) / total_shards, 3) if legacy_sizes and total_shards else None
            ),
        },
        "relations": relations,
        "strictlySmaller": {"count": strictly_smaller, "pct": strict_subset_pct},
        "legacyOnlyShards": {
            "shardFrequency": _frequency_table(legacy_only_frequency),
            "reportsWithLegacyOnlyShards": reports_with_legacy_only,
            "reportsWithLegacyOnlyShardsExcludingLegacyRunAll": reports_with_legacy_only_excluding_legacy_run_all,
            "occurrences": legacy_only_occurrences,
        },
        "runAllFallbacks": {
            "capability": {
                "count": sum(capability_run_all_reasons.values()),
                "reasons": _frequency_table(capability_run_all_reasons),
            },
            "legacy": {
                "count": sum(legacy_run_all_reasons.values()),
                "reasons": _frequency_table(legacy_run_all_reasons),
            },
        },
        "switchRecommendation": {
            "preconditionsMet": all(criterion["met"] for criterion in criteria),
            "escapesReviewed": escapes_reviewed,
            "safeToSwitch": all(criterion["met"] for criterion in criteria) and escapes_reviewed,
            "criteria": criteria,
            "note": (
                "Legacy-only shards (the per-report escapedDefectCandidates field) are shards "
                "ADR-0037 would run but the capability selector would not. They are definitionally "
                "present in every comparison where the capability selection is strictly smaller, so "
                "they cannot be a zero-tolerance switch criterion for a selector that is supposed to "
                "be tighter. The per-PR reports carry no shard outcome data, so the quantitative "
                "criteria above are only preconditions: safeToSwitch additionally requires the "
                "operator to correlate the runs listed in legacyOnlyShards.occurrences (the "
                "'Reports needing escape correlation' table) with actual shard failures on those "
                "PRs and affirm the review by re-running with --escapes-reviewed."
            ),
        },
    }


def markdown(summary: dict, days: int) -> str:
    sizes = summary["selectionSizes"]
    relations = summary["relations"]
    legacy_only = summary["legacyOnlyShards"]
    fallbacks = summary["runAllFallbacks"]
    recommendation = summary["switchRecommendation"]

    def size_row(name: str, dist: dict) -> str:
        return f"| {name} | {dist['min']} | {dist['median']} | {dist['mean']} | {dist['max']} |"

    lines = [
        "## Capability selection observation report",
        "",
        "> ADR-0037 remains authoritative; this aggregate is the observation-period evidence for the switch decision (#2897).",
        "",
        f"- Window: last {days} days, {summary['comparisonCount']} comparisons",
        f"- run_all shard count: {summary['totalShardCount']}",
        f"- Mean fraction of run_all: capability {sizes['meanCapabilityFractionOfRunAll']}, legacy {sizes['meanLegacyFractionOfRunAll']}",
        "",
        "### Selection size distribution (shards)",
        "",
        "| Selector | Min | Median | Mean | Max |",
        "|---|---:|---:|---:|---:|",
        size_row("Capability", sizes["capability"]),
        size_row("ADR-0037 legacy", sizes["legacy"]),
        f"| run_all | {summary['totalShardCount']} | {summary['totalShardCount']} | {summary['totalShardCount']} | {summary['totalShardCount']} |",
        "",
        "### Set relation per comparison (capability vs ADR-0037)",
        "",
        f"- Equal: {relations['equal']}",
        f"- Capability strict subset of legacy: {relations['capabilitySubsetOfLegacy']}",
        f"- Capability strict superset of legacy: {relations['capabilitySupersetOfLegacy']}",
        f"- Divergent (both sides have unique shards): {relations['divergent']}",
        f"- Capability selection strictly smaller than legacy: {summary['strictlySmaller']['count']} ({summary['strictlySmaller']['pct']}%)",
        "",
        "### Legacy-only shards (potential escapes, informational)",
        "",
    ]
    frequency = legacy_only["shardFrequency"]
    if frequency:
        lines.extend(["| Shard | Comparisons |", "|---|---:|"])
        lines.extend(f"| {shard} | {count} |" for shard, count in list(frequency.items())[:15])
        if len(frequency) > 15:
            lines.append(f"| … {len(frequency) - 15} more | |")
    else:
        lines.append("None.")
    lines.extend(
        [
            "",
            f"- Comparisons with legacy-only shards: {legacy_only['reportsWithLegacyOnlyShards']}",
            f"- Excluding comparisons where ADR-0037 itself fell back to run_all: {legacy_only['reportsWithLegacyOnlyShardsExcludingLegacyRunAll']}",
            "",
            "### Reports needing escape correlation",
            "",
        ]
    )
    occurrences = legacy_only["occurrences"]
    if occurrences:
        lines.extend(["| Run | PR | Legacy-only shards |", "|---|---|---|"])
        for occurrence in occurrences[:20]:
            if occurrence["runUrl"]:
                run_cell = f"[{occurrence['runId'] or 'run'}]({occurrence['runUrl']})"
            else:
                run_cell = occurrence["runId"] or occurrence["source"] or "?"
            pr_cell = f"#{occurrence['prNumber']}" if occurrence["prNumber"] else "?"
            shard_cell = ", ".join(occurrence["legacyOnlyShards"])
            if occurrence["legacyRunAll"]:
                shard_cell += " (legacy run_all)"
            lines.append(f"| {run_cell} | {pr_cell} | {shard_cell} |")
        if len(occurrences) > 20:
            lines.append(f"| … {len(occurrences) - 20} more (see JSON `legacyOnlyShards.occurrences`) | | |")
    else:
        lines.append("None.")
    lines.extend(
        [
            "",
            "### run_all fallbacks",
            "",
            f"- Capability selector: {fallbacks['capability']['count']} ({json.dumps(fallbacks['capability']['reasons'])})",
            f"- ADR-0037 selector: {fallbacks['legacy']['count']} ({json.dumps(fallbacks['legacy']['reasons'])})",
            "",
            "### Switch recommendation",
            "",
        ]
    )
    for criterion in recommendation["criteria"]:
        mark = "x" if criterion["met"] else " "
        lines.append(f"- [{mark}] {criterion['name']} {criterion['threshold']} (observed: {criterion['observed']})")
    reviewed_mark = "x" if recommendation["escapesReviewed"] else " "
    lines.append(
        f"- [{reviewed_mark}] legacy-only shard failures correlated over the window (operator acknowledgment, `--escapes-reviewed`)"
    )
    if recommendation["safeToSwitch"]:
        verdict = "**Verdict: SAFE to switch** (quantitative preconditions met and escape review acknowledged)."
    elif recommendation["preconditionsMet"]:
        verdict = (
            "**Verdict: PRECONDITIONS MET — manual legacy-only shard failure correlation still "
            "required before switching.** Re-run with `--escapes-reviewed` after correlating the "
            "legacy-only shard table with actual shard failures on those PRs."
        )
    else:
        verdict = "**Verdict: NOT yet safe to switch** (quantitative preconditions not met)."
    lines.extend(
        [
            "",
            verdict,
            "",
            recommendation["note"],
        ]
    )
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", default="honua-io/honua-server", help="GitHub repository (owner/name) to pull artifacts from.")
    parser.add_argument("--days", type=int, default=28, help="Observation window in days (live mode).")
    parser.add_argument("--from-dir", type=Path, help="Offline mode: read already-downloaded report JSONs from this directory.")
    parser.add_argument("--markdown", type=Path, help="Write a markdown summary to this path.")
    parser.add_argument("--total-shards", type=int, help="Override the run_all shard count (default: .github/ci-shards.json).")
    parser.add_argument("--min-comparisons", type=int, default=25, help="Switch criterion: minimum number of comparisons.")
    parser.add_argument(
        "--min-strict-subset-pct",
        type=float,
        default=60.0,
        help="Switch criterion: minimum percentage of comparisons where the capability selection is strictly smaller.",
    )
    parser.add_argument(
        "--escapes-reviewed",
        action="store_true",
        help=(
            "Operator acknowledgment that legacy-only shard failures were manually correlated over "
            "the window. Without it the verdict caps at PRECONDITIONS MET and safeToSwitch stays "
            "false, because the per-PR reports carry no shard outcome data."
        ),
    )
    args = parser.parse_args(argv)

    if args.from_dir is not None:
        reports = load_reports_from_dir(args.from_dir)
    else:
        reports = fetch_reports(args.repo, args.days)
    total_shards = args.total_shards if args.total_shards is not None else load_total_shard_count()
    summary = aggregate(
        reports,
        total_shards,
        min_comparisons=args.min_comparisons,
        min_strict_subset_pct=args.min_strict_subset_pct,
        escapes_reviewed=args.escapes_reviewed,
    )
    print(json.dumps(summary, indent=2))
    if args.markdown:
        args.markdown.write_text(markdown(summary, args.days), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
