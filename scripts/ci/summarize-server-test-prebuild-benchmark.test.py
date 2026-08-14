#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
from pathlib import Path

SCRIPT = Path(__file__).with_name("summarize-server-test-prebuild-benchmark.py")
SPEC = importlib.util.spec_from_file_location("prebuild_summary", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

HEAD = "a" * 40


def metric(mode: str, identity: str, start: int, elapsed: int, result: str = "b" * 64) -> dict:
    value = {
        "contract": MODULE.METRIC_CONTRACT,
        "mode": mode,
        "identity": identity,
        "run_attempt": 1,
        "test_started_epoch_ms": start + (240_000 if mode == "baseline" else 30_000),
        "filter_sha256": "f" * 64,
        "result_sha256": result,
        "result_count": 1,
        "result_outcomes": {"Passed": 1},
    }
    if mode == "consumer-ready":
        value["prebuild"] = {
            "contract": "honua.server-test-prebuild-consumer/v1",
            "mode": "prebuild",
            "reason": "accepted",
        }
    return value


def job(name: str, start: int, elapsed: int, attempt: int = 1) -> dict:
    return {
        "name": name,
        "attempt": attempt,
        "conclusion": "success",
        "start_ms": start,
        "end_ms": start + elapsed,
        "elapsed_ms": elapsed,
    }


def inputs() -> tuple:
    plan = {
        "contract": MODULE.PLAN_CONTRACT,
        "profile": "two-same-project",
        "baseline": [
            {"identity": "server-a", "project": "server", "reuse_expected": True},
            {"identity": "server-b", "project": "server", "reuse_expected": True},
        ],
        "reused_projects": ["server"],
    }
    config = {"decision_thresholds": {"max_wall_clock_regression_percent": 5}}
    metrics = [
        metric("baseline", "server-a", 10_000, 300_000),
        metric("baseline", "server-b", 10_000, 300_000),
        metric("consumer-ready", "server-a", 400_000, 60_000),
        metric("consumer-ready", "server-b", 400_000, 60_000),
    ]
    benchmark_jobs = [
        job("Independent prebuild baseline / server-a", 10_000, 300_000),
        job("Independent prebuild baseline / server-b", 10_000, 300_000),
        job("Opportunistic prebuild candidate / server-a", 400_000, 60_000),
        job("Opportunistic prebuild candidate / server-b", 400_000, 60_000),
    ]
    producer_metrics = [
        {
            "contract": MODULE.PRODUCER_METRIC_CONTRACT,
            "project": "server",
            "head_sha": HEAD,
            "run_attempt": 1,
        }
    ]
    producer_jobs = [
        job("Plan bounded exact-head prebuild", 1_000, 20_000),
        job("Prebuild repeated project / server", 1_000, 180_000),
    ]
    return plan, config, metrics, producer_metrics, benchmark_jobs, producer_jobs


def summarize(values: tuple) -> dict:
    return MODULE.summarize(
        *values,
        benchmark_attempt=1,
        producer_attempt=1,
        head_sha=HEAD,
    )


values = inputs()
result = summarize(values)
assert result["decision"] == "eligible-for-20-head-shadow"
assert result["baseline"]["rounded_runner_minutes"] == 10
assert result["candidate"]["rounded_runner_minutes_including_prebuild"] == 6

values = inputs()
values[2][-1]["result_sha256"] = "c" * 64
assert summarize(values)["decision"] == "keep-local-build-authoritative"

values = inputs()
values[2][-1]["prebuild"]["mode"] = "local-fallback"
assert summarize(values)["reuse_failures"] == ["server-b"]

values = inputs()
values[5][1]["elapsed_ms"] = 600_001
values[5][1]["end_ms"] = values[5][1]["start_ms"] + 600_001
assert not summarize(values)["rounded_runner_minutes_ok"]

print("server-test-prebuild-benchmark-summary=ok")
