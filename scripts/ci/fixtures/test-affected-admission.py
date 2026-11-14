#!/usr/bin/env python3
"""Exercise fanout bounds, safe deferral and coexistence with PR Gate."""

import json
import os
import re
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import yaml

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "scripts/ci"))
import affected_admission as admission
import trailing_selection


class AdmissionTests(unittest.TestCase):
    def test_total_job_boundary_includes_side_jobs(self):
        for count, allowed in ((0, True), (7, True), (8, False), (139, False)):
            result, _ = admission.decision({"shards": list(range(count)), "run_all": False})
            self.assertEqual(result, allowed, count)

    def test_full_routing_defers_even_a_small_selection(self):
        self.assertFalse(admission.decision({"shards": [], "run_all": True})[0])

    def test_active_pr_gate_does_not_defer_a_bounded_selection(self):
        # The two-shard width, not PR activity, keeps affected runs off the PR pool:
        # with ten open PRs a PR Gate is nearly always active (0 of 32 admitted on 14 Nov).
        with patch.object(admission, "selection", return_value={"shards": [1, 2], "run_all": False}):
            self.assertTrue(admission.admission("base", "head")[0])
        self.assertFalse(hasattr(admission, "active_pr_gate"))
        self.assertEqual(trailing_selection.affected_max_parallel(7), 2)

    def test_unknown_baseline_does_not_dispatch_full_ci(self):
        with patch.object(admission, "selection") as select:
            self.assertFalse(admission.admission("", "head")[0])
            select.assert_not_called()

    def test_router_or_history_outage_defers_instead_of_escalating(self):
        with patch.object(admission, "selection", side_effect=subprocess.CalledProcessError(1, "probe")):
            self.assertFalse(admission.admission("base", "head")[0])

    def test_unknown_descriptor_defers(self):
        for descriptor in ({"shards": [], "run_all": "false"}, {"shards": "bad", "run_all": False}, {"shards": []}):
            with patch.object(admission, "selection", return_value=descriptor):
                self.assertFalse(admission.admission("base", "head")[0])

    def test_recomputed_matrix_rejects_router_fallback_before_outputs(self):
        job = yaml.safe_load((ROOT / ".github/workflows/ci.yml").read_text())["jobs"]["targeted-shards"]
        script = next(step["run"] for step in job["steps"] if step.get("id") == "compute")
        guard = script.split('echo "Descriptor: ${descriptor}"', 1)[1].split("# Build the matrix", 1)[0]
        self.assertLess(script.index("--descriptor"), script.index("matrix_include="))
        for expression, value in (("github.event_name", "workflow_dispatch"),
                                  ("github.ref", "refs/heads/trunk"), ("inputs.full_ci", "false")):
            guard = guard.replace("${{ " + expression + " }}", value)
        with tempfile.TemporaryDirectory() as directory:
            for count, full, expected in ((7, False, 0), (8, False, 1), (142, True, 1)):
                environment = {**os.environ, "TRAILING_SELECTIVE_BASE": "a" * 40,
                               "descriptor": json.dumps({"shards": list(range(count)), "run_all": full}),
                               "GITHUB_STEP_SUMMARY": f"{directory}/summary"}
                result = subprocess.run(["bash", "-e", "-c", guard], cwd=ROOT,
                                        env=environment, capture_output=True, text=True)
                self.assertEqual(result.returncode, expected, result.stderr)

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


class CoexistenceTests(unittest.TestCase):
    def test_only_covering_or_own_runs_cancel_affected_ci(self):
        # Preempting a started affected run for PR Gate discarded its work and, with a
        # PR Gate almost always active, left trunk without post-merge coverage.
        def cancels(text):
            imports = [line for line in text.splitlines() if line.startswith("from supersede_trailing_runs")]
            return re.search(r"actions/runs/\S*/cancel", text) or any("cancel" in line for line in imports)
        cancellers = {path.name for path in (ROOT / "scripts/ci").glob("*.py") if cancels(path.read_text())}
        self.assertEqual(cancellers, {"supersede_trailing_runs.py", "resolve-trailing-base.py"})

    def test_affected_shards_keep_the_two_runner_width(self):
        job = yaml.safe_load((ROOT / ".github/workflows/ci.yml").read_text())["jobs"]["server-tests"]
        self.assertIn("needs.targeted-shards.outputs.affected_max_parallel", job["strategy"]["max-parallel"])


if __name__ == "__main__":
    unittest.main()
