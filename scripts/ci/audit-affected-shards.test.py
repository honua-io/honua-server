#!/usr/bin/env python3
"""Offline failure injections for the affected-shard promotion measurement."""

import importlib.util
import json
from collections import Counter
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path


spec = importlib.util.spec_from_file_location("audit", Path(__file__).with_name("audit-affected-shards.py"))
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


def run(identifier, messages=None):
    return {
        "run_id": identifier, "run_attempt": 2, "head_sha": f"{identifier:040x}",
        "created_at": "2026-10-06T12:00:00Z", "status": "completed",
        "aggregate_conclusion": "success",
        "annotations": [{"message": message} for message in (
            messages if messages is not None else ["HONUA_AFFECTED_SHARD_GREEN 6 selected shard families passed."]
        )],
    }


def window(runs):
    return {"contract": "honua.affected-shards-observations/v1",
            "from": "2026-09-30T16:45:00Z", "to": "2026-10-07T16:45:00Z", "runs": runs}


def decision(identifier, shard="STAC", verdict="confirmed-red"):
    return {"run_id": identifier, "run_attempt": 2, "head_sha": f"{identifier:040x}",
            "shard": shard, "verdict": verdict, "merge_sha": "a" * 40,
            "trailing_job_url": "https://github.com/honua-io/honua-server/actions/runs/123/job/456",
            "reason": "Reconciled the same shard on the merge commit."}


def red(shard="STAC"):
    return f"HONUA_AFFECTED_SHARD_RED shard='{shard}' status='failed'"


