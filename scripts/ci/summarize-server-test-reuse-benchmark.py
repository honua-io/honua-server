#!/usr/bin/env python3
"""Evaluate hybrid repeated-project reuse against independent shard builds."""

from __future__ import annotations

import argparse
import json
import math
from collections import Counter
from datetime import datetime
from pathlib import Path

METRIC_CONTRACT = "honua.server-test-transfer-benchmark.v1"
SUMMARY_CONTRACT = "honua.server-test-reuse-summary/v1"


def load_metrics(root: Path) -> list[dict]:
    metrics: list[dict] = []
    for path in root.rglob("*.json"):
        try:
            value = json.loads(path.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError):
            continue
        if isinstance(value, dict) and value.get("contract") == METRIC_CONTRACT:
            metrics.append(value)
    return metrics


def epoch_ms(value: str) -> int:
    return int(datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp() * 1000)


def load_hosted_jobs(root: Path) -> dict[tuple[int, str, str], dict[str, int]]:
    prefixes = {
        "producer": "Reuse payload producer / ",
        "baseline": "Independent baseline shard / ",
        "consumer-artifact": "Overlapped reuse shard / ",
    }
    intervals: dict[tuple[int, str, str], dict[str, int]] = {}
    for path in root.rglob("*.json"):
        value = json.loads(path.read_text(encoding="utf-8"))
        pages = value if isinstance(value, list) else [value]
        for page in pages:
            if not isinstance(page, dict) or not isinstance(page.get("jobs"), list):
                raise ValueError(f"hosted jobs payload is invalid: {path}")
            for job in page["jobs"]:
                name = job.get("name")
                attempt = job.get("run_attempt")
                started_at = job.get("started_at")
                completed_at = job.get("completed_at")
                if not isinstance(name, str):
                    continue
                matched = next(
                    ((mode, name[len(prefix) :]) for mode, prefix in prefixes.items() if name.startswith(prefix)),
                    None,
                )
                if matched is None:
                    continue
                if not isinstance(attempt, int) or not isinstance(started_at, str) or not isinstance(completed_at, str):
                    raise ValueError(f"hosted benchmark job interval is incomplete: {name}")
                start = epoch_ms(started_at)
                end = epoch_ms(completed_at)
                if end <= start:
                    raise ValueError(f"hosted benchmark job interval is invalid: {name}")
                mode, identity = matched
                key = (attempt, mode, identity)
                if key in intervals:
                    raise ValueError(f"duplicate hosted benchmark job interval: {name} attempt {attempt}")
                intervals[key] = {"job_start_epoch_ms": start, "job_elapsed_ms": end - start}
    return intervals


def bind_hosted_intervals(
    metrics: list[dict], hosted_jobs: dict[tuple[int, str, str], dict[str, int]]
) -> list[dict]:
    bound: list[dict] = []
    for metric in metrics:
        key = (metric.get("run_attempt"), metric.get("mode"), metric.get("identity"))
        interval = hosted_jobs.get(key)
        if interval is None:
            raise ValueError(f"missing hosted job interval for metric {key}")
        item = dict(metric)
        item.update(interval)
        bound.append(item)
    return bound


def nearest_rank(values: list[int], percentile: float) -> int:
    if not values:
        raise ValueError("percentile requires observations")
    ordered = sorted(values)
    return ordered[max(0, math.ceil(percentile * len(ordered)) - 1)]


def rounded_minutes(jobs: list[dict]) -> int:
    return sum(math.ceil(item["job_elapsed_ms"] / 60_000) for item in jobs)


def path_timing(jobs: list[dict], test_jobs: list[dict]) -> dict:
    origin = min(item["job_start_epoch_ms"] for item in jobs)
    starts = [item["test_started_epoch_ms"] - origin for item in test_jobs]
    completion = max(item["job_start_epoch_ms"] + item["job_elapsed_ms"] for item in jobs) - origin
    return {
        "first_test_ms": min(starts),
        "p90_test_start_ms": nearest_rank(starts, 0.90),
        "all_tests_started_ms": max(starts),
        "wall_clock_ms": completion,
    }


