#!/usr/bin/env python3
"""Offline tests for full-ci-dispatch.py and its workflow wiring."""

from __future__ import annotations

import datetime as dt
import importlib.util
import sys
import subprocess
import unittest
from pathlib import Path
from unittest import mock
from urllib.parse import parse_qsl

import yaml

SCRIPT = Path(__file__).with_name("full-ci-dispatch.py")
SPEC = importlib.util.spec_from_file_location("full_ci_dispatch", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)

ROOT = Path(__file__).resolve().parents[2]
HEAD = "a" * 40
OLD = "b" * 40


def at(text: str) -> dt.datetime:
    return dt.datetime.fromisoformat(text + "+00:00")


RUN_DEFAULTS = dict(event="schedule", display_title="CI full ", status="completed",
                    conclusion="failure", head_sha=OLD, head_branch="trunk")


def run(run_id: int, created: str, **fields) -> dict:
    return {**RUN_DEFAULTS, "id": run_id, "created_at": created + "Z", **fields}


def decide(moment: str, runs: list[dict]) -> tuple[bool, str]:
    return MODULE.decide(MODULE.Snapshot(at(moment), runs, HEAD))


class DecideTests(unittest.TestCase):
    def test_dispatches_inside_window_when_trunk_head_has_no_full_result(self):
        runs = [run(1, "2026-11-15T01:05:40", conclusion="cancelled")]
        self.assertTrue(decide("2026-11-15T06:10:00", runs)[0])

    def test_never_dispatches_outside_window(self):
        for moment in ("2026-11-15T05:59:59", "2026-11-15T11:00:00", "2026-11-15T20:00:00",
                       "2027-07-15T04:30:00", "2027-07-15T10:00:00"):
            dispatch, reason = decide(moment, [])
            self.assertFalse(dispatch, moment)
            self.assertIn("outside", reason)

    def test_last_minute_of_window_still_dispatches(self):
        self.assertTrue(decide("2026-11-15T10:59:00", [])[0])

    def test_window_follows_pacific_daylight_time(self):
        # PDT: 22:00-03:00 Pacific is 05:00-10:00 UTC.
        self.assertTrue(decide("2027-07-15T05:00:00", [])[0])
        self.assertTrue(decide("2027-07-15T09:59:00", [])[0])
        self.assertFalse(decide("2027-07-15T10:00:00", [])[0])

    def test_night_starts_at_pacific_window_open_across_midnight(self):
        # 08:30 UTC is 00:30 PST; a run created at 06:05 UTC (22:05 PST the day before) counts.
        runs = [run(20, "2026-11-15T06:05:00", event="workflow_dispatch", conclusion="success"),
                run(21, "2026-11-15T07:30:00", event="schedule", conclusion="failure")]
        self.assertFalse(decide("2026-11-15T08:30:00", runs)[0])
        # The previous evening's 05:59 UTC run (21:59 PST) is outside tonight's window.
        runs[0]["created_at"] = "2026-11-15T05:59:00Z"
        self.assertTrue(decide("2026-11-15T08:30:00", runs)[0])

    def test_active_full_run_blocks_dispatch(self):
        for status in ("queued", "in_progress", "waiting", "pending"):
            runs = [run(7, "2026-11-15T03:00:00", status=status, conclusion=None)]
            dispatch, reason = decide("2026-11-15T07:00:00", runs)
            self.assertFalse(dispatch, status)
            self.assertIn(status, reason)

    def test_active_affected_run_does_not_block(self):
        runs = [run(8, "2026-11-15T06:30:00", event="workflow_dispatch", status="in_progress",
                    conclusion=None, display_title="CI affected affected 98c4e5355c31..b54d93edd04d")]
        self.assertTrue(decide("2026-11-15T07:00:00", runs)[0])

    def test_branch_runs_are_ignored(self):
        runs = [run(9, "2026-11-15T06:30:00", status="in_progress", conclusion=None, head_branch="train/batch/x")]
        self.assertTrue(decide("2026-11-15T07:00:00", runs)[0])

    def test_trunk_head_with_full_result_is_not_rerun(self):
        runs = [run(10, "2026-11-15T06:05:00", event="workflow_dispatch", conclusion="success", head_sha=HEAD)]
        dispatch, reason = decide("2026-11-15T09:30:00", runs)
        self.assertFalse(dispatch)
        self.assertIn("already has full result", reason)

    def test_failed_full_result_on_head_is_not_rerun(self):
        runs = [run(10, "2026-11-15T06:05:00", event="workflow_dispatch", conclusion="failure", head_sha=HEAD)]
        self.assertFalse(decide("2026-11-15T09:30:00", runs)[0])

    def test_cancelled_full_run_on_head_does_not_count(self):
        runs = [run(11, "2026-11-15T06:05:00", event="workflow_dispatch", conclusion="cancelled", head_sha=HEAD)]
        self.assertTrue(decide("2026-11-15T09:30:00", runs)[0])

    def test_second_run_of_the_night_when_trunk_moved(self):
        runs = [run(12, "2026-11-15T06:05:00", event="workflow_dispatch", conclusion="success", head_sha=OLD)]
        self.assertTrue(decide("2026-11-15T09:10:00", runs)[0])

    def test_nightly_limit_counts_runs_that_were_not_cancelled(self):
        runs = [run(13, "2026-11-15T06:05:00", event="workflow_dispatch", conclusion="success"),
                run(14, "2026-11-15T09:05:00", event="schedule", conclusion="failure"),
                run(15, "2026-11-15T05:00:00", conclusion="success")]
        dispatch, reason = decide("2026-11-15T10:50:00", runs)
        self.assertFalse(dispatch)
        self.assertIn("2 full runs", reason)
        runs[1]["conclusion"] = "cancelled"
        self.assertTrue(decide("2026-11-15T10:50:00", runs)[0])