class AuditTests(unittest.TestCase):
    def test_checked_in_reconciliation_counts_match_all_verdicts(self):
        path = Path(__file__).resolve().parents[2] / "docs/ci/affected-shards-reconciliation-20261007.json"
        reconciliation = json.loads(path.read_text())
        self.assertEqual(reconciliation["counts"],
                         dict(Counter(row["verdict"] for row in reconciliation["rows"])))

    def test_floor_is_executed_samples_not_successful_shells(self):
        runs = [run(index) for index in range(1, 60)]
        runs += [run(70, []), run(71, ["HONUA_AFFECTED_SHARD_UNAVAILABLE no timing receipt"])]
        pending = run(72)
        pending["status"] = "in_progress"
        runs.append(pending)
        result = audit.summarize(window(runs), [])
        self.assertEqual(result["sample_shortfall"], 1)
        self.assertEqual(result["counts"]["skipped_runs"], 1)
        self.assertEqual(result["counts"]["unavailable_runs"], 1)
        self.assertEqual(result["counts"]["pending_runs"], 1)
        self.assertFalse(result["promotion_ready"])
        runs.append(run(60))
        self.assertFalse(audit.summarize(window(runs), [])["promotion_ready"])
        pending["status"] = "completed"
        self.assertTrue(audit.summarize(window(runs), [])["promotion_ready"])

    def test_pending_window_runs_block_promotion_after_sample_floor(self):
        greens = [run(index) for index in range(1, 61)]
        for status in ("queued", "in_progress"):
            with self.subTest(status=status):
                unfinished = [{**run(index, []), "status": status,
                               "aggregate_conclusion": None} for index in (61, 62)]
                runs = greens + unfinished
                result = audit.summarize(window(runs), [])
                self.assertEqual(result["counts"]["sample_runs"], 60)
                self.assertEqual(result["counts"]["pending_runs"], 2)
                self.assertTrue(result["gates"]["sample_floor"])
                self.assertFalse(result["gates"]["all_runs_completed"])
                self.assertFalse(result["promotion_ready"])
                self.assertTrue(audit.summarize(window(greens + [run(61), run(62)]), [])[
                    "promotion_ready"])
                result = audit.summarize(window(greens + [run(61, [red()]), run(62, [red()])]),
                                         [decision(index, verdict="false-red") for index in (61, 62)])
                self.assertTrue(result["gates"]["all_runs_completed"])
                self.assertGreater(result["false_red_rate"], .02)
                self.assertFalse(result["promotion_ready"])

    def test_report_only_success_does_not_adjudicate_a_red(self):
        runs = [run(index) for index in range(1, 61)]
        runs[0] = run(1, [red()])
        result = audit.summarize(window(runs), [])
        self.assertIsNone(result["false_red_rate"])
        self.assertEqual(result["false_red_rate_lower_bound"], 0)
        self.assertEqual(result["counts"]["unresolved_red_shards"], 1)
        self.assertFalse(result["promotion_ready"])
        self.assertTrue(audit.summarize(window(runs), [decision(1)])["promotion_ready"])

    def test_strict_two_percent_bar_and_run_denominator(self):
        runs = [run(index) for index in range(1, 101)]
        runs[0] = run(1, [red(), red("ImageServer")])
        runs[1] = run(2, [red()])
        decisions = [decision(1, verdict="false-red"), decision(1, "ImageServer", "false-red"),
                     decision(2, verdict="false-red")]
        result = audit.summarize(window(runs), decisions)
        self.assertEqual(result["counts"]["false_red_runs"], 2)
        self.assertEqual(result["false_red_rate"], .02)
        self.assertFalse(result["promotion_ready"])
        runs.append(run(101))
        self.assertTrue(audit.summarize(window(runs), decisions)["promotion_ready"])

    def test_rejects_stale_or_unsubstantiated_adjudications(self):
        for change in ({"run_attempt": 1}, {"head_sha": "b" * 40}, {"shard": "Other"},
                       {"merge_sha": "bad"}, {"trailing_job_url": "https://example.com"}, {"reason": ""}):
            entry = {**decision(1), **change}
            with self.subTest(change=change), self.assertRaises(ValueError):
                audit.summarize(window([run(1, [red()])]), [entry])

    def test_rejects_truncated_windows_duplicates_and_conflicting_verdicts(self):
        cases = [window([run(1), run(1)]), window([run(1, [red(), "HONUA_AFFECTED_SHARD_GREEN 1 passed."])]),
                 window([run(1, ["HONUA_AFFECTED_SHARD_RED malformed"])])]
        short = window([run(1)])
        short["from"] = "2026-10-01T16:45:00Z"
        cases.append(short)
        outside = window([run(1)])
        outside["runs"][0]["created_at"] = outside["to"]
        cases.append(outside)
        for value in cases:
            with self.subTest(value=value), self.assertRaises(ValueError):
                audit.summarize(value, [])

    def test_collection_passes_twenty_runs_and_pages_all_annotations(self):
        calls = []
        runs = [{**run(index), "id": index, "html_url": f"https://github.com/honua-io/honua-server/actions/runs/{index}"}
                for index in range(1, 106)]

        def api(endpoint, parameters):
            calls.append((endpoint, parameters))
            page = parameters.get("page", 1)
            if endpoint.endswith("/runs"):
                return {"total_count": len(runs), "workflow_runs": runs[(page - 1) * 100:page * 100]}
            if endpoint.endswith("/jobs"):
                self.assertIn("/attempts/2/jobs", endpoint)
                return {"total_count": 1, "jobs": [{"name": audit.CONTEXT, "conclusion": "success",
                         "check_run_url": "https://api.github.com/repos/honua-io/honua-server/check-runs/123"}]}
            if endpoint.endswith("/annotations"):
                annotations = [{"message": "unrelated runner notice"}] * 100 + [{"message": "HONUA_AFFECTED_SHARD_GREEN 1 passed."}]
                return annotations[(page - 1) * 100:page * 100]
            return {"output": {"annotations_count": 101}}

        with tempfile.TemporaryDirectory() as directory:
            result = audit.collect(datetime(2026, 10, 7, 16, 45, tzinfo=timezone.utc), Path(directory), api)
            self.assertTrue((Path(directory) / "observations.json").exists())
            calls.clear()
            audit.collect(datetime(2026, 10, 7, 16, 45, tzinfo=timezone.utc), Path(directory), api)
            # Completed, identical attempts resume without refetching jobs.
            self.assertEqual(len(calls), 2)
            calls.clear()
            runs[0]["run_attempt"] = 3
            with self.assertRaises(AssertionError):
                audit.collect(datetime(2026, 10, 7, 16, 45, tzinfo=timezone.utc), Path(directory), api)
            self.assertFalse((Path(directory) / "observations.json").exists())
        self.assertEqual(len(result["runs"]), 105)
        self.assertEqual(audit.summarize(result, [])["counts"]["sample_runs"], 105)

    def test_incomplete_catalog_is_not_published_as_complete(self):
        def api(endpoint, parameters):
            if endpoint.endswith("/runs"):
                return {"total_count": 1, "workflow_runs": [{**run(1), "id": 1, "html_url": "https://github.com/honua-io/honua-server/actions/runs/1"}]}
            return {"total_count": 2, "jobs": []}
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ValueError):
                audit.collect(datetime(2026, 10, 7, 16, 45, tzinfo=timezone.utc), Path(directory), api)
            self.assertFalse((Path(directory) / "observations.json").exists())


if __name__ == "__main__":
    unittest.main()
