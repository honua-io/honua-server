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
CI_WORKFLOW_PATH = ".github/workflows/ci.yml"
# ci.yml names its shard matrix jobs `Server Tests (${{ matrix.shard_name }})`
# where shard_name comes from .github/ci-shards.json — the same names the
# selection reports carry.
SHARD_JOB_NAME_FORMAT = "Server Tests ({shard})"
SHARD_JOB_NAME_PREFIX = "Server Tests ("
# Executed conclusions that raise the blocking escaped-defect signal: the
# shard demonstrably did not pass. Every other non-"success" conclusion
# (cancelled, skipped, action_required, startup_failure, neutral, stale,
# unknown, ...) is a coverage gap the operator must review explicitly.
SIGNAL_CONCLUSIONS = frozenset({"failure", "timed_out"})
# Conclusions where the shard job actually ran to a verdict. Merge precedence
# across runs at one head: the NEWEST executed result wins (a successful
# rerun supersedes an older flake failure — e.g. the documented transient
# 40P01 deadlock reruns — and a newer failure supersedes an older success),
# while non-executed conclusions (skipped/cancelled/unknown/...) never shadow
# an executed result from any run.
EXECUTED_CONCLUSIONS = frozenset({"success"}) | SIGNAL_CONCLUSIONS
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


