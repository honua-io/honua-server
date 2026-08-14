#!/usr/bin/env python3
"""Compare opportunistic cross-workflow prebuilds with independent shard builds."""

from __future__ import annotations

import argparse
import json
import math
from datetime import datetime
from pathlib import Path

METRIC_CONTRACT = "honua.server-test-transfer-benchmark.v1"
PRODUCER_METRIC_CONTRACT = "honua.server-test-prebuild-metric/v1"
PLAN_CONTRACT = "honua.server-test-prebuild-benchmark-plan/v1"
SUMMARY_CONTRACT = "honua.server-test-prebuild-benchmark-summary/v1"


def load_json(path: Path) -> object:
    return json.loads(path.read_text(encoding="utf-8"))


def load_metrics(root: Path, contract: str) -> list[dict]:
    result = []
    for path in root.rglob("*.json"):
        try:
            value = load_json(path)
        except (OSError, json.JSONDecodeError):
            continue
        if isinstance(value, dict) and value.get("contract") == contract:
            result.append(value)
    return result


def epoch_ms(value: str) -> int:
    return int(datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp() * 1000)


def load_hosted_jobs(root: Path) -> list[dict]:
    jobs: list[dict] = []
    for path in root.rglob("*.json"):
        value = load_json(path)
        pages = value if isinstance(value, list) else [value]
        for page in pages:
            if not isinstance(page, dict) or not isinstance(page.get("jobs"), list):
                raise ValueError(f"hosted jobs payload is invalid: {path}")
            for job in page["jobs"]:
                name = job.get("name")
                attempt = job.get("run_attempt")
                started = job.get("started_at")
                completed = job.get("completed_at")
                conclusion = job.get("conclusion")
                if not isinstance(name, str) or conclusion == "skipped":
                    continue
                if not isinstance(attempt, int) or not isinstance(started, str) or not isinstance(completed, str):
                    raise ValueError(f"hosted job interval is incomplete: {name}")
                start = epoch_ms(started)
                end = epoch_ms(completed)
                if end <= start:
                    raise ValueError(f"hosted job interval is invalid: {name}")
                jobs.append(
                    {
                        "attempt": attempt,
                        "conclusion": conclusion,
                        "elapsed_ms": end - start,
                        "end_ms": end,
                        "name": name,
                        "start_ms": start,
                    }
                )
    return jobs


def nearest_rank(values: list[int], percentile: float) -> int:
    if not values:
        raise ValueError("percentile requires observations")
    ordered = sorted(values)
    return ordered[max(0, math.ceil(percentile * len(ordered)) - 1)]


def rounded_minutes(jobs: list[dict]) -> int:
    return sum(math.ceil(item["elapsed_ms"] / 60_000) for item in jobs)


def bind_metrics(metrics: list[dict], jobs: list[dict], *, attempt: int) -> dict[tuple[str, str], dict]:
    prefixes = {
        "baseline": "Independent prebuild baseline / ",
        "consumer-ready": "Opportunistic prebuild candidate / ",
    }
    intervals: dict[tuple[str, str], dict] = {}
    for job in jobs:
        if job["attempt"] != attempt:
            continue
        match = next(
            ((mode, job["name"][len(prefix) :]) for mode, prefix in prefixes.items() if job["name"].startswith(prefix)),
            None,
        )
        if match is not None:
            if match in intervals:
                raise ValueError(f"duplicate hosted interval for {match}")
            intervals[match] = job
    result: dict[tuple[str, str], dict] = {}
    for metric in metrics:
        if metric.get("run_attempt") != attempt:
            continue
        key = (metric.get("mode"), metric.get("identity"))
        if key in result:
            raise ValueError(f"duplicate benchmark metric for {key}")
        interval = intervals.get(key)
        if interval is None:
            raise ValueError(f"missing hosted interval for {key}")
        if interval["conclusion"] != "success":
            raise ValueError(f"hosted interval was not successful for {key}")
        value = dict(metric)
        value["job_start_epoch_ms"] = interval["start_ms"]
        value["job_elapsed_ms"] = interval["elapsed_ms"]
        result[key] = value
    return result


