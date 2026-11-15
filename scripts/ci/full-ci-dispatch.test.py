#!/usr/bin/env python3
"""Offline tests for full-ci-dispatch.py and its workflow wiring."""

from __future__ import annotations

import datetime as dt
import importlib.util
import sys
import unittest
from pathlib import Path

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


def run(run_id: int, created: str, *, event: str = "schedule", title: str = "CI full ",
        status: str = "completed", conclusion: str | None = "failure", sha: str = OLD,
        branch: str = "trunk") -> dict:
    return dict(id=run_id, created_at=created + "Z", event=event, display_title=title, status=status,
                conclusion=conclusion, head_sha=sha, head_branch=branch)


class DecideTests(unittest.TestCase):
    def test_dispatches_inside_window_when_trunk_head_has_no_full_result(self):
        runs = [run(1, "2026-11-15T01:05:40", conclusion="cancelled")]
        self.assertTrue(MODULE.decide(at("2026-11-15T06:10:00"), runs, HEAD)[0])

    def test_never_dispatches_outside_window(self):
        for moment in ("2026-11-15T05:59:59", "2026-11-15T11:00:00", "2026-11-15T20:00:00",
                       "2027-07-15T04:30:00", "2027-07-15T10:00:00"):
            dispatch, reason = MODULE.decide(at(moment), [], HEAD)
            self.assertFalse(dispatch, moment)
            self.assertIn("outside", reason)

    def test_last_minute_of_window_still_dispatches(self):
        self.assertTrue(MODULE.decide(at("2026-11-15T10:59:00"), [], HEAD)[0])

    def test_window_follows_pacific_daylight_time(self):
        # PDT: 22:00-03:00 Pacific is 05:00-10:00 UTC.
        self.assertTrue(MODULE.decide(at("2027-07-15T05:00:00"), [], HEAD)[0])
        self.assertTrue(MODULE.decide(at("2027-07-15T09:59:00"), [], HEAD)[0])
        self.assertFalse(MODULE.decide(at("2027-07-15T10:00:00"), [], HEAD)[0])

    def test_night_starts_at_pacific_window_open_across_midnight(self):
        # 08:30 UTC is 00:30 PST; a run created at 06:05 UTC (22:05 PST the day before) counts.
        runs = [run(20, "2026-11-15T06:05:00", event="workflow_dispatch", conclusion="success"),
                run(21, "2026-11-15T07:30:00", event="schedule", conclusion="failure")]
        self.assertFalse(MODULE.decide(at("2026-11-15T08:30:00"), runs, HEAD)[0])
        # The previous evening's 05:59 UTC run (21:59 PST) is outside tonight's window.
        runs[0]["created_at"] = "2026-11-15T05:59:00Z"
        self.assertTrue(MODULE.decide(at("2026-11-15T08:30:00"), runs, HEAD)[0])

    def test_active_full_run_blocks_dispatch(self):
        for status in ("queued", "in_progress", "waiting", "pending"):
            runs = [run(7, "2026-11-15T03:00:00", status=status, conclusion=None)]
            dispatch, reason = MODULE.decide(at("2026-11-15T07:00:00"), runs, HEAD)
            self.assertFalse(dispatch, status)
            self.assertIn(status, reason)

    def test_active_affected_run_does_not_block(self):
        runs = [run(8, "2026-11-15T06:30:00", event="workflow_dispatch", status="in_progress",
                    conclusion=None, title="CI affected affected 98c4e5355c31..b54d93edd04d")]
        self.assertTrue(MODULE.decide(at("2026-11-15T07:00:00"), runs, HEAD)[0])

    def test_branch_runs_are_ignored(self):
        runs = [run(9, "2026-11-15T06:30:00", status="in_progress", conclusion=None, branch="train/batch/x")]
        self.assertTrue(MODULE.decide(at("2026-11-15T07:00:00"), runs, HEAD)[0])

    def test_trunk_head_with_full_result_is_not_rerun(self):
        runs = [run(10, "2026-11-15T06:05:00", event="workflow_dispatch", conclusion="success", sha=HEAD)]
        dispatch, reason = MODULE.decide(at("2026-11-15T09:30:00"), runs, HEAD)
        self.assertFalse(dispatch)
        self.assertIn("already has full result", reason)

    def test_failed_full_result_on_head_is_not_rerun(self):
        runs = [run(10, "2026-11-15T06:05:00", event="workflow_dispatch", conclusion="failure", sha=HEAD)]
        self.assertFalse(MODULE.decide(at("2026-11-15T09:30:00"), runs, HEAD)[0])

    def test_cancelled_full_run_on_head_does_not_count(self):
        runs = [run(11, "2026-11-15T06:05:00", event="workflow_dispatch", conclusion="cancelled", sha=HEAD)]
        self.assertTrue(MODULE.decide(at("2026-11-15T09:30:00"), runs, HEAD)[0])

    def test_second_run_of_the_night_when_trunk_moved(self):
        runs = [run(12, "2026-11-15T06:05:00", event="workflow_dispatch", conclusion="success", sha=OLD)]
        self.assertTrue(MODULE.decide(at("2026-11-15T09:10:00"), runs, HEAD)[0])

    def test_nightly_limit_counts_runs_that_were_not_cancelled(self):
        runs = [run(13, "2026-11-15T06:05:00", event="workflow_dispatch", conclusion="success"),
                run(14, "2026-11-15T09:05:00", event="schedule", conclusion="failure"),
                run(15, "2026-11-15T05:00:00", conclusion="success")]
        dispatch, reason = MODULE.decide(at("2026-11-15T10:50:00"), runs, HEAD)
        self.assertFalse(dispatch)
        self.assertIn("2 full runs", reason)
        runs[1]["conclusion"] = "cancelled"
        self.assertTrue(MODULE.decide(at("2026-11-15T10:50:00"), runs, HEAD)[0])


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

    def test_full_title_prefix_matches_ci_run_name(self):
        ci = (ROOT / ".github/workflows/ci.yml").read_text()
        self.assertIn("&& 'full ' ||", ci.splitlines()[1])
        self.assertTrue(ci.splitlines()[1].startswith('run-name: "CI '))


if __name__ == "__main__":
    unittest.main()