class RunHistoryTests(unittest.TestCase):
    """Filtered, fully paged listings, so selective traffic cannot hide a full run."""

    TARGET = MODULE.Target("honua-io/honua-server", "ci.yml", "trunk")
    # Tonight's window opened at 22:00 PST, which is 06:00 UTC.
    SINCE = dt.datetime(2026, 11, 14, 22, tzinfo=MODULE.PACIFIC)

    def fetcher(self, listings: dict[str, list[dict]]):
        requested = []

        def fetch(path: str) -> dict:
            requested.append(path)
            base, _, query = path.partition("?")
            params = dict(parse_qsl(query))
            page, size = int(params.pop("page")), int(params.pop("per_page"))
            self.assertEqual(base, "repos/honua-io/honua-server/actions/workflows/ci.yml/runs")
            self.assertEqual(params.pop("branch"), "trunk")
            (name, value), = params.items()
            rows = listings.get(f"{name}={value}", [])
            return {"total_count": len(rows), "workflow_runs": rows[(page - 1) * size:page * size]}

        return fetch, requested

    def affected(self, first_id: int, count: int, **fields) -> list[dict]:
        return [run(first_id + index, "2026-11-15T07:00:00", event="workflow_dispatch",
                    display_title="CI affected x..y", **fields) for index in range(count)]

    def test_active_full_run_behind_a_page_of_affected_runs_blocks_dispatch(self):
        full = run(1, "2026-11-15T03:00:00", status="in_progress", conclusion=None)
        fetch, _ = self.fetcher({"status=in_progress": self.affected(100, 150, status="in_progress",
                                                                     conclusion=None) + [full]})
        runs = MODULE.RunHistory(fetch, self.TARGET).collect(HEAD, self.SINCE)
        self.assertIn(1, {row["id"] for row in runs})
        dispatch, reason = MODULE.decide(MODULE.Snapshot(at("2026-11-15T08:00:00"), runs, HEAD))
        self.assertFalse(dispatch)
        self.assertIn("full run 1 is in_progress", reason)

    def test_tonight_runs_and_head_results_are_read_across_pages(self):
        tonight = self.affected(200, 120) + [
            run(2, "2026-11-15T06:05:00", event="workflow_dispatch", conclusion="success"),
            run(3, "2026-11-15T07:30:00", conclusion="failure")]
        at_head = self.affected(400, 101, head_sha=HEAD) + [
            run(4, "2026-11-14T06:05:00", conclusion="success", head_sha=HEAD)]
        fetch, requested = self.fetcher({"created=>=2026-11-15T06:00:00Z": tonight,
                                         f"head_sha={HEAD}": at_head})
        ids = {row["id"] for row in MODULE.RunHistory(fetch, self.TARGET).collect(HEAD, self.SINCE)}
        self.assertTrue({2, 3, 4} <= ids)
        for status in MODULE.ACTIVE_STATUSES:
            self.assertTrue(any(f"status={status}&" in path for path in requested), status)

    def test_truncated_listing_refuses_to_decide(self):
        def fetch(path: str) -> dict:
            return {"total_count": 5, "workflow_runs": []}

        with self.assertRaisesRegex(MODULE.DispatchError, "truncated"):
            MODULE.RunHistory(fetch, self.TARGET).collect(HEAD, self.SINCE)