def valid_metric(item: dict | None, *, test: bool) -> bool:
    if not item or not isinstance(item.get("job_elapsed_ms"), int) or item["job_elapsed_ms"] <= 0:
        return False
    if not isinstance(item.get("job_start_epoch_ms"), int) or item["job_start_epoch_ms"] <= 0:
        return False
    if test:
        return (
            isinstance(item.get("test_started_epoch_ms"), int)
            and item["test_started_epoch_ms"] >= item["job_start_epoch_ms"]
            and item["test_started_epoch_ms"] <= item["job_start_epoch_ms"] + item["job_elapsed_ms"]
            and isinstance(item.get("result_sha256"), str)
            and len(item["result_sha256"]) == 64
            and isinstance(item.get("result_count"), int)
            and item["result_count"] > 0
        )
    return True


def summarize(
    plan: dict,
    config: dict,
    metrics: list[dict],
    hosted_jobs: dict[tuple[int, str, str], dict[str, int]],
) -> dict:
    if plan.get("contract") != "honua.server-test-reuse-plan/v1":
        raise ValueError("benchmark plan contract is invalid")
    thresholds = config.get("decision_thresholds", {})
    max_wall_regression = thresholds.get("max_wall_clock_regression_percent")
    if not isinstance(max_wall_regression, (int, float)) or not 0 <= max_wall_regression <= 20:
        raise ValueError("wall-clock threshold is invalid")

    metrics = bind_hosted_intervals(metrics, hosted_jobs)
    attempt_one = [item for item in metrics if item.get("run_attempt") == 1]
    baseline_by_id = {
        item["identity"]: item for item in attempt_one if item.get("mode") == "baseline"
    }
    consumer_by_id = {
        item["identity"]: item for item in attempt_one if item.get("mode") == "consumer-artifact"
    }
    producer_by_suffix = {
        item["artifact_suffix"]: item for item in attempt_one if item.get("mode") == "producer"
    }
    shards = {item["identity"]: item for item in plan["baseline"]}
    profile_results = []

    for profile in plan["profiles"]:
        names = profile["shards"]
        selected = [shards[name] for name in names]
        project_counts = Counter(item["project"] for item in selected)
        reused_projects = {project for project, count in project_counts.items() if count >= 2}
        baseline_jobs = [baseline_by_id.get(name) for name in names]
        reused_names = [item["identity"] for item in selected if item["project"] in reused_projects]
        unique_names = [item["identity"] for item in selected if item["project"] not in reused_projects]
        producer_suffixes = sorted(
            {item["project_suffix"] for item in selected if item["project"] in reused_projects}
        )
        producer_jobs = [producer_by_suffix.get(suffix) for suffix in producer_suffixes]
        consumer_jobs = [consumer_by_id.get(name) for name in reused_names]
        unique_jobs = [baseline_by_id.get(name) for name in unique_names]
        complete = (
            all(valid_metric(item, test=True) for item in baseline_jobs)
            and all(valid_metric(item, test=False) for item in producer_jobs)
            and all(valid_metric(item, test=True) for item in consumer_jobs)
        )
        row: dict = {
            "profile": profile["name"],
            "complete": complete,
            "reused_project_count": len(reused_projects),
            "reused_shard_count": len(reused_names),
        }
        if not complete:
            profile_results.append(row)
            continue

        baseline_ready = [item for item in baseline_jobs if item]
        producer_ready = [item for item in producer_jobs if item]
        consumer_ready = [item for item in consumer_jobs if item]
        unique_ready = [item for item in unique_jobs if item]
        hybrid_jobs = producer_ready + consumer_ready + unique_ready
        hybrid_test_jobs = consumer_ready + unique_ready
        parity_failures = []
        for name in reused_names:
            baseline = baseline_by_id[name]
            consumer = consumer_by_id[name]
            if (
                baseline["filter_sha256"] != consumer["filter_sha256"]
                or baseline["result_sha256"] != consumer["result_sha256"]
                or baseline["result_count"] != consumer["result_count"]
                or baseline.get("result_outcomes") != consumer.get("result_outcomes")
            ):
                parity_failures.append(name)

        baseline_timing = path_timing(baseline_ready, baseline_ready)
        hybrid_timing = path_timing(hybrid_jobs, hybrid_test_jobs)
        baseline_raw = sum(item["job_elapsed_ms"] for item in baseline_ready)
        hybrid_raw = sum(item["job_elapsed_ms"] for item in hybrid_jobs)
        baseline_billed = rounded_minutes(baseline_ready)
        hybrid_billed = rounded_minutes(hybrid_jobs)
        wall_limit = baseline_timing["wall_clock_ms"] * (1 + max_wall_regression / 100)
        if reused_projects:
            billed_ok = hybrid_billed < baseline_billed
            p90_ok = hybrid_timing["p90_test_start_ms"] < baseline_timing["p90_test_start_ms"]
        else:
            billed_ok = hybrid_billed == baseline_billed
            p90_ok = hybrid_timing["p90_test_start_ms"] == baseline_timing["p90_test_start_ms"]
        eligible = not parity_failures and billed_ok and p90_ok and hybrid_timing["wall_clock_ms"] <= wall_limit
        row.update(
            {
                "baseline": {
                    "raw_runner_ms": baseline_raw,
                    "rounded_runner_minutes": baseline_billed,
                    **baseline_timing,
                },
                "hybrid": {
                    "raw_runner_ms": hybrid_raw,
                    "rounded_runner_minutes": hybrid_billed,
                    **hybrid_timing,
                },
                "parity_failures": parity_failures,
                "rounded_runner_minutes_ok": billed_ok,
                "p90_test_start_ok": p90_ok,
                "wall_clock_ok": hybrid_timing["wall_clock_ms"] <= wall_limit,
                "eligible": eligible,
            }
        )
        profile_results.append(row)

    reusable = [row for row in profile_results if row["reused_project_count"] > 0]
    eligible = bool(reusable) and all(row.get("eligible") for row in profile_results)
    rerun_consumers = [
        item for item in metrics if item.get("mode") == "consumer-artifact" and item.get("run_attempt", 1) > 1
    ]
    rerun_evidence = []
    for item in rerun_consumers:
        first = consumer_by_id.get(item["identity"])
        rerun_evidence.append(
            {
                "identity": item["identity"],
                "attempt": item["run_attempt"],
                "build_ms": item.get("build_ms"),
                "same_result": bool(first and first.get("result_sha256") == item.get("result_sha256")),
            }
        )
    return {
        "contract": SUMMARY_CONTRACT,
        "decision": "eligible-for-20-head-shadow" if eligible else "keep-shard-local-authoritative",
        "mode": plan["mode"],
        "profiles": profile_results,
        "rerun_evidence": rerun_evidence,
        "threshold": {
            "max_wall_clock_regression_percent": max_wall_regression,
            "requires": ["result parity", "lower p90 test start", "lower rounded runner minutes"],
        },
    }


