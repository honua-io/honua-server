#!/usr/bin/env python3
"""Collect a reproducible GitHub Actions latency and runner-time baseline.

The collector is deliberately read-only. Live mode queries workflow runs and
all job attempts through ``gh api``; fixture mode consumes the same normalized
input shape without network access. JSON and Markdown render from one summary.

Examples::

    scripts/ci/measure-actions-baseline.py \
      --workflow pr-gate.yml --workflow merge-train.yml --limit 30

    scripts/ci/measure-actions-baseline.py \
      --fixture scripts/ci/fixtures/actions-baseline.sample.json --format markdown

``estimated_rounded_linux_minutes`` is not an invoice value. GitHub's timing
endpoint reports zero billable milliseconds for public-repository runs, so this
tool transparently estimates Linux usage by rounding each observed Linux job's
wall duration up to a minute. Raw summed job time is always reported beside it.
"""

from __future__ import annotations

import argparse
import json
import math
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib.parse import quote, urlencode

SCHEMA = "honua.actions-baseline/v1"
INPUT_SCHEMA = "honua.actions-baseline.input/v1"
FAILURE_CONCLUSIONS = frozenset({"action_required", "failure", "startup_failure", "timed_out"})
RUN_INPUT_FIELDS = (
    "id",
    "run_attempt",
    "event",
    "head_sha",
    "status",
    "conclusion",
    "created_at",
    "run_started_at",
    "updated_at",
    "html_url",
)
JOB_INPUT_FIELDS = (
    "id",
    "run_attempt",
    "name",
    "status",
    "conclusion",
    "started_at",
    "completed_at",
    "labels",
)


def parse_time(value: Any) -> datetime | None:
    if isinstance(value, datetime):
        return value.replace(tzinfo=value.tzinfo or timezone.utc).astimezone(timezone.utc)
    if not isinstance(value, str) or not value:
        return None
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return parsed.astimezone(timezone.utc)


def elapsed_seconds(start: Any, end: Any) -> float | None:
    start_time = parse_time(start)
    end_time = parse_time(end)
    if start_time is None or end_time is None or end_time < start_time:
        return None
    return (end_time - start_time).total_seconds()