class GetJsonTests(unittest.TestCase):
    """The shared retrying GET in gh_api.py."""

    def results(self, *outcomes: tuple[int, str, str]):
        return [subprocess.CompletedProcess([], code, out, err) for code, out, err in outcomes]

    def get(self, *outcomes: tuple[int, str, str], delays=(1, 2)):
        with mock.patch.object(MODULE.gh_api.subprocess, "run", side_effect=self.results(*outcomes)) as run, \
                mock.patch.object(MODULE.gh_api.time, "sleep") as sleep:
            try:
                return MODULE.gh_api.get_json("repos/x", MODULE.DispatchError, delays), run, sleep
            except MODULE.DispatchError as exc:
                return exc, run, sleep

    def test_retries_transient_failure_then_returns_json(self):
        value, run, sleep = self.get((1, "", "HTTP 502"), (0, '{"sha": "a"}', ""))
        self.assertEqual(value, {"sha": "a"})
        self.assertEqual(run.call_count, 2)
        sleep.assert_called_once_with(1)

    def test_404_and_422_fail_without_retry(self):
        for status in ("HTTP 404", "HTTP 422"):
            value, run, sleep = self.get((1, "", f"gh: Not Found ({status})"))
            self.assertIsInstance(value, MODULE.DispatchError)
            self.assertEqual(run.call_count, 1)
            sleep.assert_not_called()

    def test_persistent_failure_and_non_json_raise(self):
        value, run, _ = self.get((1, "", "HTTP 502"), (1, "", "HTTP 502"), (1, "", "HTTP 502"))
        self.assertIn("failed", str(value))
        self.assertEqual(run.call_count, 3)
        value, _, _ = self.get((0, "not json", ""))
        self.assertIn("non-JSON", str(value))

    def test_no_delays_means_a_single_attempt(self):
        value, run, _ = self.get((1, "", "HTTP 502"), delays=())
        self.assertIsInstance(value, MODULE.DispatchError)
        self.assertEqual(run.call_count, 1)


class WorkflowTests(unittest.TestCase):
    def setUp(self):
        text = (ROOT / ".github/workflows/full-ci-dispatch.yml").read_text()
        self.workflow = yaml.load(text, Loader=yaml.BaseLoader)

    def test_triggers_on_trunk_push_with_hourly_backup_inside_window(self):
        on = self.workflow["on"]
        self.assertEqual(on["push"]["branches"], ["trunk"])
        self.assertEqual(on["schedule"], [{"cron": "41 5-10 * * *"}])
        self.assertIn("workflow_dispatch", on)

    def test_runs_serialized_with_least_privilege(self):
        self.assertEqual(self.workflow["permissions"], {})
        self.assertEqual(self.workflow["concurrency"]["cancel-in-progress"], "false")
        job = self.workflow["jobs"]["dispatch"]
        self.assertEqual(job["permissions"], {"actions": "write", "contents": "read"})
        self.assertIn("vars.HONUA_FULL_CI_DISPATCH != 'off'", job["if"])
        self.assertIn("github.repository == 'honua-io/honua-server'", job["if"])

    def test_dispatches_the_full_run_without_selective_inputs(self):
        steps = self.workflow["jobs"]["dispatch"]["steps"]
        script = next(step["run"] for step in steps if "full-ci-dispatch.py " in step.get("run", ""))
        self.assertNotIn("selective_base", script)
        self.assertIn("python3 scripts/ci/full-ci-dispatch.test.py", "".join(step.get("run", "") for step in steps))

    def test_sparse_checkout_carries_the_shared_gh_api_module(self):
        checkout = self.workflow["jobs"]["dispatch"]["steps"][0]["with"]["sparse-checkout"].split()
        self.assertIn("scripts/ci/gh_api.py", checkout)

    def test_full_title_prefix_matches_ci_run_name(self):
        ci = (ROOT / ".github/workflows/ci.yml").read_text()
        self.assertIn("&& 'full ' ||", ci.splitlines()[1])
        self.assertTrue(ci.splitlines()[1].startswith('run-name: "CI '))


if __name__ == "__main__":
    unittest.main()
