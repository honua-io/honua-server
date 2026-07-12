#!/usr/bin/env python3
"""Summarize hosted transfer evidence against the predeclared dual threshold."""

from __future__ import annotations

import argparse
import json
from pathlib import Path


def load_metrics(root: Path) -> list[dict]:
    metrics: list[dict] = []
    for path in root.rglob("*.json"):
        try:
            value = json.loads(path.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError):
            continue
        if value.get("contract") == "honua.server-test-transfer-benchmark.v1":
            metrics.append(value)
    return metrics


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--metrics", type=Path, required=True)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--markdown", type=Path, required=True)
    args = parser.parse_args()

    config = json.loads(args.config.read_text(encoding="utf-8"))
    metrics = load_metrics(args.metrics)
    by_mode_identity = {(item["mode"], item["identity"]): item for item in metrics if item["run_attempt"] == 1}
    shards = {item["name"]: item for item in config["shards"]}
    results = []

    for profile in config["profiles"]:
        names = profile["shards"]
        unique_suffixes = sorted({shards[name]["artifact_suffix"] for name in names})
        baseline = [by_mode_identity.get(("baseline", name)) for name in names]
        producers = [by_mode_identity.get(("producer", suffix_)) for suffix_ in unique_suffixes]
        artifact = [by_mode_identity.get(("consumer-artifact", name)) for name in names]
        cache = [by_mode_identity.get(("consumer-cache", name)) for name in names]
        complete = all(baseline + producers + artifact + cache)
        row = {"profile": profile["name"], "complete": complete}
        if complete:
            baseline_runner = sum(item["job_elapsed_ms"] for item in baseline)
            baseline_ttf = max(item["job_elapsed_ms"] - item["test_ms"] for item in baseline)
            artifact_producer_runner = sum(item["artifact_job_elapsed_ms"] for item in producers)
            cache_producer_runner = sum(item["cache_job_elapsed_ms"] for item in producers)
            artifact_producer_ttf = max(item["artifact_job_elapsed_ms"] for item in producers)
            cache_producer_ttf = max(item["cache_job_elapsed_ms"] for item in producers)
            artifact_runner = artifact_producer_runner + sum(item["job_elapsed_ms"] for item in artifact)
            cache_runner = cache_producer_runner + sum(item["job_elapsed_ms"] for item in cache)
            artifact_ttf = artifact_producer_ttf + max(item["job_elapsed_ms"] - item["test_ms"] for item in artifact)
            cache_ttf = cache_producer_ttf + max(item["job_elapsed_ms"] - item["test_ms"] for item in cache)
            row.update({
                "baseline_runner_ms": baseline_runner,
                "baseline_time_to_first_test_ms": baseline_ttf,
                "artifact_runner_ms": artifact_runner,
                "artifact_time_to_first_test_ms": artifact_ttf,
                "cache_runner_ms": cache_runner,
                "cache_time_to_first_test_ms": cache_ttf,
                "artifact_eligible": artifact_runner < baseline_runner and artifact_ttf < baseline_ttf,
                "cache_eligible": cache_runner < baseline_runner and cache_ttf < baseline_ttf,
            })
        results.append(row)

    eligible = bool(results) and all(row.get("artifact_eligible") for row in results)
    cache_eligible = bool(results) and all(row.get("cache_eligible") for row in results)
    decision = "shared-artifact" if eligible else "shared-cache" if cache_eligible else "no-shared-producer"
    summary = {
        "contract": "honua.server-test-transfer-benchmark.summary.v1",
        "decision": decision,
        "threshold": "must improve both runner time and initial time-to-first-test in every profile",
        "profiles": results,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(summary, indent=2, sort_keys=True) + "\n", encoding="utf-8")

    lines = [
        "# Server-test transfer benchmark",
        "",
        f"Decision: **{decision}**",
        "",
        "Eligibility requires lower runner time and lower initial time-to-first-test in all profiles.",
        "",
        "| Profile | Baseline runner ms | Artifact runner ms | Cache runner ms | Baseline TTF ms | Artifact TTF ms | Cache TTF ms |",
        "|---|---:|---:|---:|---:|---:|---:|",
    ]
    for row in results:
        if not row["complete"]:
            lines.append(f"| {row['profile']} | incomplete | incomplete | incomplete | incomplete | incomplete | incomplete |")
        else:
            lines.append(
                f"| {row['profile']} | {row['baseline_runner_ms']} | {row['artifact_runner_ms']} | "
                f"{row['cache_runner_ms']} | {row['baseline_time_to_first_test_ms']} | "
                f"{row['artifact_time_to_first_test_ms']} | {row['cache_time_to_first_test_ms']} |"
            )
    args.markdown.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(args.markdown.read_text(encoding="utf-8"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