def timing(jobs: list[dict]) -> dict:
    origin = min(item["job_start_epoch_ms"] for item in jobs)
    starts = [item["test_started_epoch_ms"] - origin for item in jobs]
    wall = max(item["job_start_epoch_ms"] + item["job_elapsed_ms"] for item in jobs) - origin
    return {
        "first_test_ms": min(starts),
        "p90_test_start_ms": nearest_rank(starts, 0.90),
        "wall_clock_ms": wall,
    }


def summarize(
    plan: dict,
    config: dict,
    metrics: list[dict],
    producer_metrics: list[dict],
    benchmark_jobs: list[dict],
    producer_jobs: list[dict],
    *,
    benchmark_attempt: int,
    producer_attempt: int,
    head_sha: str,
) -> dict:
    if plan.get("contract") != PLAN_CONTRACT:
        raise ValueError("prebuild benchmark plan contract is invalid")
    threshold = config.get("decision_thresholds", {}).get("max_wall_clock_regression_percent")
    if not isinstance(threshold, (int, float)) or not 0 <= threshold <= 20:
        raise ValueError("wall-clock threshold is invalid")

    bound = bind_metrics(metrics, benchmark_jobs, attempt=benchmark_attempt)
    baseline = []
    candidate = []
    parity_failures = []
    reuse_failures = []
    for shard in plan.get("baseline", []):
        identity = shard["identity"]
        before = bound.get(("baseline", identity))
        after = bound.get(("consumer-ready", identity))
        if before is None or after is None:
            raise ValueError(f"benchmark evidence is incomplete for {identity}")
        for item in (before, after):
            if (
                not isinstance(item.get("test_started_epoch_ms"), int)
                or not isinstance(item.get("result_sha256"), str)
                or len(item["result_sha256"]) != 64
                or item.get("result_count", 0) < 1
            ):
                raise ValueError(f"benchmark test evidence is invalid for {identity}")
        baseline.append(before)
        candidate.append(after)
        if (
            before["filter_sha256"] != after["filter_sha256"]
            or before["result_sha256"] != after["result_sha256"]
            or before["result_count"] != after["result_count"]
            or before.get("result_outcomes") != after.get("result_outcomes")
        ):
            parity_failures.append(identity)
        prebuild = after.get("prebuild")
        if not isinstance(prebuild, dict):
            reuse_failures.append(identity)
        elif shard.get("reuse_expected") and prebuild.get("mode") != "prebuild":
            reuse_failures.append(identity)
        elif not shard.get("reuse_expected") and prebuild.get("mode") != "local-fallback":
            reuse_failures.append(identity)

    external_jobs = [
        item
        for item in producer_jobs
        if item["attempt"] == producer_attempt
        and (
            item["name"] == "Plan bounded exact-head prebuild"
            or item["name"].startswith("Prebuild repeated project / ")
        )
    ]
    if not external_jobs:
        raise ValueError("producer hosted timing evidence is missing")
    expected_projects = set(plan.get("reused_projects", []))
    plan_jobs = [item for item in external_jobs if item["name"] == "Plan bounded exact-head prebuild"]
    project_jobs = [item for item in external_jobs if item["name"].startswith("Prebuild repeated project / ")]
    if len(plan_jobs) != 1 or len(project_jobs) != len(expected_projects):
        raise ValueError("producer hosted interval set is incomplete or duplicated")
    unsuccessful_producer_jobs = [item["name"] for item in external_jobs if item["conclusion"] != "success"]
    if unsuccessful_producer_jobs:
        raise ValueError(f"producer hosted interval was not successful: {unsuccessful_producer_jobs}")
    matching_producer_metrics = [
        item
        for item in producer_metrics
        if item.get("head_sha") == head_sha and item.get("run_attempt") == producer_attempt
    ]
    observed_projects = [item.get("project") for item in matching_producer_metrics]
    if len(observed_projects) != len(set(observed_projects)):
        raise ValueError("producer metric project evidence is duplicated")
    producer_evidence_ok = set(observed_projects) == expected_projects
    producer_ready_before_candidate = max(item["end_ms"] for item in external_jobs) <= min(
        item["job_start_epoch_ms"] for item in candidate
    )

    baseline_timing = timing(baseline)
    candidate_timing = timing(candidate)
    baseline_jobs = [
        {
            "elapsed_ms": item["job_elapsed_ms"],
            "start_ms": item["job_start_epoch_ms"],
            "end_ms": item["job_start_epoch_ms"] + item["job_elapsed_ms"],
        }
        for item in baseline
    ]
    candidate_jobs = [
        {
            "elapsed_ms": item["job_elapsed_ms"],
            "start_ms": item["job_start_epoch_ms"],
            "end_ms": item["job_start_epoch_ms"] + item["job_elapsed_ms"],
        }
        for item in candidate
    ]
    baseline_minutes = rounded_minutes(baseline_jobs)
    candidate_minutes = rounded_minutes(candidate_jobs) + rounded_minutes(external_jobs)
    minutes_ok = candidate_minutes < baseline_minutes
    p90_ok = candidate_timing["p90_test_start_ms"] < baseline_timing["p90_test_start_ms"]
    wall_ok = candidate_timing["wall_clock_ms"] <= baseline_timing["wall_clock_ms"] * (1 + threshold / 100)
    eligible = all(
        (
            not parity_failures,
            not reuse_failures,
            producer_evidence_ok,
            producer_ready_before_candidate,
            minutes_ok,
            p90_ok,
            wall_ok,
        )
    )
    producer_origin = min(item["start_ms"] for item in external_jobs)
    return {
        "contract": SUMMARY_CONTRACT,
        "decision": "eligible-for-20-head-shadow" if eligible else "keep-local-build-authoritative",
        "profile": plan["profile"],
        "head_sha": head_sha,
        "baseline": {**baseline_timing, "rounded_runner_minutes": baseline_minutes},
        "candidate": {
            **candidate_timing,
            "rounded_runner_minutes_including_prebuild": candidate_minutes,
            "head_to_first_test_ms": min(item["test_started_epoch_ms"] for item in candidate) - producer_origin,
        },
        "parity_failures": parity_failures,
        "reuse_failures": reuse_failures,
        "producer_evidence_ok": producer_evidence_ok,
        "producer_ready_before_candidate": producer_ready_before_candidate,
        "rounded_runner_minutes_ok": minutes_ok,
        "p90_test_start_ok": p90_ok,
        "wall_clock_ok": wall_ok,
    }