def markdown(summary: dict) -> str:
    lines = [
        "# Repeated-project server-test reuse benchmark",
        "",
        f"Decision: **{summary['decision']}**",
        "",
        "| Profile | Reused projects/shards | Baseline billed min | Hybrid billed min | Baseline p90 start ms | Hybrid p90 start ms | Wall regression safe | Parity | Eligible |",
        "|---|---:|---:|---:|---:|---:|---|---|---|",
    ]
    for row in summary["profiles"]:
        if not row["complete"]:
            lines.append(
                f"| {row['profile']} | {row['reused_project_count']}/{row['reused_shard_count']} | incomplete | incomplete | incomplete | incomplete | no | no | no |"
            )
            continue
        lines.append(
            f"| {row['profile']} | {row['reused_project_count']}/{row['reused_shard_count']} | "
            f"{row['baseline']['rounded_runner_minutes']} | {row['hybrid']['rounded_runner_minutes']} | "
            f"{row['baseline']['p90_test_start_ms']} | {row['hybrid']['p90_test_start_ms']} | "
            f"{'yes' if row['wall_clock_ok'] else 'no'} | "
            f"{'yes' if not row['parity_failures'] else 'no'} | {'yes' if row['eligible'] else 'no'} |"
        )
    if summary["rerun_evidence"]:
        lines.extend(["", "Failed-only rerun evidence:", ""])
        lines.extend(
            f"- `{item['identity']}` attempt {item['attempt']}: build_ms={item['build_ms']}, same_result={item['same_result']}"
            for item in summary["rerun_evidence"]
        )
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--metrics", type=Path, required=True)
    parser.add_argument("--plan", type=Path, required=True)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--hosted-jobs", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--markdown", type=Path, required=True)
    args = parser.parse_args()
    result = summarize(
        json.loads(args.plan.read_text(encoding="utf-8")),
        json.loads(args.config.read_text(encoding="utf-8")),
        load_metrics(args.metrics),
        load_hosted_jobs(args.hosted_jobs),
    )
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    rendered = markdown(result)
    args.markdown.write_text(rendered, encoding="utf-8")
    print(rendered)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