def nearest_rank(values: list[float], fraction: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    rank = max(1, math.ceil(fraction * len(ordered)))
    return ordered[min(rank, len(ordered)) - 1]


def rounded(value: float | None, digits: int = 2) -> float | None:
    return None if value is None else round(value, digits)


def percentile_pair(values: list[float]) -> dict[str, float | None]:
    return {
        "p50": rounded(nearest_rank(values, 0.5)),
        "p90": rounded(nearest_rank(values, 0.9)),
    }


def is_linux_job(job: dict[str, Any]) -> bool:
    labels = [str(label).lower() for label in job.get("labels", []) if label is not None]
    return any(label == "linux" or label.startswith("ubuntu") for label in labels)


def normalize_run(run: dict[str, Any]) -> dict[str, Any]:
    created_at = run.get("created_at")
    run_started_at = run.get("run_started_at")
    run_completed_at = run.get("updated_at") if run.get("status") == "completed" else None

    runner_seconds = 0.0
    rounded_job_minutes = 0
    rounded_linux_minutes = 0
    observed_jobs = 0
    linux_jobs = 0
    skipped_jobs = 0
    jobs_missing_timestamps = 0
    failure_offsets: list[float] = []
    job_starts: list[datetime] = []
    job_completions: list[datetime] = []

    jobs = sorted(run.get("jobs", []), key=lambda job: (job.get("run_attempt", 1), job.get("id", 0)))
    for job in jobs:
        conclusion = job.get("conclusion")
        if conclusion == "skipped":
            skipped_jobs += 1
            continue
        job_start = parse_time(job.get("started_at"))
        job_completion = parse_time(job.get("completed_at"))
        if job_start is not None:
            job_starts.append(job_start)
        if job_completion is not None:
            job_completions.append(job_completion)
        duration = elapsed_seconds(job.get("started_at"), job.get("completed_at"))
        if duration is None:
            jobs_missing_timestamps += 1
        else:
            observed_jobs += 1
            runner_seconds += duration
            rounded_job_minutes += math.ceil(duration / 60.0)
            if is_linux_job(job):
                linux_jobs += 1
                rounded_linux_minutes += math.ceil(duration / 60.0)
        if conclusion in FAILURE_CONCLUSIONS:
            offset = elapsed_seconds(created_at, job.get("completed_at"))
            if offset is not None:
                failure_offsets.append(offset)

    # GitHub may update run_started_at when a run is rerun. Job timestamps are
    # the stable all-attempt record, so queue and critical path use their
    # earliest start/latest completion. Run timestamps are only a no-job
    # fallback (for example a workflow cancelled before runner assignment).
    first_started_at: datetime | str | None = min(job_starts) if job_starts else run_started_at
    last_completed_at: datetime | str | None = max(job_completions) if job_completions else run_completed_at
    queue_seconds = elapsed_seconds(created_at, first_started_at)
    critical_path_seconds = elapsed_seconds(first_started_at, last_completed_at)
    wall_seconds = elapsed_seconds(created_at, last_completed_at)

    conclusion = run.get("conclusion")
    return {
        "id": run.get("id"),
        "url": run.get("html_url"),
        "attempts": int(run.get("run_attempt") or 1),
        "event": run.get("event"),
        "head_sha": run.get("head_sha"),
        "status": run.get("status"),
        "conclusion": conclusion,
        "created_at": created_at,
        "queue_seconds": rounded(queue_seconds),
        "critical_path_seconds": rounded(critical_path_seconds),
        "wall_seconds": rounded(wall_seconds),
        "time_to_first_failure_seconds": rounded(min(failure_offsets)) if failure_offsets else None,
        "runner_seconds": rounded(runner_seconds),
        "estimated_rounded_job_minutes": rounded_job_minutes,
        "estimated_rounded_linux_minutes": rounded_linux_minutes,
        "cancelled_runner_seconds": rounded(runner_seconds) if conclusion == "cancelled" else 0.0,
        "jobs": {
            "total_records": len(jobs),
            "observed": observed_jobs,
            "linux": linux_jobs,
            "skipped": skipped_jobs,
            "missing_timestamps": jobs_missing_timestamps,
        },
    }


def summarize_workflow(workflow: dict[str, Any]) -> dict[str, Any]:
    runs = [normalize_run(run) for run in workflow.get("runs", [])]
    counts: dict[str, int] = {}
    for run in runs:
        key = str(run.get("conclusion") or run.get("status") or "unknown")
        counts[key] = counts.get(key, 0) + 1

    completed_runs = [run for run in runs if run["status"] == "completed"]
    successful_runs = [run for run in completed_runs if run["conclusion"] == "success"]

    def samples(field: str) -> list[float]:
        return [float(run[field]) for run in completed_runs if run.get(field) is not None]

    def success_samples(field: str) -> list[float]:
        return [float(run[field]) for run in successful_runs if run.get(field) is not None]

    # Runner usage is observed job evidence, not a terminal-run percentile.
    # Completed jobs in an active workflow have already consumed real minutes
    # and must remain in the cost baseline even though that run is excluded
    # from terminal latency samples.
    runner_seconds = sum(float(run["runner_seconds"]) for run in runs)
    cancelled_runner_seconds = sum(float(run["cancelled_runner_seconds"]) for run in runs)
    return {
        "workflow": workflow.get("workflow"),
        "name": workflow.get("name") or workflow.get("workflow"),
        "sampled_runs": len(runs),
        "completed_runs": len(completed_runs),
        "counts": dict(sorted(counts.items())),
        "queue_seconds": percentile_pair(samples("queue_seconds")),
        "critical_path_seconds": percentile_pair(samples("critical_path_seconds")),
        "successful_critical_path_seconds": percentile_pair(success_samples("critical_path_seconds")),
        "wall_seconds": percentile_pair(samples("wall_seconds")),
        "time_to_first_failure_seconds": percentile_pair(samples("time_to_first_failure_seconds")),
        "runner_minutes": rounded(runner_seconds / 60.0),
        "cancelled_runner_minutes": rounded(cancelled_runner_seconds / 60.0),
        "estimated_rounded_job_minutes": sum(
            int(run["estimated_rounded_job_minutes"]) for run in runs
        ),
        "estimated_rounded_linux_minutes": sum(
            int(run["estimated_rounded_linux_minutes"]) for run in runs
        ),
        "runs": runs,
    }


def summarize(dataset: dict[str, Any], generated_at: str) -> dict[str, Any]:
    if dataset.get("schema") != INPUT_SCHEMA:
        raise ValueError(f"input schema must be {INPUT_SCHEMA}")
    workflows = [summarize_workflow(workflow) for workflow in dataset.get("workflows", [])]
    workflows.sort(key=lambda item: str(item["workflow"]))
    return {
        "schema": SCHEMA,
        "generated_at": generated_at,
        "repository": dataset.get("repository"),
        "created_after": dataset.get("created_after"),
        "sample_limit_per_workflow": dataset.get("sample_limit_per_workflow"),
        "methodology": {
            "attempts": "all job attempts returned by the Actions jobs API",
            "critical_path": "earliest non-skipped job start to latest job completion across all attempts",
            "queue": "created_at to earliest non-skipped job start across all attempts",
            "runner_time": (
                "sum of completed_at - started_at for non-skipped jobs across every sampled run, "
                "including completed jobs in active workflows"
            ),
            "estimated_rounded_job_minutes": "ceil each observed non-skipped job duration to one minute",
            "estimated_rounded_linux_minutes": (
                "ceil each observed job with a linux/ubuntu label to one minute; estimate, not invoice usage"
            ),
            "percentiles": "nearest-rank over observations with valid timestamps",
        },
        "workflows": workflows,
    }


def compact_dataset(dataset: dict[str, Any]) -> dict[str, Any]:
    """Keep only the run/job fields that the baseline contract consumes."""
    workflows: list[dict[str, Any]] = []
    for workflow in dataset.get("workflows", []):
        runs: list[dict[str, Any]] = []
        for run in workflow.get("runs", []):
            compact_run = {field: run.get(field) for field in RUN_INPUT_FIELDS}
            compact_run["jobs"] = [
                {field: job.get(field) for field in JOB_INPUT_FIELDS}
                for job in run.get("jobs", [])
            ]
            runs.append(compact_run)
        workflows.append(
            {
                "workflow": workflow.get("workflow"),
                "name": workflow.get("name") or workflow.get("workflow"),
                "runs": runs,
            }
        )
    return {
        "schema": dataset.get("schema"),
        "repository": dataset.get("repository"),
        "created_after": dataset.get("created_after"),
        "sample_limit_per_workflow": dataset.get("sample_limit_per_workflow"),
        "workflows": workflows,
    }


def gh_pages(endpoint: str) -> list[dict[str, Any]]:
    process = subprocess.run(
        ["gh", "api", "--paginate", "--slurp", endpoint],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    if process.returncode != 0:
        raise RuntimeError(f"gh api failed for {endpoint}: {process.stderr.strip()}")
    payload = json.loads(process.stdout)
    if not isinstance(payload, list):
        raise RuntimeError(f"gh api --slurp returned a non-list payload for {endpoint}")
    return payload


def gh_json(endpoint: str) -> dict[str, Any]:
    process = subprocess.run(
        ["gh", "api", endpoint],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    if process.returncode != 0:
        raise RuntimeError(f"gh api failed for {endpoint}: {process.stderr.strip()}")
    payload = json.loads(process.stdout)
    if not isinstance(payload, dict):
        raise RuntimeError(f"gh api returned a non-object payload for {endpoint}")
    return payload


def bounded_workflow_run_pages(
    endpoint: str,
    limit: int,
    fetch_page: Any = gh_json,
) -> list[dict[str, Any]]:
    """Fetch only enough newest workflow-run pages to satisfy ``limit``."""
    pages: list[dict[str, Any]] = []
    observed = 0
    page_number = 1
    while observed < limit:
        separator = "&" if "?" in endpoint else "?"
        page = fetch_page(f"{endpoint}{separator}page={page_number}")
        runs = page.get("workflow_runs", [])
        if not isinstance(runs, list):
            raise RuntimeError("workflow run page has no workflow_runs list")
        pages.append(page)
        observed += len(runs)
        if len(runs) < 100:
            break
        page_number += 1
    return pages


def collect_live(
    repo: str,
    workflows: list[str],
    created_after: str | None,
    limit: int,
    api_workers: int,
) -> dict[str, Any]:
    collected: list[dict[str, Any]] = []
    for workflow in workflows:
        metadata_pages = gh_pages(f"repos/{repo}/actions/workflows/{quote(workflow, safe='')}")
        display_name = str(metadata_pages[0].get("name") or workflow) if metadata_pages else workflow
        query = {"per_page": "100"}
        if created_after:
            query["created"] = f">={created_after}"
        endpoint = (
            f"repos/{repo}/actions/workflows/{quote(workflow, safe='')}/runs?{urlencode(query)}"
        )
        pages = bounded_workflow_run_pages(endpoint, limit)
        runs = [run for page in pages for run in page.get("workflow_runs", [])]
        runs.sort(key=lambda run: str(run.get("created_at") or ""), reverse=True)

        def load_jobs(run: dict[str, Any]) -> dict[str, Any]:
            jobs_endpoint = f"repos/{repo}/actions/runs/{run['id']}/jobs?per_page=100&filter=all"
            job_pages = gh_pages(jobs_endpoint)
            jobs_by_id: dict[int, dict[str, Any]] = {}
            for page in job_pages:
                for job in page.get("jobs", []):
                    job_id = int(job["id"])
                    jobs_by_id[job_id] = job
            normalized_run = dict(run)
            normalized_run["jobs"] = list(jobs_by_id.values())
            return normalized_run

        with ThreadPoolExecutor(max_workers=api_workers) as executor:
            normalized_runs = list(executor.map(load_jobs, runs[:limit]))
        collected.append({"workflow": workflow, "name": display_name, "runs": normalized_runs})
    return {
        "schema": INPUT_SCHEMA,
        "repository": repo,
        "created_after": created_after,
        "sample_limit_per_workflow": limit,
        "workflows": collected,
    }


def fmt_seconds(value: float | None) -> str:
    if value is None:
        return "-"
    return f"{value / 60.0:.1f}m"


def render_markdown(summary: dict[str, Any]) -> str:
    lines = [
        "# GitHub Actions baseline",
        "",
        f"Repository: `{summary.get('repository')}`  ",
        f"Created after: `{summary.get('created_after') or 'not constrained'}`  ",
        f"Sample limit: `{summary.get('sample_limit_per_workflow')}` per workflow",
        "",
        (
            "> Rounded Linux minutes are an estimate from observed job timestamps; "
            "they are not GitHub invoice data."
        ),
        "",
        "| Workflow | Runs | Success | Failure | Cancelled | Queue p90 | Successful critical p90 | First failure p50 | Raw runner min | Rounded Linux min | Cancelled runner min |",
        "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|",
    ]
    for workflow in summary["workflows"]:
        counts = workflow["counts"]
        failure_count = sum(counts.get(conclusion, 0) for conclusion in FAILURE_CONCLUSIONS)
        lines.append(
            f"| {workflow['name']} | {workflow['sampled_runs']} | {counts.get('success', 0)} | "
            f"{failure_count} | {counts.get('cancelled', 0)} | "
            f"{fmt_seconds(workflow['queue_seconds']['p90'])} | "
            f"{fmt_seconds(workflow['successful_critical_path_seconds']['p90'])} | "
            f"{fmt_seconds(workflow['time_to_first_failure_seconds']['p50'])} | "
            f"{workflow['runner_minutes']:.2f} | {workflow['estimated_rounded_linux_minutes']} | "
            f"{workflow['cancelled_runner_minutes']:.2f} |"
        )
    lines.extend(
        [
            "",
            "Methodology: all attempts, skipped jobs excluded from runner time, nearest-rank percentiles.",
        ]
    )
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--repo", default="honua-io/honua-server", help="GitHub owner/repository")
    parser.add_argument("--workflow", action="append", default=[], help="workflow file/name; repeatable")
    parser.add_argument("--created-after", help="inclusive UTC date or timestamp passed to the Actions API")
    parser.add_argument("--limit", type=int, default=30, help="maximum runs per workflow")
    parser.add_argument(
        "--api-workers",
        type=int,
        default=6,
        help="bounded concurrent read-only gh API requests in live mode",
    )
    parser.add_argument("--fixture", type=Path, help="read a normalized input fixture instead of GitHub")
    parser.add_argument("--format", choices=("json", "markdown"), default="json")
    parser.add_argument("--input-out", type=Path, help="write normalized raw run/job input for reproducibility")
    parser.add_argument("--json-out", type=Path, help="also write the stable JSON report to this path")
    parser.add_argument("--markdown-out", type=Path, help="also write the Markdown report to this path")
    parser.add_argument(
        "--generated-at",
        default=datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        help="report timestamp; override for deterministic fixtures",
    )
    args = parser.parse_args(argv)

    if args.limit < 1 or args.limit > 100:
        parser.error("--limit must be between 1 and 100")
    if args.api_workers < 1 or args.api_workers > 16:
        parser.error("--api-workers must be between 1 and 16")
    if args.fixture:
        dataset = json.loads(args.fixture.read_text(encoding="utf-8"))
    else:
        if not args.workflow:
            parser.error("live mode requires at least one --workflow")
        dataset = collect_live(args.repo, args.workflow, args.created_after, args.limit, args.api_workers)

    dataset = compact_dataset(dataset)
    try:
        report = summarize(dataset, args.generated_at)
    except ValueError as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    if args.input_out:
        args.input_out.parent.mkdir(parents=True, exist_ok=True)
        args.input_out.write_text(json.dumps(dataset, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    json_text = json.dumps(report, indent=2, sort_keys=True) + "\n"
    markdown_text = render_markdown(report) + "\n"
    if args.json_out:
        args.json_out.parent.mkdir(parents=True, exist_ok=True)
        args.json_out.write_text(json_text, encoding="utf-8")
    if args.markdown_out:
        args.markdown_out.parent.mkdir(parents=True, exist_ok=True)
        args.markdown_out.write_text(markdown_text, encoding="utf-8")
    if args.format == "markdown":
        print(markdown_text, end="")
    else:
        print(json_text, end="")
    return 0


if __name__ == "__main__":
    sys.exit(main())
