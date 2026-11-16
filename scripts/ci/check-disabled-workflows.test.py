#!/usr/bin/env python3
"""Offline tests for check-disabled-workflows.py."""

from __future__ import annotations

import datetime as dt
import importlib.util
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("check-disabled-workflows.py")
SPEC = importlib.util.spec_from_file_location("check_disabled_workflows", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

TODAY = dt.date(2026, 11, 15)
CALLER = "jobs:\n  proofs:\n    uses: ./.github/workflows/reusable.yml\n"
WORKFLOWS = {
    ".github/workflows/ci.yml": CALLER,
    ".github/workflows/reusable.yml": "on: workflow_call\n",
    ".github/workflows/old.yml": "on: push\n",
}


def check(states, allowed=None, workflows=None):
    return MODULE.violations(workflows or WORKFLOWS, states, allowed or {}, TODAY)


class DisabledWorkflowTests(unittest.TestCase):
    def test_active_workflows_pass(self):
        self.assertEqual(check({".github/workflows/old.yml": "active"}), [])

    def test_disabled_tracked_workflow_fails(self):
        problems = check({".github/workflows/old.yml": "disabled_manually"})
        self.assertEqual(len(problems), 1)
        self.assertIn("old.yml is disabled", problems[0])

    def test_disabled_reusable_called_by_active_workflow_passes(self):
        self.assertEqual(check({".github/workflows/reusable.yml": "disabled_manually"}), [])

    def test_disabled_reusable_called_only_by_disabled_workflow_fails(self):
        states = {".github/workflows/reusable.yml": "disabled_manually",
                  ".github/workflows/ci.yml": "disabled_manually"}
        self.assertEqual(len(check(states)), 2)

    def test_disabled_workflow_called_through_a_disabled_reusable_passes(self):
        workflows = {**WORKFLOWS, ".github/workflows/reusable.yml": "jobs:\n  inner:\n    uses: './.github/workflows/old.yml'\n"}
        states = {".github/workflows/reusable.yml": "disabled_manually",
                  ".github/workflows/old.yml": "disabled_manually"}
        self.assertEqual(check(states, workflows=workflows), [])

    def test_commented_call_does_not_exempt(self):
        workflows = {**WORKFLOWS, ".github/workflows/ci.yml": CALLER + "# uses: ./.github/workflows/old.yml\n"}
        self.assertEqual(len(check({".github/workflows/old.yml": "disabled_manually"}, workflows=workflows)), 1)

    def test_allowance_for_a_called_workflow_fails(self):
        problems = check({".github/workflows/reusable.yml": "disabled_manually"},
                         {".github/workflows/reusable.yml": ("#1", "2026-12-31")})
        self.assertIn("drop the entry", problems[0])

    def test_disabled_workflow_already_deleted_passes(self):
        self.assertEqual(check({".github/workflows/gone.yml": "disabled_manually"}), [])

    def test_unexpired_allowance_passes_and_expired_fails(self):
        states = {".github/workflows/old.yml": "disabled_inactivity"}
        self.assertEqual(check(states, {".github/workflows/old.yml": ("#1", "2026-11-15")}), [])
        problems = check(states, {".github/workflows/old.yml": ("#1", "2026-11-14")})
        self.assertIn("expired on 2026-11-14", problems[0])

    def test_stale_allowance_fails(self):
        problems = check({".github/workflows/old.yml": "active"},
                         {".github/workflows/old.yml": ("#1", "2026-12-31")})
        self.assertIn("drop the entry", problems[0])

    def test_repository_allowances_name_a_ticket_and_date(self):
        for path, (ticket, expires) in MODULE.ALLOWED.items():
            self.assertTrue(path.startswith(".github/workflows/"), path)
            self.assertRegex(ticket, r"^#\d+$")
            dt.date.fromisoformat(expires)

    def test_tracked_workflows_reads_the_repository(self):
        tracked = MODULE.tracked_workflows(MODULE.ROOT)
        self.assertIn(".github/workflows/ci.yml", tracked)
        self.assertIn(".github/workflows/disabled-workflow-check.yml", tracked)


if __name__ == "__main__":
    unittest.main()
