#!/usr/bin/env python3
"""Exercise fanout bounds, safe deferral and PR priority isolation."""

import json
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import patch

import yaml

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "scripts/ci"))
import affected_admission as admission
import pr_gate_priority as priority


class AdmissionTests(unittest.TestCase):
    def test_total_job_boundary_includes_side_jobs(self):
        for count, allowed in ((0, True), (7, True), (8, False), (139, False)):
            result, _ = admission.decision({"shards": list(range(count)), "run_all": False}, pr_busy=False)
            self.assertEqual(result, allowed, count)

    def test_full_routing_and_pr_backlog_defer_even_a_small_selection(self):
        for full, busy in ((True, False), (False, True), (True, True)):
            self.assertFalse(admission.decision({"shards": [], "run_all": full}, busy)[0])

    def test_unknown_baseline_does_not_dispatch_full_ci(self):
        with patch.object(admission, "selection") as select:
            self.assertFalse(admission.admission("", "head")[0])
            select.assert_not_called()

    def test_router_or_history_outage_defers_instead_of_escalating(self):
        for target in ("selection", "active_pr_gate"):
            with patch.object(admission, "selection", return_value={"shards": [], "run_all": False}), \
                    patch.object(admission, "active_pr_gate", return_value=False), \
                    patch.object(admission, target, side_effect=subprocess.CalledProcessError(1, "probe")):
                self.assertFalse(admission.admission("base", "head")[0])

    def test_unknown_descriptor_defers(self):
        for descriptor in ({"shards": [], "run_all": "false"}, {"shards": "bad", "run_all": False}, {"shards": []}):
            with patch.object(admission, "selection", return_value=descriptor), \
                    patch.object(admission, "active_pr_gate", return_value=False):
                self.assertFalse(admission.admission("base", "head")[0])

    def test_active_pr_gate_in_each_api_status_blocks_admission(self):
        for active in admission.ACTIVE_STATUSES:
            with patch.object(admission, "gh_api", side_effect=lambda endpoint: {
                    "workflow_runs": [{"id": 1}] if f"status={active}&" in endpoint else []}), \
                    patch.dict("os.environ", GITHUB_REPOSITORY="honua-io/honua-server"):
                self.assertTrue(admission.active_pr_gate(), active)

    def test_side_job_bound_covers_workflow_and_matrix_expansion(self):
        jobs = yaml.safe_load((ROOT / ".github/workflows/ci.yml").read_text())["jobs"]
        excluded = {"server-tests", "merge-queue-gate", "store-image-proofs", "geoparquet-interop", "geoarrow-interop"}
        bound = 0
        for name, job in jobs.items():
            if name in excluded:
                continue
            self.assertNotIn("uses", job, "new reusable workflows need an explicit expanded bound")
            matrix = job.get("strategy", {}).get("matrix", {})
            if name == "postgres-compat":
                # Check the runtime producer rather than trusting a static allowance.
                text = (ROOT / ".github/workflows/ci.yml").read_text()
                full = text.split("POSTGRES_FULL='")[1].split("'", 1)[0]
                bound += len(json.loads(full))
            elif "include" in matrix:
                self.assertIsInstance(matrix["include"], list, "unknown dynamic matrix")
                bound += len(matrix["include"])
            else:
                width = 1
                for values in matrix.values():
                    self.assertIsInstance(values, list, "unknown matrix expression")
                    width *= len(values)
                bound += width
        self.assertLessEqual(bound, admission.SIDE_JOB_BUDGET)
        for name in excluded - {"server-tests", "merge-queue-gate"}:
            self.assertIn("github.event_name == 'schedule'", jobs[name]["if"])
            self.assertIn("inputs.full_ci", jobs[name]["if"])


class PriorityTests(unittest.TestCase):
    def test_only_same_repository_trunk_affected_dispatch_is_preemptible(self):
        run = {"head_branch": "trunk", "event": "workflow_dispatch",
               "head_repository": {"full_name": "honua-io/honua-server"},
               "display_title": "CI affected abc..def"}
        self.assertTrue(priority.preemptible(run, "honua-io/honua-server"))
        for change in ({"head_branch": "feature"}, {"event": "schedule"},
                       {"display_title": "CI full retry"}, {"display_title": "CI "},
                       {"head_repository": {"full_name": "fork/server"}}, {"head_repository": None}):
            self.assertFalse(priority.preemptible({**run, **change}, "honua-io/honua-server"), change)

    def test_priority_workflow_checks_out_only_trusted_trunk_code(self):
        workflow = yaml.load((ROOT / ".github/workflows/pr-gate-priority.yml").read_text(), Loader=yaml.BaseLoader)
        self.assertEqual(workflow["on"]["workflow_run"], {
            "workflows": ["PR Gate"], "types": ["requested", "in_progress"]})
        job = workflow["jobs"]["reserve"]
        self.assertEqual(job["permissions"], {"actions": "write", "contents": "read"})
        self.assertEqual(job["steps"][0]["with"]["ref"], "trunk")
        self.assertEqual(job["steps"][0]["with"]["persist-credentials"], "false")


if __name__ == "__main__":
    unittest.main()
