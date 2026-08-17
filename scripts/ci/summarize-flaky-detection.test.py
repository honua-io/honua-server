#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("summarize-flaky-detection.py")
SPEC = importlib.util.spec_from_file_location("flaky_summary", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def trx(results: list[tuple[str, str]], message: str = "boom") -> str:
    rows = "".join(
        f'<UnitTestResult testName="{name}" outcome="{outcome}">'
        + (f"<Output><ErrorInfo><Message>{message}</Message></ErrorInfo></Output>"
           if outcome == "Failed" else "")
        + "</UnitTestResult>"
        for name, outcome in results
    )
    return f'<TestRun xmlns="urn:test"><Results>{rows}</Results></TestRun>'


def write(root: Path, shard: str, iteration: int, results: list[tuple[str, str]]) -> None:
    path = root / f"flaky-detection-{shard}" / f"flake-{shard}__iter{iteration}.trx"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(trx(results), encoding="utf-8")


class FlakyDetectionTests(unittest.TestCase):
    def test_inconsistent_test_in_one_shard_is_a_candidate(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            write(root, "core", 1, [("A", "Passed"), ("B", "Passed")])
            write(root, "core", 2, [("A", "Failed"), ("B", "Passed")])
            report = MODULE.summarize(root)
            self.assertEqual(report["flaky_count"], 1)
            candidate = report["flaky_candidates"][0]
            self.assertEqual(candidate["name"], "A")
            self.assertEqual(candidate["shard"], "core")
            self.assertEqual(candidate["sample_message"], "boom")

    def test_consistently_failing_test_is_not_a_candidate(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            write(root, "core", 1, [("A", "Failed")])
            write(root, "core", 2, [("A", "Failed")])
            report = MODULE.summarize(root)
            self.assertEqual(report["flaky_count"], 0)
            self.assertEqual(report["shards_observed"], 1)

    def test_shards_are_scored_independently(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            write(root, "core", 1, [("A", "Passed")])
            write(root, "core", 2, [("A", "Passed")])
            write(root, "odata", 1, [("A", "Passed")])
            write(root, "odata", 2, [("A", "Failed")])
            report = MODULE.summarize(root)
            self.assertEqual(report["shards_observed"], 2)
            self.assertEqual(report["flaky_count"], 1)
            self.assertEqual(report["flaky_candidates"][0]["shard"], "odata")

    def test_test_missing_from_an_iteration_is_ignored(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            write(root, "core", 1, [("A", "Passed"), ("B", "Failed")])
            write(root, "core", 2, [("A", "Failed")])
            report = MODULE.summarize(root)
            self.assertEqual([item["name"] for item in report["flaky_candidates"]], ["A"])

    def test_empty_results_produce_an_empty_report(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            report = MODULE.summarize(Path(temp))
            self.assertEqual(report["contract"], "honua.flaky-detection/v1")
            self.assertEqual(report["shards_observed"], 0)
            self.assertEqual(report["flaky_count"], 0)
            self.assertIn("No flake candidates", MODULE.render_markdown(report))


if __name__ == "__main__":
    unittest.main()