def markdown(summary: dict) -> str:
    return "\n".join(
        [
            "# Opportunistic server-test prebuild benchmark",
            "",
            f"Decision: **{summary['decision']}**",
            "",
            "| Path | Rounded runner min | First test ms | p90 test start ms | Wall ms |",
            "|---|---:|---:|---:|---:|",
            f"| Independent baseline | {summary['baseline']['rounded_runner_minutes']} | {summary['baseline']['first_test_ms']} | {summary['baseline']['p90_test_start_ms']} | {summary['baseline']['wall_clock_ms']} |",
            f"| Opportunistic (producer included) | {summary['candidate']['rounded_runner_minutes_including_prebuild']} | {summary['candidate']['first_test_ms']} | {summary['candidate']['p90_test_start_ms']} | {summary['candidate']['wall_clock_ms']} |",
            "",
            f"Parity failures: `{summary['parity_failures']}`  ",
            f"Reuse failures: `{summary['reuse_failures']}`  ",
            f"Producer ready before verification: `{summary['producer_ready_before_candidate']}`  ",
            f"Head-to-first-test elapsed: `{summary['candidate']['head_to_first_test_ms']}` ms",
        ]
    ) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--metrics", type=Path, required=True)
    parser.add_argument("--producer-metrics", type=Path, required=True)
    parser.add_argument("--plan", type=Path, required=True)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--benchmark-jobs", type=Path, required=True)
    parser.add_argument("--producer-jobs", type=Path, required=True)
    parser.add_argument("--benchmark-attempt", type=int, required=True)
    parser.add_argument("--producer-attempt", type=int, required=True)
    parser.add_argument("--head-sha", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--markdown", type=Path, required=True)
    args = parser.parse_args()
    result = summarize(
        load_json(args.plan),
        load_json(args.config),
        load_metrics(args.metrics, METRIC_CONTRACT),
        load_metrics(args.producer_metrics, PRODUCER_METRIC_CONTRACT),
        load_hosted_jobs(args.benchmark_jobs),
        load_hosted_jobs(args.producer_jobs),
        benchmark_attempt=args.benchmark_attempt,
        producer_attempt=args.producer_attempt,
        head_sha=args.head_sha,
    )
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    rendered = markdown(result)
    args.markdown.write_text(rendered, encoding="utf-8")
    print(rendered)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
