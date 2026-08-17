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


def trx(results: list[tuple[str, str]] | list[tuple[str, str, str]], message: str = "boom") -> str:
    rows = ""
    for row in results:
        if len(row) == 3:
            name, outcome, test_id = row
        else:
            name, outcome = row
            test_id = f"id-{name}"
        rows += (
            f'<UnitTestResult testId="{test_id}" testName="{name}" outcome="{outcome}">'
            + (
                f"<Output><ErrorInfo><Message>{message}</Message></ErrorInfo></Output>"
                if outcome == "Failed"
                else ""
            )
            + "</UnitTestResult>"
        )
    return f'<TestRun xmlns="urn:test"><Results>{rows}</Results></TestRun>'


def write(root: Path, shard: str, iteration: int, results, message: str = "boom") -> None:
    path = root / f"flaky-detection-{shard}" / f"flake-{shard}__iter{iteration}.trx"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(trx(results, message), encoding="utf-8")


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

    def test_theory_rows_sharing_a_display_name_are_not_merged(self) -> None:
        # Two theory rows, same display name, distinct testId: row-1 always
        # passes and row-2 always fails. Keying on testName alone would report
        # one "flaky" test; keying on testId reports none.
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            rows = [("Theory", "Passed", "row-1"), ("Theory", "Failed", "row-2")]
            write(root, "core", 1, rows)
            write(root, "core", 2, rows)
            report = MODULE.summarize(root)
            self.assertEqual(report["flaky_count"], 0)
            self.assertEqual(report["tests_seen"], 2)

    def test_duplicate_rows_within_one_iteration_collapse_to_one_outcome(self) -> None:
        # Same testId twice in one iteration (a retry row, or a TRX that lists
        # the case more than once) must be ONE trial, not a pass+fail pair.
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            write(root, "core", 1, [("A", "Passed", "id-A"), ("A", "Failed", "id-A")])
            write(root, "core", 2, [("A", "Failed", "id-A")])
            report = MODULE.summarize(root)
            self.assertEqual(report["flaky_count"], 0)

    def test_truncated_trx_is_recorded_and_does_not_abort_other_shards(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            write(root, "core", 1, [("A", "Passed")])
            write(root, "core", 2, [("A", "Failed")])
            broken = root / "flaky-detection-odata" / "flake-odata__iter1.trx"
            broken.parent.mkdir(parents=True, exist_ok=True)
            broken.write_text("", encoding="utf-8")
            report = MODULE.summarize(root)
            self.assertEqual(report["unparseable_count"], 1)
            self.assertEqual(report["unparseable"][0]["shard"], "odata")
            self.assertEqual(report["flaky_count"], 1)

    def test_missing_selection_is_a_coverage_problem(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            write(root, "core", 1, [("A", "Passed")])
            write(root, "core", 2, [("A", "Passed")])
            report = MODULE.summarize(root)
            problems = MODULE.coverage_problems(report, expect_shards=2, expect_iterations=2)
            self.assertEqual(len(problems), 1)
            self.assertIn("found 1", problems[0])

    def test_missing_iteration_is_a_coverage_problem(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            write(root, "core", 1, [("A", "Passed")])
            report = MODULE.summarize(root)
            problems = MODULE.coverage_problems(report, expect_shards=1, expect_iterations=2)
            self.assertEqual(len(problems), 1)
            self.assertIn("parsed 1 of 2", problems[0])

    def test_complete_evidence_has_no_coverage_problem(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            write(root, "core", 1, [("A", "Passed")])
            write(root, "core", 2, [("A", "Passed")])
            report = MODULE.summarize(root)
            self.assertEqual(MODULE.coverage_problems(report, 1, 2), [])

    def test_zero_evidence_is_never_a_clean_report(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            report = MODULE.summarize(Path(temp))
            self.assertEqual(report["contract"], "honua.flaky-detection/v1")
            self.assertEqual(report["shards_observed"], 0)
            problems = MODULE.coverage_problems(report, expect_shards=6, expect_iterations=2)
            self.assertTrue(problems)
            self.assertIn("Incomplete evidence", MODULE.render_markdown(report, problems))


if __name__ == "__main__":
    unittest.main()
