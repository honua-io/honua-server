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


class AdmissionTests(unittest.TestCase):
    def test_total_job_boundary_includes_side_jobs(self):
        for count, allowed in ((0, True), (7, True), (8, False), (139, False)):
            result, _ = admission.decision({"shards": list(range(count)), "run_all": False})
            self.assertEqual(result, allowed, count)

    def test_full_routing_defers_even_a_small_selection(self):
        self.assertFalse(admission.decision({"shards": [], "run_all": True})[0])

    def test_active_pr_gate_does_not_defer_a_bounded_selection(self):
        routed = json.dumps({"shards": [1, 2], "run_all": False})

        def run(command, **_):
            if command[0] == "gh":
                return json.dumps({"workflow_runs": [{"id": 1, "status": "in_progress"}]})
            return "src/changed.cs\n" if command[0] == "git" else routed

        with patch.object(admission.subprocess, "check_output", side_effect=run) as check_output:
            self.assertTrue(admission.admission("base", "head")[0])
        self.assertNotIn("gh", [call.args[0][0] for call in check_output.call_args_list])

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


CANCELLATION = re.compile(r"gh run cancel|actions/runs/\S*/(force-)?cancel|cancelWorkflowRun|supersede_trailing_runs\.cancel\b"
                          r"|from supersede_trailing_runs import[^\n]*\bcancel\b|from supersede_trailing_runs import \([^)]*\bcancel\b")
# Only these may cancel trunk CI: a full run cancels selective runs it covers, a
# selective run cancels itself, and the native-image check lists the pattern as forbidden.
ALLOWED_CANCELLERS = {"supersede_trailing_runs.py", "resolve-trailing-base.py", "native-image-impact.py"}


class CoexistenceTests(unittest.TestCase):
    def test_only_covering_or_own_runs_cancel_trunk_ci(self):
        sources = [*(path for pattern in ("*.yml", "*.yaml") for path in (ROOT / ".github").rglob(pattern)),
                   *(path for pattern in ("*.py", "*.sh", "*.mjs") for path in (ROOT / "scripts/ci").rglob(pattern)
                     if "fixtures" not in path.parts)]
        cancellers = {path.name for path in sources if CANCELLATION.search(path.read_text())}
        self.assertEqual(cancellers, ALLOWED_CANCELLERS)

    def test_cancellation_scan_detects_each_preemption_form(self):
        for text in ("gh run cancel 1", "actions/runs/{id}/cancel", "actions/runs/{id}/force-cancel",
                     "supersede_trailing_runs.cancel(run)",
                     "from supersede_trailing_runs import active_trunk_runs, cancel",
                     "from supersede_trailing_runs import (\n    active_trunk_runs,\n    cancel,\n)"):
            self.assertRegex(text, CANCELLATION)


if __name__ == "__main__":
    unittest.main()