def fetch_reports(repo: str, days: int) -> tuple[list[dict], list[dict]]:
    """Download `capability-impact-*` artifacts from recent pull_request runs.

    Returns ``(reports, missing_evidence_prs)``: accepted comparison reports
    (with ``_meta`` provenance) plus one gap record per PR in the run window
    that produced NO parseable report from any run.

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
                ".workflow_runs[] | {id: .id, url: .html_url, createdAt: .created_at, headSha: .head_sha, "
                + "prNumber: (.pull_requests[0].number // null)} | tojson"
            ),
        ]
    ).splitlines()
    runs = [json.loads(run_line) for run_line in run_lines if run_line.strip()]
    # Inventory the newest run per PR from the RUN list, not from parseable
    # artifacts: if a PR's newest push failed completeness or was cancelled
    # before uploading a report, the older artifact must not silently pass as
    # its current evidence — the aggregation flags such PRs as evidence gaps.
    newest_run_by_pr: dict[int, dict] = {}
    for run in runs:
        pr_number = run.get("prNumber")
        if pr_number is None:
            continue
        current = newest_run_by_pr.get(pr_number)
        if current is None or (run.get("createdAt") or "") > (current.get("createdAt") or ""):
            newest_run_by_pr[pr_number] = run
    reports: list[dict] = []
    for run in runs:
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
                newest_run = newest_run_by_pr.get(run.get("prNumber")) if run.get("prNumber") is not None else None
                document["_meta"] = {
                    "runId": run["id"],
                    "runUrl": run.get("url"),
                    "runCreatedAt": run.get("createdAt"),
                    "headSha": run.get("headSha"),
                    "prNumber": run.get("prNumber"),
                    "artifactName": artifact.get("name"),
                    "newestRunForPr": (
                        {
                            "runId": newest_run["id"],
                            "runUrl": newest_run.get("url"),
                            "runCreatedAt": newest_run.get("createdAt"),
                        }
                        if newest_run is not None
                        else None
                    ),
                }
                reports.append(document)
    # A PR whose every run in the window failed/cancelled before uploading a
    # parseable comparison artifact would otherwise vanish entirely — neither
    # a comparison nor a stale-evidence gap. Emit an explicit missing-evidence
    # record for every PR seen in the RUN list with no accepted report at all;
    # the aggregate blocks the SAFE verdict while any exist.
    accepted_prs = {
        report["_meta"]["prNumber"] for report in reports if report["_meta"].get("prNumber") is not None
    }
    missing_evidence_prs = [
        {
            "prNumber": pr_number,
            "newestRunId": newest_run["id"],
            "newestRunUrl": newest_run.get("url"),
            "reason": "no run for this PR produced a comparison report",
        }
        for pr_number, newest_run in sorted(newest_run_by_pr.items())
        if pr_number not in accepted_prs
    ]
    return reports, missing_evidence_prs


def _legacy_only_candidates(report: dict) -> list[str]:
    comparison = report.get("comparison", {})
    return comparison.get("escapedDefectCandidates", comparison.get("legacyOnlyShards", []))


def _fetch_ci_outcomes(repo: str, head_sha: str) -> dict:
    """Return {url, jobs} with `Server Tests (<shard>)` outcomes merged across
    the ci.yml runs at ``head_sha``.

    Only ci.yml runs are queried for jobs: it is the sole workflow defining
    the `Server Tests (<shard>)` matrix (verified across .github/workflows/;
    the nightly schedule and the merge train's batch dispatch both run
    ci.yml), and the run listing already carries each run's workflow path, so
    filtering costs no extra API calls while avoiding a serial jobs-endpoint
    scan of PR Gate/CodeQL/governance runs that could exhaust API quota or
    the job timeout on a busy window. In the merge-train model ci.yml has no
    per-PR pull_request trigger, so a per-PR head usually has no ci.yml run
    at all and this returns an empty jobs map (outcomes then read as
    "unknown" coverage gaps for the operator). ``jobs`` maps job display
    name -> {jobName, conclusion, runUrl}; lookup failures degrade the same
    way.
    """
    try:
        run_lines = gh_api(
            [
                "-X",
                "GET",
                f"repos/{repo}/actions/runs",
                "-f",
                f"head_sha={head_sha}",
                "-f",
                "per_page=100",
                "--jq",
                ".workflow_runs[] | {id: .id, url: .html_url, createdAt: .created_at, path: .path} | tojson",
            ]
        ).splitlines()
        runs = [json.loads(line) for line in run_lines if line.strip()]
        runs = [run for run in runs if run.get("path") == CI_WORKFLOW_PATH]
        runs.sort(key=lambda entry: entry.get("createdAt") or "", reverse=True)
        merged: dict[str, dict] = {}
        primary_url = None
        for run in runs:
            job_lines = gh_api(
                [
                    "-X",
                    "GET",
                    f"repos/{repo}/actions/runs/{run['id']}/jobs",
                    "-f",
                    "per_page=100",
                    "--paginate",
                    "--jq",
                    ".jobs[] | {name: .name, conclusion: .conclusion} | tojson",
                ]
            ).splitlines()
            contributed = False
            for job_line in job_lines:
                if not job_line.strip():
                    continue
                job = json.loads(job_line)
                name = job["name"]
                if not name.startswith(SHARD_JOB_NAME_PREFIX):
                    continue
                # Merge shard outcomes ACROSS all ci.yml runs at the head so
                # a shard executed only by an older run is not dropped as
                # unknown. Precedence (see EXECUTED_CONCLUSIONS): among
                # executed results the newest run wins (first-seen under the
                # newest-first sort) — so a successful rerun
                # supersedes an older flake failure and a newer failure
                # supersedes an older success. Non-executed conclusions
                # (skipped/cancelled/unknown/...) only ever fill an empty
                # slot and are replaced by an executed result from ANY run.
                candidate = {
                    "jobName": name,
                    "conclusion": job.get("conclusion") or "unknown",
                    "runUrl": run.get("url"),
                }
                existing = merged.get(name)
                if existing is not None and not (
                    candidate["conclusion"] in EXECUTED_CONCLUSIONS
                    and existing["conclusion"] not in EXECUTED_CONCLUSIONS
                ):
                    continue
                merged[name] = candidate
                contributed = True
            if contributed and primary_url is None:
                primary_url = run.get("url")
        return {"url": primary_url, "jobs": merged}
    except subprocess.CalledProcessError:
        # Degrade instead of failing the whole aggregate: the join is a
        # best-effort enrichment, and every shard it cannot resolve is
        # reported as conclusion "unknown" — a coverage gap the operator must
        # explicitly review before affirming --escapes-reviewed, so a lookup
        # failure can hide nothing silently.
        pass
    return {"url": None, "jobs": {}}


def join_executed_outcomes(repo: str, reports: list[dict]) -> None:
    """Join legacy-only candidates to EXECUTED shard outcomes at the same head.

    The comparison workflow is report-only and never executes shards. For
    every report with candidates, look for a workflow run at the same head
    SHA that executed `Server Tests (<shard>)` jobs, match each candidate
    shard to its job, and stamp per-shard conclusions onto ``_meta`` for the
    aggregation. When a lane executed the shards at that head (nightly trunk,
    a train-batch dispatch, or any future per-PR shard lane) the real
    conclusions become the primary escape correlation; in the current
    merge-train model per-PR heads usually have none, so outcomes surface as
    conclusion "unknown" — a coverage gap the operator resolves manually
    (e.g. via the train batch run that landed the PR), never a signal.
    """
    cache: dict[str, dict] = {}
    for report in reports:
        candidates = _legacy_only_candidates(report)
        if not candidates:
            continue
        meta = report.setdefault("_meta", {})
        head_sha = meta.get("headSha")
        if not head_sha:
            continue
        if head_sha not in cache:
            cache[head_sha] = _fetch_ci_outcomes(repo, head_sha)
        outcomes = cache[head_sha]
        meta["ciRunUrl"] = outcomes.get("url")
        meta["shardOutcomes"] = {
            shard: outcomes["jobs"].get(
                SHARD_JOB_NAME_FORMAT.format(shard=shard), {"jobName": None, "conclusion": "unknown"}
            )
            for shard in candidates
        }


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


def _sample_key(report: dict, index: int) -> tuple:
    meta = report.get("_meta") if isinstance(report.get("_meta"), dict) else {}
    if meta.get("prNumber") is not None:
        return ("pr", meta["prNumber"])
    if meta.get("runId") is not None:
        return ("run", meta["runId"])
    if meta.get("source"):
        return ("source", meta["source"])
    return ("index", index)


def _latest_per_pr_indices(reports: list[dict]) -> set[int]:
    """Indices of the comparison sample: the latest run per pull request.

    Multiple synchronize pushes of one PR upload one report each; counting
    them all lets a single hot PR satisfy the sample criteria, so the sample
    keeps only the newest run (by runCreatedAt, falling back to input order)
    per prNumber. Reports without a derivable prNumber stay individual.
    """
    latest: dict[tuple, tuple] = {}
    for index, report in enumerate(reports):
        key = _sample_key(report, index)
        meta = report.get("_meta") if isinstance(report.get("_meta"), dict) else {}
        rank = (meta.get("runCreatedAt") or "", index)
        if key not in latest or rank >= latest[key][:2]:
            latest[key] = (*rank, index)
    return {entry[2] for entry in latest.values()}


def aggregate(
    reports: list[dict],
    total_shards: int,
    *,
    min_comparisons: int,
    min_strict_subset_pct: float,
    escapes_reviewed: bool = False,
    missing_evidence_prs: list[dict] | None = None,
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

    sample_indices = _latest_per_pr_indices(reports)
    escaped_defect_signal = False
    failed_shard_jobs: list[dict] = []
    unknown_outcome_count = 0
    stale_evidence_prs: list[dict] = []
    missing_evidence = list(missing_evidence_prs or [])

    for index, report in enumerate(reports):
        comparison = report["comparison"]
        selection = report["capabilitySelection"]
        legacy = report.get("legacy", {}) or {}
        meta = report.get("_meta") if isinstance(report.get("_meta"), dict) else {}
        in_sample = index in sample_indices
        capability_size = comparison.get("capabilityShardCount", len(selection.get("shards", [])))
        legacy_size = comparison.get("legacyShardCount", len(legacy.get("shards", [])))
        legacy_only = comparison.get("legacyOnlyShards", [])
        capability_only = comparison.get("capabilityOnlyShards", [])
        # capability-impact.py emits `comparison.escapedDefectCandidates` as
        # exactly `legacyOnlyShards` (the raw set difference legacy - graph);
        # the per-PR report itself carries no shard outcome data, so a
        # legacy-only shard is only a *potential* escape and is aggregated
        # here under its honest name. Consume the authoritative per-report
        # field, falling back to `legacyOnlyShards` for older reports.
        legacy_only_candidates = _legacy_only_candidates(report)

        if in_sample:
            capability_sizes.append(capability_size)
            legacy_sizes.append(legacy_size)
            if not legacy_only and not capability_only:
                relations["equal"] += 1
            elif not capability_only:
                relations["capabilitySubsetOfLegacy"] += 1
            elif not legacy_only:
                relations["capabilitySupersetOfLegacy"] += 1
            else:
                relations["divergent"] += 1
            for shard in legacy_only_candidates:
                legacy_only_frequency[shard] = legacy_only_frequency.get(shard, 0) + 1
            if legacy_only_candidates:
                reports_with_legacy_only += 1
                if not legacy.get("run_all"):
                    reports_with_legacy_only_excluding_legacy_run_all += 1
            if selection.get("runAll"):
                reason = selection.get("reason", "unknown")
                capability_run_all_reasons[reason] = capability_run_all_reasons.get(reason, 0) + 1
            if legacy.get("run_all"):
                reason = legacy.get("reason", "unknown")
                legacy_run_all_reasons[reason] = legacy_run_all_reasons.get(reason, 0) + 1
            if capability_size < legacy_size:
                strictly_smaller += 1
            # The latest PARSEABLE report is only current evidence if it came
            # from the PR's newest run: a newer run that produced no report
            # (failed completeness, cancelled before upload) means the PR's
            # evidence may be stale and must be surfaced, not silently reused.
            newest_run = meta.get("newestRunForPr") if isinstance(meta.get("newestRunForPr"), dict) else None
            if (
                newest_run
                and meta.get("runId") is not None
                and newest_run.get("runId") is not None
                and newest_run["runId"] != meta["runId"]
            ):
                stale_evidence_prs.append(
                    {
                        "prNumber": meta.get("prNumber"),
                        "latestReportRunId": meta.get("runId"),
                        "latestReportRunUrl": meta.get("runUrl"),
                        "newestRunId": newest_run["runId"],
                        "newestRunUrl": newest_run.get("runUrl"),
                    }
                )

        if legacy_only_candidates:
            # Preserve run identity plus the EXECUTED shard outcomes joined by
            # join_executed_outcomes (or embedded on --from-dir fixtures):
            # where a lane executed the shard jobs at the same head SHA their
            # real conclusions are the primary escape correlation; otherwise
            # the outcome stays "unknown" as an operator coverage gap.
            outcomes_map = meta.get("shardOutcomes") if isinstance(meta.get("shardOutcomes"), dict) else {}
            shard_outcomes = []
            for shard in sorted(legacy_only_candidates):
                outcome = outcomes_map.get(shard) or {}
                conclusion = outcome.get("conclusion") or "unknown"
                shard_outcomes.append(
                    {
                        "shard": shard,
                        "jobName": outcome.get("jobName"),
                        "conclusion": conclusion,
                        "runUrl": outcome.get("runUrl"),
                    }
                )
                # Only a verified "success" is clean. failure/timed_out raise
                # the blocking signal; every other conclusion (unknown,
                # cancelled, skipped, action_required, startup_failure, ...)
                # is a coverage gap the operator must review explicitly.
                if conclusion in SIGNAL_CONCLUSIONS:
                    escaped_defect_signal = True
                    failed_shard_jobs.append(
                        {
                            "prNumber": meta.get("prNumber"),
                            "shard": shard,
                            "jobName": outcome.get("jobName"),
                            "conclusion": conclusion,
                            "ciRunUrl": outcome.get("runUrl") or meta.get("ciRunUrl"),
                        }
                    )
                elif conclusion != "success":
                    unknown_outcome_count += 1
            legacy_only_occurrences.append(
                {
                    "runId": meta.get("runId"),
                    "runUrl": meta.get("runUrl"),
                    "runCreatedAt": meta.get("runCreatedAt"),
                    "prNumber": meta.get("prNumber"),
                    "artifactName": meta.get("artifactName"),
                    "source": meta.get("source"),
                    "reportIndex": index,
                    "latestForPr": in_sample,
                    "ciRunUrl": meta.get("ciRunUrl"),
                    "legacyOnlyShards": sorted(legacy_only_candidates),
                    "shardOutcomes": shard_outcomes,
                    "legacyRunAll": bool(legacy.get("run_all")),
                }
            )

    count = len(sample_indices)
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
        "rawReportCount": len(reports),
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
        "staleEvidencePrs": stale_evidence_prs,
        "missingEvidencePrs": missing_evidence,
        "legacyOnlyShards": {
            "shardFrequency": _frequency_table(legacy_only_frequency),
            "reportsWithLegacyOnlyShards": reports_with_legacy_only,
            "reportsWithLegacyOnlyShardsExcludingLegacyRunAll": reports_with_legacy_only_excluding_legacy_run_all,
            "unknownOutcomeCount": unknown_outcome_count,
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
            "escapedDefectSignal": escaped_defect_signal,
            "failedShardJobs": failed_shard_jobs,
            "staleEvidencePrCount": len(stale_evidence_prs),
            "missingEvidencePrCount": len(missing_evidence),
            "safeToSwitch": (
                all(criterion["met"] for criterion in criteria)
                and escapes_reviewed
                and not escaped_defect_signal
                and not stale_evidence_prs
                and not missing_evidence
            ),
            "criteria": criteria,
            "note": (
                "Legacy-only shards (the per-report escapedDefectCandidates field) are shards "
                "ADR-0037 would run but the capability selector would not. They are definitionally "
                "present in every comparison where the capability selection is strictly smaller, so "
                "they cannot be a zero-tolerance switch criterion for a selector that is supposed to "
                "be tighter. escapedDefectSignal joins each candidate to the executed "
                "'Server Tests (<shard>)' job conclusions merged across the workflow runs at the "
                "same head SHA and is true if any such job failed or timed out in the window, which "
                "blocks safeToSwitch outright. Only a verified 'success' counts as clean: every "
                "other conclusion (unknown, cancelled, skipped, action_required, startup_failure, "
                "...) is a coverage gap, never a signal — and in the merge-train model per-PR heads "
                "often have no executed shard jobs at all (the matrix runs on nightly trunk and "
                "train-batch dispatches). safeToSwitch therefore additionally requires the operator "
                "to review the non-success rows in legacyOnlyShards.occurrences (e.g. against the "
                "train batch run that landed the PR) and affirm with --escapes-reviewed. Sample "
                "counts use the latest run per distinct PR (rawReportCount shows all), and "
                "safeToSwitch also requires staleEvidencePrs and missingEvidencePrs to be empty: a "
                "PR whose newest run produced no comparison report is being judged on outdated "
                "evidence, and a PR with no report from any run was never observed at all."
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
        f"- Window: last {days} days, {summary['comparisonCount']} comparisons "
        f"(latest run per distinct PR; {summary['rawReportCount']} raw reports)",
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
        lines.extend(["| Run | PR | Legacy-only shards | Executed CI outcome |", "|---|---|---|---|"])
        for occurrence in occurrences[:20]:
            if occurrence["runUrl"]:
                run_cell = f"[{occurrence['runId'] or 'run'}]({occurrence['runUrl']})"
            else:
                run_cell = occurrence["runId"] or occurrence["source"] or "?"
            pr_cell = f"#{occurrence['prNumber']}" if occurrence["prNumber"] else "?"
            if not occurrence["latestForPr"]:
                pr_cell += " (superseded)"
            shard_cell = ", ".join(occurrence["legacyOnlyShards"])
            if occurrence["legacyRunAll"]:
                shard_cell += " (legacy run_all)"
            outcome_text = ", ".join(f"{entry['shard']}={entry['conclusion']}" for entry in occurrence["shardOutcomes"])
            if occurrence["ciRunUrl"]:
                outcome_cell = f"[{outcome_text}]({occurrence['ciRunUrl']})"
            else:
                outcome_cell = outcome_text or "unknown"
            lines.append(f"| {run_cell} | {pr_cell} | {shard_cell} | {outcome_cell} |")
        if len(occurrences) > 20:
            lines.append(f"| … {len(occurrences) - 20} more (see JSON `legacyOnlyShards.occurrences`) | | | |")
    else:
        lines.append("None.")
    if legacy_only["unknownOutcomeCount"]:
        lines.extend(
            [
                "",
                f"- Non-success shard outcomes needing review: {legacy_only['unknownOutcomeCount']} "
                "(unknown/cancelled/skipped/... coverage gaps — only a verified `success` is clean; "
                "the operator must review these before affirming `--escapes-reviewed`)",
            ]
        )
    lines.extend(
        [
            "",
            "### run_all fallbacks",
            "",
            f"- Capability selector: {fallbacks['capability']['count']} ({json.dumps(fallbacks['capability']['reasons'])})",
            f"- ADR-0037 selector: {fallbacks['legacy']['count']} ({json.dumps(fallbacks['legacy']['reasons'])})",
        ]
    )
    if summary["staleEvidencePrs"] or summary["missingEvidencePrs"]:
        lines.extend(["", "### Evidence gaps (newest run has no report)", ""])
        for gap in summary["missingEvidencePrs"]:
            pr_text = f"PR #{gap['prNumber']}" if gap["prNumber"] else "PR ?"
            newest_text = f"[{gap['newestRunId']}]({gap['newestRunUrl']})" if gap["newestRunUrl"] else gap["newestRunId"]
            lines.append(
                f"- {pr_text}: NO run produced a comparison report (newest run {newest_text}) — "
                "this PR's selector behavior was never observed."
            )
        for gap in summary["staleEvidencePrs"]:
            pr_text = f"PR #{gap['prNumber']}" if gap["prNumber"] else "PR ?"
            newest_text = f"[{gap['newestRunId']}]({gap['newestRunUrl']})" if gap["newestRunUrl"] else gap["newestRunId"]
            report_text = (
                f"[{gap['latestReportRunId']}]({gap['latestReportRunUrl']})"
                if gap["latestReportRunUrl"]
                else gap["latestReportRunId"]
            )
            lines.append(
                f"- {pr_text}: newest run {newest_text} produced no comparison report — "
                f"the sampled evidence comes from older run {report_text} and may be stale."
            )
    lines.extend(
        [
            "",
            "### Switch recommendation",
            "",
        ]
    )
    for criterion in recommendation["criteria"]:
        mark = "x" if criterion["met"] else " "
        observed_suffix = " distinct PRs/runs" if criterion["name"] == "comparisonCount" else ""
        lines.append(
            f"- [{mark}] {criterion['name']} {criterion['threshold']} (observed: {criterion['observed']}{observed_suffix})"
        )
    signal_mark = " " if recommendation["escapedDefectSignal"] else "x"
    lines.append(
        f"- [{signal_mark}] no escaped-defect signal "
        "(no legacy-only shard job failed or timed out in executed CI over the window)"
    )
    stale_mark = " " if recommendation["staleEvidencePrCount"] else "x"
    lines.append(
        f"- [{stale_mark}] no stale-evidence PRs (every sampled PR's newest run produced a comparison report; "
        f"observed: {recommendation['staleEvidencePrCount']})"
    )
    missing_mark = " " if recommendation["missingEvidencePrCount"] else "x"
    lines.append(
        f"- [{missing_mark}] no missing-evidence PRs (every PR in the window produced at least one "
        f"comparison report; observed: {recommendation['missingEvidencePrCount']})"
    )
    reviewed_mark = "x" if recommendation["escapesReviewed"] else " "
    lines.append(
        f"- [{reviewed_mark}] non-success outcomes reviewed (operator acknowledgment, `--escapes-reviewed`)"
    )
    if recommendation["escapedDefectSignal"]:
        failures = ", ".join(
            f"{job['shard']}={job['conclusion']}" + (f" (PR #{job['prNumber']})" if job["prNumber"] else "")
            for job in recommendation["failedShardJobs"]
        )
        verdict = (
            "**Verdict: NOT safe to switch — ESCAPED-DEFECT SIGNAL.** A legacy-only shard job "
            f"failed or timed out in executed CI within the window: {failures}. The capability "
            "selector would have skipped a genuinely failing shard; fix the capability mapping "
            "before switching."
        )
    elif recommendation["safeToSwitch"]:
        verdict = (
            "**Verdict: SAFE to switch** (quantitative preconditions met, no escaped-defect signal "
            "in executed CI, no stale-evidence PRs, and the non-success outcome review acknowledged)."
        )
    elif recommendation["preconditionsMet"] and (
        recommendation["staleEvidencePrCount"] or recommendation["missingEvidencePrCount"]
    ):
        verdict = (
            "**Verdict: NOT safe to switch — evidence gaps.** "
            f"{recommendation['staleEvidencePrCount']} PR(s) with stale evidence and "
            f"{recommendation['missingEvidencePrCount']} PR(s) with no comparison report at all "
            "(see 'Evidence gaps'); re-run the comparison on those PRs' newest pushes before "
            "switching."
        )
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
        # Offline mode: executed outcomes may be embedded on the fixtures
        # (_meta.shardOutcomes); no live join or run-inventory gap detection
        # is attempted (there is no run list to diff against).
        reports = load_reports_from_dir(args.from_dir)
        missing_evidence_prs: list[dict] = []
    else:
        reports, missing_evidence_prs = fetch_reports(args.repo, args.days)
        join_executed_outcomes(args.repo, reports)
    total_shards = args.total_shards if args.total_shards is not None else load_total_shard_count()
    summary = aggregate(
        reports,
        total_shards,
        min_comparisons=args.min_comparisons,
        min_strict_subset_pct=args.min_strict_subset_pct,
        escapes_reviewed=args.escapes_reviewed,
        missing_evidence_prs=missing_evidence_prs,
    )
    print(json.dumps(summary, indent=2))
    if args.markdown:
        args.markdown.write_text(markdown(summary, args.days), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
