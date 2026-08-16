#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
import json
import tempfile
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
    workload = [
        {
            "identity": "server",
            "project": "server",
            "reuse_expected": True,
            "selected_shard_count": 3,
        }
    ]
    plan = {
        "contract": MODULE.PLAN_CONTRACT,
        "profile": "exact-head-shadow:multi-shard",
        "baseline": [dict(item) for item in workload],
        "candidates": [dict(item) for item in workload],
        "reused_projects": ["server"],
    }
    config = {"decision_thresholds": {"max_wall_clock_regression_percent": 5}}
    metrics = [
        metric("baseline", "server", 10_000, 300_000),
        metric("consumer-ready", "server", 400_000, 60_000),
    ]
    benchmark_jobs = [
        job("Independent prebuild baseline / server", 10_000, 300_000),
        job("Opportunistic prebuild candidate / server", 400_000, 60_000),
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
assert result["baseline"]["rounded_runner_minutes"] == 15
assert result["candidate"]["rounded_runner_minutes_including_prebuild"] == 7
assert result["workload"] == {
    "representative_project_count": 1,
    "selected_shard_count": 3,
    "shard_weights": [{"identity": "server", "selected_shard_count": 3}],
}

values = inputs()
values[2][-1]["result_sha256"] = "c" * 64
assert summarize(values)["decision"] == "keep-local-build-authoritative"

values = inputs()
values[2][-1]["prebuild"]["mode"] = "local-fallback"
assert summarize(values)["reuse_failures"] == ["server"]

values = inputs()
values[5][1]["elapsed_ms"] = 600_001
values[5][1]["end_ms"] = values[5][1]["start_ms"] + 600_001
assert not summarize(values)["rounded_runner_minutes_ok"]

for interval in (0, 1):
    values = inputs()
    values[4][interval]["conclusion"] = "failure"
    try:
        summarize(values)
        raise AssertionError("failed benchmark interval was accepted")
    except ValueError as error:
        assert "hosted interval was not successful" in str(error)

for interval in (0, 1):
    values = inputs()
    values[5][interval]["conclusion"] = "failure"
    try:
        summarize(values)
        raise AssertionError("failed producer interval was accepted")
    except ValueError as error:
        assert "producer hosted interval was not successful" in str(error)

values = inputs()
values[5].append(job("Prebuild repeated project / duplicate", 2_000, 30_000))
try:
    summarize(values)
    raise AssertionError("duplicate producer interval was accepted")
except ValueError as error:
    assert "interval set is incomplete or duplicated" in str(error)

values = inputs()
values[3].append(
    {
        "contract": MODULE.PRODUCER_METRIC_CONTRACT,
        "project": "unused-project",
        "head_sha": HEAD,
        "run_attempt": 1,
    }
)
values[5].append(job("Prebuild repeated project / unused", 2_000, 60_000))
extra_result = summarize(values)
assert extra_result["decision"] == "eligible-for-20-head-shadow"
assert extra_result["candidate"]["rounded_runner_minutes_including_prebuild"] == 8

# The single hosted interval for each project represents every selected shard job.
# Weighting must affect both billed minutes and percentile samples without spawning
# duplicate benchmark jobs.
values = inputs()
values[0]["baseline"][0]["selected_shard_count"] = 18
values[0]["candidates"][0]["selected_shard_count"] = 18
second = {
    "identity": "geoservices",
    "project": "geoservices",
    "reuse_expected": True,
    "selected_shard_count": 2,
}
values[0]["baseline"].append(dict(second))
values[0]["candidates"].append(dict(second))
values[0]["reused_projects"].append("geoservices")
values[2][0]["test_started_epoch_ms"] = 20_000
values[2][1]["test_started_epoch_ms"] = 410_000
values[2].extend(
    [
        {**metric("baseline", "geoservices", 10_000, 60_000), "test_started_epoch_ms": 210_000},
        {
            **metric("consumer-ready", "geoservices", 400_000, 60_000),
            "test_started_epoch_ms": 420_000,
        },
    ]
)
values[4].extend(
    [
        job("Independent prebuild baseline / geoservices", 10_000, 60_000),
        job("Opportunistic prebuild candidate / geoservices", 400_000, 60_000),
    ]
)
values[3].append(
    {
        "contract": MODULE.PRODUCER_METRIC_CONTRACT,
        "project": "geoservices",
        "head_sha": HEAD,
        "run_attempt": 1,
    }
)
values[5].append(job("Prebuild repeated project / geoservices", 1_000, 60_000))
weighted_result = summarize(values)
assert weighted_result["baseline"]["rounded_runner_minutes"] == 92
assert weighted_result["candidate"]["rounded_runner_minutes_including_prebuild"] == 25
assert weighted_result["baseline"]["p90_test_start_ms"] == 10_000
assert weighted_result["candidate"]["p90_test_start_ms"] == 10_000

for invalid_weight in (None, 1, 101, True):
    values = inputs()
    values[0]["baseline"][0]["selected_shard_count"] = invalid_weight
    values[0]["candidates"][0]["selected_shard_count"] = invalid_weight
    try:
        summarize(values)
        raise AssertionError("invalid selected shard weight was accepted")
    except ValueError as error:
        assert "shard weight" in str(error)

values = inputs()
values[0]["candidates"][0]["selected_shard_count"] = 2
try:
    summarize(values)
    raise AssertionError("candidate workload drift was accepted")
except ValueError as error:
    assert "differs from baseline" in str(error)

with tempfile.TemporaryDirectory() as directory:
    payload = {
        "jobs": [
            {
                "name": "Prebuild A/B evidence summary",
                "run_attempt": 1,
                "started_at": "2026-08-14T00:00:00Z",
                "completed_at": None,
                "conclusion": None,
            },
            {
                "name": "Independent prebuild baseline / server",
                "run_attempt": 1,
                "started_at": "2026-08-14T00:00:00Z",
                "completed_at": "2026-08-14T00:01:00Z",
                "conclusion": "success",
            },
        ]
    }
    jobs_file = Path(directory) / "jobs.json"
    jobs_file.write_text(json.dumps(payload), encoding="utf-8")
    loaded = MODULE.load_hosted_jobs(Path(directory))
    assert [item["name"] for item in loaded] == ["Independent prebuild baseline / server"]

print("server-test-prebuild-benchmark-summary=ok")
