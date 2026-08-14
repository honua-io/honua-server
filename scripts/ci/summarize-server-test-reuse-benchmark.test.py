#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("summarize-server-test-reuse-benchmark.py")
SPEC = importlib.util.spec_from_file_location("reuse_summary", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def metric(mode: str, identity: str, project: str, start: int, test_start: int, elapsed: int, result: str = "a" * 64) -> dict:
    return {
        "contract": MODULE.METRIC_CONTRACT,
        "mode": mode,
        "identity": identity,
        "project": project,
        "artifact_suffix": project,
        "run_attempt": 1,
        "job_start_epoch_ms": start,
        "test_started_epoch_ms": test_start,
        "job_elapsed_ms": elapsed,
        "filter_sha256": "f" * 64,
        "result_sha256": result if mode != "producer" else "",
        "result_count": 1 if mode != "producer" else 0,
        "result_outcomes": {"Passed": 1} if mode != "producer" else {},
        "build_ms": 0 if mode == "consumer-artifact" else 100,
    }


class SummaryTests(unittest.TestCase):
    def config(self) -> dict:
        return {"decision_thresholds": {"max_wall_clock_regression_percent": 5}}

    def plan(self) -> dict:
        return {
            "contract": "honua.server-test-reuse-plan/v1",
            "mode": "core",
            "baseline": [
                {"identity": "a", "project": "server", "project_suffix": "server"},
                {"identity": "b", "project": "server", "project_suffix": "server"},
                {"identity": "unique", "project": "unique", "project_suffix": "unique"},
            ],
            "profiles": [
                {"name": "shared", "shards": ["a", "b"]},
                {"name": "mixed", "shards": ["a", "unique"]},
            ],
        }

    def green_metrics(self) -> list[dict]:
        return [
            metric("baseline", "a", "server", 1_000, 241_000, 300_000),
            metric("baseline", "b", "server", 1_000, 251_000, 300_000),
            metric("baseline", "unique", "unique", 1_000, 201_000, 240_000),
            metric("producer", "server", "server", 1_000, 0, 120_000),
            metric("consumer-artifact", "a", "server", 1_000, 101_000, 140_000),
            metric("consumer-artifact", "b", "server", 1_000, 111_000, 140_000),
        ]

    def hosted_jobs(self, metrics: list[dict] | None = None) -> dict:
        source = metrics or self.green_metrics()
        return {
            (item["run_attempt"], item["mode"], item["identity"]): {
                "job_start_epoch_ms": item["job_start_epoch_ms"],
                "job_elapsed_ms": item["job_elapsed_ms"],
            }
            for item in source
        }

    def test_hybrid_can_pass_without_serializing_unique_projects(self) -> None:
        metrics = self.green_metrics()
        result = MODULE.summarize(self.plan(), self.config(), metrics, self.hosted_jobs(metrics))
        self.assertEqual("eligible-for-20-head-shadow", result["decision"])
        self.assertTrue(result["profiles"][0]["eligible"])
        self.assertTrue(result["profiles"][1]["eligible"])

    def test_parity_mismatch_fails_closed(self) -> None:
        metrics = self.green_metrics()
        metrics[-1]["result_sha256"] = "b" * 64
        result = MODULE.summarize(self.plan(), self.config(), metrics, self.hosted_jobs(metrics))
        self.assertEqual("keep-shard-local-authoritative", result["decision"])
        self.assertEqual(["b"], result["profiles"][0]["parity_failures"])

    def test_billed_or_p90_regression_fails(self) -> None:
        metrics = self.green_metrics()
        for item in metrics:
            if item["mode"] == "consumer-artifact":
                item["test_started_epoch_ms"] += 100_000
                item["job_elapsed_ms"] += 100_000
        result = MODULE.summarize(self.plan(), self.config(), metrics, self.hosted_jobs(metrics))
        self.assertEqual("keep-shard-local-authoritative", result["decision"])

    def test_billing_uses_complete_hosted_intervals(self) -> None:
        metrics = self.green_metrics()
        hosted = self.hosted_jobs(metrics)
        hosted[(1, "producer", "server")]["job_elapsed_ms"] = 120_001
        hosted[(1, "consumer-artifact", "a")]["job_elapsed_ms"] = 180_001
        hosted[(1, "consumer-artifact", "b")]["job_elapsed_ms"] = 180_001
        result = MODULE.summarize(self.plan(), self.config(), metrics, hosted)
        shared = result["profiles"][0]
        self.assertEqual(11, shared["hybrid"]["rounded_runner_minutes"])
        self.assertFalse(shared["rounded_runner_minutes_ok"])


if __name__ == "__main__":
    unittest.main()
